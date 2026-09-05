namespace ModelConversion.Service.Cli;

public static class CliExitCodes
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int OutputConflict = 3;
    public const int ConversionFailed = 4;
    public const int ValidationFailed = 5;
    public const int Canceled = 130;
}
