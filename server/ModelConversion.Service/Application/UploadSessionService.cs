using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

/// <summary>POST /api/v1/upload-sessions 请求体（PRD §12 固定字段）。</summary>
public sealed record CreateUploadSessionRequest(
    string IdempotencyKey,
    string FileName,
    long SizeBytes,
    string Sha256,
    long PartSizeBytes);

/// <summary>PUT 分片成功响应（PRD §12 固定字段）。</summary>
public sealed record UploadPartResult(int PartNumber, long SizeBytes, string Sha256);

/// <summary>上传会话业务异常：errorCode 固定为 PRD §12 四值之一，HTTP 状态由端点按码映射。</summary>
public sealed class UploadSessionException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class UploadSessionNotFoundException(string message) : Exception(message);

public static class UploadSessionErrorCodes
{
    public const string Conflict = "UPLOAD_SESSION_CONFLICT";
    public const string PartInvalid = "UPLOAD_PART_INVALID";
    public const string HashMismatch = "UPLOAD_HASH_MISMATCH";
    public const string FinalizeFailed = "UPLOAD_FINALIZE_FAILED";
}

// [2026-09-09 Issue01 可恢复上传会话] 会话深模块：幂等创建去重、分片落盘与摘要校验、
// 完成状态机与持久化全部收敛在本类；Endpoint 只做 HTTP 映射。
// 目录布局（StateRoot 下）：upload-sessions/{id}/session.json + parts/{n}.part(+ .sha256 旁车摘要)。
// 持久化与作业仓储同口径：同目录临时文件原子替换，进程崩溃不留半截 JSON。
public sealed class UploadSessionService(
    ConversionOptions conversionOptions,
    ModelUploadOptions uploadOptions,
    UploadSessionFinalizeQueue finalizeQueue,
    TimeProvider timeProvider)
{
    // PRD §12 固定 64MiB 分片；请求带其他值一律拒绝，避免同一服务出现两种分片语义。
    public const long PartSizeBytes = 64L * 1024 * 1024;
    private const int Sha256HexLength = 64;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string SessionsRoot => Path.Combine(conversionOptions.StateRoot, "upload-sessions");

    public string GetSessionDirectory(Guid id) => Path.Combine(SessionsRoot, id.ToString("N"));

    public async Task<UploadSession> CreateAsync(CreateUploadSessionRequest request, CancellationToken cancellationToken)
    {
        ValidateCreateRequest(request);
        var now = timeProvider.GetUtcNow();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 幂等去重：同一 idempotencyKey+sha256 返回原会话（平台崩溃重试不会重复创建输入）；
            // 同键不同摘要说明调用方换了内容，稳定 409 拒绝（PRD §12）。
            var existing = (await ListUnsafeAsync(cancellationToken))
                .Where(session => string.Equals(session.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
                .OrderByDescending(session => session.CreatedAt)
                .FirstOrDefault();
            if (existing != null)
            {
                if (!IsSha256Equal(existing.Sha256, request.Sha256))
                    throw new UploadSessionException(UploadSessionErrorCodes.Conflict,
                        "同一幂等键已绑定不同摘要的上传内容，请更换幂等键或核对文件");
                if (existing.State is UploadSessionState.Uploading or UploadSessionState.Finalizing or UploadSessionState.Completed)
                {
                    // 已过期但尚未清理的 Uploading 会话不可继续接收分片：先置 Expired，再走下方重建。
                    if (existing.State == UploadSessionState.Uploading && IsExpired(existing, now))
                    {
                        await SaveUnsafeAsync(existing.TransitionTo(UploadSessionState.Expired, now), cancellationToken);
                    }
                    else
                    {
                        return existing;
                    }
                }
                // [2026-09-09 Issue01 会话重建] 原语义: Failed/Expired 会话占住幂等键导致平台无法重传。
                // 原因: 终态会话不再接受分片，删除其目录（仅含分片与状态，不触碰已发布输入）后以同键重建。
                await DeleteSessionDirectoryUnsafeAsync(existing.Id);
            }

            var session = new UploadSession
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = request.IdempotencyKey,
                FileName = request.FileName,
                SizeBytes = request.SizeBytes,
                Sha256 = request.Sha256.ToLowerInvariant(),
                PartSizeBytes = PartSizeBytes,
                PartCount = CheckedPartCount(request.SizeBytes),
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = now.AddHours(uploadOptions.SessionTtlHours)
            };
            Directory.CreateDirectory(GetPartsDirectory(session.Id));
            await SaveUnsafeAsync(session, cancellationToken);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>读取会话；Uploading 且已过期的会话在此惰性置 Expired（GET 语义：过期即 404）。</summary>
    public async Task<UploadSession?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken);
        if (session == null) return null;
        if (session.State == UploadSessionState.Uploading && IsExpired(session, timeProvider.GetUtcNow()))
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // 二次读取：可能刚被并发分片推进，只有仍是 Uploading 才置过期。
                var fresh = await LoadAsync(id, cancellationToken);
                if (fresh != null && fresh.State == UploadSessionState.Uploading)
                {
                    fresh = fresh.TransitionTo(UploadSessionState.Expired, timeProvider.GetUtcNow());
                    await SaveUnsafeAsync(fresh, cancellationToken);
                }
                return fresh;
            }
            finally
            {
                _gate.Release();
            }
        }
        return session;
    }

    public Task<UploadSession?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        LoadFromFileAsync(GetSessionFile(id), cancellationToken);

    public async Task<UploadSession?> LoadFromDirectoryAsync(string sessionDirectory, CancellationToken cancellationToken) =>
        await LoadFromFileAsync(Path.Combine(sessionDirectory, "session.json"), cancellationToken);

    public async Task<IReadOnlyList<UploadSession>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ListUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UploadPartResult> SavePartAsync(
        Guid id,
        int partNumber,
        Stream body,
        long? contentLength,
        string? partSha256,
        CancellationToken cancellationToken)
    {
        var session = await GetAsync(id, cancellationToken)
            ?? throw new UploadSessionNotFoundException("上传会话不存在或已过期");
        if (session.State != UploadSessionState.Uploading)
            throw new UploadSessionException(UploadSessionErrorCodes.Conflict,
                $"会话当前状态 {session.State} 不允许上传分片");
        if (partNumber < 1 || partNumber > session.PartCount)
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid,
                $"分片编号必须在 1 到 {session.PartCount} 之间");

        // 除末片外固定 64MiB；末片为总大小余量。Content-Length 必须与期望一致（PRD §12）。
        var expectedSize = ExpectedPartSize(session, partNumber);
        if (contentLength == null)
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid, "必须提供 Content-Length");
        if (contentLength.Value != expectedSize)
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid,
                $"分片 {partNumber} 大小必须为 {expectedSize} 字节");
        if (!IsSha256Hex(partSha256))
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid, "必须提供 64 位十六进制 X-Part-Sha256");

        var partPath = GetPartPath(session.Id, partNumber);
        var partShaPath = partPath + ".sha256";
        if (session.CompletedParts.Contains(partNumber))
        {
            // 重复上传：与落盘摘要一致幂等返回 200，不同摘要 409（PRD §12）。
            var stored = await ReadTextQuietlyAsync(partShaPath, cancellationToken);
            if (stored != null && IsSha256Equal(stored, partSha256!))
                return new UploadPartResult(partNumber, expectedSize, stored);
            throw new UploadSessionException(UploadSessionErrorCodes.Conflict,
                $"分片 {partNumber} 已上传且摘要不同");
        }

        // 流式落盘同时计算 SHA-256，64MiB 分片不进入内存；临时文件名带随机后缀，
        // 同分片并发重传互不覆盖，提交阶段在闸门内裁决。
        var temporaryPath = partPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string computedSha;
        long written;
        try
        {
            (written, computedSha) = await WritePartStreamingAsync(body, temporaryPath, cancellationToken);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
        if (written != expectedSize)
        {
            TryDeleteFile(temporaryPath);
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid,
                "分片正文长度与 Content-Length 不一致");
        }
        if (!IsSha256Equal(computedSha, partSha256!))
        {
            TryDeleteFile(temporaryPath);
            throw new UploadSessionException(UploadSessionErrorCodes.HashMismatch,
                $"分片 {partNumber} 摘要与 X-Part-Sha256 不一致");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 二次读取：流式传输期间会话可能被完成/过期/并发提交同一分片。
            var fresh = await LoadAsync(id, cancellationToken);
            if (fresh == null || fresh.State != UploadSessionState.Uploading)
            {
                TryDeleteFile(temporaryPath);
                throw new UploadSessionException(UploadSessionErrorCodes.Conflict,
                    "会话在分片传输期间状态已变化，请查询会话后重试");
            }
            if (fresh.CompletedParts.Contains(partNumber))
            {
                TryDeleteFile(temporaryPath);
                var stored = await ReadTextQuietlyAsync(partShaPath, cancellationToken);
                if (stored != null && IsSha256Equal(stored, partSha256!))
                    return new UploadPartResult(partNumber, expectedSize, stored);
                throw new UploadSessionException(UploadSessionErrorCodes.Conflict,
                    $"分片 {partNumber} 已由并发请求上传且摘要不同");
            }
            File.Move(temporaryPath, partPath, overwrite: true);
            await File.WriteAllTextAsync(partShaPath, computedSha, cancellationToken);
            var completed = fresh.CompletedParts.Append(partNumber).Distinct().OrderBy(number => number).ToList();
            fresh = fresh with { CompletedParts = completed, UpdatedAt = timeProvider.GetUtcNow() };
            await SaveUnsafeAsync(fresh, cancellationToken);
            return new UploadPartResult(partNumber, expectedSize, computedSha);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UploadSession> RequestCompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await LoadAsync(id, cancellationToken)
                ?? throw new UploadSessionNotFoundException("上传会话不存在或已过期");
            // 幂等：重复 complete 直接返回当前状态（PRD §12 转入 finalizing 返回 202）。
            if (session.State is UploadSessionState.Finalizing or UploadSessionState.Completed)
                return session;
            if (session.State is not UploadSessionState.Uploading)
                throw new UploadSessionException(UploadSessionErrorCodes.Conflict,
                    $"会话当前状态 {session.State} 不允许完成");
            if (IsExpired(session, timeProvider.GetUtcNow()))
            {
                session = session.TransitionTo(UploadSessionState.Expired, timeProvider.GetUtcNow());
                await SaveUnsafeAsync(session, cancellationToken);
                throw new UploadSessionNotFoundException("上传会话已过期");
            }
            var missing = Enumerable.Range(1, session.PartCount)
                .Except(session.CompletedParts)
                .ToArray();
            if (missing.Length > 0)
                throw new UploadSessionException(UploadSessionErrorCodes.Conflict,
                    $"还有 {missing.Length} 个分片未上传（首个缺失: {missing[0]}）");

            session = session.TransitionTo(UploadSessionState.Finalizing, timeProvider.GetUtcNow());
            await SaveUnsafeAsync(session, cancellationToken);
            await finalizeQueue.EnqueueAsync(session.Id, cancellationToken);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCompletedAsync(Guid id, string inputPath, string? referenceLlaPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            var session = await LoadAsync(id, CancellationToken.None);
            if (session == null || session.State != UploadSessionState.Finalizing) return;
            session = session.TransitionTo(UploadSessionState.Completed, timeProvider.GetUtcNow(),
                inputPath: inputPath, referenceLlaPath: referenceLlaPath);
            await SaveUnsafeAsync(session, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkFailedAsync(Guid id, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            var session = await LoadAsync(id, CancellationToken.None);
            if (session == null || session.State != UploadSessionState.Finalizing) return;
            session = session.TransitionTo(UploadSessionState.Failed, timeProvider.GetUtcNow(), errorCode, errorMessage);
            await SaveUnsafeAsync(session, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>仅清理工作器调用：删除整个会话目录（分片、合并包、状态），不触碰已发布输入。</summary>
    public async Task DeleteSessionDirectoryAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            await DeleteSessionDirectoryUnsafeAsync(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IsExpired(UploadSession session, DateTimeOffset now) => now >= session.ExpiresAt;

    public long ExpectedPartSize(UploadSession session, int partNumber) =>
        partNumber < session.PartCount
            ? session.PartSizeBytes
            : session.SizeBytes - (long)(session.PartCount - 1) * session.PartSizeBytes;

    public string GetPartsDirectory(Guid id) => Path.Combine(GetSessionDirectory(id), "parts");

    public string GetPartPath(Guid id, int partNumber) =>
        Path.Combine(GetPartsDirectory(id), partNumber + ".part");

    private void ValidateCreateRequest(CreateUploadSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid,
                "idempotencyKey 必须为 1 到 200 个字符");
        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 260)
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid,
                "fileName 必须为 1 到 260 个字符");
        if (request.SizeBytes <= 0)
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid, "sizeBytes 必须大于 0");
        if (request.SizeBytes > uploadOptions.MaxArchiveBytes)
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid,
                $"sizeBytes 超过 {uploadOptions.MaxArchiveBytes} 字节上限");
        if (!IsSha256Hex(request.Sha256))
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid, "sha256 必须为 64 位十六进制");
        if (request.PartSizeBytes != PartSizeBytes)
            throw new UploadSessionException(UploadSessionErrorCodes.PartInvalid,
                $"partSizeBytes 固定为 {PartSizeBytes}");
    }

    private static int CheckedPartCount(long sizeBytes) =>
        (int)((sizeBytes + PartSizeBytes - 1) / PartSizeBytes);

    private static bool IsSha256Hex(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length == Sha256HexLength
        && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    private static bool IsSha256Equal(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private async Task<(long Written, string Sha256)> WritePartStreamingAsync(
        Stream source, string destinationPath, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) break;
            hasher.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total = checked(total + read);
        }
        return (total, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
    }

    private string GetSessionFile(Guid id) => Path.Combine(GetSessionDirectory(id), "session.json");

    private async Task SaveUnsafeAsync(UploadSession session, CancellationToken cancellationToken)
    {
        var target = GetSessionFile(session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(session, _jsonOptions);
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<UploadSession?> LoadFromFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<UploadSession>(stream, _jsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            // 半截/损坏的状态文件按不存在处理，由清理工作器按残留目录回收。
            return null;
        }
    }

    private async Task<IReadOnlyList<UploadSession>> ListUnsafeAsync(CancellationToken cancellationToken)
    {
        var sessions = new List<UploadSession>();
        if (!Directory.Exists(SessionsRoot)) return sessions;
        foreach (var directory in Directory.EnumerateDirectories(SessionsRoot))
        {
            var session = await LoadFromDirectoryAsync(directory, cancellationToken);
            if (session != null) sessions.Add(session);
        }
        return sessions.OrderByDescending(session => session.CreatedAt).ToArray();
    }

    private Task DeleteSessionDirectoryUnsafeAsync(Guid id)
    {
        var directory = GetSessionDirectory(id);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private static async Task<string?> ReadTextQuietlyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path) ? (await File.ReadAllTextAsync(path, cancellationToken)).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
