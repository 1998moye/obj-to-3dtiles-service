namespace ModelConversion.Service.Domain;

// 内存档位：标准 ≥8GiB，低内存 4–8GiB（当前真实测试模型的正式支持规格），
// 极低内存 2–4GiB（带资源准入的受限规格），低于下限直接拒绝。
public enum ConversionMemoryMode
{
    Standard,
    LowMemory,
    VeryLowMemory
}

// 一次转换的资源计划：内存水位、GC 堆预算、纹理上限、阶段并发、缓存与临时磁盘预算。
// 所有数值由 ConversionResourcePlanner 集中计算，调用方不自行拼装阈值。
public sealed record ConversionResourcePlan(
    ConversionMemoryMode Mode,
    long MemoryLimitBytes,
    // 水位是否具备强制效力：仅 cgroup v1/v2 容器限制可读时为 true。
    // 进程回退口径读到的是整机内存，无法区分本服务与其他进程，软水位门控与
    // 看门狗在该口径下只报告不强制，否则会因整机占用误阻塞/误杀正常任务。
    bool ContainerLimited,
    long SoftWatermarkBytes,
    long ReduceWatermarkBytes,
    long HardWatermarkBytes,
    long GcHeapBudgetBytes,
    int MaxSourceTextureEdge,
    int MaxStageConcurrency,
    long TextureCacheBudgetBytes,
    long TempDiskBudgetBytes,
    string ModeReason);

// 结构化拒绝原因：资源准入、临时磁盘与硬性输入上限必须可区分。
public enum ConversionResourceRejectionKind
{
    InsufficientMemory,
    InsufficientTemporaryDisk,
    InputExceedsHardLimit
}

public sealed record ConversionResourceRejection(
    ConversionResourceRejectionKind Kind,
    string Reason);

// 一次预检的完整报告：快照、输入摘要、估算与最终计划或拒绝。
// 该报告会写入任务日志与服务控制台，解释任务为什么被执行或拒绝。
public sealed record ConversionResourceReport(
    RuntimeResourceSnapshot Snapshot,
    InputModelSummary Input,
    long EstimatedTemporaryDiskBytes,
    ConversionResourcePlan? Plan,
    ConversionResourceRejection? Rejection)
{
    public bool IsRejected => Rejection != null;

    public string ToDisplayString()
    {
        var snapshot = Snapshot;
        var input = Input;
        var lines = new List<string>
        {
            $"内存上限={FormatBytes(snapshot.MemoryLimitBytes)}({FormatSource(snapshot.MemorySource)}) " +
            $"当前用量={FormatNullableBytes(snapshot.MemoryCurrentBytes)} CPU={snapshot.CpuCores:0.##} " +
            $"临时磁盘可用={FormatBytes(snapshot.TempDiskFreeBytes)}({snapshot.TempPath})",
            $"输入: 顶点={input.VertexCount} 三角形={input.FaceCount} 纹理={input.Textures.Count}张 " +
            $"总输入={FormatBytes(input.TotalInputBytes)} 解码展开≈{FormatBytes(input.TotalDecodedTextureBytes)} " +
            $"单图最大展开≈{FormatBytes(input.MaxTextureDecodedBytes)} 最大边长={input.MaxTextureEdge}px"
        };
        if (input.HasUnreadableTextures)
            lines.Add("警告: 存在无法读取头部的纹理，按原样直通处理");
        if (Plan is { } plan)
        {
            lines.Add(
                $"模式={plan.Mode}（{plan.ModeReason}） 水位强制={(plan.ContainerLimited ? "启用(容器)" : "仅报告(非容器)")} " +
                $"预算: 软水位={FormatBytes(plan.SoftWatermarkBytes)} " +
                $"降级={FormatBytes(plan.ReduceWatermarkBytes)} 硬水位={FormatBytes(plan.HardWatermarkBytes)} " +
                $"GC堆={FormatBytes(plan.GcHeapBudgetBytes)} 源纹理边长上限={plan.MaxSourceTextureEdge}px " +
                $"阶段并发={plan.MaxStageConcurrency} 纹理缓存={FormatBytes(plan.TextureCacheBudgetBytes)} " +
                $"预计临时磁盘={FormatBytes(EstimatedTemporaryDiskBytes)}");
        }
        if (Rejection is { } rejection)
            lines.Add($"拒绝: [{rejection.Kind}] {rejection.Reason}");
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatBytes(long bytes)
    {
        const long gib = 1L << 30;
        const long mib = 1L << 20;
        return bytes >= gib ? $"{bytes / (double)gib:0.##}GiB"
            : bytes >= mib ? $"{bytes / (double)mib:0.##}MiB"
            : $"{bytes}B";
    }

    private static string FormatNullableBytes(long? bytes) => bytes.HasValue ? FormatBytes(bytes.Value) : "未知";

    private static string FormatSource(MemoryLimitSource source) => source switch
    {
        MemoryLimitSource.CgroupV2 => "cgroup-v2",
        MemoryLimitSource.CgroupV1 => "cgroup-v1",
        _ => "进程可见内存"
    };
}
