using System.Collections.Concurrent;

namespace ModelConversion.Service.Application;

// [2026-09-09 Issue01 结果读取租约] 进程内读租约注册表：每个结果响应进入时登记、响应结束释放；
// 终态清理只删除无活跃租约的作业。进程退出即终止所有在途响应，
// 因此租约不需要持久化，重启后天然不存在虚假租约。
public sealed class ResultReadLeaseRegistry
{
    private readonly ConcurrentDictionary<Guid, int> _activeCounts = new();

    public IDisposable Acquire(Guid jobId)
    {
        _activeCounts.AddOrUpdate(jobId, 1, (_, count) => count + 1);
        return new Lease(_activeCounts, jobId);
    }

    public bool HasActiveLease(Guid jobId) =>
        _activeCounts.TryGetValue(jobId, out var count) && count > 0;

    private sealed class Lease(ConcurrentDictionary<Guid, int> counts, Guid jobId) : IDisposable
    {
        public void Dispose()
        {
            counts.AddOrUpdate(jobId, 0, (_, count) => Math.Max(0, count - 1));
            counts.TryRemove(new KeyValuePair<Guid, int>(jobId, 0));
        }
    }
}
