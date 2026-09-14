namespace ModelConversion.Service.Cli;

public static class CliExitCodes
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int OutputConflict = 3;
    public const int ConversionFailed = 4;
    public const int ValidationFailed = 5;
    // [2026-09-07 资源结果] 6=资源准入拒绝（含主动内存保护），7=临时磁盘预算不足。
    public const int ResourceRejected = 6;
    public const int InsufficientDisk = 7;
    // [2026-09-07 疑似 OOM] 8=子进程被 SIGKILL（疑似容器 OOMKilled）。
    public const int SuspectedOomKilled = 8;
    public const int Canceled = 130;
}
