using System.Security.Cryptography;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure;

namespace ModelConversion.Service.Application;

// [2026-09-09 Issue01 可恢复上传会话] 后台有界完成器：按序合并分片（1MiB 流式，边合并边算整包 SHA-256）
// → 校验总大小与整包摘要 → 共享安全解压器解压到 InputRoot 同卷暂存 → 原子发布。
// 全程单并发执行，10GB 包的最大驻留内存为 1MiB 缓冲区；服务重启后扫描 finalizing 会话
// 整体重来一遍（合并包与暂存先清后建，发布靠同卷目录移动保持原子），恢复或稳定失败，不重复发布输入。
public sealed class UploadSessionFinalizer(
    UploadSessionService sessionService,
    UploadSessionFinalizeQueue finalizeQueue,
    SafeModelPackageExtractor packageExtractor,
    ConversionOptions conversionOptions,
    ILogger<UploadSessionFinalizer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverFinalizingSessionsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessionId = await finalizeQueue.DequeueAsync(stoppingToken);
                await FinalizeOneAsync(sessionId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "上传会话完成器发生未处理异常");
            }
        }
    }

    private async Task RecoverFinalizingSessionsAsync(CancellationToken cancellationToken)
    {
        foreach (var session in await sessionService.ListAsync(cancellationToken))
        {
            if (session.State != UploadSessionState.Finalizing) continue;
            logger.LogInformation("恢复未完成的上传会话合并: {SessionId}", session.Id.ToString("N"));
            await finalizeQueue.EnqueueAsync(session.Id, cancellationToken);
        }
    }

    private async Task FinalizeOneAsync(Guid sessionId, CancellationToken stoppingToken)
    {
        var session = await sessionService.LoadAsync(sessionId, stoppingToken);
        if (session == null || session.State != UploadSessionState.Finalizing) return;

        var sessionDirectory = sessionService.GetSessionDirectory(session.Id);
        var mergedPath = Path.Combine(sessionDirectory, "merged.zip");
        var stagingPath = Path.Combine(conversionOptions.InputRoot, ".upload-staging", session.Id.ToString("N"));
        var publishedPath = Path.Combine(conversionOptions.InputRoot, "uploads", session.Id.ToString("N"));
        try
        {
            // 发布目录已存在说明上次崩溃发生在发布之后、状态落盘之前：直接补登完成，不重复发布。
            if (Directory.Exists(publishedPath))
            {
                await CompleteFromPublishedAsync(session, publishedPath, stoppingToken);
                return;
            }

            // 重启恢复的合并包与暂存目录先清后建，保证本轮从干净状态开始。
            if (File.Exists(mergedPath)) File.Delete(mergedPath);
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
            Directory.CreateDirectory(stagingPath);

            var (mergedBytes, mergedSha) = await MergePartsStreamingAsync(session, mergedPath, stoppingToken);
            if (mergedBytes != session.SizeBytes || !string.Equals(mergedSha, session.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await sessionService.MarkFailedAsync(session.Id, UploadSessionErrorCodes.HashMismatch,
                    "合并后的整包大小或摘要与创建会话时声明不一致", stoppingToken);
                return;
            }

            var extraction = await packageExtractor.ExtractAsync(mergedPath, stagingPath, stoppingToken);

            Directory.CreateDirectory(Path.GetDirectoryName(publishedPath)!);
            // 同卷目录移动为原子发布，转换任务永远看不到半上传模型（与单请求上传同口径）。
            Directory.Move(stagingPath, publishedPath);
            await CompleteFromPublishedAsync(session, publishedPath, stoppingToken);

            // [2026-09-09 Issue01 完成清理] 原代码: 成功后保留分片与合并包。
            // 原因: 已发布输入由 InputRoot 持久保存，分片/合并包（可达 10GB）只是传输中间态，
            // 成功后立即删除；失败会话的分片保留到 TTL 供排查，由终态清理兜底。
            DeleteQuietly(mergedPath);
            DeleteDirectoryQuietly(sessionService.GetPartsDirectory(session.Id));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停机：保持 finalizing 状态，下一次启动整体重来。
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ModelUploadTooLargeException)
        {
            logger.LogWarning(exception, "上传会话 {SessionId} 合并/解压失败", session.Id.ToString("N"));
            await sessionService.MarkFailedAsync(session.Id,
                exception is ModelUploadTooLargeException ? UploadSessionErrorCodes.PartInvalid : UploadSessionErrorCodes.FinalizeFailed,
                exception.Message, stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "上传会话 {SessionId} 完成阶段未知失败", session.Id.ToString("N"));
            await sessionService.MarkFailedAsync(session.Id, UploadSessionErrorCodes.FinalizeFailed,
                "合并或解压发生内部错误", stoppingToken);
        }
        finally
        {
            // 暂存目录可能残留（失败路径）：它只属于本会话，删除不影响已发布输入。
            DeleteDirectoryQuietly(stagingPath);
        }
    }

    private async Task CompleteFromPublishedAsync(UploadSession session, string publishedPath, CancellationToken cancellationToken)
    {
        var publishedRoot = Path.GetFullPath(publishedPath);
        var objectFiles = Directory.EnumerateFiles(publishedPath, "*.obj", SearchOption.AllDirectories).ToArray();
        if (objectFiles.Length != 1)
        {
            await sessionService.MarkFailedAsync(session.Id, UploadSessionErrorCodes.FinalizeFailed,
                $"发布目录中 OBJ 数量为 {objectFiles.Length}，无法补登完成", cancellationToken);
            return;
        }
        var referenceMatches = Directory.EnumerateFiles(publishedPath, "reference_lla.json", SearchOption.AllDirectories)
            .Take(2)
            .ToArray();
        var inputPath = ToPortableRelativePath(conversionOptions.InputRoot, objectFiles[0]);
        var referenceLlaPath = referenceMatches.Length == 1
            ? ToPortableRelativePath(conversionOptions.InputRoot, referenceMatches[0])
            : null;
        await sessionService.MarkCompletedAsync(session.Id, inputPath, referenceLlaPath, cancellationToken);
        logger.LogInformation("上传会话 {SessionId} 完成并发布输入: {InputPath}", session.Id.ToString("N"), inputPath);
    }

    private async Task<(long Bytes, string Sha256)> MergePartsStreamingAsync(
        UploadSession session, string mergedPath, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            mergedPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var buffer = new byte[1024 * 1024];
        for (var partNumber = 1; partNumber <= session.PartCount; partNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partPath = sessionService.GetPartPath(session.Id, partNumber);
            if (!File.Exists(partPath))
                throw new IOException($"分片 {partNumber} 文件缺失");
            await using var source = new FileStream(
                partPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                hasher.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total = checked(total + read);
            }
        }
        return (total, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
    }

    private static string ToPortableRelativePath(string root, string path) =>
        Path.GetRelativePath(Path.GetFullPath(root), path).Replace('\\', '/');

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
