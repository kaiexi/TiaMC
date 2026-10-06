using TiaMc.Core.Java;
using TiaMc.Core.Utils;

namespace TiaMc.Core.Launch;

/// <summary>
/// 启动前检查 Java 内存设置，把"游戏根本起不来"或"卡到没法玩"的配置提前挑出来。
///
/// 这三类问题是实际会踩的坑：
///   1. **32 位 Java 给了大堆**：32 位 JVM 最多只能要到约 1.5 GB，写成 -Xmx4096m 会直接报
///      "Could not reserve enough space for object heap"，游戏窗口一闪就没；
///   2. **-Xmx 大于物理内存**：JVM 要么启动失败，要么疯狂换页，比小内存还慢；
///   3. **-Xmx 太小**：原版都可能 OOM，模组包必崩。
/// </summary>
public static class MemoryAdvisor
{
    /// <summary>32 位 JVM 能安全申请的最大堆（MB）。</summary>
    public const int MaxHeapFor32Bit = 1024;

    /// <summary>原版 + 少量模组的最低可用堆（MB）。</summary>
    public const int MinSaneHeap = 1024;

    public sealed record Advice(int MaxMemoryMb, int MinMemoryMb, List<string> Warnings, List<string> Notes);

    /// <summary>
    /// 依据实际选中的 Java 与本机内存，给出可用的 Xms/Xmx 与提示。
    /// </summary>
    public static Advice Check(int requestedMaxMb, int requestedMinMb, JavaInfo? java, int physicalMemoryMb)
    {
        var warnings = new List<string>();
        var notes = new List<string>();

        var max = requestedMaxMb > 0 ? requestedMaxMb : 4096;
        var min = requestedMinMb > 0 ? requestedMinMb : Math.Min(512, max);

        // ① 32 位 Java 的上限
        if (java is { Is64Bit: false } && max > MaxHeapFor32Bit)
        {
            warnings.Add($"选中的是 32 位 Java（{java.ShortDisplay}），最多只能用 {MaxHeapFor32Bit} MB 堆；"
                         + $"已从 {max} MB 降到 {MaxHeapFor32Bit} MB（否则 JVM 会直接报 "
                         + "\"Could not reserve enough space for object heap\" 启动失败）。建议换成 64 位 Java。");
            max = MaxHeapFor32Bit;
        }

        // ② 不能超过物理内存（留 2 GB 给系统与显存映射）
        if (physicalMemoryMb > 0)
        {
            var ceiling = Math.Max(1024, physicalMemoryMb - 2048);
            if (max > ceiling)
            {
                warnings.Add($"-Xmx{max}m 超过本机可用范围（物理内存 {physicalMemoryMb} MB），已降到 {ceiling} MB；"
                             + "堆比内存还大时系统会不停换页，反而更卡。");
                max = ceiling;
            }
        }

        // ③ 太小的堆
        if (max < MinSaneHeap)
        {
            warnings.Add($"-Xmx{max}m 偏小，原版就可能内存不足崩溃，模组包几乎必崩；建议至少 {MinSaneHeap} MB。");
        }

        // ④ Xms ≤ Xmx（否则 JVM 直接启动失败）
        if (min > max)
        {
            notes.Add($"最小内存 {min} MB 大于最大内存 {max} MB，已按最大内存处理（Xms > Xmx 会让 JVM 启动失败）。");
            min = max;
        }

        return new Advice(max, min, warnings, notes);
    }

    /// <summary>检查结果写成一行摘要，便于日志与界面显示。</summary>
    public static string Summarize(Advice advice)
        => $"-Xms{advice.MinMemoryMb}m -Xmx{advice.MaxMemoryMb}m（本机 {SystemInfo.TotalPhysicalMemoryMb} MB）";
}
