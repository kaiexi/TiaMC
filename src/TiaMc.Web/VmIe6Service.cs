using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using TiaMc.App.Services;
using TiaMc.Core.Utils;

namespace TiaMc.Web;

/// <summary>
/// 用**原版 IE6**：唯一存在原版 IE6 的地方是 Windows XP 虚拟机。
///
/// 本服务通过 VMware 的 guest operations（vmrun）驱动那台 XP：
///   1. 需要时启动虚拟机（带界面）；
///   2. 等 VMware Tools 就绪；
///   3. 在客户机里运行 `C:\Program Files\Internet Explorer\IEXPLORE.EXE <url>` —— 这就是原版 IE6；
///   4. 用 `vmrun captureScreen` 把客户机画面抓回本机，启动器的网页界面里直接显示，
///      等于把"XP 里的原版 IE6"嵌进了启动器（不用 WSL、不用新建虚拟机）。
///
/// 说明：不用 WSL、也不用泄露源码；IE6 就是客户机里那套微软原版文件。
/// </summary>
internal sealed class VmIe6Service
{
    private static readonly string[] VmrunCandidates =
    [
        @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe",
        @"C:\Program Files\VMware\VMware Workstation\vmrun.exe",
        @"C:\Program Files (x86)\VMware\VMware Player\vmrun.exe",
        @"C:\Program Files\VMware\VMware Player\vmrun.exe"
    ];

    public static string VmrunPath()
    {
        foreach (var candidate in VmrunCandidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return "";
    }

    public sealed record Vm(string Path, string Name, bool Running);

    /// <summary>找到 XP 虚拟机（配置优先，其次扫描常见目录）。</summary>
    public List<Vm> FindVms()
    {
        var result = new List<Vm>();
        var configured = "";
        try
        {
            configured = AppConfig.Load().VmPath ?? "";
        }
        catch (Exception)
        {
            // ignore
        }

        var roots = new List<string>();
        if (configured.Length > 0 && File.Exists(configured)) roots.Add(configured);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Virtual Machines"));

        var files = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                if (File.Exists(root)) files.Add(root);
                else if (Directory.Exists(root))
                {
                    files.AddRange(Directory.GetFiles(root, "*.vmx", SearchOption.AllDirectories)
                        .Where(f => !f.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
                                    f.Contains("XP", StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }

        var running = RunningVms();
        foreach (var file in files.Distinct())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            result.Add(new Vm(file, name, running.Any(r => string.Equals(r, file, StringComparison.OrdinalIgnoreCase))));
        }

        return result;
    }

    private static List<string> RunningVms()
    {
        var list = new List<string>();
        var vmrun = VmrunPath();
        if (vmrun.Length == 0) return list;

        try
        {
            var startInfo = new ProcessStartInfo(vmrun, "-T ws list")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return list;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            foreach (var line in output.Split('\n'))
            {
                var text = line.Trim();
                if (text.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase)) list.Add(text);
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return list;
    }

    public JsonObject Status()
    {
        var vmrun = VmrunPath();
        var vms = FindVms();
        string user = "", configuredVm = "";
        var hasPassword = false;
        try
        {
            var config = AppConfig.Load();
            user = config.VmGuestUser ?? "";
            configuredVm = config.VmPath ?? "";
            hasPassword = !string.IsNullOrEmpty(config.VmGuestPassword);
        }
        catch (Exception)
        {
            // ignore
        }

        var array = new JsonArray();
        foreach (var vm in vms)
        {
            array.Add(new JsonObject { ["path"] = vm.Path, ["name"] = vm.Name, ["running"] = vm.Running });
        }

        return new JsonObject
        {
            ["vmrunFound"] = vmrun.Length > 0,
            ["vmrun"] = vmrun,
            ["vms"] = array,
            ["running"] = vms.Count(v => v.Running),
            ["guestUser"] = user,
            ["hasPassword"] = hasPassword,
            ["configuredVm"] = configuredVm,
            ["screenFile"] = ScreenPath(),
            ["note"] =
                "原版 IE6 只存在于 XP 客户机里；本功能通过 VMware Tools 在客户机运行 IEXPLORE.EXE，并把客户机屏幕抓回启动器显示。" +
                "不使用 WSL，也不下载任何第三方内核。"
        };
    }

    public static string ScreenPath() => Path.Combine(AppConfig.ConfigDirectory, "vm-ie6-screen.png");

    public sealed record ActionResult(bool Ok, string Message, string Detail = "");

    private static (int ExitCode, string Output) Run(string arguments, int timeoutMs = 60000)
    {
        var vmrun = VmrunPath();
        if (vmrun.Length == 0) return (-1, "没有找到 vmrun.exe（需要安装 VMware Workstation/Player）");

        try
        {
            var startInfo = new ProcessStartInfo(vmrun, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return (-1, "无法启动 vmrun");

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // ignore
                }

                return (-1, "vmrun 超时: " + arguments);
            }

            return (process.ExitCode, output.Trim());
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }

    /// <summary>确保虚拟机在运行，并返回 vmrun 需要的路径。</summary>
    private ActionResult EnsureRunning(AppConfig config, out string vmx)
    {
        vmx = config.VmPath ?? "";
        var resolved = vmx;
        if (vmx.Length == 0 || !File.Exists(vmx))
        {
            var first = FindVms().FirstOrDefault();
            if (first is null) return new ActionResult(false, "没有找到 .vmx：请在界面里指定 XP 虚拟机路径");
            vmx = first.Path;
            config.VmPath = vmx;
            config.Save();
        }

        var currentVm = vmx;
        var running = RunningVms().Any(r => string.Equals(r, resolved, StringComparison.OrdinalIgnoreCase));
        if (!running)
        {
            var (code, output) = Run($"-T ws start \"{vmx}\" gui");
            if (code != 0 && !output.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                return new ActionResult(false, "启动虚拟机失败: " + output);
            }

            LogService.Info("正在启动 XP 虚拟机，等待 VMware Tools 就绪…", "IE6-VM");

            // 等 Tools 可用（最多 2 分钟）
            for (var i = 0; i < 12; i++)
            {
                Thread.Sleep(10000);
                var probe = Run($"-T ws -gu {config.VmGuestUser} -gp {config.VmGuestPassword} listProcessesInGuest \"{vmx}\"", 30000);
                if (probe.ExitCode == 0) break;
                if (probe.Output.Contains("Invalid user name", StringComparison.OrdinalIgnoreCase))
                {
                    return new ActionResult(false, "虚拟机已启动，但客户机用户名/密码不正确，请在界面里填写 XP 的账号密码");
                }
            }
        }

        return new ActionResult(true, "虚拟机运行中");
    }

    /// <summary>在 XP 客户机里用原版 IE6 打开 URL，并把客户机屏幕抓回来。</summary>
    public ActionResult Launch(string url, bool captureScreen = true)
    {
        var config = AppConfig.Load();
        if (string.IsNullOrWhiteSpace(config.VmGuestUser))
        {
            return new ActionResult(false, "还没有配置 XP 客户机的用户名/密码（界面「原版 IE6」卡片里填写）");
        }

        var ready = EnsureRunning(config, out var vmx);
        if (!ready.Ok) return ready;

        var target = url.Length > 0 ? url : "http://127.0.0.1/";
        var credentials = $"-gu {config.VmGuestUser} -gp {config.VmGuestPassword}";

        // 客户机里的 IE6 路径（XP 默认位置）
        var ie = config.VmIe6Path is { Length: > 0 }
            ? config.VmIe6Path
            : @"C:\Program Files\Internet Explorer\IEXPLORE.EXE";

        var (code, output) = Run(
            $"-T ws {credentials} runProgramInGuest \"{vmx}\" -interactive \"{ie}\" \"{target}\"", 60000);

        if (code != 0)
        {
            LogService.Warn($"在客户机里启动 IE6 失败: {output}", "IE6-VM");
            return new ActionResult(false, "在 XP 里启动 IE6 失败: " + output);
        }

        LogService.Ok($"已用 XP 客户机里的原版 IE6 打开: {target}", "IE6-VM");

        var detail = "";
        if (captureScreen)
        {
            var screen = ScreenPath();
            var (cc, cout) = Run($"-T ws captureScreen \"{vmx}\" \"{screen}\"", 60000);
            detail = cc == 0 && File.Exists(screen)
                ? "已抓取客户机屏幕: " + screen
                : "抓屏失败: " + cout;
        }

        // 顺便确认 IE6 进程真的在客户机里跑着
        var (pc, pout) = Run($"-T ws {credentials} listProcessesInGuest \"{vmx}\"", 60000);
        var ieRunning = pc == 0 && pout.Contains("IEXPLORE", StringComparison.OrdinalIgnoreCase);

        return new ActionResult(true,
            $"原版 IE6 已在 XP 客户机中打开该页面（客户机内 IEXPLORE 进程: {(ieRunning ? "运行中 ✓" : "未检测到")}）" +
            (detail.Length > 0 ? "；" + detail : ""), detail);
    }

    /// <summary>只抓一次客户机屏幕（界面里预览用）。</summary>
    public ActionResult CaptureScreen()
    {
        var config = AppConfig.Load();
        var vmx = config.VmPath ?? "";
        if (vmx.Length == 0 || !File.Exists(vmx))
        {
            var first = FindVms().FirstOrDefault();
            if (first is null) return new ActionResult(false, "没有找到虚拟机");
            vmx = first.Path;
        }

        var screen = ScreenPath();
        var (code, output) = Run($"-T ws captureScreen \"{vmx}\" \"{screen}\"", 60000);
        return code == 0 && File.Exists(screen)
            ? new ActionResult(true, "已抓取客户机屏幕", screen)
            : new ActionResult(false, "抓屏失败: " + output);
    }
}
