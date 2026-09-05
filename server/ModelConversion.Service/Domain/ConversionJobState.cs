namespace ModelConversion.Service.Domain;

public enum ConversionJobState
{
    Queued,
    Running,
    Validating,
    Succeeded,
    Failed,
    Canceled
}
