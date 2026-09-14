namespace ModelConversion.Service.Configuration;

public sealed class ConversionOptions
{
    public string InputRoot { get; set; } = "/data/obj2tiles/input";
    public string OutputRoot { get; set; } = "/data/obj2tiles/output";
    public string StateRoot { get; set; } = "/data/obj2tiles/state";
    public string Obj2TilesExecutable { get; set; } = "/app/obj2tiles/Obj2Tiles";
    public int MaxConcurrentJobs { get; set; } = 1;
    public int JobTimeoutMinutes { get; set; } = 720;
    public long MaxTilesetJsonBytes { get; set; } = 16 * 1024 * 1024;
    // [2026-09-09 Issue01 终态清理] 终态作业（成功/失败/取消）的输入、输出与状态保留时长；
    // 0 表示立即清理（仅测试/演示用）。可用 Conversion__TerminalRetentionHours 覆盖。
    public double TerminalRetentionHours { get; set; } = 24;
    // [2026-09-09 Issue01 终态清理] 清理工作器扫描周期（秒）。
    public int CleanupIntervalSeconds { get; set; } = 300;
    // [2026-09-09 Issue01 健康容量] /api/v1/health 的 storage.reserveBytes 与降级阈值：
    // 状态盘可用空间低于该值时健康状态报 degraded（预警口径，不是硬门控）。
    public long StorageReserveBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public ResourcePolicyOptions Resources { get; set; } = new();
    public Dictionary<string, ConversionProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void EnsureValid()
    {
        if (MaxConcurrentJobs is < 1 or > 8)
            throw new InvalidOperationException("MaxConcurrentJobs 必须在 1 到 8 之间");
        if (JobTimeoutMinutes is < 1 or > 7 * 24 * 60)
            throw new InvalidOperationException("JobTimeoutMinutes 必须在 1 分钟到 7 天之间");
        if (TerminalRetentionHours is < 0 or > 24 * 366)
            throw new InvalidOperationException("TerminalRetentionHours 必须在 0 到 8784 之间");
        if (CleanupIntervalSeconds is < 1 or > 86400)
            throw new InvalidOperationException("CleanupIntervalSeconds 必须在 1 到 86400 秒之间");
        if (StorageReserveBytes is < 0 or > 1L * 1024 * 1024 * 1024 * 1024)
            throw new InvalidOperationException("StorageReserveBytes 必须在 0 到 1TiB 之间");
        Resources.EnsureValid();
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
    // [2026-09-07 默认高清] 原代码: 2048。原因: 2048 图集密度不足以支撑近距离查看（用户实测 8192 才清晰），
    // 单张 8192² RGBA 瞬态 256MiB，由图集保存有界队列与资源水位兜底
    public int MaxTextureSize { get; set; } = 8192;
    public string TextureFormat { get; set; } = "Jpeg";
    public int TextureQuality { get; set; } = 85;
    public int Ktx2Quality { get; set; } = 192;
    public bool Local { get; set; }
    // [2026-09-07 深层封底剥离] 默认开启：剥离远低于地表的封洞/封底几何（灰色块来源），
    // 同时修复高度回正被深层几何带偏；StripDeepBottom=false 可整体关闭
    public bool StripDeepBottom { get; set; } = true;
    public double DeepBottomMinDropMeters { get; set; } = 100;
    // [2026-09-07 修复浅层裙边] 原默认: 15m。原因: 固定保留过深，会显示为模型边缘黑色幕墙。
    public double DeepBottomMarginMeters { get; set; } = 2;

    public void EnsureValid(string name)
    {
        if (Lods is < 1 or > 10) throw new InvalidOperationException($"配置档 {name} 的 Lods 必须在 1 到 10 之间");
        if (MinGeometryQuality is <= 0 or > 1) throw new InvalidOperationException($"配置档 {name} 的 MinGeometryQuality 必须在 (0,1] 内");
        if (HlodErrorDivisor <= 0) throw new InvalidOperationException($"配置档 {name} 的 HlodErrorDivisor 必须大于 0");
        if (HlodTargetRatio is <= 0 or > 1) throw new InvalidOperationException($"配置档 {name} 的 HlodTargetRatio 必须在 (0,1] 内");
        if (ExternalTilesetDepth is < 0 or > 8) throw new InvalidOperationException($"配置档 {name} 的 ExternalTilesetDepth 必须在 0 到 8 之间");
        if (Divisions is < 0 or > 8) throw new InvalidOperationException($"配置档 {name} 的 Divisions 必须在 0 到 8 之间");
        if (LodTextureScale is <= 0 or > 1) throw new InvalidOperationException($"配置档 {name} 的 LodTextureScale 必须在 (0,1] 内");
        // [2026-09-07 高清档位] 与 ConversionSettings 同步放开到 16384（单图集瞬态 1GiB，水位兜底）。
        if (MaxTextureSize is < 128 or > 16384) throw new InvalidOperationException($"配置档 {name} 的 MaxTextureSize 必须在 128 到 16384 之间");
        if (TextureQuality is < 1 or > 100) throw new InvalidOperationException($"配置档 {name} 的 TextureQuality 必须在 1 到 100 之间");
        if (Ktx2Quality is < 1 or > 255) throw new InvalidOperationException($"配置档 {name} 的 Ktx2Quality 必须在 1 到 255 之间");
        if (SplitStrategy is not ("AbsoluteCenter" or "VertexBaricenter" or "VertexMedian"))
            throw new InvalidOperationException($"配置档 {name} 的 SplitStrategy 不受支持");
        if (TextureFormat is not ("Jpeg" or "Webp" or "Ktx2"))
            throw new InvalidOperationException($"配置档 {name} 的 TextureFormat 不受支持");
        if (DeepBottomMinDropMeters is < 10 or > 10000)
            throw new InvalidOperationException($"配置档 {name} 的 DeepBottomMinDropMeters 必须在 10 到 10000 之间");
        if (DeepBottomMarginMeters is < 0 or > 1000)
            throw new InvalidOperationException($"配置档 {name} 的 DeepBottomMarginMeters 必须在 0 到 1000 之间");
    }
}
