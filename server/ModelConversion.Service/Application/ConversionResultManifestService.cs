using System.Security.Cryptography;
using System.Text.Json;
using ModelConversion.Service.Configuration;

namespace ModelConversion.Service.Application;

/// <summary>结果清单中的单文件条目（相对路径、字节数、SHA-256 小写十六进制）。</summary>
public sealed record ResultManifestFile(string Path, long Bytes, string Sha256);

/// <summary>持久化的结果清单（StateRoot/manifests/{jobId}.json）。</summary>
public sealed record ResultManifest(
    int Version,
    Guid ConversionId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ResultManifestFile> Files,
    long TotalBytes);

// [2026-09-09 Issue01 结果清单] 转换成功前由 Worker 一次性流式生成并原子持久化，
// 之后每次查询直接读清单，不再重复扫描目录与计算全量哈希；
// 清单存放在 StateRoot（元数据），不混入已发布输出目录（产物）。
public sealed class ConversionResultManifestService(
    ConversionOptions options,
    TimeProvider timeProvider,
    ILogger<ConversionResultManifestService> logger)
{
    private const int ManifestVersion = 1;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string GetManifestPath(Guid jobId) =>
        Path.Combine(options.StateRoot, "manifests", jobId.ToString("N") + ".json");

    /// <summary>清单存在则直接读取；不存在则流式生成并原子落盘（幂等，重启恢复与旧作业补建共用）。</summary>
    public async Task<ResultManifest> EnsureAsync(Guid jobId, string outputDirectory, CancellationToken cancellationToken)
    {
        var existing = await TryLoadAsync(jobId, cancellationToken);
        if (existing != null) return existing;

        var files = new List<ResultManifestFile>();
        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sha256 = await ComputeSha256StreamingAsync(path, cancellationToken);
            files.Add(new ResultManifestFile(
                ToPortableRelativePath(outputDirectory, path),
                new FileInfo(path).Length,
                sha256));
        }
        var manifest = new ResultManifest(
            ManifestVersion,
            jobId,
            timeProvider.GetUtcNow(),
            files,
            files.Sum(file => file.Bytes));

        var target = GetManifestPath(jobId);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, _jsonOptions), cancellationToken);
            // 与作业状态同口径：同目录临时文件原子替换，断电不留半截清单。
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return manifest;
    }

    /// <summary>读取已有清单；缺失或损坏返回 null（损坏文件删除后由调用方决定是否重建）。</summary>
    public async Task<ResultManifest?> TryLoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var path = GetManifestPath(jobId);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<ResultManifest>(stream, _jsonOptions, cancellationToken);
            return manifest is { Version: ManifestVersion } ? manifest : null;
        }
        catch (JsonException)
        {
            logger.LogWarning("结果清单损坏，按缺失处理: {JobId}", jobId.ToString("N"));
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            return null;
        }
    }

    private static async Task<string> ComputeSha256StreamingAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) break;
            hasher.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ToPortableRelativePath(string root, string path) =>
        Path.GetRelativePath(Path.GetFullPath(root), path).Replace('\\', '/');
}
