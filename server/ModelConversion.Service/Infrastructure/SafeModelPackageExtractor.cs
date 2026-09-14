using System.Buffers;
using System.IO.Compression;
using ModelConversion.Service.Application;
using ModelConversion.Service.Configuration;

namespace ModelConversion.Service.Infrastructure;

/// <summary>安全解压结果：总量、文件数、唯一 OBJ 与包内 reference_lla.json（均为相对暂存目录的相对路径）。</summary>
public sealed record ModelPackageExtraction(
    long ExtractedBytes,
    int FileCount,
    string ObjectRelativePath,
    string? ReferenceLlaRelativePath);

// [2026-09-09 Issue01 可恢复上传会话] ZIP 安全解压与包结构校验抽为共享深模块：
// 单请求 /api/v1/uploads（ModelUploadService）与可恢复上传会话后台合并（UploadSessionFinalizer）
// 必须走同一套路径穿越/重名/符号链接/压缩比/上限校验，禁止复制两套安全实现。
public sealed class SafeModelPackageExtractor(ModelUploadOptions uploadOptions)
{
    public async Task<ModelPackageExtraction> ExtractAsync(
        string archivePath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var summary = await ExtractArchiveEntriesAsync(archivePath, stagingPath, cancellationToken);

        var objectFiles = Directory.EnumerateFiles(stagingPath, "*.obj", SearchOption.AllDirectories).ToArray();
        if (objectFiles.Length != 1)
            throw new InvalidDataException($"模型包必须且只能包含一个 OBJ 文件，实际发现 {objectFiles.Length} 个");

        var referenceMatches = Directory.EnumerateFiles(stagingPath, "reference_lla.json", SearchOption.AllDirectories)
            .Take(2)
            .ToArray();

        return new ModelPackageExtraction(
            summary.ExtractedBytes,
            summary.FileCount,
            Path.GetRelativePath(stagingPath, objectFiles[0]),
            referenceMatches.Length == 1 ? Path.GetRelativePath(stagingPath, referenceMatches[0]) : null);
    }

    private async Task<ExtractionSummary> ExtractArchiveEntriesAsync(
        string archivePath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long extractedBytes = 0;
        var fileCount = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeEntryPath(entry.FullName);
            if (relativePath == null) continue;
            RejectSymbolicLink(entry);

            var destinationPath = PathBoundary.ResolveOwnedPath(stagingPath, relativePath);
            if (IsDirectory(entry))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            fileCount++;
            if (fileCount > uploadOptions.MaxFileCount)
                throw new ModelUploadTooLargeException($"文件数量超过 {uploadOptions.MaxFileCount} 个上限");
            if (!destinations.Add(destinationPath))
                throw new InvalidDataException($"压缩包包含重复路径: {entry.FullName}");
            if (entry.Length > uploadOptions.MaxExtractedBytes - extractedBytes)
                throw new ModelUploadTooLargeException($"解压后总大小超过 {uploadOptions.MaxExtractedBytes} 字节上限");
            if (entry.Length > 0 && entry.Length / (double)Math.Max(1, entry.CompressedLength) > uploadOptions.MaxCompressionRatio)
                throw new InvalidDataException($"文件压缩比异常: {entry.FullName}");

            var parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            await using var input = entry.Open();
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            extractedBytes = await CopyEntryAsync(input, output, extractedBytes, cancellationToken);
        }

        return new ExtractionSummary(extractedBytes, fileCount);
    }

    private async Task<long> CopyEntryAsync(
        Stream input,
        Stream output,
        long extractedBefore,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        var total = extractedBefore;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                total = checked(total + read);
                if (total > uploadOptions.MaxExtractedBytes)
                    throw new ModelUploadTooLargeException($"解压后总大小超过 {uploadOptions.MaxExtractedBytes} 字节上限");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    private string? NormalizeEntryPath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath)) return null;
        var normalized = entryPath.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0) return null;
        if (normalized.Length > uploadOptions.MaxRelativePathLength)
            throw new InvalidDataException($"压缩包内路径过长: {entryPath}");
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException($"压缩包包含绝对路径: {entryPath}");

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException($"压缩包包含非法路径: {entryPath}");
        return Path.Combine(segments);
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static void RejectSymbolicLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        if (unixMode == unixSymbolicLink)
            throw new InvalidDataException($"压缩包不允许包含符号链接: {entry.FullName}");
    }

    private sealed record ExtractionSummary(long ExtractedBytes, int FileCount);
}
