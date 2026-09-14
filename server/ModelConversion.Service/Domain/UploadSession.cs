namespace ModelConversion.Service.Domain;

// [2026-09-09 Issue01 可恢复上传会话] 会话状态机（PRD §12 固定五态，API 以小写序列化）：
// Uploading → Finalizing → Completed / Failed；Uploading 超过 TTL → Expired。
public enum UploadSessionState
{
    Uploading,
    Finalizing,
    Completed,
    Failed,
    Expired
}

/// <summary>
/// 可恢复上传会话的持久记录（StateRoot/upload-sessions/{id}/session.json）。
/// 分片正文在同目录 parts/ 下按编号落盘，重启后凭本记录继续接收缺失分片。
/// </summary>
public sealed record UploadSession
{
    public required Guid Id { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required long PartSizeBytes { get; init; }
    public required int PartCount { get; init; }
    /// <summary>已完成分片号（从 1 开始，持久化为升序数组）。</summary>
    public List<int> CompletedParts { get; init; } = [];
    public UploadSessionState State { get; init; } = UploadSessionState.Uploading;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    /// <summary>仅 Completed 有值：发布到 InputRoot 的 OBJ 相对路径，可直接用于创建转换。</summary>
    public string? InputPath { get; init; }
    public string? ReferenceLlaPath { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public UploadSession TransitionTo(UploadSessionState next, DateTimeOffset now,
        string? errorCode = null, string? errorMessage = null,
        string? inputPath = null, string? referenceLlaPath = null)
    {
        // 状态机校验：只允许 创建→Uploading→Finalizing→Completed/Failed、Uploading→Expired，
        // 防止并发分片/重复完成把会话带入未定义状态。
        var allowed = (State, next) switch
        {
            (UploadSessionState.Uploading, UploadSessionState.Finalizing) => true,
            (UploadSessionState.Uploading, UploadSessionState.Expired) => true,
            (UploadSessionState.Finalizing, UploadSessionState.Completed) => true,
            (UploadSessionState.Finalizing, UploadSessionState.Failed) => true,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"非法上传会话状态迁移: {State} -> {next}");
        return this with
        {
            State = next,
            UpdatedAt = now,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            InputPath = inputPath ?? InputPath,
            ReferenceLlaPath = referenceLlaPath ?? ReferenceLlaPath
        };
    }
}
