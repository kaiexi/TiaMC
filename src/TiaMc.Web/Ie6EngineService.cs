using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using TiaMc.App.Services;
using TiaMc.Core.Utils;

namespace TiaMc.Web;

/// <summary>
/// 「在启动器里放真 IE6 内核」的可插拔引擎槽。
///
/// 事实说明（为什么不能由本项目直接打包 IE6 内核）：
///   * IE6 的引擎是 2001 年的 mshtml.dll 6.0，属于 **Windows XP/2000 的系统组件**，
///     微软不提供再分发许可；
///   * 新版 Windows 上同名 mshtml.dll 已被 IE11 占用（IE 一个系统只能有一套），
///     IE6 的 DLL 依赖 XP 时代系统栈，无法在 Win10/11 上注册加载；
///   * 所以"把 IE6 内核塞进安装包"在技术上与许可上都不可行——**除非以独立引擎目录的形式提供**。
///
/// 本类做的是可行的那部分：**引擎槽**。只要把一份可运行的 IE6 引擎目录（例如
/// evolt.org 的 "IE6 standalone" 解压结果，含 iexplore.exe + mshtml.dll）放到下面任意位置，
/// 启动器就能检测到、读出它的引擎版本，并用它打开页面：
///
///     &lt;程序目录&gt;\ie6\          （推荐，跟着启动器走）
///     &lt;配置目录&gt;\ie6\
///     环境变量 TIAMC_IE6_DIR 指向的目录
///
/// 没有真引擎时，界面会明确说明区别，并提供另外两条可用路线：
///   1. 系统 IE 引擎（MSHTML/Trident）的 IE5 quirks 文档模式 ≈ IE6 排版；
///   2. 用 --host 0.0.0.0 把页面开到局域网，让另一台真 IE6 机器访问。
/// </summary>
internal sealed class Ie6EngineService
{
    public sealed record Engine(string Directory, string Executable, string Version, string Source);

    private readonly string _programDir = AppContext.BaseDirectory;
    private readonly string _configDir = AppConfig.ConfigDirectory;
    private Engine? _cached;

    /// <summary>候选目录（按优先级）。</summary>
    private IEnumerable<string> Candidates()
    {
        var configured = "";
        try
        {
            configured = AppConfig.Load().Ie6EnginePath ?? "";
        }
        catch (Exception)
        {
            // 读不到配置就跳过
        }

        if (configured.Length > 0) yield return configured;

        var env = Environment.GetEnvironmentVariable("TIAMC_IE6_DIR") ?? "";
        if (env.Length > 0) yield return env;
        yield return Path.Combine(_programDir, "ie6");
        yield return Path.Combine(_configDir, "ie6");
        yield return Path.Combine(_programDir, "ie6-standalone");
        yield return Path.Combine(_configDir, "engine", "ie6");
    }

    /// <summary>扫描真 IE6 引擎（iexplore.exe + mshtml.dll）。</summary>
    public Engine? Discover(bool refresh = false)
    {
        if (_cached is not null && !refresh) return _cached;

        foreach (var directory in Candidates())
        {
            try
            {
                if (!Directory.Exists(directory)) continue;

                var exe = Directory.GetFiles(directory, "iexplore.exe", SearchOption.AllDirectories).FirstOrDefault()
                          ?? Directory.GetFiles(directory, "IE6*.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe is null) continue;

                var mshtml = Directory.GetFiles(directory, "mshtml.dll", SearchOption.AllDirectories).FirstOrDefault();
                var shellVersion = FileVersion(exe);
                var version = mshtml is not null
                    ? FileVersion(mshtml)
                    : (shellVersion.Length > 0 ? shellVersion + "（仅外壳，缺 mshtml.dll 引擎）" : "未知");

                _cached = new Engine(directory, exe, version, mshtml is not null ? "本地引擎目录（含引擎）" : "本地引擎目录（仅外壳）");
                return _cached;
            }
            catch (Exception)
            {
                // 目录不可读就跳过
            }
        }

        _cached = null;
        return null;
    }

    private static string FileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileVersion ?? info.ProductVersion ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>当前进程是 32 位还是 64 位（32 位版会使用 SysWOW64 里的 32 位 MSHTML）。</summary>
    public static int ProcessBits => Environment.Is64BitProcess ? 64 : 32;

    /// <summary>本进程实际承载的 MSHTML 引擎 DLL（32 位进程走 SysWOW64）。</summary>
    public static string HostedEngineDll()
    {
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var path = Environment.Is64BitProcess
                ? Path.Combine(system, "mshtml.dll")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "mshtml.dll");
            return File.Exists(path) ? path : Path.Combine(system, "mshtml.dll");
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>宿主引擎的版本字符串。</summary>
    public static string HostedEngineVersion()
    {
        var path = HostedEngineDll();
        return path.Length > 0 && File.Exists(path) ? FileVersion(path) : "未知";
    }

    /// <summary>版本号是否属于 IE6（6.0.x）。</summary>
    public static bool IsIe6(string version) => version.StartsWith("6.", StringComparison.Ordinal);

    /// <summary>系统 IE 引擎（Trident）的版本，用于说明"这不是 IE6"。</summary>
    public static string SystemEngineVersion()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mshtml.dll");
            var version = File.Exists(path) ? FileVersion(path) : "";
            return version.Length > 0 ? version : "未知";
        }
        catch (Exception)
        {
            return "未知";
        }
    }

    public sealed record ActionResult(bool Ok, string Message, string Engine = "");

    /// <summary>ie6\ 目录里是否只有在线安装存根（ie6setup.exe）。</summary>
    public static bool SetupStubDetected()
    {
        try
        {
            foreach (var directory in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "ie6"),
                         Path.Combine(AppConfig.ConfigDirectory, "ie6")
                     })
            {
                if (Directory.Exists(directory) &&
                    Directory.GetFiles(directory, "ie6setup.exe", SearchOption.AllDirectories).Length > 0)
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return false;
    }

    /// <summary>用真 IE6 引擎打开页面。</summary>
    public ActionResult Launch(string url)
    {
        var engine = Discover(refresh: true);
        if (engine is null)
        {
            return new ActionResult(false,
                "没有找到真 IE6 引擎。把一份可运行的 IE6 引擎目录（含 iexplore.exe 与 mshtml.dll）放到 " +
                Path.Combine(_programDir, "ie6") + " 即可；本机系统 IE 引擎是 " + SystemEngineVersion() +
                "（不是 IE6）。也可点「打开引擎目录」查看说明。");
        }

        try
        {
            var arguments = url.Length > 0 ? $"\"{url}\"" : "";
            Process.Start(new ProcessStartInfo
            {
                FileName = engine.Executable,
                Arguments = arguments,
                WorkingDirectory = engine.Directory,
                UseShellExecute = true
            });

            LogService.User($"用真 IE6 引擎打开: {url}（引擎 {engine.Version} @ {engine.Directory}）", "IE6");
            return new ActionResult(true, $"已用真 IE6 引擎打开（版本 {engine.Version}）", engine.Executable);
        }
        catch (Exception e)
        {
            return new ActionResult(false, "启动 IE6 引擎失败: " + e.Message, engine.Executable);
        }
    }

    /// <summary>打开引擎目录（没有就创建并放入说明文件）。</summary>
    public ActionResult OpenEngineDirectory()
    {
        try
        {
            var directory = Path.Combine(_configDir, "ie6");
            AppPaths.EnsureDirectory(directory);

            var readme = Path.Combine(directory, "请把IE6引擎放在这里.txt");
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme,
                    "把一份可运行的 IE6 引擎解压到本目录（或其子目录），要求包含：\r\n" +
                    "  iexplore.exe        （IE6 主程序）\r\n" +
                    "  mshtml.dll          （IE6 渲染引擎，版本 6.0.x）\r\n" +
                    "\r\n" +
                    "常见来源：evolt.org 的 \"Internet Explorer 6 Standalone\" 归档包（第三方归档，自行确认许可）。\r\n" +
                    "\r\n" +
                    "为什么本程序不直接内置：IE6 引擎是 Windows XP/2000 的系统组件，微软不提供再分发许可，\r\n" +
                    "且新版 Windows 上的 mshtml.dll 已被 IE11 占用，IE6 的 DLL 无法在该系统上加载。\r\n" +
                    "\r\n" +
                    "放好之后重启 TiaMC-Web，界面「IE6 引擎」卡片会显示检测到的版本，并可一键用它打开页面。\r\n",
                    System.Text.Encoding.UTF8);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = false });
            return new ActionResult(true, "已打开引擎目录: " + directory, directory);
        }
        catch (Exception e)
        {
            return new ActionResult(false, "打开引擎目录失败: " + e.Message);
        }
    }

    /// <summary>WSL / Wine / 真 IE6 通道状态。</summary>
    private JsonObject WslStatus()
    {
        var state = DetectWsl();
        var commands = new JsonArray();
        foreach (var command in PrepareWslCommands(state.DefaultDistro)) commands.Add(command);

        return new JsonObject
        {
            ["wslPresent"] = state.WslPresent,
            ["distros"] = new JsonArray(state.Distros.Select(d => (JsonNode)d).ToArray()),
            ["defaultDistro"] = state.DefaultDistro,
            ["wineReady"] = state.WineReady,
            ["source"] = "参考 GitHub: oldweb-today/wine-browsers（ie6/run.sh：wine start /max /W IEXPLORE.exe $URL）",
            ["prepareCommands"] = commands
        };
    }

    /// <summary>给界面用的状态。</summary>
    public JsonObject Status()
    {
        var engine = Discover(refresh: true);
        return new JsonObject
        {
            ["engineFound"] = engine is not null,
            ["engineVersion"] = engine?.Version ?? "",
            ["enginePath"] = engine?.Executable ?? "",
            ["engineDirectory"] = engine?.Directory ?? Path.Combine(_programDir, "ie6"),
            ["configDirectory"] = Path.Combine(_configDir, "ie6"),
            ["systemEngine"] = SystemEngineVersion(),
            ["processBits"] = ProcessBits,
            ["hostedEngineDll"] = HostedEngineDll(),
            ["hostedEngineVersion"] = HostedEngineVersion(),
            ["hostedIsIe6"] = IsIe6(HostedEngineVersion()),
            ["setupStubDetected"] = SetupStubDetected(),
            ["setupStubHint"] =
                "如果你手上是 ie6setup.exe（472 KB、微软签名的在线安装存根）：它内部只有下载向导（ie6wzd.exe/wininet.dll/iesetup.inf），" +
                "不含 mshtml.dll 与 iexplore.exe，且微软下载服务器早已下线，无法提供引擎。" +
                "可插拔槽需要的是**可运行的引擎目录**（iexplore.exe + mshtml.dll 6.0），例如从已装好 IE6 的 XP 机器上复制整份 Internet Explorer 目录。",
            ["shellOnly"] = engine is not null && MissingEngineFiles().Count == EngineFiles.Length - 1,
            ["missingEngineFiles"] = new JsonArray(MissingEngineFiles().Select(f => (JsonNode)f).ToArray()),
            ["explanation"] =
                "真 IE6 内核是 XP/2000 的系统组件，无法由本程序打包（许可 + 新版系统不加载）。" +
                "把引擎目录放到 ie6\\ 即可被检测并直接使用；未放引擎时可用系统 IE 引擎的 IE5 quirks 模式（≈IE6 排版）。",
            ["wsl"] = WslStatus(),
            ["channels"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "真 IE6 引擎（可插拔）",
                    ["ready"] = engine is not null,
                    ["detail"] = engine is not null
                        ? $"已检测到：版本 {engine.Version}（{engine.Executable}）"
                        : $"未检测到：把含 iexplore.exe + mshtml.dll 的引擎目录放到 {Path.Combine(_programDir, "ie6")}"
                },
                new JsonObject
                {
                    ["name"] = "系统 IE 引擎（MSHTML/Trident，IE5 quirks ≈ IE6 排版）",
                    ["ready"] = true,
                    ["detail"] = "文档模式 5000（IE5 quirks）；本轮页面 /legacy 为 IE6 专用写法"
                },
                new JsonObject
                {
                    ["name"] = "WSL + Wine 里的真 IE6（GitHub: oldweb-today/wine-browsers）",
                    ["ready"] = Discover() is not null || DetectWsl().WineReady,
                    ["detail"] = DetectWsl() is { Distros.Count: > 0 } wsl
                        ? (wsl.WineReady
                            ? $"WSL({wsl.DefaultDistro}) 里已装 wine：可一键用真 IE6 打开"
                            : $"WSL({wsl.DefaultDistro}) 已安装，但还缺 wine + winetricks ie6（见准备命令）")
                        : "WSL 未安装发行版：管理员执行 wsl --install -d Ubuntu 后按准备命令装 wine 与真 IE6"
                },
                new JsonObject
                {
                    ["name"] = "局域网真 IE6 机器访问",
                    ["ready"] = true,
                    ["detail"] = "用 --host 0.0.0.0 启动，然后在 XP/2000 的 IE6 里打开 http://<本机IP>:<端口>/legacy"
                }
            }
        };
    }
    // ------------------------------------------------- WSL + Wine 里的真 IE6
    // 思路来自 GitHub 上的 oldweb-today/wine-browsers（ie6/ 子目录：Wine 前缀 + 真 IE6，
    // run.sh 里用 `wine start /max /W 'C:/Program Files/Internet Explorer/IEXPLORE.exe' $URL`）。
    // Windows 上 Wine 不能原生运行，但 Win11 的 WSL2 + WSLg 可以跑 Linux GUI 程序，
    // 因此在 WSL 里装 wine + winetricks（`winetricks ie6` 会安装真正的 IE6），
    // 启动器就能一键把 URL 交给那个真 IE6——不是虚拟机，也不是仿制引擎。

    public sealed record WslState(bool WslPresent, List<string> Distros, string DefaultDistro, bool WineReady);

    /// <summary>
    /// 找到 wsl.exe。注意：32 位进程访问 System32 会被重定向到 SysWOW64，而那里没有 wsl.exe，
    /// 所以显式尝试 System32 → Sysnative（32 位进程访问真实 System32 的别名）。
    /// </summary>
    public static string ResolveWsl()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var candidate in new[]
                 {
                     Path.Combine(windows, "System32", "wsl.exe"),
                     Path.Combine(windows, "Sysnative", "wsl.exe"),
                     Path.Combine(windows, "SysWOW64", "wsl.exe")
                 })
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // ignore
            }
        }

        return "";
    }

    /// <summary>检测 WSL、发行版列表与其中的 wine。</summary>
    private (DateTime At, WslState State)? _wslCache;

    public WslState DetectWsl()
    {
        if (_wslCache is { } cached && (DateTime.UtcNow - cached.At).TotalSeconds < 30) return cached.State;

        var distros = new List<string>();
        var wineReady = false;
        var wsl = ResolveWsl();
        if (wsl.Length == 0) return new WslState(false, distros, "", false);

        // 已安装的发行版直接读注册表：比 wsl -l 快，而且没有发行版时不会弹出安装提示等 60 秒
        try
        {
            using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is not null)
            {
                foreach (var keyName in lxss.GetSubKeyNames())
                {
                    using var sub = lxss.OpenSubKey(keyName);
                    var name = sub?.GetValue("DistributionName")?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) distros.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // 注册表读不到就当作没有发行版
        }

        var defaultDistro = distros.FirstOrDefault() ?? "";
        if (defaultDistro.Length > 0)
        {
            wineReady = ProbeWine(wsl, defaultDistro);
        }

        _wslCache = (DateTime.UtcNow, new WslState(true, distros, defaultDistro, wineReady));        return new WslState(true, distros, defaultDistro, wineReady);
    }

    /// <summary>在发行版里探测 wine（带超时，避免 wsl 卡住界面）。</summary>
    private static bool ProbeWine(string wsl, string distro)
    {
        try
        {
            var startInfo = new ProcessStartInfo(wsl,
                $"-d {distro} -- bash -lc \"command -v wine >/dev/null && echo WINE_OK || echo NO_WINE\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.Environment["WSL_UTF8"] = "1";

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(6000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // ignore
                }

                return false;
            }

            return output.Wait(1000) && output.Result.Contains("WINE_OK", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>在 WSL 里准备真 IE6 的命令（用户执行一次即可）。</summary>
    public List<string> PrepareWslCommands(string distro)
    {
        var name = distro.Length > 0 ? distro : "<发行版名>";
        return
        [
            "# 1) 以管理员身份安装 WSL 与 Ubuntu（装完需重启）",
            "wsl --install -d Ubuntu",
            "",
            "# 2) 进入 WSL，安装 wine 与 winetricks",
            $"wsl -d {name} -- bash -lc \"sudo dpkg --add-architecture i386 && sudo apt update && sudo apt install -y wine winetricks\"",
            "",
            "# 3) 用 winetricks 安装真正的 IE6（会从微软遗留镜像下载安装包）",
            $"wsl -d {name} -- bash -lc \"WINEARCH=win32 WINEPREFIX=$HOME/.wine-ie6 winetricks -q ie6\"",
            "",
            "# 4) 参考实现（GitHub：Wine 跑 IE6 的官方示例脚本）",
            "#    git clone https://github.com/oldweb-today/wine-browsers",
            "#    见 ie6/run.sh 与 base-wine-browser/Dockerfile（Wine 1.8 + winetricks）",
            "",
            "# 5) 准备完成后回到启动器点「用 WSL 真 IE6 打开」"
        ];
    }

    /// <summary>把 URL 交给 WSL 里的真 IE6。</summary>
    public ActionResult LaunchViaWsl(string url, string distro = "")
    {
        var state = DetectWsl();
        if (!state.WslPresent) return new ActionResult(false, "本机没有 wsl.exe：请先以管理员身份运行 wsl --install");
        if (state.Distros.Count == 0)
        {
            return new ActionResult(false,
                "WSL 还没有安装任何 Linux 发行版：请以管理员身份运行 wsl --install -d Ubuntu（装完重启），" +
                "然后按「准备命令」安装 wine 与真 IE6。");
        }

        var name = distro.Length > 0 ? distro : state.DefaultDistro;
        if (!state.WineReady)
        {
            return new ActionResult(false,
                $"发行版 {name} 里还没有 wine：请先执行准备命令（wine + winetricks ie6）。");
        }

        try
        {
            var wsl = ResolveWsl();
            var target = url.Length > 0 ? url : "about:blank";
            var script = "WINEPREFIX=$HOME/.wine-ie6 wine start /max /W " +
                         "'C:\\Program Files\\Internet Explorer\\IEXPLORE.exe' '" + target.Replace("'", "") + "'";

            var startInfo = new ProcessStartInfo(wsl, $"-d {name} -- bash -lc \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(startInfo);

            LogService.User($"用 WSL 里的真 IE6 打开: {target}（发行版 {name}）", "IE6");
            return new ActionResult(true, $"已交给 WSL({name}) 里的真 IE6 打开（首次启动较慢）");
        }
        catch (Exception e)
        {
            return new ActionResult(false, "启动 WSL 真 IE6 失败: " + e.Message);
        }
    }
    // ------------------------------------------------ IE6 兼容启动（Win11）
    // 结论（实测）：XP 的 IEXPLORE.EXE 6.00.2900.5512 在 Win11 上三种方式都秒退，
    // 退出码 0xC0000142 = STATUS_DLL_INIT_FAILED（DLL 初始化失败）：
    //   1) 直接启动           → 失败
    //   2) __COMPAT_LAYER=WINXPSP3（XP 兼容层） → 失败
    //   3) iexplore.exe.local（目录内 DLL 优先）→ 失败
    // 原因是它按名字绑定 mshtml/shdocvw/urlmon/wininet 等，Win11 上这些解析到 IE11 版本，
    // 接口不兼容；而且该目录只有外壳，没有 mshtml.dll 6.0 引擎。
    // 因此本项目提供"兼容启动"+ 结果解释 + 缺失文件清单，并把真 IE6 的正路指向 WSL+Wine。

    /// <summary>SP1 引擎目录所需的随行文件（XP 的 Internet Explorer 目录内容）。</summary>
    private static readonly string[] EngineFiles =
    [
        "mshtml.dll", "shdocvw.dll", "urlmon.dll", "wininet.dll", "browseui.dll",
        "inseng.dll", "mlang.dll", "cdfview.dll", "danim.dll", "dxtmsft.dll", "dxtrans.dll",
        "iedkcs32.dll", "iepeers.dll", "imgutil.dll", "occache.dll", "webcheck.dll",
        "iecont.dll", "msrating.dll", "hmmapi.dll", "iexplore.exe"
    ];

    /// <summary>目录里缺哪些引擎文件（只有 iexplore.exe 时就是"纯外壳"）。</summary>
    public List<string> MissingEngineFiles()
    {
        var engine = Discover();
        if (engine is null) return EngineFiles.ToList();

        var missing = new List<string>();
        foreach (var name in EngineFiles)
        {
            var found = Directory.GetFiles(engine.Directory, name, SearchOption.AllDirectories).Length > 0;
            if (!found) missing.Add(name);
        }

        return missing;
    }

    /// <summary>把退出码翻译成人话。</summary>
    public static string ExplainExitCode(int code) => code switch
    {
        0 => "正常退出",
        -1073741502 => "0xC0000142 STATUS_DLL_INIT_FAILED：DLL 初始化失败——IE6 外壳绑定到 Win11 上的 IE11 系统 DLL（mshtml/shdocvw/urlmon 等），接口不兼容；若目录里没有 mshtml.dll 6.0 引擎则更不可能初始化",
        -1073741515 => "0xC0000135 STATUS_DLL_NOT_FOUND：缺少依赖 DLL",
        -1073741819 => "0xC0000005 访问冲突",
        _ => $"退出码 {code}（0x{(uint)code:X8}）"
    };

    /// <summary>
    /// IE6 兼容启动：XP 兼容层（__COMPAT_LAYER）+ 目录内 DLL 重定向（*.local）+ 结果解释。
    /// 这是 Windows 上能做到的全部；实测仍会以 0xC0000142 退出，界面会如实显示原因与替代方案。
    /// </summary>
    public ActionResult CompatLaunch(string url)
    {
        var engine = Discover(refresh: true);
        if (engine is null) return new ActionResult(false, "没有找到 IE6 引擎目录（ie6\\ 或配置里的路径）");

        try
        {
            // 1) DLL 重定向标记：<exe>.local 让同目录 DLL 优先加载
            var local = engine.Executable + ".local";
            if (!File.Exists(local)) File.WriteAllText(local, "");

            // 2) XP 兼容层：通过当前进程环境传给子进程（UseShellExecute 不能直接设环境变量）
            var previous = Environment.GetEnvironmentVariable("__COMPAT_LAYER");
            Environment.SetEnvironmentVariable("__COMPAT_LAYER", "WINXPSP3");

            var startInfo = new ProcessStartInfo
            {
                FileName = engine.Executable,
                Arguments = url.Length > 0 ? $"\"{url}\"" : "about:blank",
                WorkingDirectory = engine.Directory,
                UseShellExecute = true
            };

            var missing = MissingEngineFiles();
            using var process = Process.Start(startInfo);
            if (process is null) return new ActionResult(false, "无法启动引擎进程");

            Environment.SetEnvironmentVariable("__COMPAT_LAYER", previous);

            var exited = process.WaitForExit(8000);
            if (!exited)
            {
                LogService.User($"IE6 兼容启动成功（仍在运行）: {url}", "IE6");
                return new ActionResult(true, $"IE6 已启动并保持运行（引擎 {engine.Version}）", engine.Executable);
            }

            var explanation = ExplainExitCode(process.ExitCode);
            var message = $"IE6 兼容启动失败：{explanation}。" +
                          (missing.Count > 0
                              ? $" 另外该目录缺少 {missing.Count} 个引擎文件（{string.Join(", ", missing.Take(8))}…），只有外壳无法渲染。"
                              : "") +
                          " 在 Win11 上运行真 IE6 的现实办法：WSL + Wine（winetricks ie6，本启动器有一键通道与准备命令），" +
                          "或在一台真实的 XP 机器上用局域网访问本页。";

            LogService.Warn(message, "IE6");
            return new ActionResult(false, message, engine.Executable);
        }
        catch (Exception e)
        {
            return new ActionResult(false, "IE6 兼容启动异常: " + e.Message);
        }
    }
}
