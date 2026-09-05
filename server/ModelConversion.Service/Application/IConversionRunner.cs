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
    Canceled
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
