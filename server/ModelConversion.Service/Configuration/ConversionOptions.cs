namespace ModelConversion.Service.Configuration;

public sealed class ConversionOptions
{
    public string InputRoot { get; set; } = "/data/input";
    public string OutputRoot { get; set; } = "/data/output";
    public string StateRoot { get; set; } = "/data/state";
    public string Obj2TilesExecutable { get; set; } = "/app/obj2tiles/Obj2Tiles";
    public int MaxConcurrentJobs { get; set; } = 1;
    public int JobTimeoutMinutes { get; set; } = 720;
    public long MaxTilesetJsonBytes { get; set; } = 16 * 1024 * 1024;
    public Dictionary<string, ConversionProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void EnsureValid()
    {
        if (MaxConcurrentJobs is < 1 or > 8)
            throw new InvalidOperationException("MaxConcurrentJobs 必须在 1 到 8 之间");
        if (JobTimeoutMinutes is < 1 or > 7 * 24 * 60)
            throw new InvalidOperationException("JobTimeoutMinutes 必须在 1 分钟到 7 天之间");
        if (Profiles.Count == 0)
            throw new InvalidOperationException("至少需要配置一个转换配置档");
        foreach (var (name, profile) in Profiles)
            profile.EnsureValid(name);
    }

    public ConversionProfile GetProfile(string name)
    {
        var match = Profiles.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        return match.Value ?? throw new KeyNotFoundException($"未知转换配置档: {name}");
    }
}

public sealed class ConversionProfile
{
    public int Lods { get; set; } = 5;
    public double MinGeometryQuality { get; set; } = 0.5;
    public bool Hierarchical { get; set; } = true;
    public double HlodErrorDivisor { get; set; } = 200;
    public double HlodTargetRatio { get; set; } = 0.10;
    public int ExternalTilesetDepth { get; set; } = 2;
    public bool Octree { get; set; } = true;
    public int Divisions { get; set; }
    public bool ZSplit { get; set; }
    public string SplitStrategy { get; set; } = "VertexMedian";
    public double LodTextureScale { get; set; } = 0.5;
    public int MaxTextureSize { get; set; } = 2048;
    public string TextureFormat { get; set; } = "Jpeg";
    public int TextureQuality { get; set; } = 85;
    public int Ktx2Quality { get; set; } = 192;
    public bool Local { get; set; }

    public void EnsureValid(string name)
    {
        if (Lods is < 1 or > 10) throw new InvalidOperationException($"配置档 {name} 的 Lods 必须在 1 到 10 之间");
        if (MinGeometryQuality is <= 0 or > 1) throw new InvalidOperationException($"配置档 {name} 的 MinGeometryQuality 必须在 (0,1] 内");
        if (HlodErrorDivisor <= 0) throw new InvalidOperationException($"配置档 {name} 的 HlodErrorDivisor 必须大于 0");
        if (HlodTargetRatio is <= 0 or > 1) throw new InvalidOperationException($"配置档 {name} 的 HlodTargetRatio 必须在 (0,1] 内");
        if (ExternalTilesetDepth is < 0 or > 8) throw new InvalidOperationException($"配置档 {name} 的 ExternalTilesetDepth 必须在 0 到 8 之间");
        if (Divisions is < 0 or > 8) throw new InvalidOperationException($"配置档 {name} 的 Divisions 必须在 0 到 8 之间");
        if (LodTextureScale is <= 0 or > 1) throw new InvalidOperationException($"配置档 {name} 的 LodTextureScale 必须在 (0,1] 内");
        if (MaxTextureSize is < 128 or > 8192) throw new InvalidOperationException($"配置档 {name} 的 MaxTextureSize 必须在 128 到 8192 之间");
        if (TextureQuality is < 1 or > 100) throw new InvalidOperationException($"配置档 {name} 的 TextureQuality 必须在 1 到 100 之间");
        if (Ktx2Quality is < 1 or > 255) throw new InvalidOperationException($"配置档 {name} 的 Ktx2Quality 必须在 1 到 255 之间");
        if (SplitStrategy is not ("AbsoluteCenter" or "VertexBaricenter" or "VertexMedian"))
            throw new InvalidOperationException($"配置档 {name} 的 SplitStrategy 不受支持");
        if (TextureFormat is not ("Jpeg" or "Webp" or "Ktx2"))
            throw new InvalidOperationException($"配置档 {name} 的 TextureFormat 不受支持");
    }
}
