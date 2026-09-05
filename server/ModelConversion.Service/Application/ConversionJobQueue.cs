using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ModelConversion.Service.Application;

public sealed class ConversionJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, byte> _scheduled = new();

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!_scheduled.TryAdd(jobId, 0)) return ValueTask.CompletedTask;
        return _channel.Writer.WriteAsync(jobId, cancellationToken);
    }

    public async ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken)
    {
        var jobId = await _channel.Reader.ReadAsync(cancellationToken);
        _scheduled.TryRemove(jobId, out _);
        return jobId;
    }
}
