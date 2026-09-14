using System.Globalization;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Infrastructure;

public sealed record ConverterCommand(string Executable, IReadOnlyList<string> Arguments);

public sealed class Obj2TilesCommandBuilder(ConversionOptions options)
{
    public ConverterCommand Build(string inputPath, string outputPath, ConversionSettings settings, GeoReference? geoReference,
        ConversionResourcePlan? resourcePlan = null)
    {
        settings.EnsureValid();
        if (!settings.Local && geoReference == null)
            throw new InvalidOperationException("地理参考转换必须提供 reference_lla 或显式经纬高");

        var args = new List<string>
        {
            "--lods", settings.Lods.ToString(CultureInfo.InvariantCulture),
            "--min-geometry-quality", settings.MinGeometryQuality.ToString(CultureInfo.InvariantCulture),
            "--divisions", settings.Divisions.ToString(CultureInfo.InvariantCulture),
            "--split-strategy", settings.SplitStrategy.ToString(),
            "--lod-texture-scale", settings.LodTextureScale.ToString(CultureInfo.InvariantCulture),
            "--max-texture-size", settings.MaxTextureSize.ToString(CultureInfo.InvariantCulture),
            "--texture-quality", settings.TextureQuality.ToString(CultureInfo.InvariantCulture),
            "--texture-format", settings.TextureFormat.ToString()
        };

        // [2026-09-07 资源计划下发] 纹理缓存预算与阶段并发由资源计划决定，
        // 让 Obj2Tiles 内部的解码/图集/LOD 并发受同一套预算约束。
        if (resourcePlan != null)
        {
            args.Add("--texture-cache-budget-bytes");
            args.Add(resourcePlan.TextureCacheBudgetBytes.ToString(CultureInfo.InvariantCulture));
            args.Add("--stage-concurrency");
            args.Add(resourcePlan.MaxStageConcurrency.ToString(CultureInfo.InvariantCulture));
        }

        if (settings.Hierarchical)
        {
            args.Add("--hierarchical");
            args.AddRange([
                "--hlod-error-divisor", settings.HlodErrorDivisor.ToString(CultureInfo.InvariantCulture),
                "--hlod-target-ratio", settings.HlodTargetRatio.ToString(CultureInfo.InvariantCulture),
                "--external-tileset-depth", settings.ExternalTilesetDepth.ToString(CultureInfo.InvariantCulture)
            ]);
            // [2026-09-07 HLOD 磁盘暂存] 极低内存档位把 HLOD 中间网格暂存到任务磁盘，
            // 用磁盘换驻留内存；暂存目录默认在 HLOD 工作目录内，随任务暂存区一并清理。
            if (resourcePlan?.Mode == ConversionMemoryMode.VeryLowMemory)
                args.Add("--hlod-spool");
        }
        else if (settings.Octree) args.Add("--octree");
        if (settings.ZSplit) args.Add("--zsplit");
        if (settings.TextureFormat == ConversionTextureFormat.Ktx2)
        {
            args.Add("--ktx2-quality");
            args.Add(settings.Ktx2Quality.ToString(CultureInfo.InvariantCulture));
        }

        if (settings.Local)
        {
            args.Add("--local");
        }
        else
        {
            geoReference!.EnsureValid();
            args.AddRange([
                "--lat", geoReference.Latitude.ToString("R", CultureInfo.InvariantCulture),
                "--lon", geoReference.Longitude.ToString("R", CultureInfo.InvariantCulture),
                "--alt", geoReference.Altitude.ToString("R", CultureInfo.InvariantCulture)
            ]);
        }

        args.Add(inputPath);
        args.Add(outputPath);
        return new ConverterCommand(options.Obj2TilesExecutable, args);
    }
}
