namespace ModelConversion.Service.Domain;

public enum ConversionProgressStage
{
    Queued,
    Preparing,
    Converting,
    Validating,
    Publishing,
    Canceling,
    Completed,
    Failed,
    Canceled
}

public sealed record ConversionProgressUpdate(
    int Percent,
    ConversionProgressStage Stage,
    string? Message = null);

public sealed record ConversionProgressSnapshot(
    int Percent,
    ConversionProgressStage Stage,
    string? Message,
    DateTimeOffset UpdatedAt);
