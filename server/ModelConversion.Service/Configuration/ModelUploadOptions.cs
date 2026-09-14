namespace ModelConversion.Service.Configuration;

public sealed class ModelUploadOptions
{
    public long MaxArchiveBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public long MaxExtractedBytes { get; set; } = 100L * 1024 * 1024 * 1024;
    public int MaxFileCount { get; set; } = 20_000;
    public int MaxRelativePathLength { get; set; } = 1024;
    public double MaxCompressionRatio { get; set; } = 1000;
    // [2026-09-09 Issue01 可恢复上传会话] 会话默认 24 小时过期；过期后 GET 返回 404 并由清理工作器删除。
    // 允许极小值仅用于自动化测试。可用 Uploads__SessionTtlHours 覆盖。
    public double SessionTtlHours { get; set; } = 24;

    public void EnsureValid()
    {
        if (MaxArchiveBytes is < 1024 or > 1024L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Uploads:MaxArchiveBytes 必须在 1KiB 到 1TiB 之间");
        if (MaxExtractedBytes < MaxArchiveBytes || MaxExtractedBytes > 4L * 1024 * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Uploads:MaxExtractedBytes 必须不小于压缩包上限且不超过 4TiB");
        if (MaxFileCount is < 1 or > 1_000_000)
            throw new InvalidOperationException("Uploads:MaxFileCount 必须在 1 到 1000000 之间");
        if (MaxRelativePathLength is < 64 or > 4096)
            throw new InvalidOperationException("Uploads:MaxRelativePathLength 必须在 64 到 4096 之间");
        if (MaxCompressionRatio is < 1 or > 1_000_000)
            throw new InvalidOperationException("Uploads:MaxCompressionRatio 必须在 1 到 1000000 之间");
        if (SessionTtlHours is <= 0 or > 24 * 366)
            throw new InvalidOperationException("Uploads:SessionTtlHours 必须在 (0, 8784] 小时之间");
    }
}
