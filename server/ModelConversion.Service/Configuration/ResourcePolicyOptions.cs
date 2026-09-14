namespace ModelConversion.Service.Configuration;

// 资源策略集中配置：档位、水位、纹理上限、并发与临时磁盘估算全部从这里出，
// 调用方与转换算法不各自拼装阈值。可用 Conversion__Resources__* 环境变量覆盖。
public sealed class ResourcePolicyOptions
{
    // 软水位：达到后停止启动新的重型转换工作。
    public double SoftWatermarkRatio { get; set; } = 0.65;
    // 降级水位：达到后进一步压低并发（单并发部署下与软水位行为一致）。
    public double ReduceWatermarkRatio { get; set; } = 0.75;
    // 硬水位：达到后主动终止运行中的转换进程，避免被 OOM Killer 无提示杀死。
    public double HardWatermarkRatio { get; set; } = 0.85;

    public long StandardModeMinMemoryBytes { get; set; } = 8L << 30;
    public long LowMemoryModeMinMemoryBytes { get; set; } = 4L << 30;
    public long VeryLowMemoryModeMinMemoryBytes { get; set; } = 2L << 30;

    // 源纹理边长上限（规范化副本，不是输出图集）：标准与低内存 4096，极低内存 2048。
    public int StandardModeMaxSourceTextureEdge { get; set; } = 4096;
    public int LowMemoryModeMaxSourceTextureEdge { get; set; } = 4096;
    public int VeryLowMemoryModeMaxSourceTextureEdge { get; set; } = 2048;

    // .NET GC 堆预算占容器内存比例；为 native 图像库、文件缓存和进程本身保留空间。
    // 只是保护措施，不替代超大纹理的流式规范化。
    public double GcHeapRatio { get; set; } = 0.5;

    // 任务级纹理缓存预算占容器内存比例（按解码后字节计）。
    public double TextureCacheBudgetRatio { get; set; } = 0.2;

    // 各档位允许的重型阶段并发；默认 1，高内存档位最多 2。
    public int StandardModeMaxStageConcurrency { get; set; } = 2;
    public int LowMemoryModeMaxStageConcurrency { get; set; } = 1;
    public int VeryLowMemoryModeMaxStageConcurrency { get; set; } = 1;

    // 临时磁盘估算倍率：规范化副本 + HLOD 中间 OBJ + 未发布输出的保守上界。
    public double TemporaryDiskEstimateMultiplier { get; set; } = 3.0;
    // 临时磁盘预算占可用空间的上限，避免单个任务耗尽磁盘。
    public double TemporaryDiskBudgetRatio { get; set; } = 0.5;

    // 硬性输入上限：纹理完整解码总量超过该值时任何档位都不受理。
    public long HardMaxTotalDecodedTextureBytes { get; set; } = 48L << 30;

    // 运行中内存看门狗的采样间隔。
    public int MemoryWatchdogIntervalSeconds { get; set; } = 5;

    // 软水位门控触发后重新排队前的等待秒数。
    public int SoftWatermarkRetrySeconds { get; set; } = 30;

    public void EnsureValid()
    {
        if (SoftWatermarkRatio is <= 0 or >= 1) throw new InvalidOperationException("SoftWatermarkRatio 必须在 (0,1) 内");
        if (ReduceWatermarkRatio is <= 0 or >= 1) throw new InvalidOperationException("ReduceWatermarkRatio 必须在 (0,1) 内");
        if (HardWatermarkRatio is <= 0 or >= 1) throw new InvalidOperationException("HardWatermarkRatio 必须在 (0,1) 内");
        if (!(SoftWatermarkRatio < ReduceWatermarkRatio && ReduceWatermarkRatio < HardWatermarkRatio))
            throw new InvalidOperationException("内存水位必须满足 软 < 降级 < 硬");
        if (StandardModeMinMemoryBytes < LowMemoryModeMinMemoryBytes
            || LowMemoryModeMinMemoryBytes < VeryLowMemoryModeMinMemoryBytes
            || VeryLowMemoryModeMinMemoryBytes < 512L << 20)
            throw new InvalidOperationException("档位内存下限必须满足 标准 ≥ 低内存 ≥ 极低内存 ≥ 512MiB");
        if (StandardModeMaxSourceTextureEdge is < 128 || LowMemoryModeMaxSourceTextureEdge is < 128
            || VeryLowMemoryModeMaxSourceTextureEdge is < 128)
            throw new InvalidOperationException("源纹理边长上限不能小于 128");
        if (GcHeapRatio is <= 0 or >= 1) throw new InvalidOperationException("GcHeapRatio 必须在 (0,1) 内");
        if (TextureCacheBudgetRatio is <= 0 or >= 1) throw new InvalidOperationException("TextureCacheBudgetRatio 必须在 (0,1) 内");
        if (StandardModeMaxStageConcurrency is < 1 or > 8
            || LowMemoryModeMaxStageConcurrency is < 1 or > 8
            || VeryLowMemoryModeMaxStageConcurrency is < 1 or > 8)
            throw new InvalidOperationException("阶段并发必须在 1 到 8 之间");
        if (TemporaryDiskEstimateMultiplier is < 1 or > 20)
            throw new InvalidOperationException("TemporaryDiskEstimateMultiplier 必须在 1 到 20 之间");
        if (TemporaryDiskBudgetRatio is <= 0 or >= 1) throw new InvalidOperationException("TemporaryDiskBudgetRatio 必须在 (0,1) 内");
        if (HardMaxTotalDecodedTextureBytes < 1L << 30)
            throw new InvalidOperationException("HardMaxTotalDecodedTextureBytes 不能小于 1GiB");
        if (MemoryWatchdogIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("MemoryWatchdogIntervalSeconds 必须在 1 到 300 秒之间");
        if (SoftWatermarkRetrySeconds is < 1 or > 600)
            throw new InvalidOperationException("SoftWatermarkRetrySeconds 必须在 1 到 600 秒之间");
    }
}
