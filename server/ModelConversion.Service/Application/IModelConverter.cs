using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

public sealed record ModelConversionContext(
    Guid JobId,
    string InputPath,
    string StagingOutputPath,
    string LogPath,
    ConversionSettings Settings,
    GeoReference? GeoReference,
    Action<ConversionProgressUpdate>? Progress = null,
    // 资源计划：由 ConversionRunner 预检生成，驱动 GC 堆预算与内存看门狗。
    ConversionResourcePlan? ResourcePlan = null);

// [2026-09-07 疑似 OOM 诊断] SuspectedOom 标记退出码 137（SIGKILL）场景；
// SampledPeakMemoryBytes/LastStage 由转换器在运行期采样/解析，供诊断与回归报告使用。
public sealed record ModelConversionResult(int ExitCode, string? Diagnostic)
{
    public bool SuspectedOom { get; init; }
    public long? SampledPeakMemoryBytes { get; init; }
    public ConversionProgressStage? LastStage { get; init; }
}

public interface IModelConverter
{
    Task<ModelConversionResult> ConvertAsync(ModelConversionContext context, CancellationToken cancellationToken);
}
