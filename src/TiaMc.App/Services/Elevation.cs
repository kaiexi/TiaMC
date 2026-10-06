using System.Diagnostics;
using System.IO;

namespace TiaMc.App.Services;

/// <summary>
/// Administrator helpers. The launcher deliberately runs as a normal user (asInvoker):
/// forcing requireAdministrator would break drag and drop from Explorer and is not
/// needed to play. The few operations that do need elevation (purging the standby
/// list, clearing the file cache, binding the auth server to all interfaces) detect
/// the state and offer a one click elevated restart instead.
/// </summary>
public static class Elevation
{
    public static bool IsElevated => TiaMc.Core.Utils.MemoryTrimmer.IsElevated;

    /// <summary>Status line for the UI.</summary>
    public static string StatusText => IsElevated
        ? "管理员权限：已获得（可清理待机列表 / 文件缓存）"
        : "管理员权限：普通用户（清理待机列表、文件缓存需要管理员，可一键提权重启）";

    /// <summary>
    /// Restarts the launcher elevated through the UAC prompt. Returns false when the
    /// user declined or the current build has no executable path (in-process debug).
    /// </summary>
    public static bool RestartElevated(string? arguments = null)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            if (IsElevated) return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments ?? "",
                UseShellExecute = true,
                Verb = "runas",          // triggers the UAC prompt
                WorkingDirectory = AppContext.BaseDirectory
            };

            Process.Start(startInfo);
            LogService.User("以管理员身份重启启动器", "权限");
            return true;
        }
        catch (Exception e)
        {
            LogService.Warn("提权重启被取消或失败: " + e.Message, "权限");
            return false;
        }
    }
}