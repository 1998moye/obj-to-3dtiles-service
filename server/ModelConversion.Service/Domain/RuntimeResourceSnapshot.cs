namespace ModelConversion.Service.Domain;

// 内存上限来源：容器限制必须优先于进程可见物理内存，否则 Docker 限制无法约束转换策略。
public enum MemoryLimitSource
{
    CgroupV2,
    CgroupV1,
    ProcessFallback
}

// 一次任务开始时固化的运行时资源快照；转换中途不再随配置或环境变化。
public sealed record RuntimeResourceSnapshot(
    long MemoryLimitBytes,
    long? MemoryCurrentBytes,
    MemoryLimitSource MemorySource,
    double CpuCores,
    long TempDiskFreeBytes,
    string TempPath,
    DateTimeOffset CapturedAt);
