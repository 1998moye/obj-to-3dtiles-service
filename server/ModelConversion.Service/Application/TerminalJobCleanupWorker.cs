using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure;

namespace ModelConversion.Service.Application;

// [2026-09-09 Issue01 终态清理] 后台定时清理：
// 1) 终态（成功/失败/取消）超过 TTL 的作业：删除输出目录、服务管理输入、暂存/日志/清单与作业状态文件；
// 2) 已过期的上传会话目录（分片、合并包、状态），不触碰已发布输入。
// 安全不变量：运行中/校验中/未到 TTL/有活跃结果读租约的作业绝不删除；
// 清理前二次读取作业状态与时间；单个作业失败不停止工作器，下轮扫描重试；
// 日志只打作业 ID 与相对路径，不泄露用户 ZIP 内容或宿主敏感路径。
public sealed class TerminalJobCleanupWorker(
    IConversionJobRepository repository,
    ConversionProgressRegistry progressRegistry,
    ResultReadLeaseRegistry leaseRegistry,
    UploadSessionService sessionService,
    ConversionOptions options,
    ModelUploadOptions uploadOptions,
    TimeProvider timeProvider,
    ILogger<TerminalJobCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.CleanupIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await CleanupTerminalJobsOnceAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "终态作业清理扫描失败，下轮重试");
            }

            try
            {
                await CleanupExpiredSessionsOnceAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "过期上传会话清理扫描失败，下轮重试");
            }
        }
    }

    private async Task CleanupTerminalJobsOnceAsync(CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        var retention = TimeSpan.FromHours(options.TerminalRetentionHours);
        var cutoff = now - retention;
        var jobs = await repository.ListAsync(stoppingToken);
        var candidates = jobs
            .Where(job => job.IsTerminal && (job.FinishedAt ?? job.UpdatedAt) <= cutoff)
            .OrderBy(job => job.FinishedAt ?? job.UpdatedAt)
            .ToArray();

        foreach (var candidate in candidates)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                // [2026-09-09 Issue01 二次读取] 扫描快照可能已过期：清理前重新读取作业，
                // 状态变为非终态（如被重试引用中）或时间未到一律跳过，运行作业绝不被删除。
                var fresh = await repository.GetAsync(candidate.Id, stoppingToken);
                if (fresh == null || !fresh.IsTerminal || (fresh.FinishedAt ?? fresh.UpdatedAt) > cutoff)
                    continue;
                if (leaseRegistry.HasActiveLease(fresh.Id))
                {
                    logger.LogInformation("跳过正在下载结果的终态作业: {JobId}", fresh.Id.ToString("N"));
                    continue;
                }

                // [2026-09-09 Issue01 删除顺序] 固定：输出目录 → 服务管理输入 → 暂存/日志/清单 → 作业状态文件。
                // 状态文件最后删：中途失败时下轮扫描仍能从状态文件识别该作业并重试；
                // 反向顺序（先删状态）会让残留产物变成无人认领的孤儿。
                DeleteOutputDirectory(fresh);
                await TryDeleteManagedInputAsync(fresh, cutoff, stoppingToken);
                DeleteStateResidue(fresh);
                await repository.DeleteAsync(fresh.Id, stoppingToken);
                progressRegistry.Remove(fresh.Id);
                logger.LogInformation("已清理终态作业: {JobId} 状态={State} 输出={OutputRelativePath}",
                    fresh.Id.ToString("N"), fresh.State, fresh.OutputRelativePath);
            }
            catch (Exception exception)
            {
                // 异常隔离：单个作业清理失败不停止工作器与其他作业，下轮扫描重试。
                logger.LogWarning("清理终态作业失败，下轮重试: {JobId} ({Message})",
                    candidate.Id.ToString("N"), exception.Message);
            }
        }
    }

    private void DeleteOutputDirectory(ConversionJob job)
    {
        var outputDirectory = PathBoundary.ResolveOwnedPath(options.OutputRoot, job.OutputRelativePath);
        if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
    }

    private async Task TryDeleteManagedInputAsync(ConversionJob job, DateTimeOffset cutoff, CancellationToken stoppingToken)
    {
        // [2026-09-09 Issue01 输入删除边界] 只删除服务命名空间 uploads/{key} 下的输入
        // （单请求上传与上传会话发布的目录）；用户手工放入 InputRoot 的目录（如 obj1/...）绝不删除。
        var normalized = job.InputRelativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "uploads", StringComparison.OrdinalIgnoreCase))
            return;
        var inputTopRelative = segments[0] + "/" + segments[1];

        // 共享输入保护：重试/多作业可引用同一输入；存在任何未过期引用时跳过，
        // 由最后一个过期的引用者负责删除。重新列表避免使用扫描开始时的旧快照。
        var referencing = (await repository.ListAsync(stoppingToken))
            .Where(other => other.Id != job.Id
                && other.InputRelativePath.Replace('\\', '/')
                    .StartsWith(inputTopRelative + "/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (referencing.Any(other => !other.IsTerminal || (other.FinishedAt ?? other.UpdatedAt) > cutoff))
        {
            logger.LogInformation("输入仍被未过期作业引用，跳过删除: {JobId} 输入={InputTop}",
                job.Id.ToString("N"), inputTopRelative);
            return;
        }

        var inputTopDirectory = PathBoundary.ResolveOwnedPath(options.InputRoot, inputTopRelative);
        if (Directory.Exists(inputTopDirectory)) Directory.Delete(inputTopDirectory, recursive: true);
    }

    private void DeleteStateResidue(ConversionJob job)
    {
        // 暂存工作目录、任务日志与结果清单（均属于该作业独占的状态残留）。
        DeleteDirectoryQuietly(Path.Combine(options.StateRoot, "work", job.Id.ToString("N")));
        DeleteFileQuietly(Path.Combine(options.StateRoot, "logs", job.Id.ToString("N") + ".log"));
        DeleteFileQuietly(Path.Combine(options.StateRoot, "manifests", job.Id.ToString("N") + ".json"));
    }

    private async Task CleanupExpiredSessionsOnceAsync(CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!Directory.Exists(sessionService.SessionsRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(sessionService.SessionsRoot))
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                var session = await sessionService.LoadFromDirectoryAsync(directory, stoppingToken);
                if (session == null)
                {
                    // [2026-09-09 Issue01 会话残留] 状态文件损坏/缺失的目录按目录写入时间超 TTL 回收。
                    var writtenAt = Directory.GetLastWriteTimeUtc(directory);
                    if (now - writtenAt > TimeSpan.FromHours(uploadOptions.SessionTtlHours))
                        DeleteDirectoryQuietly(directory);
                    continue;
                }
                // finalizing 交给完成器收尾（完成/失败后随 TTL 清理）；已发布输入位于 InputRoot，不在此删除。
                if (session.State == UploadSessionState.Finalizing) continue;
                if (session.ExpiresAt <= now)
                {
                    await sessionService.DeleteSessionDirectoryAsync(session.Id);
                    logger.LogInformation("已清理过期上传会话: {SessionId} 状态={State}",
                        session.Id.ToString("N"), session.State);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning("清理上传会话失败，下轮重试: {SessionDirectory} ({Message})",
                    Path.GetFileName(directory), exception.Message);
            }
        }
    }

    private static void DeleteFileQuietly(string path)
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
