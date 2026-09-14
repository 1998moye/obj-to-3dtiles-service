using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Obj2Tiles.Library;

// [2026-09-07 任务级纹理缓存] 替代原静态全局 TexturesCache（ConcurrentDictionary 终身持有、
// 从不释放，是跨任务内存残留与单任务内存尖峰的来源）。
// 语义：按解码后字节数（W×H×4）预算的 LRU 缓存，归属一次转换运行；调用方用 Rent/Release
// 固定正在使用的条目，未被固定的条目在预算压力下按 LRU 淘汰并立即 Dispose。
// 解码在锁内进行：全进程同时最多一次解码（默认并发 1），用锁换确定性内存上界。
public sealed class TextureCache : IDisposable
{
    private sealed class Entry(Image<Rgba32> image, long bytes)
    {
        public readonly Image<Rgba32> Image = image;
        public readonly long Bytes = bytes;
        public int Pins;
        public LinkedListNode<string>? LruNode;
    }

    private readonly long _budgetBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new(); // 头部 = 最近使用，尾部 = 最久未用
    private long _currentBytes;
    private bool _disposed;

    public TextureCache(long budgetBytes)
    {
        if (budgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        _budgetBytes = budgetBytes;
    }

    public long CurrentBytes { get { lock (_gate) return _currentBytes; } }
    public long PeakBytes { get; private set; }
    public long LoadCount { get; private set; }
    public long EvictionCount { get; private set; }

    // 取出（或解码并缓存）一张纹理并固定它；调用方必须配对 Release。
    public Image<Rgba32> Rent(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_entries.TryGetValue(path, out var existing))
            {
                existing.Pins++;
                // 命中即提到 LRU 头部。
                if (existing.LruNode != null)
                {
                    _lru.Remove(existing.LruNode);
                    _lru.AddFirst(existing.LruNode);
                }
                return existing.Image;
            }

            var image = Image.Load<Rgba32>(path);
            var bytes = (long)image.Width * image.Height * 4;
            var entry = new Entry(image, bytes) { Pins = 1 };
            _entries.Add(path, entry);
            _currentBytes += bytes;
            LoadCount++;
            // 峰值在淘汰前记录：装载瞬间（含待淘汰条目）才是真实内存高点，
            // 淘汰后的占用只能反映稳态，会低估内存压力。
            if (_currentBytes > PeakBytes) PeakBytes = _currentBytes;

            // 预算内放不下时从尾部淘汰未固定的条目；正在使用的条目不可淘汰，
            // 单张超预算的工作纹理允许超支（否则任务无法进行），由统计与日志呈现。
            while (_budgetBytes > 0 && _currentBytes > _budgetBytes && TryEvictTail(out _)) { }

            entry.LruNode = _lru.AddFirst(path);
            return image;
        }
    }

    public void Release(string path)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var entry) && entry.Pins > 0)
                entry.Pins--;
        }
    }

    private bool TryEvictTail(out string path)
    {
        path = string.Empty;
        var node = _lru.Last;
        while (node != null)
        {
            var entry = _entries[node.Value];
            if (entry.Pins == 0)
            {
                path = node.Value;
                _lru.Remove(node);
                _entries.Remove(path);
                _currentBytes -= entry.Bytes;
                entry.Image.Dispose();
                EvictionCount++;
                return true;
            }
            node = node.Previous;
        }
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values)
                entry.Image.Dispose();
            _entries.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }
}
