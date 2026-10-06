using System.Runtime.InteropServices;

namespace TiaMc.Core.Utils;

/// <summary>
/// Machine facts the launcher adapts to. Every machine is different, so memory
/// defaults and slider limits are read from the real hardware instead of being
/// hard coded.
/// </summary>
public static class SystemInfo
{
    /// <summary>Total physical memory in MB (0 when it cannot be determined).</summary>
    public static int TotalPhysicalMemoryMb
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var status = new MemoryStatusEx();
                    if (GlobalMemoryStatusEx(status)) return (int)(status.TotalPhys / (1024 * 1024));
                }
                catch (Exception)
                {
                    // fall through to the managed estimate
                }
            }

            // Managed fallback: the GC limit is a good approximation of usable memory.
            try
            {
                var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (available > 0) return (int)(available / (1024 * 1024));
            }
            catch (Exception)
            {
                // unknown
            }

            return 0;
        }
    }

    /// <summary>Currently available (free) physical memory in MB.</summary>
    public static int AvailablePhysicalMemoryMb
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return 0;
            try
            {
                var status = new MemoryStatusEx();
                return GlobalMemoryStatusEx(status) ? (int)(status.AvailPhys / (1024 * 1024)) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>Logical processor count (used for the default GC/thread hints).</summary>
    public static int ProcessorCount => Environment.ProcessorCount;

    /// <summary>
    /// Recommended -Xmx for this machine: leave room for the system, cap at 3/4 of
    /// the physical memory, never below 1 GB, rounded down to 512 MB steps.
    /// </summary>
    public static int RecommendedMaxMemoryMb
    {
        get
        {
            var total = TotalPhysicalMemoryMb;
            if (total <= 0) return 4096;

            // 参考主流启动器的默认值：大约一半内存，并**封顶 8 GB**。
            // 以前是 min(内存-2G, 75%)，32 GB 的机器会给到 23.5 GB —— 堆越大 GC 停顿越长
            // （几秒级的全堆回收），游戏反而更卡；需要更大的用户在实例设置里自己调即可。
            var target = Math.Min(total / 2, 8192);
            if (target < 1024) target = Math.Max(1024, total / 2);
            return Math.Max(1024, target / 512 * 512);
        }
    }

    /// <summary>Recommended -Xms: a quarter of the recommended maximum, 512 MB steps.</summary>
    public static int RecommendedMinMemoryMb
    {
        get
        {
            var max = RecommendedMaxMemoryMb;
            return Math.Max(512, max / 4 / 512 * 512);
        }
    }

    /// <summary>Upper bound for the -Xmx slider: the physical memory of this machine.</summary>
    public static int MemoryLimitMb
    {
        get
        {
            var total = TotalPhysicalMemoryMb;
            return total > 0 ? total : 32768;
        }
    }

    public static string FormatMb(int megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024.0:0.#} GB" : $"{megabytes} MB";

    /// <summary>Human readable summary such as "物理内存 31.9 GB · 可用 12.4 GB".</summary>
    public static string MemorySummary()
    {
        var total = TotalPhysicalMemoryMb;
        if (total <= 0) return "无法读取物理内存";

        var available = AvailablePhysicalMemoryMb;
        return available > 0
            ? $"物理内存 {FormatMb(total)} · 当前可用 {FormatMb(available)}"
            : $"物理内存 {FormatMb(total)}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}
