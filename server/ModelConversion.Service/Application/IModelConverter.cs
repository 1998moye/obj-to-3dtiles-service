using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

public sealed record ModelConversionContext(
    Guid JobId,
    string InputPath,
    string StagingOutputPath,
    string LogPath,
    ConversionSettings Settings,
    GeoReference? GeoReference,
    Action<ConversionProgressUpdate>? Progress = null);

public sealed record ModelConversionResult(int ExitCode, string? Diagnostic);

public interface IModelConverter
{
    Task<ModelConversionResult> ConvertAsync(ModelConversionContext context, CancellationToken cancellationToken);
}
