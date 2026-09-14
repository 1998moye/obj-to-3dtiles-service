using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ModelConversion.Service.Application;

// [2026-09-09 Issue01 可恢复上传会话] 完成请求只把会话转入 finalizing 并入队；
// 10GB 合并/校验/解压由后台工作器有界执行，HTTP 请求不占长连接。
public sealed class UploadSessionFinalizeQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, byte> _scheduled = new();

    public ValueTask EnqueueAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_scheduled.TryAdd(sessionId, 0)) return ValueTask.CompletedTask;
        return _channel.Writer.WriteAsync(sessionId, cancellationToken);
    }

    public async ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken)
    {
        var sessionId = await _channel.Reader.ReadAsync(cancellationToken);
        _scheduled.TryRemove(sessionId, out _);
        return sessionId;
    }
}
