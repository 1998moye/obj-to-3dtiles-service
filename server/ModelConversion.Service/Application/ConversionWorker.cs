using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure;

namespace ModelConversion.Service.Application;

public sealed class ConversionWorker(
    IConversionJobRepository repository,
    ConversionJobQueue queue,
    RunningJobRegistry runningJobs,
    ConversionProgressRegistry progressRegistry,
    IConversionRunner runner,
    ConversionSettingsResolver settingsResolver,
    ConversionOptions options,
    TimeProvider timeProvider,
    ILogger<ConversionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        var consumers = Enumerable.Range(0, options.MaxConcurrentJobs)
            .Select(index => ConsumeAsync(index, stoppingToken));
        await Task.WhenAll(consumers);
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var job in await repository.ListAsync(cancellationToken))
        {
            if (job.IsTerminal) continue;
            var recovered = job.State == ConversionJobState.Queued ? job : job.RecoverToQueue(timeProvider.GetUtcNow());
            if (!ReferenceEquals(recovered, job)) await repository.SaveAsync(recovered, cancellationToken);
            progressRegistry.Report(recovered.Id,
                new ConversionProgressUpdate(0, ConversionProgressStage.Queued, "服务重启后等待恢复执行"));
            await queue.EnqueueAsync(recovered.Id, cancellationToken);
        }
    }

    private async Task ConsumeAsync(int consumerIndex, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobId = await queue.DequeueAsync(stoppingToken);
                await ProcessAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "转换消费者 {ConsumerIndex} 发生未处理异常", consumerIndex);
            }
        }
    }

    private async Task ProcessAsync(Guid jobId, CancellationToken stoppingToken)
    {
        var job = await repository.GetAsync(jobId, stoppingToken);
        if (job == null || job.State != ConversionJobState.Queued) return;

        var inputPath = PathBoundary.ResolveExistingFile(options.InputRoot, job.InputRelativePath);
        var finalOutput = PathBoundary.ResolveOwnedPath(options.OutputRoot, job.OutputRelativePath);
        var stagingOutput = PathBoundary.ResolveOwnedPath(options.StateRoot, Path.Combine("work", job.Id.ToString("N")));
        var logPath = PathBoundary.ResolveOwnedPath(options.StateRoot, Path.Combine("logs", job.Id.ToString("N") + ".log"));

        job = job.TransitionTo(ConversionJobState.Running, timeProvider.GetUtcNow());
        await repository.SaveAsync(job, stoppingToken);
        progressRegistry.Report(job.Id,
            new ConversionProgressUpdate(5, ConversionProgressStage.Preparing, "任务已开始"));
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var registration = runningJobs.Register(job.Id, jobCancellation);

        try
        {
            // 参数快照优先；历史作业没有快照时按 profile 名解析一次，保持兼容。
            var settings = job.SettingsSnapshot ?? settingsResolver.Resolve(job.ProfileName, overrides: null);
            var result = await runner.RunAsync(
                new ConversionRunRequest(job.Id, inputPath, finalOutput, stagingOutput, logPath,
                    settings, job.GeoReference, RevalidateExistingOutput: true,
                    Progress: update => progressRegistry.Report(job.Id, update)),
                jobCancellation.Token);

            switch (result.Outcome)
            {
                case ConversionRunOutcome.Succeeded:
                    var validating = job.TransitionTo(ConversionJobState.Validating, timeProvider.GetUtcNow(),
                        diagnostic: result.ReusedExistingOutput ? "检测到已发布输出，执行幂等复核" : null,
                        exitCode: result.ExitCode);
                    await repository.SaveAsync(validating, stoppingToken);
                    job = validating.TransitionTo(ConversionJobState.Succeeded, timeProvider.GetUtcNow(), validation: result.Validation);
                    await repository.SaveAsync(job, stoppingToken);
                    progressRegistry.MarkTerminal(job, ConversionProgressStage.Completed, "转换完成");
                    return;
                case ConversionRunOutcome.Canceled when stoppingToken.IsCancellationRequested:
                    // [2026-09-04 重启恢复] 服务停机时保留 Running/Validating，下一次启动统一恢复排队。
                    return;
                case ConversionRunOutcome.Canceled:
                    await MarkCanceledAsync(job);
                    return;
                default:
                    await MarkFailedAsync(job, result.Diagnostic ?? $"转换运行结果异常: {result.Outcome}");
                    return;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "转换任务 {JobId} 失败", job.Id);
            await MarkFailedAsync(job, exception.Message);
        }
    }

    private async Task MarkFailedAsync(ConversionJob job, string diagnostic)
    {
        var current = await repository.GetAsync(job.Id, CancellationToken.None) ?? job;
        if (current.IsTerminal) return;
        var truncated = diagnostic.Length <= 4000 ? diagnostic : diagnostic[..4000];
        current = current.TransitionTo(ConversionJobState.Failed, timeProvider.GetUtcNow(), truncated);
        await repository.SaveAsync(current, CancellationToken.None);
        progressRegistry.MarkTerminal(current, ConversionProgressStage.Failed, truncated);
    }

    private async Task MarkCanceledAsync(ConversionJob job)
    {
        var current = await repository.GetAsync(job.Id, CancellationToken.None) ?? job;
        if (current.IsTerminal) return;
        current = current.TransitionTo(ConversionJobState.Canceled, timeProvider.GetUtcNow(), "任务已取消");
        await repository.SaveAsync(current, CancellationToken.None);
        progressRegistry.MarkTerminal(current, ConversionProgressStage.Canceled, "任务已取消");
    }
}
