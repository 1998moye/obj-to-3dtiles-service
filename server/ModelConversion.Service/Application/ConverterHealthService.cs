using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure.Resources;

namespace ModelConversion.Service.Application;

/// <summary>/api/v1/health 的聚合结果（字段与枚举由 PRD §12 固定）。</summary>
public sealed record ConverterHealthSnapshot(
    string Status,
    int QueueDepth,
    int RunningJobs,
    int MaxConcurrentJobs,
    bool InputReady,
    bool OutputReady,
    bool StateReady,
    long StorageFreeBytes,
    long StorageReserveBytes,
    string ResourcePressure,
    DateTimeOffset UpdatedAt);

// [2026-09-09 Issue01 健康容量] 平台调度唯一容量事实来源：作业计数直接来自现有作业仓储
// （不建立第二套计数），资源口径与 Worker 软水位门控共用同一 RuntimeResourceProbe；
// 响应只含相对事实，不返回宿主机绝对路径。
public sealed class ConverterHealthService(
    IConversionJobRepository repository,
    ConversionOptions options,
    IRuntimeResourceProbe resourceProbe,
    TimeProvider timeProvider)
{
    public async Task<ConverterHealthSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var jobs = await repository.ListAsync(cancellationToken);
        var queueDepth = jobs.Count(job => job.State == ConversionJobState.Queued);
        var runningJobs = jobs.Count(job => job.State is ConversionJobState.Running or ConversionJobState.Validating);

        var inputReady = Directory.Exists(options.InputRoot);
        var outputReady = Directory.Exists(options.OutputRoot);
        var stateReady = Directory.Exists(options.StateRoot);
        var executableReady = File.Exists(options.Obj2TilesExecutable);

        // 与 Worker 软水位门控同一快照口径：状态盘（临时工作区所在卷）可用空间。
        var snapshot = resourceProbe.Snapshot(options.StateRoot);
        var pressure = ResolvePressure(snapshot);
        var storageLow = snapshot.TempDiskFreeBytes < options.StorageReserveBytes;

        var status = !executableReady || !inputReady || !outputReady || !stateReady
            ? "not_ready"
            : pressure != "normal" || storageLow
                ? "degraded"
                : "ready";

        return new ConverterHealthSnapshot(
            status,
            queueDepth,
            runningJobs,
            options.MaxConcurrentJobs,
            inputReady,
            outputReady,
            stateReady,
            snapshot.TempDiskFreeBytes,
            options.StorageReserveBytes,
            pressure,
            timeProvider.GetUtcNow());
    }

    private string ResolvePressure(RuntimeResourceSnapshot snapshot)
    {
        if (snapshot.MemoryCurrentBytes is not { } current || snapshot.MemoryLimitBytes <= 0)
            return "normal";
        // 与 ResourcePolicyOptions 水位语义对齐：达到软水位新任务已被门控（throttled），
        // 达到硬水位看门狗会主动终止转换进程（blocked）。
        if (current >= snapshot.MemoryLimitBytes * options.Resources.HardWatermarkRatio)
            return "blocked";
        if (current >= snapshot.MemoryLimitBytes * options.Resources.SoftWatermarkRatio)
            return "throttled";
        return "normal";
    }
}
