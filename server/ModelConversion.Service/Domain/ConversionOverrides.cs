namespace ModelConversion.Service.Domain;

// 单次转换的白名单参数覆盖，所有字段可空；null 表示沿用 profile 基础值。
public sealed record ConversionOverrides
{
    public int? Lods { get; init; }
    public double? MinGeometryQuality { get; init; }
    public bool? Hierarchical { get; init; }
    public double? HlodErrorDivisor { get; init; }
    public double? HlodTargetRatio { get; init; }
    public int? ExternalTilesetDepth { get; init; }
    public bool? Octree { get; init; }
    public int? Divisions { get; init; }
    public bool? ZSplit { get; init; }
    public ConversionSplitStrategy? SplitStrategy { get; init; }
    public double? LodTextureScale { get; init; }
    public int? MaxTextureSize { get; init; }
    public ConversionTextureFormat? TextureFormat { get; init; }
    public int? TextureQuality { get; init; }
    public int? Ktx2Quality { get; init; }
    public bool? Local { get; init; }
    // [2026-09-07 深层封底剥离] 单次覆盖；null=沿用 profile（默认开）
    public bool? StripDeepBottom { get; init; }
    public double? DeepBottomMinDropMeters { get; init; }
    public double? DeepBottomMarginMeters { get; init; }
}
