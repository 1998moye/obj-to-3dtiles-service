using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

// 资源规划深模块：Interface 只接收转换请求、输入摘要和运行时资源快照，
// 返回可执行的资源计划或结构化拒绝结果；档位计算、水位、纹理上限、并发与
// 临时磁盘估算全部隐藏在本模块内，CLI 与 HTTP Worker 只消费结果。
public sealed class ConversionResourcePlanner(ConversionOptions options)
{
    // 几何工作集保守估算：MeshT 每顶点约 96B（坐标+字典开销）、每 UV 约 48B、每面约 64B；
    // HLOD 按层切分期间父子两代网格会同时驻留，再乘 2。
    private const long VertexWeightBytes = 96;
    private const long TextureVertexWeightBytes = 48;
    private const long FaceWeightBytes = 64;
    private const int HierarchyOverlapFactor = 2;

    public ConversionResourceReport Plan(
        ConversionRunRequest request,
        InputModelSummary input,
        RuntimeResourceSnapshot snapshot)
    {
        var policy = options.Resources;
        var limit = snapshot.MemoryLimitBytes;

        // 临时磁盘估算：规范化副本 + HLOD 中间 OBJ + 未发布输出的保守上界。
        var estimatedTempBytes = (long)(input.TotalInputBytes * policy.TemporaryDiskEstimateMultiplier);
        var tempDiskBudget = (long)(snapshot.TempDiskFreeBytes * policy.TemporaryDiskBudgetRatio);
        if (estimatedTempBytes > tempDiskBudget)
        {
            return Reject(input, snapshot, estimatedTempBytes, new ConversionResourceRejection(
                ConversionResourceRejectionKind.InsufficientTemporaryDisk,
                $"预计临时磁盘需求 {ConversionResourceReport.FormatBytes(estimatedTempBytes)} 超过可用预算 " +
                $"{ConversionResourceReport.FormatBytes(tempDiskBudget)}（{snapshot.TempPath} 可用 " +
                $"{ConversionResourceReport.FormatBytes(snapshot.TempDiskFreeBytes)} 的 {policy.TemporaryDiskBudgetRatio:P0}）"));
        }

        // 硬性输入上限：完整解码总量超过上限时任何档位都不受理（即使规范化也保不住语义）。
        if (input.TotalDecodedTextureBytes > policy.HardMaxTotalDecodedTextureBytes)
        {
            return Reject(input, snapshot, estimatedTempBytes, new ConversionResourceRejection(
                ConversionResourceRejectionKind.InputExceedsHardLimit,
                $"纹理完整解码总量约 {ConversionResourceReport.FormatBytes(input.TotalDecodedTextureBytes)} " +
                $"超过硬性上限 {ConversionResourceReport.FormatBytes(policy.HardMaxTotalDecodedTextureBytes)}"));
        }

        // 内存档位：标准 ≥8GiB，低内存 4–8GiB（正式支持），极低内存 2–4GiB（受限准入）。
        ConversionMemoryMode mode;
        string modeReason;
        if (limit >= policy.StandardModeMinMemoryBytes)
        {
            mode = ConversionMemoryMode.Standard;
            modeReason = "标准模式";
        }
        else if (limit >= policy.LowMemoryModeMinMemoryBytes)
        {
            mode = ConversionMemoryMode.LowMemory;
            modeReason = "正式支持档位";
        }
        else if (limit >= policy.VeryLowMemoryModeMinMemoryBytes)
        {
            mode = ConversionMemoryMode.VeryLowMemory;
            modeReason = "受限档位（带资源准入）";
        }
        else
        {
            return Reject(input, snapshot, estimatedTempBytes, new ConversionResourceRejection(
                ConversionResourceRejectionKind.InsufficientMemory,
                $"容器内存上限 {ConversionResourceReport.FormatBytes(limit)} 低于极低内存模式下限 " +
                $"{ConversionResourceReport.FormatBytes(policy.VeryLowMemoryModeMinMemoryBytes)}"));
        }

        var softWatermark = (long)(limit * policy.SoftWatermarkRatio);
        var reduceWatermark = (long)(limit * policy.ReduceWatermarkRatio);
        var hardWatermark = (long)(limit * policy.HardWatermarkRatio);

        // 源纹理边长上限：档位默认值再受输出图集规格约束（保留 2 倍输出尺寸的重采样余量）。
        var modeTextureEdge = mode switch
        {
            ConversionMemoryMode.Standard => policy.StandardModeMaxSourceTextureEdge,
            ConversionMemoryMode.LowMemory => policy.LowMemoryModeMaxSourceTextureEdge,
            _ => policy.VeryLowMemoryModeMaxSourceTextureEdge
        };
        var maxSourceTextureEdge = Math.Min(modeTextureEdge, Math.Max(request.Settings.MaxTextureSize * 2, 2048));

        var stageConcurrency = mode switch
        {
            ConversionMemoryMode.Standard => policy.StandardModeMaxStageConcurrency,
            ConversionMemoryMode.LowMemory => policy.LowMemoryModeMaxStageConcurrency,
            _ => policy.VeryLowMemoryModeMaxStageConcurrency
        };

        // 极低内存模式准入：规范化后的纹理驻留 + 几何工作集必须落在硬水位内，
        // 否则执行前安全拒绝，而不是进入必然 OOM 的执行过程。
        if (mode == ConversionMemoryMode.VeryLowMemory)
        {
            var normalizedTextureBytes = input.Textures.Sum(texture =>
            {
                if (!texture.HeaderReadable || texture.MaxEdge <= maxSourceTextureEdge)
                    return texture.EstimatedDecodedBytes;
                var scale = maxSourceTextureEdge / (double)texture.MaxEdge;
                return (long)(texture.EstimatedDecodedBytes * scale * scale);
            });
            var geometryBytes = ((long)input.VertexCount * VertexWeightBytes
                + (long)input.TextureVertexCount * TextureVertexWeightBytes
                + (long)input.FaceCount * FaceWeightBytes) * HierarchyOverlapFactor;
            var estimatedPeak = normalizedTextureBytes + geometryBytes;
            if (estimatedPeak > hardWatermark)
            {
                return Reject(input, snapshot, estimatedTempBytes, new ConversionResourceRejection(
                    ConversionResourceRejectionKind.InsufficientMemory,
                    $"极低内存模式准入失败：规范化后预计峰值 {ConversionResourceReport.FormatBytes(estimatedPeak)}" +
                    $"（纹理≈{ConversionResourceReport.FormatBytes(normalizedTextureBytes)} + " +
                    $"几何≈{ConversionResourceReport.FormatBytes(geometryBytes)}）超过硬水位 " +
                    $"{ConversionResourceReport.FormatBytes(hardWatermark)}"));
            }
        }

        var plan = new ConversionResourcePlan(
            mode,
            limit,
            snapshot.MemorySource != MemoryLimitSource.ProcessFallback,
            softWatermark,
            reduceWatermark,
            hardWatermark,
            (long)(limit * policy.GcHeapRatio),
            maxSourceTextureEdge,
            stageConcurrency,
            (long)(limit * policy.TextureCacheBudgetRatio),
            tempDiskBudget,
            modeReason);
        return new ConversionResourceReport(snapshot, input, estimatedTempBytes, plan, null);
    }

    private static ConversionResourceReport Reject(
        InputModelSummary input,
        RuntimeResourceSnapshot snapshot,
        long estimatedTempBytes,
        ConversionResourceRejection rejection) =>
        new(snapshot, input, estimatedTempBytes, null, rejection);
}
