using System.Text.RegularExpressions;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Infrastructure;

public static partial class Obj2TilesProgressParser
{
    public static bool TryParse(string line, out ConversionProgressUpdate progress)
    {
        var message = line.Trim();
        var match = HlodDepthRegex().Match(message);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, out var depth)
            && int.TryParse(match.Groups[2].Value, out var maximumDepth))
        {
            var ratio = (depth + 1d) / (maximumDepth + 1d);
            progress = new ConversionProgressUpdate(
                12 + (int)Math.Round(ratio * 58), ConversionProgressStage.Converting, message);
            return true;
        }

        var percent = message switch
        {
            var value when value.Contains("Loading source mesh", StringComparison.OrdinalIgnoreCase) => 12,
            var value when value.Contains("Decimation stage with", StringComparison.OrdinalIgnoreCase) => 15,
            var value when value.Contains("Decimation stage done", StringComparison.OrdinalIgnoreCase) => 35,
            var value when value.Contains("Splitting stage with", StringComparison.OrdinalIgnoreCase) => 40,
            var value when value.Contains("Splitting stage done", StringComparison.OrdinalIgnoreCase) => 65,
            var value when value.Contains("Tiling HLOD", StringComparison.OrdinalIgnoreCase) => 72,
            var value when value.Contains("Tiling stage", StringComparison.OrdinalIgnoreCase) => 72,
            var value when value.Contains("Working on objs conversion", StringComparison.OrdinalIgnoreCase) => 75,
            var value when value.Contains("Generating tileset.json", StringComparison.OrdinalIgnoreCase) => 82,
            var value when value.Contains("Pipeline completed", StringComparison.OrdinalIgnoreCase) => 86,
            _ => -1
        };
        if (percent < 0)
        {
            progress = null!;
            return false;
        }

        progress = new ConversionProgressUpdate(percent, ConversionProgressStage.Converting, message);
        return true;
    }

    [GeneratedRegex(@"HLOD spatial depth\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HlodDepthRegex();
}
