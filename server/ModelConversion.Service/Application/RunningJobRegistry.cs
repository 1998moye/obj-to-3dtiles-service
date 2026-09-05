using System.Collections.Concurrent;

namespace ModelConversion.Service.Application;

public sealed class RunningJobRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokens = new();

    public IDisposable Register(Guid jobId, CancellationTokenSource tokenSource)
    {
        if (!_tokens.TryAdd(jobId, tokenSource))
            throw new InvalidOperationException($"任务已在运行: {jobId}");
        return new Registration(_tokens, jobId);
    }

    public bool Cancel(Guid jobId)
    {
        if (!_tokens.TryGetValue(jobId, out var source)) return false;
        source.Cancel();
        return true;
    }

    private sealed class Registration(ConcurrentDictionary<Guid, CancellationTokenSource> tokens, Guid jobId) : IDisposable
    {
        public void Dispose() => tokens.TryRemove(jobId, out _);
    }
}
