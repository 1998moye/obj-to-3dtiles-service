using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

// 与传输层无关的一次转换请求；路径边界由 CLI/API 适配层各自解析后传入绝对路径。
public sealed record ConversionRunRequest(
    Guid RunId,
    string InputPath,
    string FinalOutputPath,
    string StagingPath,
    string LogPath,
    ConversionSettings Settings,
    GeoReference? GeoReference,
    bool RevalidateExistingOutput,
    Action<ConversionProgressUpdate>? Progress = null);

public enum ConversionRunOutcome
{
    Succeeded,
    OutputConflict,
    ConversionFailed,
    ValidationFailed,
    TimedOut,
    Canceled,
    // 资源准入拒绝（内存档位不足/极低内存准入失败/硬性输入上限）。
    ResourceRejected,
    // 临时磁盘预算不足。
    InsufficientDisk,
    // [2026-09-07 疑似 OOM] 子进程退出码 137（SIGKILL）：疑似被容器 OOM Killer 终止，
    // 与主动内存保护（看门狗在硬水位前拦截）和普通转换失败相区分。
    SuspectedOomKilled
}

public sealed record ConversionRunResult(
    ConversionRunOutcome Outcome,
    int? ExitCode = null,
    string? Diagnostic = null,
    TilesetValidationReport? Validation = null,
    bool ReusedExistingOutput = false,
    TimeSpan Duration = default);

public interface IConversionRunner
{
    Task<ConversionRunResult> RunAsync(ConversionRunRequest request, CancellationToken cancellationToken);
}
