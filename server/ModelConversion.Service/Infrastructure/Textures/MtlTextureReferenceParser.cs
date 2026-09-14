using System.Globalization;

namespace ModelConversion.Service.Infrastructure.Textures;

// MTL 纹理引用行的结构化解析结果：重写临时 MTL 时按原样保留前导空白与选项前缀。
internal sealed record MtlTextureReference(
    string LeadingWhitespace,
    string Keyword,
    string OptionsPrefix,
    string TexturePath,
    string? ResolvedPath);

// MTL 纹理引用解析的唯一权威实现，语义与 Obj2Tiles 的 Material.ExtractTexturePath/ResolvePath
// 保持一致（含 \ 与 / 分隔符规范化）：预检（InputModelInspector）与规范化重写
// （TextureNormalizationPipeline）必须看到同一个纹理集合，不能各自实现导致口径漂移。
internal static class MtlTextureReferenceParser
{
    // 与 Obj2Tiles Material.MtlOptionValueCounts 同步：带参数值的 MTL 贴图选项。
    private static readonly IReadOnlyDictionary<string, int> MtlOptionValueCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["-blendu"] = 1, ["-blendv"] = 1, ["-bm"] = 1, ["-boost"] = 1,
            ["-cc"] = 1, ["-clamp"] = 1, ["-imfchan"] = 1, ["-mm"] = 2,
            ["-texres"] = 1, ["-type"] = 1,
            // -o, -s, -t 带 1-3 个数值参数，单独处理（与 Obj2Tiles 相同）
        };

    // 解析一行 MTL；非纹理引用行（含注释与空行）返回 false。
    public static bool TryParseLine(
        string line,
        string mtlDirectory,
        string objDirectory,
        out MtlTextureReference? reference)
    {
        reference = null;
        if (line.Length == 0 || line[0] == '#') return false;
        var trimmed = line.TrimStart();
        var leading = line[..(line.Length - trimmed.Length)];

        string keyword;
        string remainder;
        if (trimmed.StartsWith("map_Kd ", StringComparison.Ordinal))
        {
            keyword = "map_Kd";
            remainder = trimmed["map_Kd ".Length..];
        }
        else if (trimmed.StartsWith("norm ", StringComparison.Ordinal))
        {
            keyword = "norm";
            remainder = trimmed["norm ".Length..];
        }
        else
        {
            return false;
        }

        var (optionsPrefix, texturePath) = SplitTexturePath(remainder);
        if (texturePath.Length == 0) return false;
        reference = new MtlTextureReference(leading, keyword, optionsPrefix, texturePath,
            ResolveTexturePath(texturePath, mtlDirectory, objDirectory));
        return true;
    }

    // 与 Obj2Tiles ExtractTexturePath 同步：剥离前导选项参数后取行尾纹理路径（可含空格），
    // 返回（选项前缀原样、分隔符已规范化的纹理路径）。
    private static (string OptionsPrefix, string TexturePath) SplitTexturePath(string lineRemainder)
    {
        var tokens = lineRemainder.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        while (index < tokens.Length && tokens[index].StartsWith('-'))
        {
            var option = tokens[index];
            index++;
            if (option.Equals("-o", StringComparison.OrdinalIgnoreCase)
                || option.Equals("-s", StringComparison.OrdinalIgnoreCase)
                || option.Equals("-t", StringComparison.OrdinalIgnoreCase))
            {
                var consumed = 0;
                while (index < tokens.Length && consumed < 3
                    && double.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    index++;
                    consumed++;
                }
            }
            else
            {
                MtlOptionValueCounts.TryGetValue(option, out var count);
                if (count == 0) count = 1;
                index = Math.Min(index + count, tokens.Length);
            }
        }

        var optionsPrefix = index == 0 ? string.Empty : string.Join(' ', tokens[..index]) + " ";
        var raw = string.Join(' ', tokens[index..]);
        return (optionsPrefix,
            raw.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
    }

    // 与 Obj2Tiles ResolvePath 同步：MTL 目录 → OBJ 目录 → 仅文件名回退 → 原始路径。
    public static string? ResolveTexturePath(string path, string mtlDirectory, string objDirectory)
    {
        var candidate = Path.GetFullPath(Path.Combine(mtlDirectory, path));
        if (File.Exists(candidate)) return candidate;

        if (!string.Equals(mtlDirectory, objDirectory, StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.GetFullPath(Path.Combine(objDirectory, path));
            if (File.Exists(candidate)) return candidate;
        }

        var fileName = Path.GetFileName(path);
        if (fileName.Length < path.Length)
        {
            candidate = Path.GetFullPath(Path.Combine(mtlDirectory, fileName));
            if (File.Exists(candidate)) return candidate;
            if (!string.Equals(mtlDirectory, objDirectory, StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.GetFullPath(Path.Combine(objDirectory, fileName));
                if (File.Exists(candidate)) return candidate;
            }
        }

        candidate = Path.GetFullPath(path);
        return File.Exists(candidate) ? candidate : null;
    }
}
