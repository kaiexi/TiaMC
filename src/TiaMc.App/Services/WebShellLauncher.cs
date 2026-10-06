using System.Diagnostics;
using System.IO;

namespace TiaMc.App.Services;

/// <summary>
/// 把 Web 界面当作启动器来启动。
///
/// 发布目录里 `TiaMC.exe`（WPF 版）和 `TiaMC-Web.exe`（Web 版）放在一起；
/// 本类负责在需要时把 Web 版拉起来：
///   * `TiaMC.exe --web`         → 直接启动 Web 版并退出自身（Web 界面就是启动器）
///   * 主界面的「Web 界面」按钮    → 同上
///   * 配置 `webUiFirst = true`   → 每次启动都直接进 Web 界面
///
/// Web 版自己是"启动器 + HTTP 服务 + 内嵌浏览器窗口"：默认打开概览页，
/// 并把地址打印到日志里（也可用浏览器访问）。
/// </summary>
internal static class WebShellLauncher
{
    /// <summary>Web 版可执行文件名（发布目录里与主程序同级）。</summary>
    public const string ExecutableName = "TiaMC-Web.exe";

    public static string Path()
    {
        var directory = AppContext.BaseDirectory;
        var candidate = System.IO.Path.Combine(directory, ExecutableName);
        if (File.Exists(candidate)) return candidate;

        // 开发目录（bin\Debug\net10.0-windows\...）里往上找一次
        var parent = Directory.GetParent(directory)?.Parent?.Parent?.FullName;
        if (parent is not null)
        {
            var up = System.IO.Path.Combine(parent, "TiaMc.Web", "bin", "Release", "net10.0-windows", ExecutableName);
            if (File.Exists(up)) return up;
        }

        return candidate;
    }

    public static bool Available => File.Exists(Path());

    /// <summary>清掉上一轮遗留的 Web 实例：它们占着端口会让新实例退到随机端口，页面就"点不动"。</summary>
    private static void KillStaleInstances()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("TiaMC-Web"))
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    LogService.Info($"已结束旧的 Web 实例 PID={process.Id}", "Web");
                }
                catch (Exception)
                {
                    // 结束不了就交给端口探测兜底
                }
            }
        }
        catch (Exception)
        {
            // ignore
        }
    }

    /// <summary>启动 Web 版（返回是否成功以及说明）。</summary>
    public static (bool Ok, string Message) Start()
    {
        var exe = Path();
        if (!File.Exists(exe))
        {
            return (false,
                $"没有找到 {ExecutableName}：请把 Web 版和主程序放在同一个目录（发布包里两者默认在一起）");
        }

        KillStaleInstances();

        try
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
                UseShellExecute = true
            };

            // 便携/演示配置：主程序如果用了自定义配置目录，Web 版跟着用同一个
            var configDirectory = AppConfig.ConfigDirectory;
            if (configDirectory.Length > 0)
            {
                startInfo.ArgumentList.Add("--config");
                startInfo.ArgumentList.Add(configDirectory);
            }

            // 把当前配置目录与 MC 根目录一起交给 Web 版，保证它看到的实例和桌面版一致
            try
            {
                var mcRoot = AppConfig.Load().MinecraftRoot;
                if (!string.IsNullOrWhiteSpace(mcRoot))
                {
                    startInfo.ArgumentList.Add("--mc");
                    startInfo.ArgumentList.Add(mcRoot);
                }
            }
            catch (Exception)
            {
                // 配置读不到就不传
            }

            Process.Start(startInfo);
            LogService.User($"已启动 Web 界面: {exe}", "Web");
            return (true, "Web 界面已启动（浏览器窗口 + http://127.0.0.1:端口/）");
        }
        catch (Exception e)
        {
            LogService.Error($"启动 Web 界面失败: {e.Message}", "Web");
            return (false, "启动 Web 界面失败: " + e.Message);
        }
    }
}
