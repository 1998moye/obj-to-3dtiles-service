using System.IO.Compression;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure;

namespace ModelConversion.Service.Application;

// [2026-09-09 Issue01 结果清单] 原代码: (string Path, long Bytes)。
// 原因: PRD §12 要求清单携带每文件 SHA-256；缺省 null 兼容无清单的旧数据。
public sealed record ConversionResultFile(string Path, long Bytes, string? Sha256 = null);

public sealed record ConversionResultSnapshot(
    Guid ConversionId,
    string OutputDirectory,
    IReadOnlyList<ConversionResultFile> Files,
    long TotalBytes);

public sealed class ConversionResultService(
    IConversionJobRepository repository,
    ConversionResultManifestService manifestService,
    ConversionOptions options,
    ILogger<ConversionResultService> logger)
{
    public async Task<ConversionResultSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await repository.GetAsync(id, cancellationToken);
        if (job == null || job.State != ConversionJobState.Succeeded) return null;

        var outputDirectory = PathBoundary.ResolveOwnedPath(options.OutputRoot, job.OutputRelativePath);
        if (!Directory.Exists(outputDirectory)) return null;

        // [2026-09-09 Issue01 结果清单] 优先读 Worker 成功前持久化的清单（含 SHA-256，零重复哈希）；
        // 本功能上线前成功的旧作业没有清单：惰性补建一次（只新增派生文件，不改写旧作业状态），
        // 补建失败回退为无摘要的目录枚举，不改变可预览性。
        try
        {
            var manifest = await manifestService.EnsureAsync(id, outputDirectory, cancellationToken);
            var manifestFiles = manifest.Files
                .Select(file => new ConversionResultFile(file.Path, file.Bytes, file.Sha256))
                .ToArray();
            return new ConversionResultSnapshot(id, outputDirectory, manifestFiles, manifest.TotalBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("结果清单读取/补建失败，回退目录枚举: {JobId} ({Message})", id.ToString("N"), exception.Message);
        }

        var files = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new ConversionResultFile(
                ToPortableRelativePath(outputDirectory, path),
                new FileInfo(path).Length))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        return new ConversionResultSnapshot(id, outputDirectory, files, files.Sum(file => file.Bytes));
    }

    public async Task WriteZipAsync(
        ConversionResultSnapshot snapshot,
        Stream destination,
        CancellationToken cancellationToken)
    {
        // [2026-09-08 结果目录拉取] 直接向响应流写 ZIP，避免大型 tiles 目录二次完整落盘或进入内存。
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = PathBoundary.ResolveExistingFile(snapshot.OutputDirectory, file.Path);
            var entry = archive.CreateEntry(file.Path, CompressionLevel.NoCompression);
            await using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = entry.Open();
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
        }
    }

    private static string ToPortableRelativePath(string root, string path) =>
        Path.GetRelativePath(Path.GetFullPath(root), path).Replace('\\', '/');
}
