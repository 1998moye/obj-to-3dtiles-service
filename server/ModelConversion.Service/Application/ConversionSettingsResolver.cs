using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

// 唯一的 profile + typed overrides 合并/校验入口；CLI 与 API 都通过它生成不可变参数快照。
public sealed class ConversionSettingsResolver(ConversionOptions options)
{
    public ConversionSettings Resolve(string profileName, ConversionOverrides? overrides)
    {
        var profile = options.GetProfile(profileName);
        var settings = new ConversionSettings
        {
            Lods = overrides?.Lods ?? profile.Lods,
            MinGeometryQuality = overrides?.MinGeometryQuality ?? profile.MinGeometryQuality,
            Hierarchical = overrides?.Hierarchical ?? profile.Hierarchical,
            HlodErrorDivisor = overrides?.HlodErrorDivisor ?? profile.HlodErrorDivisor,
            HlodTargetRatio = overrides?.HlodTargetRatio ?? profile.HlodTargetRatio,
            ExternalTilesetDepth = overrides?.ExternalTilesetDepth ?? profile.ExternalTilesetDepth,
            Octree = overrides?.Octree ?? profile.Octree,
            Divisions = overrides?.Divisions ?? profile.Divisions,
            ZSplit = overrides?.ZSplit ?? profile.ZSplit,
            SplitStrategy = overrides?.SplitStrategy ?? ParseEnum<ConversionSplitStrategy>(profile.SplitStrategy, profileName),
            LodTextureScale = overrides?.LodTextureScale ?? profile.LodTextureScale,
            MaxTextureSize = overrides?.MaxTextureSize ?? profile.MaxTextureSize,
            TextureFormat = overrides?.TextureFormat ?? ParseEnum<ConversionTextureFormat>(profile.TextureFormat, profileName),
            TextureQuality = overrides?.TextureQuality ?? profile.TextureQuality,
            Ktx2Quality = overrides?.Ktx2Quality ?? profile.Ktx2Quality,
            Local = overrides?.Local ?? profile.Local,
            StripDeepBottom = overrides?.StripDeepBottom ?? profile.StripDeepBottom,
            DeepBottomMinDropMeters = overrides?.DeepBottomMinDropMeters ?? profile.DeepBottomMinDropMeters,
            DeepBottomMarginMeters = overrides?.DeepBottomMarginMeters ?? profile.DeepBottomMarginMeters
        };
        settings.EnsureValid();
        return settings;
    }

    private static TEnum ParseEnum<TEnum>(string value, string profileName) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new InvalidOperationException($"配置档 {profileName} 的 {typeof(TEnum).Name} 不受支持: {value}");
        return parsed;
    }
}
