namespace ModelConversion.Service.Domain;

public enum ConversionTextureFormat
{
    Jpeg,
    Webp,
    Ktx2
}

public enum ConversionSplitStrategy
{
    AbsoluteCenter,
    VertexBaricenter,
    VertexMedian
}

// 转换配置：profile 与单次参数覆盖合并后的不可变参数快照，作业持久化、恢复和重试均以它为准。
public sealed record ConversionSettings
{
    public required int Lods { get; init; }
    public required double MinGeometryQuality { get; init; }
    public required bool Hierarchical { get; init; }
    public required double HlodErrorDivisor { get; init; }
    public required double HlodTargetRatio { get; init; }
    public required int ExternalTilesetDepth { get; init; }
    public required bool Octree { get; init; }
    public required int Divisions { get; init; }
    public required bool ZSplit { get; init; }
    public required ConversionSplitStrategy SplitStrategy { get; init; }
    public required double LodTextureScale { get; init; }
    public required int MaxTextureSize { get; init; }
    public required ConversionTextureFormat TextureFormat { get; init; }
    public required int TextureQuality { get; init; }
    public required int Ktx2Quality { get; init; }
    public required bool Local { get; init; }

    // [2026-09-07 深层封底剥离] 摄影测量封洞/封底几何（可深入地下数百米）会渲染成灰色块
    // 并带偏高度回正；默认开启剥离。三个字段不用 required：作业快照（FileConversionJobRepository）
    // 反序列化升级前的旧 JSON 时缺字段会抛异常，给默认值保证重启恢复不中断。
    public bool StripDeepBottom { get; init; } = true;
    /// 判定存在深层封底的最小落差（地表带底到模型最低点的距离），小于该值视为无封底直接直通
    public double DeepBottomMinDropMeters { get; init; } = 100;
    /// 剥离阈值 = 地表带底 - 该余量；默认保留一个 2m 直方图桶，兼顾数值容差且不残留浅层裙边
    public double DeepBottomMarginMeters { get; init; } = 2;

    public void EnsureValid()
    {
        if (Lods is < 1 or > 10) throw new InvalidOperationException("Lods 必须在 1 到 10 之间");
        if (MinGeometryQuality is <= 0 or > 1) throw new InvalidOperationException("MinGeometryQuality 必须在 (0,1] 内");
        if (HlodErrorDivisor <= 0) throw new InvalidOperationException("HlodErrorDivisor 必须大于 0");
        if (HlodTargetRatio is <= 0 or > 1) throw new InvalidOperationException("HlodTargetRatio 必须在 (0,1] 内");
        if (ExternalTilesetDepth is < 0 or > 8) throw new InvalidOperationException("ExternalTilesetDepth 必须在 0 到 8 之间");
        if (Divisions is < 0 or > 8) throw new InvalidOperationException("Divisions 必须在 0 到 8 之间");
        if (LodTextureScale is <= 0 or > 1) throw new InvalidOperationException("LodTextureScale 必须在 (0,1] 内");
        // [2026-09-07 高清档位] 上限 8192→16384：大图集单张 16384²×RGBA=1GiB 瞬态分配，
        // 由图集保存有界队列与资源水位兜底；超过 16384 收益递减且内存风险不可控。
        if (MaxTextureSize is < 128 or > 16384) throw new InvalidOperationException("MaxTextureSize 必须在 128 到 16384 之间");
        if (TextureQuality is < 1 or > 100) throw new InvalidOperationException("TextureQuality 必须在 1 到 100 之间");
        if (Ktx2Quality is < 1 or > 255) throw new InvalidOperationException("Ktx2Quality 必须在 1 到 255 之间");
        if (DeepBottomMinDropMeters is < 10 or > 10000) throw new InvalidOperationException("DeepBottomMinDropMeters 必须在 10 到 10000 之间");
        if (DeepBottomMarginMeters is < 0 or > 1000) throw new InvalidOperationException("DeepBottomMarginMeters 必须在 0 到 1000 之间");
    }
}
