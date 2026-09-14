using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Obj2Tiles.Library;

// [2026-09-07 有界图集保存队列] 替代原 TrimTextures 的无界 LongRunning 任务列表：
// 信号量限制并发编码数，槽位不足时阻塞调用方形成反压，图集在保存完成后立即 Dispose。
// 编码异常通过 WaitAll 原样传播（与 Task.WhenAll 一致），且已入队任务都会执行完毕并释放。
public sealed class BoundedAtlasSaver : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly List<Task> _pending = new();
    private int _pendingCount;

    public BoundedAtlasSaver(int maxConcurrency)
    {
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    public int PendingCount => Interlocked.CompareExchange(ref _pendingCount, 0, 0);

    // 入队即占用槽位：并发满时阻塞调用方（反压），图集像素内存随任务结束释放。
    public void Enqueue(Image<Rgba32> atlas, string path, Action<Image, string> save)
    {
        _slots.Wait();
        Interlocked.Increment(ref _pendingCount);
        _pending.Add(Task.Run(() =>
        {
            try
            {
                save(atlas, path);
            }
            finally
            {
                atlas.Dispose();
                Interlocked.Decrement(ref _pendingCount);
                _slots.Release();
            }
        }));
    }

    public void WaitAll()
    {
        Task.WaitAll(_pending.ToArray());
    }

    public void Dispose()
    {
        try
        {
            // 异常展开路径上的兜底等待：保证不遗留后台任务与文件句柄；
            // 编码异常已由调用方的 WaitAll 传播，这里不掩盖原始异常。
            WaitAll();
        }
        catch (Exception)
        {
            // 忽略二次传播：编码失败已由主路径抛出。
        }
        _slots.Dispose();
    }
}
