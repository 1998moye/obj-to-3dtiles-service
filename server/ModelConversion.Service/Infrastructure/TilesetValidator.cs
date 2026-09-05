using System.Text.Json;
using System.Buffers.Binary;
using ModelConversion.Service.Application;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Infrastructure;

public sealed class TilesetValidator(ConversionOptions options) : ITilesetValidator
{
    public async Task<TilesetValidationReport> ValidateAsync(string outputRoot, CancellationToken cancellationToken)
    {
        var rootPath = Path.Combine(outputRoot, "tileset.json");
        if (!File.Exists(rootPath)) throw new InvalidDataException("输出缺少 tileset.json");

        var state = new ValidationState(Path.GetFullPath(outputRoot));
        await ValidateTilesetAsync(rootPath, state, cancellationToken);
        var totalBytes = Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        return new TilesetValidationReport(
            state.VisitedTilesets.Count,
            state.TileCount,
            state.ContentCount,
            totalBytes,
            state.RootGeometricError);
    }

    private async Task ValidateTilesetAsync(
        string path,
        ValidationState state,
        CancellationToken cancellationToken,
        double? gatewayError = null)
    {
        var fullPath = Path.GetFullPath(path);
        if (!state.VisitedTilesets.Add(fullPath)) return;
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new InvalidDataException($"外部 tileset 不存在: {fullPath}");
        if (info.Length > options.MaxTilesetJsonBytes)
            throw new InvalidDataException($"tileset JSON 超过限制: {info.Length} bytes");

        await using var stream = File.OpenRead(fullPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var documentRoot = document.RootElement;
        if (!documentRoot.TryGetProperty("asset", out var asset) || !asset.TryGetProperty("version", out _))
            throw new InvalidDataException($"tileset 缺少 asset.version: {fullPath}");
        if (!documentRoot.TryGetProperty("root", out var root))
            throw new InvalidDataException($"tileset 缺少 root: {fullPath}");
        var declaredError = documentRoot.TryGetProperty("geometricError", out var tilesetError)
            && tilesetError.TryGetDouble(out var parsedTilesetError)
            ? parsedTilesetError
            : throw new InvalidDataException($"tileset 缺少 geometricError: {fullPath}");
        if (!double.IsFinite(declaredError) || declaredError < 0)
            throw new InvalidDataException($"tileset geometricError 非法: {declaredError}");

        if (root.TryGetProperty("content", out var rootContent)
            && rootContent.ValueKind == JsonValueKind.Object
            && (!root.TryGetProperty("refine", out var rootRefine) || rootRefine.GetString() != "REPLACE"))
            throw new InvalidDataException("带内容的根瓦片必须使用 REPLACE，避免父子 LOD 叠加驻留");

        var rootError = ReadGeometricError(root, fullPath);
        if (rootError > declaredError + 1e-9)
            throw new InvalidDataException("root geometricError 不能大于 tileset geometricError");
        if (gatewayError.HasValue && rootError > gatewayError.Value + 1e-9)
            throw new InvalidDataException(
                $"外部 tileset 根 geometricError({rootError}) 大于入口瓦片({gatewayError.Value})");
        if (state.TileCount == 0) state.RootGeometricError = rootError;
        await ValidateTileAsync(root, rootError, Path.GetDirectoryName(fullPath)!, state, cancellationToken);
    }

    private async Task ValidateTileAsync(
        JsonElement tile,
        double parentError,
        string baseDirectory,
        ValidationState state,
        CancellationToken cancellationToken)
    {
        state.TileCount++;
        if (!tile.TryGetProperty("boundingVolume", out _))
            throw new InvalidDataException("瓦片缺少 boundingVolume");

        var geometricError = ReadGeometricError(tile, baseDirectory);
        if (geometricError > parentError + 1e-9)
            throw new InvalidDataException($"子瓦片 geometricError({geometricError}) 大于父瓦片({parentError})");

        if (tile.TryGetProperty("refine", out var refine))
        {
            var value = refine.GetString();
            if (value is not ("ADD" or "REPLACE"))
                throw new InvalidDataException($"不支持的 refine: {value}");
        }

        // content 可为 null（Obj2Tiles 会对无内容瓦片显式输出 null），仅对象形态才校验内容引用。
        if (tile.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
        {
            if (content.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("tile content must be an object or null");
            var uri = content.TryGetProperty("uri", out var uriElement)
                ? uriElement.GetString()
                : content.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(uri)) throw new InvalidDataException("content 缺少 uri/url");
            state.ContentCount++;
            var contentPath = ResolveContentPath(baseDirectory, uri, state.OutputRoot);
            if (!File.Exists(contentPath)) throw new InvalidDataException($"瓦片内容不存在: {uri}");
            if (contentPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                await ValidateTilesetAsync(contentPath, state, cancellationToken, geometricError);
            else
                ValidateBinaryContent(contentPath);
        }

        if (!tile.TryGetProperty("children", out var children) || children.ValueKind == JsonValueKind.Null) return;
        if (children.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("tile children must be an array or null");

        foreach (var child in children.EnumerateArray())
            await ValidateTileAsync(child, geometricError, baseDirectory, state, cancellationToken);
    }

    private static double ReadGeometricError(JsonElement tile, string location)
    {
        if (!tile.TryGetProperty("geometricError", out var value) || !value.TryGetDouble(out var error))
            throw new InvalidDataException($"瓦片缺少 geometricError: {location}");
        if (!double.IsFinite(error) || error < 0)
            throw new InvalidDataException($"geometricError 非法: {error}");
        return error;
    }

    private static string ResolveContentPath(string baseDirectory, string uri, string outputRoot)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out _))
            throw new InvalidDataException($"不允许远程瓦片内容 URI: {uri}");
        var relative = Uri.UnescapeDataString(uri.Split('?', '#')[0]).Replace('/', Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(baseDirectory, relative));
        var rootPrefix = outputRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // [2026-09-04 输出校验] 防止恶意 tileset 通过 ../ 引用服务输出目录之外的文件。
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"瓦片内容路径越界: {uri}");
        return resolved;
    }

    private static void ValidateBinaryContent(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".b3dm", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".glb", StringComparison.OrdinalIgnoreCase))
            return;

        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != header.Length)
            throw new InvalidDataException($"瓦片二进制头不完整: {path}");

        var expectedMagic = extension.Equals(".b3dm", StringComparison.OrdinalIgnoreCase) ? "b3dm"u8 : "glTF"u8;
        if (!header[..4].SequenceEqual(expectedMagic))
            throw new InvalidDataException($"瓦片二进制 magic 不正确: {path}");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) != 1)
            throw new InvalidDataException($"瓦片二进制版本不受支持: {path}");
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        if (declaredLength != stream.Length)
            throw new InvalidDataException($"瓦片二进制长度声明与文件不一致: {path}");
    }

    private sealed class ValidationState(string outputRoot)
    {
        public string OutputRoot { get; } = outputRoot.TrimEnd(Path.DirectorySeparatorChar);
        public HashSet<string> VisitedTilesets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int TileCount { get; set; }
        public int ContentCount { get; set; }
        public double RootGeometricError { get; set; }
    }
}
