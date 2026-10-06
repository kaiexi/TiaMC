using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TiaMc.Core.Utils;

/// <summary>
/// Memory reclaim in the spirit of Mem Reduct (https://github.com/henrypp/memreduct):
/// it empties the working set of every process, can purge the standby page list and
/// can clear the system file cache. The last two need administrator rights; the
/// working set pass always works and usually returns the most memory.
/// </summary>
public static class MemoryTrimmer
{
    /// <summary>What to do while trimming.</summary>
    public sealed class Options
    {
        /// <summary>EmptyWorkingSet on every accessible process (Mem Reduct "working set").</summary>
        public bool EmptyWorkingSets { get; set; } = true;
        /// <summary>Purge the standby page list (needs admin, SeProfileSingleProcessPrivilege).</summary>
        public bool PurgeStandbyList { get; set; }
        /// <summary>Flush the modified page list (needs admin).</summary>
        public bool FlushModifiedList { get; set; }
        /// <summary>Clear the system file cache (needs admin, SeIncreaseQuotaPrivilege).</summary>
        public bool ClearFileCache { get; set; }
        /// <summary>Skip these process names (the launcher itself and the running game by default).</summary>
        public string[] SkipProcessNames { get; set; } = ["TiaMC", "java", "javaw", "Minecraft"];
    }

    public sealed class Result
    {
        public int BeforeAvailableMb { get; init; }
        public int AfterAvailableMb { get; init; }
        public int FreedMb => Math.Max(0, AfterAvailableMb - BeforeAvailableMb);
        public int ProcessesTrimmed { get; init; }
        public int ProcessesSkipped { get; init; }
        public bool WorkingSetsDone { get; init; }
        public bool StandbyPurged { get; init; }
        public bool FileCacheCleared { get; init; }
        public bool ModifiedListFlushed { get; init; }
        public string[] Notes { get; init; } = [];

        public string Summary =>
            $"回收前可用 {SystemInfo.FormatMb(BeforeAvailableMb)} → 回收后可用 {SystemInfo.FormatMb(AfterAvailableMb)}" +
            $"（释放 {SystemInfo.FormatMb(FreedMb)}）";
    }

    /// <summary>Runs the requested memory reclaim passes.</summary>
    public static Result Trim(Options? options = null, Action<string>? log = null)
    {
        options ??= new Options();
        var before = SystemInfo.AvailablePhysicalMemoryMb;
        var notes = new List<string>();
        var trimmed = 0;
        var skipped = 0;
        var workingSets = false;
        var standby = false;
        var fileCache = false;
        var modified = false;

        if (options.EmptyWorkingSets)
        {
            (trimmed, skipped) = EmptyAllWorkingSets(options.SkipProcessNames, log);
            workingSets = true;
        }

        // The kernel passes need privileges; enabling them fails without admin.
        var profilePrivilege = EnablePrivilege("SeProfileSingleProcessPrivilege", log);
        var quotaPrivilege = EnablePrivilege("SeIncreaseQuotaPrivilege", log);

        if (options.PurgeStandbyList)
        {
            if (profilePrivilege)
            {
                standby = SetMemoryList(MemoryPurgeStandbyList) == 0;
                if (!standby) notes.Add("清理待机列表失败");
            }
            else
            {
                notes.Add("清理待机列表需要管理员权限");
            }
        }

        if (options.FlushModifiedList)
        {
            if (profilePrivilege)
            {
                modified = SetMemoryList(MemoryFlushModifiedList) == 0;
                if (!modified) notes.Add("刷新已修改页面失败");
            }
            else
            {
                notes.Add("刷新已修改页面需要管理员权限");
            }
        }

        if (options.ClearFileCache)
        {
            if (quotaPrivilege)
            {
                fileCache = ClearSystemFileCache(out var message);
                if (!fileCache) notes.Add("清空文件缓存失败: " + message);
            }
            else
            {
                notes.Add("清空文件缓存需要管理员权限");
            }
        }

        // Let the system settle so the "after" number is meaningful.
        Thread.Sleep(400);

        return new Result
        {
            BeforeAvailableMb = before,
            AfterAvailableMb = SystemInfo.AvailablePhysicalMemoryMb,
            ProcessesTrimmed = trimmed,
            ProcessesSkipped = skipped,
            WorkingSetsDone = workingSets,
            StandbyPurged = standby,
            FileCacheCleared = fileCache,
            ModifiedListFlushed = modified,
            Notes = notes.ToArray()
        };
    }

    private static (int Trimmed, int Skipped) EmptyAllWorkingSets(string[] skip, Action<string>? log)
    {
        var trimmed = 0;
        var skipped = 0;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (skip.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
                    name.Contains("TiaMC", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                if (process.Id is 0 or 4)
                {
                    skipped++;
                    continue;
                }

                if (EmptyWorkingSet(process.Handle)) trimmed++;
                else skipped++;
            }
            catch (Exception)
            {
                // Access denied (system processes) or the process exited: not an error.
                skipped++;
            }
            finally
            {
                process.Dispose();
            }
        }

        log?.Invoke($"已清空 {trimmed} 个进程的工作集（跳过 {skipped} 个）");
        return (trimmed, skipped);
    }

    private static int SetMemoryList(int command)
    {
        var value = command;
        return NtSetSystemInformation(SystemMemoryListInformation, ref value, sizeof(int));
    }

    /// <summary>Clears the system file cache by shrinking the cache working set to nothing.</summary>
    private static bool ClearSystemFileCache(out string message)
    {
        message = "";

        var info = new FileCacheInformation
        {
            MinimumWorkingSet = ulong.MaxValue,
            MaximumWorkingSet = ulong.MaxValue
        };

        var size = Marshal.SizeOf<FileCacheInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            var status = NtSetSystemInformation(SystemFileCacheInfoClass, buffer, size);
            if (status != 0)
            {
                message = "NtSetSystemInformation 返回 0x" + status.ToString("X8");
                return false;
            }

            // Restore the default limits so the cache can grow again.
            info.MinimumWorkingSet = 0;
            info.MaximumWorkingSet = 0;
            Marshal.StructureToPtr(info, buffer, false);
            NtSetSystemInformation(SystemFileCacheInfoClass, buffer, size);
            return true;
        }
        catch (Exception e)
        {
            message = e.Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Enables one token privilege; returns false when not permitted.</summary>
    public static bool EnablePrivilege(string name, Action<string>? log = null)
    {
        var handle = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out handle)) return false;

            if (!LookupPrivilegeValue(null, name, out var luid)) return false;

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            if (!AdjustTokenPrivileges(handle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotAllAssigned)
            {
                log?.Invoke($"{name} 未授予（需要管理员权限）");
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    /// <summary>True when the current process runs elevated (needed for the kernel passes).</summary>
    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private const int SystemFileCacheInfoClass = 0x15;
    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCacheInformation
    {
        public ulong CurrentSize;
        public ulong PeakSize;
        public uint PageFaultCount;
        public ulong MinimumWorkingSet;
        public ulong MaximumWorkingSet;
        public ulong CurrentSizeIncludingTransitionInPages;
        public ulong PeakSizeIncludingTransitionInPages;
        public uint TransitionRePurposeCount;
        public uint Flags;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
