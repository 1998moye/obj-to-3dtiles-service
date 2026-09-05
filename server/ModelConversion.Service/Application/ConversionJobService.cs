using ModelConversion.Service.Api;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure;

namespace ModelConversion.Service.Application;

public sealed class ConversionJobService(
    IConversionJobRepository repository,
    ConversionJobQueue queue,
    RunningJobRegistry runningJobs,
    ConversionProgressRegistry progressRegistry,
    ReferenceLlaReader referenceLlaReader,
    ConversionSettingsResolver settingsResolver,
    ConversionOptions options,
    TimeProvider timeProvider)
{
    public async Task<ConversionJob> CreateAsync(CreateConversionRequest request, CancellationToken cancellationToken)
    {
        if (!request.InputPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("当前版本只接受 OBJ 输入", nameof(request.InputPath));
        _ = PathBoundary.ResolveExistingFile(options.InputRoot, request.InputPath);

        if (request.GeoReference != null && !string.IsNullOrWhiteSpace(request.ReferenceLlaPath))
            throw new ArgumentException("GeoReference 与 ReferenceLlaPath 只能提供一个");

        var settings = settingsResolver.Resolve(request.Profile, request.Overrides);
        GeoReference? geoReference = request.GeoReference;
        if (!string.IsNullOrWhiteSpace(request.ReferenceLlaPath))
        {
            var referencePath = PathBoundary.ResolveExistingFile(options.InputRoot, request.ReferenceLlaPath);
            geoReference = await referenceLlaReader.ReadAsync(referencePath, cancellationToken);
        }
        geoReference?.EnsureValid();
        if (!settings.Local && geoReference == null)
            throw new ArgumentException("非本地转换必须提供 reference_lla.json 或显式经纬高");
        if (settings.Local && geoReference != null)
            throw new ArgumentException("本地模式下不应提供 reference_lla.json 或经纬高");

        var id = Guid.NewGuid();
        var outputRelativePath = string.IsNullOrWhiteSpace(request.OutputPath)
            ? Path.Combine("jobs", id.ToString("N"))
            : await ResolveOutputPathAsync(request.OutputPath, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var job = new ConversionJob
        {
            Id = id,
            InputRelativePath = request.InputPath,
            ReferenceLlaRelativePath = request.ReferenceLlaPath,
            OutputRelativePath = outputRelativePath,
            ProfileName = request.Profile,
            SettingsSnapshot = settings,
            GeoReference = geoReference,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.SaveAsync(job, cancellationToken);
        progressRegistry.Report(job.Id,
            new ConversionProgressUpdate(0, ConversionProgressStage.Queued, "等待执行"));
        await queue.EnqueueAsync(job.Id, cancellationToken);
        return job;
    }

    // 调用方只能指定 OutputRoot 内的相对目录；jobs/ 是服务自留命名空间。
    private async Task<string> ResolveOutputPathAsync(string outputPath, CancellationToken cancellationToken)
    {
        var absolute = PathBoundary.ResolveOwnedPath(options.OutputRoot, outputPath.Trim());
        var relative = Path.GetRelativePath(Path.GetFullPath(options.OutputRoot), absolute);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.Equals(firstSegment, "jobs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("outputPath 不能使用服务保留前缀 jobs");
        if (Directory.Exists(absolute) || File.Exists(absolute))
            throw new OutputConflictException($"输出路径已存在: {relative}");
        var jobs = await repository.ListAsync(cancellationToken);
        if (jobs.Any(job => !job.IsTerminal
            && string.Equals(NormalizeRelativePath(job.OutputRelativePath), NormalizeRelativePath(relative), StringComparison.OrdinalIgnoreCase)))
            throw new OutputConflictException($"输出路径已被进行中的任务占用: {relative}");
        return relative;
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    public Task<ConversionJob?> GetAsync(Guid id, CancellationToken cancellationToken) => repository.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<ConversionJob>> ListAsync(CancellationToken cancellationToken) => repository.ListAsync(cancellationToken);

    public async Task<ConversionJob?> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await repository.GetAsync(id, cancellationToken);
        if (job == null || job.IsTerminal) return job;
        if (job.State == ConversionJobState.Queued)
        {
            var canceled = job.TransitionTo(ConversionJobState.Canceled, timeProvider.GetUtcNow(), "排队阶段由调用方取消");
            await repository.SaveAsync(canceled, cancellationToken);
            progressRegistry.MarkTerminal(canceled, ConversionProgressStage.Canceled, "排队阶段由调用方取消");
            return canceled;
        }

        if (!runningJobs.Cancel(id))
            throw new InvalidOperationException("任务正在切换运行状态，请稍后重试取消");
        var current = progressRegistry.Get(job);
        progressRegistry.Report(id,
            new ConversionProgressUpdate(current.Percent, ConversionProgressStage.Canceling, "已请求取消，正在终止转换进程"));
        return job;
    }

    public async Task<ConversionJob> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var previous = await repository.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"任务不存在: {id}");
        if (previous.State is not (ConversionJobState.Failed or ConversionJobState.Canceled))
            throw new InvalidOperationException("只有失败或已取消任务可以重试");

        var now = timeProvider.GetUtcNow();
        var nextId = Guid.NewGuid();
        // [2026-09-04 重试审计] 重试创建新任务，不覆盖旧任务的失败证据和参数快照。
        // 重试复用原输出位置与参数快照，不重新读取当前 profile。
        var retry = previous with
        {
            Id = nextId,
            State = ConversionJobState.Queued,
            Attempt = previous.Attempt + 1,
            PreviousJobId = previous.Id,
            CreatedAt = now,
            UpdatedAt = now,
            StartedAt = null,
            FinishedAt = null,
            ExitCode = null,
            Diagnostic = null,
            Validation = null
        };
        await repository.SaveAsync(retry, cancellationToken);
        progressRegistry.Report(retry.Id,
            new ConversionProgressUpdate(0, ConversionProgressStage.Queued, "重试任务等待执行"));
        await queue.EnqueueAsync(retry.Id, cancellationToken);
        return retry;
    }
}
