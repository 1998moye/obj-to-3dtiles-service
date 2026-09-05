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

    public void EnsureValid()
    {
        if (Lods is < 1 or > 10) throw new InvalidOperationException("Lods 必须在 1 到 10 之间");
        if (MinGeometryQuality is <= 0 or > 1) throw new InvalidOperationException("MinGeometryQuality 必须在 (0,1] 内");
        if (HlodErrorDivisor <= 0) throw new InvalidOperationException("HlodErrorDivisor 必须大于 0");
        if (HlodTargetRatio is <= 0 or > 1) throw new InvalidOperationException("HlodTargetRatio 必须在 (0,1] 内");
        if (ExternalTilesetDepth is < 0 or > 8) throw new InvalidOperationException("ExternalTilesetDepth 必须在 0 到 8 之间");
        if (Divisions is < 0 or > 8) throw new InvalidOperationException("Divisions 必须在 0 到 8 之间");
        if (LodTextureScale is <= 0 or > 1) throw new InvalidOperationException("LodTextureScale 必须在 (0,1] 内");
        if (MaxTextureSize is < 128 or > 8192) throw new InvalidOperationException("MaxTextureSize 必须在 128 到 8192 之间");
        if (TextureQuality is < 1 or > 100) throw new InvalidOperationException("TextureQuality 必须在 1 到 100 之间");
        if (Ktx2Quality is < 1 or > 255) throw new InvalidOperationException("Ktx2Quality 必须在 1 到 255 之间");
    }
}
