using System.Globalization;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Infrastructure.Resources;

public interface IRuntimeResourceProbe
{
    // 任务开始时取一次快照并固化，转换中途不重新解释配置。
    RuntimeResourceSnapshot Snapshot(string tempPath);

    // 运行中内存用量采样（看门狗用）；取不到容器口径时返回 null，由调用方回退。
    long? TryReadCurrentMemoryBytes();
}

// 容器资源探测：优先直读 cgroup v2/v1 限制，缺失容器限制时回退到进程可见物理内存。
// 不读 /proc/meminfo 的宿主机总量，避免 Docker 限制被绕过。
public sealed class RuntimeResourceProbe(string? cgroupRoot = null) : IRuntimeResourceProbe
{
    // v1 用极大哨兵值表示“无限制”（通常是 2^63 对齐页大小），达到该量级视同无限制。
    private const long CgroupV1UnlimitedSentinel = 1L << 60;

    private readonly string _cgroupRoot = cgroupRoot ?? "/sys/fs/cgroup";

    public RuntimeResourceSnapshot Snapshot(string tempPath)
    {
        var (limit, current, source) = ReadMemory();
        var cores = ReadCpuCores();
        var diskFree = ReadDiskFreeBytes(tempPath);
        return new RuntimeResourceSnapshot(limit, current, source, cores, diskFree,
            tempPath, DateTimeOffset.UtcNow);
    }

    public long? TryReadCurrentMemoryBytes()
    {
        if (TryReadCgroupCurrent(out var current))
            return current;
        // 非容器回退：GC 信息里的系统内存占用（Linux 下本身就读 cgroup）。
        var info = GC.GetGCMemoryInfo();
        return info.MemoryLoadBytes > 0 ? info.MemoryLoadBytes : null;
    }

    private (long Limit, long? Current, MemoryLimitSource Source) ReadMemory()
    {
        // cgroup v2：memory.max 存在即判定为 v2 层次结构。
        var v2Max = Path.Combine(_cgroupRoot, "memory.max");
        if (File.Exists(v2Max))
        {
            var raw = ReadText(v2Max);
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0)
                return (limit, TryReadCgroupCurrent(out var current) ? current : null, MemoryLimitSource.CgroupV2);
            // "max" 或非法值：容器未设限，落到进程可见内存。
            return (ProcessFallbackLimit(), ProcessFallbackCurrent(), MemoryLimitSource.ProcessFallback);
        }

        // cgroup v1：memory/memory.limit_in_bytes。
        var v1Limit = Path.Combine(_cgroupRoot, "memory", "memory.limit_in_bytes");
        if (File.Exists(v1Limit))
        {
            var raw = ReadText(v1Limit);
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
                && limit > 0 && limit < CgroupV1UnlimitedSentinel)
            {
                TryReadCgroupCurrent(out var current);
                return (limit, current, MemoryLimitSource.CgroupV1);
            }
            return (ProcessFallbackLimit(), ProcessFallbackCurrent(), MemoryLimitSource.ProcessFallback);
        }

        return (ProcessFallbackLimit(), ProcessFallbackCurrent(), MemoryLimitSource.ProcessFallback);
    }

    private bool TryReadCgroupCurrent(out long current)
    {
        var v2 = Path.Combine(_cgroupRoot, "memory.current");
        if (File.Exists(v2) && long.TryParse(ReadText(v2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v2Value))
        {
            current = v2Value;
            return true;
        }
        var v1 = Path.Combine(_cgroupRoot, "memory", "memory.usage_in_bytes");
        if (File.Exists(v1) && long.TryParse(ReadText(v1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v1Value))
        {
            current = v1Value;
            return true;
        }
        current = 0;
        return false;
    }

    private double ReadCpuCores()
    {
        // cgroup v2：cpu.max = "<quota|max> <period>"，如 "200000 100000" 表示 2 核。
        var v2 = Path.Combine(_cgroupRoot, "cpu.max");
        if (File.Exists(v2))
        {
            var parts = (ReadText(v2) ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var period) && period > 0
                && !string.Equals(parts[0], "max", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quota) && quota > 0)
                return Math.Max(0.5, (double)quota / period);
        }

        // cgroup v1：cpu.cfs_quota_us / cpu.cfs_period_us；-1 表示无限制。
        var v1Quota = Path.Combine(_cgroupRoot, "cpu", "cpu.cfs_quota_us");
        var v1Period = Path.Combine(_cgroupRoot, "cpu", "cpu.cfs_period_us");
        if (File.Exists(v1Quota) && File.Exists(v1Period)
            && long.TryParse(ReadText(v1Quota), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) && q > 0
            && long.TryParse(ReadText(v1Period), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0)
            return Math.Max(0.5, (double)q / p);

        return Environment.ProcessorCount;
    }

    private static long ProcessFallbackLimit()
    {
        // GC 堆可用的总内存：Linux 容器下运行时已计入 cgroup，非容器即物理内存口径。
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes : 1L << 30;
    }

    private static long? ProcessFallbackCurrent()
    {
        // 与上限同一口径的当前用量（系统物理内存占用；Linux 下运行时已计入 cgroup）。
        var info = GC.GetGCMemoryInfo();
        return info.MemoryLoadBytes > 0 ? info.MemoryLoadBytes : null;
    }

    private static long ReadDiskFreeBytes(string tempPath)
    {
        // 暂存目录可能尚未创建，向上找第一个已存在的祖先再取卷信息。
        var full = Path.GetFullPath(tempPath);
        var probe = full;
        while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
            probe = Path.GetDirectoryName(probe);
        if (string.IsNullOrEmpty(probe))
            return 0;
        try
        {
            return new DriveInfo(Path.GetPathRoot(probe)!).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // 某些挂载点 DriveInfo 不可用（如伪文件系统），视为未知，避免误拒绝。
            return long.MaxValue;
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
