using System.Buffers;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Infrastructure;

namespace ModelConversion.Service.Application;

public sealed record ModelUploadResult(
    Guid UploadId,
    string InputPath,
    string? ReferenceLlaPath,
    long ArchiveBytes,
    long ExtractedBytes,
    int FileCount);

public sealed class ModelUploadTooLargeException(string message) : Exception(message);

public sealed class ModelUploadService(
    ConversionOptions conversionOptions,
    ModelUploadOptions uploadOptions,
    SafeModelPackageExtractor packageExtractor)
{
    public async Task<ModelUploadResult> UploadAsync(Stream requestBody, long? contentLength, CancellationToken cancellationToken)
    {
        if (contentLength is <= 0)
            throw new InvalidDataException("上传内容不能为空");
        if (contentLength > uploadOptions.MaxArchiveBytes)
            throw new ModelUploadTooLargeException($"压缩包超过 {uploadOptions.MaxArchiveBytes} 字节上限");

        var uploadId = Guid.NewGuid();
        var uploadKey = uploadId.ToString("N");
        var incomingRoot = Path.Combine(conversionOptions.StateRoot, "uploads", "incoming");
        var stagingRoot = Path.Combine(conversionOptions.InputRoot, ".upload-staging");
        var publishedRoot = Path.Combine(conversionOptions.InputRoot, "uploads");
        var archivePath = Path.Combine(incomingRoot, uploadKey + ".zip.part");
        var stagingPath = Path.Combine(stagingRoot, uploadKey);
        var publishedPath = Path.Combine(publishedRoot, uploadKey);

        Directory.CreateDirectory(incomingRoot);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(publishedRoot);

        try
        {
            var archiveBytes = await SaveArchiveAsync(requestBody, archivePath, cancellationToken);
            Directory.CreateDirectory(stagingPath);
            // [2026-09-09 Issue01 可恢复上传会话] 原代码: 本类私有 ExtractArchiveAsync + OBJ 数量校验。
            // 原因: ZIP 安全解压与包结构校验抽为 SafeModelPackageExtractor，与上传会话后台合并共用同一套实现。
            var extraction = await packageExtractor.ExtractAsync(archivePath, stagingPath, cancellationToken);

            // [2026-09-08 模型包上传] 同卷目录移动为原子发布，转换任务永远看不到半上传模型。
            Directory.Move(stagingPath, publishedPath);

            var inputPath = ToPortableRelativePath(conversionOptions.InputRoot,
                Path.Combine(publishedPath, extraction.ObjectRelativePath));
            var referenceLlaPath = extraction.ReferenceLlaRelativePath == null
                ? null
                : ToPortableRelativePath(conversionOptions.InputRoot, Path.Combine(publishedPath, extraction.ReferenceLlaRelativePath));
            return new ModelUploadResult(
                uploadId,
                inputPath,
                referenceLlaPath,
                archiveBytes,
                extraction.ExtractedBytes,
                extraction.FileCount);
        }
        finally
        {
            // [2026-09-08 模型包上传] 这里只清理由本次 uploadId 独占的暂存物，不触碰已发布输入。
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingPath);
        }
    }

    private async Task<long> SaveArchiveAsync(Stream source, string archivePath, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                total = checked(total + read);
                if (total > uploadOptions.MaxArchiveBytes)
                    throw new ModelUploadTooLargeException($"压缩包超过 {uploadOptions.MaxArchiveBytes} 字节上限");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (total == 0)
            throw new InvalidDataException("上传内容不能为空");
        return total;
    }

    private static string ToPortableRelativePath(string root, string path) =>
        Path.GetRelativePath(Path.GetFullPath(root), path).Replace('\\', '/');

    private static void TryDeleteFile(string path)
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

    private static void TryDeleteDirectory(string path)
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
