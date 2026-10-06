using System.Diagnostics;
using System.Windows.Forms;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMc.App.Services;
using TiaMc.Core.Accounts;
using TiaMc.Core.Java;
using TiaMc.Core.Launch;
using TiaMc.Core.Integrity;
using TiaMc.Core.Minecraft;
using TiaMc.Core.Mods;
using TiaMc.Core.Utils;

namespace TiaMc.Web;

/// <summary>
/// TiaMC-Web: the same launch core as the desktop build, but the GUI is a browser
/// page. The process starts a tiny HTTP server on 127.0.0.1, serves the web UI from
/// wwwroot and exposes a JSON API that talks to <see cref="LaunchService"/>.
///
/// Flash: the web UI ships a Ruffle (open source Flash emulator) loader so SWF files
/// run inside a modern browser without the dead NPAPI plugin, and it can hand a SWF
/// to a standalone Flash projector (flashplayer_*.exe) when the user has one.
/// </summary>
internal static class Program
{
    private static LaunchService _launcher = null!;
    private static FlashService _flash = null!;
    private static Ie6EngineService _ie6 = null!;
    private static BrowserChannelService _browsers = null!;
    private static VmIe6Service _vmIe6 = null!;
    private static MiniblinkService _miniblink = null!;
    private static string _wwwroot = "";
    private static readonly DateTime Started = DateTime.Now;

    [STAThread]
    private static int Main(string[] args)
    {
        var noBrowser = args.Contains("--no-browser");
        // 默认打开内置 IE6 外观窗口；--browser=default 时改用系统默认浏览器
        var useSystemBrowser = args.Contains("--browser=default") || args.Contains("--edge");
        var noIe6 = args.Contains("--no-ie6");
        var quirks = args.Contains("--ie6-quirks");
        var legacyFirst = args.Contains("--legacy");
        var ie11Mode = args.Contains("--ie11-mode");
        var hostIndex = Array.IndexOf(args, "--host");
        var bindHost = hostIndex >= 0 && hostIndex + 1 < args.Length ? args[hostIndex + 1] : "127.0.0.1";
        var portable = args.Contains("--portable");
        var configIndex = Array.IndexOf(args, "--config");
        var configDir = configIndex >= 0 && configIndex + 1 < args.Length ? args[configIndex + 1] : "";
        var port = 32123;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed)) port = parsed;
        }

        var exitAfterIndex = Array.IndexOf(args, "--exit-after");
        var exitAfter = exitAfterIndex >= 0 && exitAfterIndex + 1 < args.Length &&
                        int.TryParse(args[exitAfterIndex + 1], out var seconds)
            ? seconds
            : 0;

        // --mc <目录>：和桌面版用同一个 Minecraft 根目录（由桌面版启动 Web 时自动传入）
        var mcIndex = Array.IndexOf(args, "--mc");
        var mcRoot = mcIndex >= 0 && mcIndex + 1 < args.Length ? args[mcIndex + 1] : "";

        try
        {
            if (portable && configDir.Length == 0)
            {
                configDir = Path.Combine(AppContext.BaseDirectory, "config");
            }

            if (configDir.Length > 0 && AppConfig.UseConfigDirectory(configDir))
            {
                LogService.Info($"配置目录: {AppConfig.ConfigDirectory}（--config）", "Web");
            }

            AppConfig.ApplyCacheRoot();
            LogService.InitializeLogFile(Path.Combine(AppConfig.ConfigDirectory, "logs"));

            _wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _flash = new FlashService();
            _ie6 = new Ie6EngineService();
            _browsers = new BrowserChannelService();
            _vmIe6 = new VmIe6Service();
            _miniblink = new MiniblinkService();

            LogService.Info("TiaMC-Web 启动中（浏览器 GUI 版）", "Web");
            var config = AppConfig.Load();

            // 桌面版启动 Web 时会带上 --mc，保证两边看到同一个 Minecraft 目录
            if (mcRoot.Length > 0)
            {
                config.MinecraftRoot = mcRoot;
                config.PortableRoot = false;   // 桌面版已经决定了根目录，Web 版照用
                config.Save();
                LogService.Info($"Minecraft 根目录由 --mc 指定: {mcRoot}", "Web");
            }

            _launcher = new LaunchService(config);
            _launcher.ReloadInstallation();

            LogService.Info($"Minecraft 目录: {_launcher.Paths.Root}（实例 {_launcher.Installed.Count} 个）", "Web");
            _launcher.DetectJava();
            LogService.Info($"Java 运行时: {_launcher.JavaRuntimes.Count} 个", "Web");

            var server = new WebServer(port, bindHost);
            Register(server);
            var actualPort = server.Start();

            // 把真实端口写进文件：桌面版/脚本据此找到 Web 界面，避免端口漂移导致"页面点不动"
            try
            {
                File.WriteAllText(Path.Combine(AppConfig.ConfigDirectory, "web-port.txt"),
                    actualPort.ToString() + Environment.NewLine + $"http://127.0.0.1:{actualPort}/" + Environment.NewLine);
            }
            catch (Exception portError)
            {
                LogService.Warn("写入 web-port.txt 失败: " + portError.Message, "Web");
            }

            LogService.Ok($"Web GUI 已就绪: http://127.0.0.1:{actualPort}/", "Web");
            LogService.Info("启动器 Web 界面 = 这个内嵌窗口 + 上面的本地地址（浏览器也能开）", "Web");
            LogService.Info($"IE6 兼容页: http://127.0.0.1:{actualPort}/legacy", "Web");
            if (bindHost != "127.0.0.1")
            {
                foreach (var address in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                             .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                {
                    LogService.Ok($"真 IE6 机器可访问: http://{address}:{actualPort}/legacy（IE6 专用页面）", "Web");
                }
            }

            if (!noBrowser)
            {
                if (useSystemBrowser || noIe6)
                {
                    OpenBrowser($"http://127.0.0.1:{actualPort}/");
                }
                else
                {
                    // 内置 IE 内核（JScript）跑不了现代页的 ES2017 语法，
                    // 所以：内嵌窗口显示 IE6 兼容页（ES3，功能完整），现代页交给系统浏览器。
                    OpenEmbeddedIe6($"http://127.0.0.1:{actualPort}/legacy", ie11Mode ? 11001 : 5000);
                    if (!legacyFirst) OpenBrowser($"http://127.0.0.1:{actualPort}/");
                }
            }

            if (exitAfter > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(exitAfter));
                LogService.Info("--exit-after 到期，退出", "Web");
                return 0;
            }

            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        catch (Exception e)
        {
            LogService.Error("TiaMC-Web 启动失败: " + e, "Web");
            return 1;
        }
    }

    /// <summary>启动内置的 IE6 外观浏览器窗口（系统 MSHTML 内核 + 仿 IE6 外壳）。</summary>
    private static void OpenEmbeddedIe6(string url, int emulationMode)
    {
        try
        {
            Ie6Window.ConfigureEmulation(emulationMode);
            LogService.Info($"打开内置 IE6 浏览器窗口（文档模式 {emulationMode}）: {url}", "Web");

            var thread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Ie6Window(url));
            })
            {
                IsBackground = true,
                Name = "ie6-window"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception e)
        {
            LogService.Warn("内置浏览器打开失败，改用系统浏览器：" + e.Message, "Web");
            OpenBrowser(url);
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false });
            }
            catch (Exception e)
            {
                LogService.Warn("打开浏览器失败，请手动访问 " + url + "：" + e.Message, "Web");
            }
        }
    }

    // ------------------------------------------------------------------ 路由

    private static void Register(WebServer server)
    {
        // ---- UI 静态文件 ----
        server.Map("GET", "/", _ => WebServer.File(Path.Combine(_wwwroot, "index.html"), "text/html; charset=utf-8"));
        server.Map("GET", "/app.css", _ => WebServer.File(Path.Combine(_wwwroot, "app.css"), "text/css; charset=utf-8"));
        server.Map("GET", "/app.js", _ => WebServer.File(Path.Combine(_wwwroot, "app.js"), "application/javascript; charset=utf-8"));

        // ---- Trident 4.0（IE4）档：服务端整页渲染，页面不需要任何 JavaScript ----
        server.Map("GET", "/ie4", request =>
        {
            var message = request.Query.TryGetValue("msg", out var msg) ? msg : "";
            return WebServer.Text(Ie4Actions.Render(Path.Combine(_wwwroot, "ie4.html"), _launcher, server.Port, message),
                "text/html; charset=utf-8");
        });
        server.Map("GET", "/ie4.html", request =>
        {
            var message = request.Query.TryGetValue("msg", out var msg) ? msg : "";
            return WebServer.Text(Ie4Actions.Render(Path.Combine(_wwwroot, "ie4.html"), _launcher, server.Port, message),
                "text/html; charset=utf-8");
        });
        server.Map("GET", "/ie4.css", _ => WebServer.File(Path.Combine(_wwwroot, "ie4.css"), "text/css; charset=utf-8"));
        server.Map("GET", "/ie4/do", request =>
        {
            var action = request.Query.TryGetValue("action", out var a) ? a : "refresh";
            var note = Ie4Actions.Run(action, request, _launcher, server.Port);

            // IE4 时代没有 302 之后的整页重载习惯，这里直接服务端渲染结果页（更接近当时的做法）
            return WebServer.Text(
                Ie4Actions.Render(Path.Combine(_wwwroot, "ie4.html"), _launcher, server.Port, note),
                "text/html; charset=utf-8");
        });

        // IE6 兼容页（ES3 + XMLHttpRequest，可在 IE5 quirks 模式下运行）
        server.Map("GET", "/legacy", _ => WebServer.File(Path.Combine(_wwwroot, "legacy.html"), "text/html; charset=utf-8"));
        server.Map("GET", "/legacy.html", _ => WebServer.File(Path.Combine(_wwwroot, "legacy.html"), "text/html; charset=utf-8"));
        server.Map("GET", "/legacy.js", _ => WebServer.File(Path.Combine(_wwwroot, "legacy.js"), "application/javascript; charset=utf-8"));
        server.Map("GET", "/ie6.css", _ => WebServer.File(Path.Combine(_wwwroot, "ie6.css"), "text/css; charset=utf-8"));
        server.Map("GET", "/favicon.ico", _ => WebServer.File(Path.Combine(_wwwroot, "favicon.ico"), "image/x-icon"));

        // 页面脚本完整执行的回执
        server.Map("GET", "/api/clientready", request =>
        {
            var charset = request.Query.TryGetValue("charset", out var cs) ? cs : "?";
            LogService.Ok("IE6 档脚本已完整执行（页面可用），文档编码: " + charset, "IE6 客户端");
            return WebServer.Text("ok", "text/plain; charset=utf-8");
        });

        // 浏览器端脚本错误上报（IE6 档页面里的 window.onerror 会打这里）
        server.Map("GET", "/api/clienterror", request =>
        {
            var message = request.Query.TryGetValue("msg", out var raw) ? raw : "(空)";
            LogService.Error("浏览器端脚本错误: " + message, "IE6 客户端");
            return WebServer.Text("ok", "text/plain; charset=utf-8");
        });

        // ---- 状态与日志 ----
        server.MapJson("GET", "/api/state", _ => State());
        server.MapJson("GET", "/api/logs", request =>
        {
            var since = request.Query.TryGetValue("since", out var raw) && int.TryParse(raw, out var n) ? n : 0;
            var entries = LogService.Snapshot();
            var slice = entries.Skip(Math.Max(0, since)).Take(500).ToList();
            return new JsonObject
            {
                ["total"] = entries.Count,
                ["next"] = since + slice.Count,
                ["lines"] = new JsonArray(slice.Select(e => (JsonNode)$"{e.TimeText} [{e.LevelText}] {e.Source}: {e.Message}")
                    .ToArray())
            };
        });

        // ---- 版本与实例 ----
        server.MapJson("GET", "/api/versions", _ =>
        {
            var installed = new JsonArray();
            foreach (var v in _launcher.Installed)
            {
                installed.Add(new JsonObject
                {
                    ["id"] = v.Id,
                    ["loader"] = v.Json.InheritsFrom ?? "vanilla",
                    ["javaMajor"] = JavaRuntimeInstaller.RequiredMajor(v.Json.JavaVersion?.MajorVersion, v.Id),
                    ["releaseTime"] = v.Json.ReleaseTime
                });
            }

            var remote = new JsonArray();
            if (_launcher.RemoteManifest is { } manifest)
            {
                foreach (var v in manifest.Versions.Take(2000))
                {
                    remote.Add(new JsonObject
                    {
                        ["id"] = v.Id,
                        ["type"] = v.Type,
                        ["releaseTime"] = v.ReleaseTime
                    });
                }
            }

            return new JsonObject
            {
                ["root"] = _launcher.Paths.Root,
                ["installed"] = installed,
                ["remote"] = remote,
                ["remoteTotal"] = _launcher.RemoteManifest?.Versions.Count ?? 0
            };
        });

        server.MapJson("POST", "/api/manifest/refresh", _ =>
        {
            var ok = _launcher.RefreshRemoteManifestAsync().GetAwaiter().GetResult();
            return new JsonObject { ["ok"] = ok, ["versions"] = _launcher.RemoteManifest?.Versions.Count ?? 0 };
        });

        server.MapJson("POST", "/api/instance/install", request =>
        {
            var id = request.Field("id");
            if (id.Length == 0) return new JsonObject { ["ok"] = false, ["message"] = "缺少版本 id" };

            var manifest = _launcher.RemoteManifest;
            var target = manifest?.Versions.FirstOrDefault(v => v.Id == id);
            if (target is null) return new JsonObject { ["ok"] = false, ["message"] = "在线清单里没有这个版本，先刷新清单" };

            var progress = new Progress<DownloadProgress>(p => LogService.Info(p.Summary, "Install"));
            var ok = _launcher.InstallVanillaAsync(target, progress).GetAwaiter().GetResult();
            if (ok) _launcher.ReloadInstallation();
            return new JsonObject { ["ok"] = ok, ["message"] = ok ? $"{id} 已安装" : $"{id} 安装失败", ["instances"] = _launcher.Installed.Count };
        });

        // 校验并补齐已安装实例的文件（库 / natives / 资源），页面上「校验并补齐」按钮用它
        server.MapJson("POST", "/api/instance/repair", request =>
        {
            var id = request.Field("id");
            if (id.Length == 0) id = _launcher.Config.ActiveInstance ?? "";
            var version = _launcher.Find(id);
            if (version is null) return new JsonObject { ["ok"] = false, ["message"] = "实例不存在: " + id };

            var check = _launcher.CheckFiles(version);
            var summary = check.Summary;
            if (check.IsComplete)
            {
                return new JsonObject { ["ok"] = true, ["message"] = $"{id} 文件完整，无需补齐（{summary}）", ["missing"] = 0 };
            }

            LogService.Info($"{id} 缺失 {check.Missing.Count} 个文件（{TextUtil.FormatBytes(check.MissingBytes)}），开始补齐…", "Repair");
            var progress = new Progress<DownloadProgress>(p => LogService.Info(p.Summary, "Repair"));
            var ok = _launcher.DownloadAllAsync(version, progress, CancellationToken.None).GetAwaiter().GetResult();
            var after = _launcher.CheckFiles(version);

            return new JsonObject
            {
                ["ok"] = ok && after.IsComplete,
                ["message"] = $"{(ok ? "补齐完成" : "补齐未完成")}：之前缺失 {check.Missing.Count} 个，现在 {(after.IsComplete ? "完整" : "还缺 " + after.Missing.Count + " 个")}",
                ["missing"] = after.Missing.Count,
                ["detail"] = summary + " → " + after.Summary
            };
        });

        // ---- MC 控制台：客户端输出 + 启动方案 + 退出原因（"哪里有问题"看这里）----
        server.MapJson("GET", "/api/gameconsole", request =>
        {
            var since = request.Query.TryGetValue("since", out var raw) && int.TryParse(raw, out var n) ? n : 0;
            var total = _launcher.GameLogCount;
            var slice = _launcher.GameLogSlice(since);
            var plan = _launcher.LastPlan;
            var exit = _launcher.LastExit;

            return new JsonObject
            {
                ["total"] = total,
                ["next"] = since + slice.Count,
                ["lines"] = new JsonArray(slice.Select(l => (JsonNode)l).ToArray()),
                ["running"] = _launcher.IsRunning,
                ["pid"] = _launcher.GameProcessId,
                ["exitCode"] = exit?.Code,
                ["exitReason"] = exit?.Reason ?? "",
                ["state"] = _launcher.State.ToString(),
                ["java"] = plan?.JavaPath ?? "",
                ["requiredJavaMajor"] = plan?.RequiredJavaMajor ?? 0,
                ["natives"] = plan?.NativesDirectory ?? "",
                ["gameDir"] = plan?.GameDirectory ?? "",
                ["classpathCount"] = plan is null ? 0 : plan.Classpath.Split(';', StringSplitOptions.RemoveEmptyEntries).Length,
                ["summary"] = plan?.Summary ?? "",
                ["command"] = plan is null ? "" : GameProcess.ToCommandLine(plan)
            };
        });

        // ---- 启动 / 停止 ----
        server.MapJson("POST", "/api/launch", request =>
        {
            var id = request.Field("instance");
            if (id.Length == 0) id = _launcher.Config.ActiveInstance ?? "";
            var version = _launcher.Installed.FirstOrDefault(v => v.Id == id);
            if (version is null) return new JsonObject { ["ok"] = false, ["message"] = $"实例不存在: {id}" };

            var plan = _launcher.BuildPlan(version, out var java, out var error);
            if (plan is null) return new JsonObject { ["ok"] = false, ["message"] = error ?? "无法生成启动方案" };

            var started = _launcher.Start(plan);
            return new JsonObject
            {
                ["ok"] = started,
                ["message"] = started ? $"{version.Id} 已启动（PID {_launcher.GameProcessId}）" : "启动失败",
                ["java"] = java?.Path ?? "",
                ["command"] = GameProcess.ToCommandLine(plan)
            };
        });

        server.MapJson("POST", "/api/stop", _ =>
        {
            _launcher.Stop();
            return new JsonObject { ["ok"] = true, ["message"] = "已请求结束游戏" };
        });

        // 一键关闭：先结束游戏，再关掉整个 Web GUI（服务 + 内嵌窗口 + 进程）
        server.MapJson("POST", "/api/shutdown", request =>
        {
            var gameWasRunning = _launcher.IsRunning;
            try
            {
                if (gameWasRunning) _launcher.Stop();
            }
            catch (Exception e)
            {
                LogService.Warn("结束游戏时出错: " + e.Message, "Web");
            }

            LogService.Ok(gameWasRunning ? "关闭客户端与界面（游戏已结束）" : "关闭界面", "Web");

            // 让响应先发出去，再退出进程
            _ = Task.Run(async () =>
            {
                await Task.Delay(600).ConfigureAwait(false);
                Environment.Exit(0);
            });

            return new JsonObject
            {
                ["ok"] = true,
                ["message"] = gameWasRunning ? "已结束游戏并关闭界面" : "已关闭界面"
            };
        });

        server.MapJson("POST", "/api/diagnose", _ =>
        {
            var analysis = _launcher.Diagnose(null, _launcher.Config.ResolveGameDirectory(_launcher.Config.ActiveInstance ?? ""));
            return new JsonObject
            {
                ["summary"] = analysis.Summary,
                ["severity"] = analysis.Worst.ToString(),
                ["hints"] = new JsonArray(analysis.Hints.Select(h => (JsonNode)h.ToString()).ToArray()),
                ["suspects"] = new JsonArray(analysis.SuspectMods.Select(h => (JsonNode)h).ToArray()),
                ["keywords"] = new JsonArray(analysis.Keywords.Select(h => (JsonNode)h).ToArray())
            };
        });

        // ---- Java ----
        server.MapJson("GET", "/api/java", _ =>
        {
            var list = new JsonArray();
            foreach (var j in _launcher.JavaRuntimes)
            {
                list.Add(new JsonObject { ["path"] = j.Path, ["major"] = j.MajorVersion, ["display"] = j.ShortDisplay });
            }

            return new JsonObject { ["runtimes"] = list, ["configPath"] = _launcher.Config.JavaPath ?? "" };
        });

        server.MapJson("POST", "/api/java/provision", request =>
        {
            var major = int.TryParse(request.Field("major"), out var m) ? m : 17;
            var runtimeRoot = Path.Combine(_launcher.Paths.Root, "runtime");
            var result = JavaRuntimeInstaller.InstallAsync(major, runtimeRoot,
                message => LogService.Info(message, "Java")).GetAwaiter().GetResult();
            if (result.Ok) _launcher.DetectJava();
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["path"] = result.JavaPath };
        });

        // ---- 账户 ----
        server.MapJson("GET", "/api/accounts", _ =>
        {
            var list = new JsonArray();
            foreach (var a in _launcher.Accounts.Accounts)
            {
                list.Add(new JsonObject
                {
                    ["name"] = a.Name,
                    ["kind"] = a.KindText,
                    ["uuid"] = a.Uuid,
                    ["key"] = a.Key,
                    ["server"] = a.AuthServerName ?? "",
                    ["selected"] = a.Key == _launcher.Accounts.Selected.Key
                });
            }

            return new JsonObject { ["accounts"] = list, ["selected"] = _launcher.Accounts.Selected.Key };
        });

        server.MapJson("POST", "/api/accounts/offline", request =>
        {
            var name = request.Field("name");
            if (name.Length == 0) return new JsonObject { ["ok"] = false, ["message"] = "请填写账户名" };

            var account = MinecraftAccount.CreateOffline(name);
            _launcher.Accounts.AddOrUpdate(account);
            _launcher.Accounts.Select(account);
            return new JsonObject { ["ok"] = true, ["message"] = $"已创建离线账户 {name}" };
        });

        server.MapJson("POST", "/api/accounts/select", request =>
        {
            var key = request.Field("key");
            var account = _launcher.Accounts.Accounts.FirstOrDefault(a => a.Key == key);
            if (account is null) return new JsonObject { ["ok"] = false, ["message"] = "账户不存在" };
            _launcher.Accounts.Select(account);
            return new JsonObject { ["ok"] = true, ["message"] = "已切换账户" };
        });

        // ---- 模组 ----
        server.MapJson("GET", "/api/mods", request =>
        {
            var id = request.Query.TryGetValue("instance", out var q) && q.Length > 0
                ? q
                : _launcher.Config.ActiveInstance ?? "";
            var dir = Path.Combine(_launcher.Paths.Root, "versions", id, "mods");
            var mods = ModsManager.Scan(dir);
            var list = new JsonArray();
            foreach (var mod in mods)
            {
                list.Add(new JsonObject
                {
                    ["name"] = mod.DisplayName,
                    ["version"] = mod.DisplayVersion,
                    ["loader"] = mod.LoaderText,
                    ["file"] = mod.FileName,
                    ["enabled"] = mod.Enabled,
                    ["size"] = mod.SizeText
                });
            }

            return new JsonObject { ["directory"] = dir, ["mods"] = list, ["count"] = mods.Count };
        });

        server.MapJson("POST", "/api/mods/toggle", request =>
        {
            var id = request.Field("instance").Length > 0 ? request.Field("instance") : _launcher.Config.ActiveInstance ?? "";
            var file = request.Field("file");
            var dir = Path.Combine(_launcher.Paths.Root, "versions", id, "mods");
            var path = Path.Combine(dir, file);
            if (!File.Exists(path) && !File.Exists(path + ".disabled"))
            {
                return new JsonObject { ["ok"] = false, ["message"] = "文件不存在: " + path };
            }

            var mods = ModsManager.Scan(Path.GetDirectoryName(path)!);
            var info = mods.FirstOrDefault(m => m.FileName.Equals(file, StringComparison.OrdinalIgnoreCase)
                                                || m.FileName.Equals(file + ".disabled", StringComparison.OrdinalIgnoreCase));
            if (info is null) return new JsonObject { ["ok"] = false, ["message"] = "没有扫描到该模组: " + file };
            ModsManager.SetEnabled(info, !info.Enabled, message => LogService.Info(message, "Mods"));
            var enabled = !info.Enabled;
            return new JsonObject { ["ok"] = true, ["message"] = enabled ? "已启用" : "已停用", ["enabled"] = enabled };
        });

        // ---- 设置 ----
        server.MapJson("GET", "/api/settings", _ => Settings());
        server.MapJson("POST", "/api/settings", request =>
        {
            var config = _launcher.Config;
            if (request.Field("instance").Length > 0) config.ActiveInstance = request.Field("instance");
            if (int.TryParse(request.Field("maxMemoryMb"), out var memory) && memory >= 512)
            {
                config.MaxMemoryMb = memory;
                config.MemoryAuto = false;
            }

            var gc = request.Field("gcMode");
            if (gc.Length > 0) config.GcMode = gc;

            if (request.Has("extraJvmArgs")) config.ExtraJvmArgs = request.Field("extraJvmArgs");
            if (request.Has("javaPath")) config.JavaPath = request.Field("javaPath");
            if (request.Has("flashProjectorPath")) config.FlashProjectorPath = request.Field("flashProjectorPath");
            if (request.Has("ie6EnginePath")) config.Ie6EnginePath = request.Field("ie6EnginePath");
            if (request.Has("vmPath")) config.VmPath = request.Field("vmPath");
            if (request.Has("vmGuestUser")) config.VmGuestUser = request.Field("vmGuestUser");
            if (request.Has("vmGuestPassword")) config.VmGuestPassword = request.Field("vmGuestPassword");
            if (request.Has("vmIe6Path")) config.VmIe6Path = request.Field("vmIe6Path");
            if (request.Has("miniblinkPath")) config.MiniblinkPath = request.Field("miniblinkPath");
            var rootChanged = false;
            if (request.Field("minecraftRoot").Length > 0 &&
                !string.Equals(config.MinecraftRoot, request.Field("minecraftRoot"), StringComparison.OrdinalIgnoreCase))
            {
                config.MinecraftRoot = request.Field("minecraftRoot");
                rootChanged = true;
            }

            config.Save();
            if (rootChanged) _launcher.ReloadInstallation();
            return new JsonObject
            {
                ["ok"] = true,
                ["message"] = "设置已保存",
                ["instances"] = _launcher.Installed.Count,
                ["root"] = _launcher.Paths.Root
            };
        });

        // ---- 真 IE6 引擎槽 ----
        server.MapJson("GET", "/api/ie6", _ => _ie6.Status());
        server.MapJson("POST", "/api/ie6/launch", request =>
        {
            var url = request.Field("url");
            if (url.Length == 0) url = $"http://127.0.0.1:{(server.Port)}/legacy";
            var result = _ie6.Launch(url);
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["engine"] = result.Engine };
        });
        server.MapJson("GET", "/api/ie6/wsl", _ =>
        {
            var state = _ie6.DetectWsl();
            var commands = new JsonArray();
            foreach (var command in _ie6.PrepareWslCommands(state.DefaultDistro)) commands.Add(command);
            return new JsonObject
            {
                ["wslPresent"] = state.WslPresent,
                ["distros"] = new JsonArray(state.Distros.Select(d => (JsonNode)d).ToArray()),
                ["defaultDistro"] = state.DefaultDistro,
                ["wineReady"] = state.WineReady,
                ["prepareCommands"] = commands
            };
        });

        server.MapJson("POST", "/api/ie6/wsl/launch", request =>
        {
            var url = request.Field("url");
            if (url.Length == 0) url = $"http://127.0.0.1:{server.Port}/legacy";
            var result = _ie6.LaunchViaWsl(url, request.Field("distro"));
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
        });

        server.MapJson("POST", "/api/ie6/extract", request =>
        {
            var image = request.Field("image");
            var result = _ie6.ExtractFromImage(image, request.Field("target"));
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["files"] = result.Files, ["directory"] = result.Directory };
        });

        server.MapJson("POST", "/api/ie6/compat-launch", request =>
        {
            var url = request.Field("url");
            if (url.Length == 0) url = $"http://127.0.0.1:{server.Port}/legacy";
            var result = _ie6.CompatLaunch(url);
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["engine"] = result.Engine };
        });

        server.MapJson("POST", "/api/ie6/open-directory", _ =>
        {
            var result = _ie6.OpenEngineDirectory();
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
        });

        // ---- 开源浏览器内核 miniblink（Apache-2.0）----
        server.MapJson("GET", "/api/miniblink", _ => _miniblink.Status());
        server.MapJson("POST", "/api/miniblink/ensure", _ =>
        {
            var result = _miniblink.EnsureAsync().GetAwaiter().GetResult();
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["detail"] = result.Detail };
        });
        server.MapJson("POST", "/api/miniblink/open", request =>
        {
            var url = request.Field("url");
            if (url.Length == 0) url = $"http://127.0.0.1:{server.Port}/legacy";
            var result = _miniblink.Open(url);
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["detail"] = result.Detail };
        });

        // ---- 原版 IE6：驱动 XP 虚拟机（不用 WSL）----
        server.MapJson("GET", "/api/vmie6", _ => _vmIe6.Status());
        server.MapJson("POST", "/api/vmie6/launch", request =>
        {
            var url = request.Field("url");
            if (url.Length == 0) url = $"http://127.0.0.1:{server.Port}/legacy";
            var result = _vmIe6.Launch(url);
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["detail"] = result.Detail };
        });
        server.MapJson("POST", "/api/vmie6/screen", _ =>
        {
            var result = _vmIe6.CaptureScreen();
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["file"] = result.Detail };
        });
        server.Map("GET", "/vm-screen.png", _ =>
            File.Exists(VmIe6Service.ScreenPath())
                ? WebServer.File(VmIe6Service.ScreenPath(), "image/png")
                : WebServer.Text("no screen", "text/plain", 404));

        // ---- 浏览器通道（世界之窗等 IE 外壳）----
        server.MapJson("GET", "/api/browsers", _ => _browsers.Status());
        server.MapJson("POST", "/api/browsers/launch", request =>
        {
            var id = request.Field("id");
            var url = request.Field("url");
            if (url.Length == 0) url = $"http://127.0.0.1:{server.Port}/legacy";
            var result = _browsers.Launch(id, url);
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["channel"] = result.Channel };
        });

        // ---- Flash ----
        server.MapJson("GET", "/api/flash", _ => _flash.Status());

        server.MapJson("POST", "/api/flash/ruffle", _ =>
        {
            var result = _flash.EnsureRuffleAsync().GetAwaiter().GetResult();
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["version"] = result.Version };
        });

        server.MapJson("POST", "/api/flash/upload", request =>
        {
            var name = request.Field("name");
            var data = request.BodyBase64();
            if (data.Length == 0) return new JsonObject { ["ok"] = false, ["message"] = "没有收到 SWF 数据" };
            var result = _flash.SaveSwf(name, data);
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["file"] = result.File };
        });

        server.MapJson("POST", "/api/flash/projector", _ =>
        {
            var result = _flash.OpenProjector();
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
        });

        server.MapJson("POST", "/api/flash/open", request =>
        {
            var target = request.Field("target");
            var result = _flash.Open(target);
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
        });

        // Ruffle 与本地 SWF 文件
        server.Map("GET", "/flash/ruffle.js", _ =>
        {
            var path = _flash.RuffleScriptPath;
            return File.Exists(path)
                ? WebServer.File(path, "application/javascript; charset=utf-8")
                : WebServer.Text("// Ruffle 尚未下载：请在 Flash 页点「获取 Ruffle」", "application/javascript");
        });
        server.Map("GET", "/flash/swf/", request =>
        {
            var name = request.Path["/flash/swf/".Length..];
            var path = _flash.ResolveSwf(name);
            return path is not null
                ? WebServer.File(path, "application/x-shockwave-flash")
                : WebServer.Text("not found", "text/plain", 404);
        });
        server.Map("GET", "/flash/ruffle/", request =>
        {
            var relative = request.Path["/flash/ruffle/".Length..];
            var path = Path.Combine(_flash.RuffleDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            var contentType = relative.EndsWith(".wasm") ? "application/wasm"
                : relative.EndsWith(".js") ? "application/javascript"
                : relative.EndsWith(".json") ? "application/json"
                : "application/octet-stream";
            return File.Exists(path) ? WebServer.File(path, contentType) : WebServer.Text("not found", "text/plain", 404);
        });
    }

    private static JsonObject State() => new()
    {
        ["launcher"] = "TiaMC-Web 1.0.0",
        ["startedAt"] = Started.ToString("yyyy-MM-dd HH:mm:ss"),
        ["state"] = _launcher.State.ToString(),
        ["running"] = _launcher.IsRunning,
        ["pid"] = _launcher.GameProcessId,
        ["root"] = _launcher.Paths.Root,
        ["activeInstance"] = _launcher.Config.ActiveInstance ?? "",
        ["instances"] = _launcher.Installed.Count,
        ["accounts"] = _launcher.Accounts.Accounts.Count,
        ["selectedAccount"] = _launcher.Accounts.Selected.Name,
        ["java"] = _launcher.JavaRuntimes.Count,
        ["remoteVersions"] = _launcher.RemoteManifest?.Versions.Count ?? 0,
        ["flash"] = _flash.Status(),
        ["browserChannels"] = JsonNode.Parse(_browsers.Status()["channels"]!.ToJsonString()),
        ["vmIe6"] = _vmIe6.Status(),
        ["miniblink"] = _miniblink.Status()
    };

    private static JsonObject Settings() => new()
    {
        ["minecraftRoot"] = _launcher.Config.MinecraftRoot ?? "",
        ["activeInstance"] = _launcher.Config.ActiveInstance ?? "",
        ["maxMemoryMb"] = _launcher.Config.MaxMemoryMb,
        ["minMemoryMb"] = _launcher.Config.MinMemoryMb,
        ["memoryAuto"] = _launcher.Config.MemoryAuto,
        ["recommendedMemoryMb"] = SystemInfo.RecommendedMaxMemoryMb,
        ["physicalMemoryMb"] = SystemInfo.TotalPhysicalMemoryMb,
        ["gcMode"] = _launcher.Config.GcMode,
        ["gcModes"] = new JsonArray("G1GC", "ShenandoahGC", "ZGC", "EpsilonGC", "OpenJ9GenCon", "ParallelGC", "SerialGC", "None"),
        ["extraJvmArgs"] = _launcher.Config.ExtraJvmArgs ?? "",
        ["javaPath"] = _launcher.Config.JavaPath ?? "",
        ["isolateInstances"] = _launcher.Config.IsolateInstances,
        ["flashProjectorPath"] = _launcher.Config.FlashProjectorPath ?? "",
        ["ie6EnginePath"] = _launcher.Config.Ie6EnginePath ?? "",
        ["vmPath"] = _launcher.Config.VmPath ?? "",
        ["vmGuestUser"] = _launcher.Config.VmGuestUser ?? "",
        ["vmHasPassword"] = !string.IsNullOrEmpty(_launcher.Config.VmGuestPassword),
        ["vmIe6Path"] = _launcher.Config.VmIe6Path ?? ""
    };
}
