using System.Globalization;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Cli;

public sealed record ConvertCommandLineParseResult(ConvertCommandLine? Command, string? Error, bool ShowHelp);

public sealed class ConvertCommandLine
{
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string ProfileName { get; set; } = "industrial-jpeg";
    public bool Local { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Altitude { get; set; }
    public string? ReferenceLlaPath { get; set; }
    public int? Lods { get; set; }
    public double? MinGeometryQuality { get; set; }
    public bool? Hierarchical { get; set; }
    public double? HlodErrorDivisor { get; set; }
    public double? HlodTargetRatio { get; set; }
    public int? ExternalTilesetDepth { get; set; }
    public bool? Octree { get; set; }
    public int? Divisions { get; set; }
    public bool? ZSplit { get; set; }
    public ConversionSplitStrategy? SplitStrategy { get; set; }
    public double? LodTextureScale { get; set; }
    public int? MaxTextureSize { get; set; }
    public ConversionTextureFormat? TextureFormat { get; set; }
    public int? TextureQuality { get; set; }
    public int? Ktx2Quality { get; set; }

    public bool HasGeoReference => ReferenceLlaPath != null || Latitude.HasValue;

    public ConversionOverrides ToOverrides() => new()
    {
        Lods = Lods,
        MinGeometryQuality = MinGeometryQuality,
        Hierarchical = Hierarchical,
        HlodErrorDivisor = HlodErrorDivisor,
        HlodTargetRatio = HlodTargetRatio,
        ExternalTilesetDepth = ExternalTilesetDepth,
        Octree = Octree,
        Divisions = Divisions,
        ZSplit = ZSplit,
        SplitStrategy = SplitStrategy,
        LodTextureScale = LodTextureScale,
        MaxTextureSize = MaxTextureSize,
        TextureFormat = TextureFormat,
        TextureQuality = TextureQuality,
        Ktx2Quality = Ktx2Quality,
        // --local 与地理参考互斥；显式提供坐标时强制关闭 profile 的 Local。
        Local = Local ? true : HasGeoReference ? false : null
    };

    public static ConvertCommandLineParseResult Parse(IReadOnlyList<string> args)
    {
        var command = new ConvertCommandLine();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h") return new ConvertCommandLineParseResult(null, null, ShowHelp: true);
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                return Error($"无法识别的参数: {argument}");
            var name = argument[2..];
            if (name == "local")
            {
                command.Local = true;
                continue;
            }
            if (index + 1 >= args.Count) return Error($"参数缺少值: --{name}");
            var error = ApplyValue(command, name, args[++index]);
            if (error != null) return Error(error);
        }

        if (command.Input == null) return Error("缺少必填参数: --input");
        if (command.Output == null) return Error("缺少必填参数: --output");
        if (command.Local && (command.ReferenceLlaPath != null || command.Latitude.HasValue || command.Longitude.HasValue || command.Altitude.HasValue))
            return Error("--local 与 --reference-lla、--lat/--lon/--alt 互斥");
        var provided = new[] { command.Latitude.HasValue, command.Longitude.HasValue, command.Altitude.HasValue };
        if (provided.Any(has => has) && provided.Any(has => !has))
            return Error("--lat、--lon、--alt 必须同时提供");
        if (command.ReferenceLlaPath != null && command.Latitude.HasValue)
            return Error("--reference-lla 与 --lat/--lon/--alt 互斥，请只选择一种地理参考来源");
        return new ConvertCommandLineParseResult(command, null, ShowHelp: false);

        static ConvertCommandLineParseResult Error(string message) => new(null, message, ShowHelp: false);
    }

    private static string? ApplyValue(ConvertCommandLine command, string name, string value) => name switch
    {
        "input" => Text(value, parsed => command.Input = parsed),
        "output" => Text(value, parsed => command.Output = parsed),
        "profile" => Text(value, parsed => command.ProfileName = parsed),
        "reference-lla" => Text(value, parsed => command.ReferenceLlaPath = parsed),
        "lat" => Number(value, name, parsed => command.Latitude = parsed),
        "lon" => Number(value, name, parsed => command.Longitude = parsed),
        "alt" => Number(value, name, parsed => command.Altitude = parsed),
        "lods" => Integer(value, name, parsed => command.Lods = parsed),
        "min-geometry-quality" => Number(value, name, parsed => command.MinGeometryQuality = parsed),
        "hierarchical" => Flag(value, name, parsed => command.Hierarchical = parsed),
        "hlod-error-divisor" => Number(value, name, parsed => command.HlodErrorDivisor = parsed),
        "hlod-target-ratio" => Number(value, name, parsed => command.HlodTargetRatio = parsed),
        "external-tileset-depth" => Integer(value, name, parsed => command.ExternalTilesetDepth = parsed),
        "octree" => Flag(value, name, parsed => command.Octree = parsed),
        "divisions" => Integer(value, name, parsed => command.Divisions = parsed),
        "zsplit" => Flag(value, name, parsed => command.ZSplit = parsed),
        "split-strategy" => Choice<ConversionSplitStrategy>(value, name, parsed => command.SplitStrategy = parsed),
        "lod-texture-scale" => Number(value, name, parsed => command.LodTextureScale = parsed),
        "max-texture-size" => Integer(value, name, parsed => command.MaxTextureSize = parsed),
        "texture-format" => Choice<ConversionTextureFormat>(value, name, parsed => command.TextureFormat = parsed),
        "texture-quality" => Integer(value, name, parsed => command.TextureQuality = parsed),
        "ktx2-quality" => Integer(value, name, parsed => command.Ktx2Quality = parsed),
        _ => $"未知参数: --{name}"
    };

    private static string? Text(string value, Action<string> set)
    {
        set(value);
        return null;
    }

    private static string? Number(string value, string name, Action<double> set)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
            return $"参数 --{name} 需要有限数值: {value}";
        set(parsed);
        return null;
    }

    private static string? Integer(string value, string name, Action<int> set)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return $"参数 --{name} 需要整数: {value}";
        set(parsed);
        return null;
    }

    private static string? Flag(string value, string name, Action<bool> set)
    {
        if (!bool.TryParse(value, out var parsed))
            return $"参数 --{name} 需要 true/false: {value}";
        set(parsed);
        return null;
    }

    private static string? Choice<TEnum>(string value, string name, Action<TEnum> set) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            return $"参数 --{name} 不受支持: {value}（可选: {string.Join("/", Enum.GetNames<TEnum>())}）";
        set(parsed);
        return null;
    }
}
