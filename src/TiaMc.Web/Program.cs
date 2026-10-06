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

            LogService.Info("TiaMC-Web 启动中（浏览器 GUI 版）", "Web");
            var config = AppConfig.Load();
            _launcher = new LaunchService(config);
            _launcher.ReloadInstallation();

            LogService.Info($"Minecraft 目录: {_launcher.Paths.Root}（实例 {_launcher.Installed.Count} 个）", "Web");
            _launcher.DetectJava();
            LogService.Info($"Java 运行时: {_launcher.JavaRuntimes.Count} 个", "Web");

            var server = new WebServer(port, bindHost);
            Register(server);
            var actualPort = server.Start();
            LogService.Ok($"Web GUI 已就绪: http://127.0.0.1:{actualPort}/", "Web");
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
                    // 内置 IE6 打开兼容页；系统浏览器打开现代页
                    OpenEmbeddedIe6($"http://127.0.0.1:{actualPort}/legacy", ie11Mode ? 11001 : 5000);
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
        // IE6 兼容页（ES3 + XMLHttpRequest，可在 IE5 quirks 模式下运行）
        server.Map("GET", "/legacy", _ => WebServer.File(Path.Combine(_wwwroot, "legacy.html"), "text/html; charset=utf-8"));
        server.Map("GET", "/legacy.html", _ => WebServer.File(Path.Combine(_wwwroot, "legacy.html"), "text/html; charset=utf-8"));
        server.Map("GET", "/legacy.js", _ => WebServer.File(Path.Combine(_wwwroot, "legacy.js"), "application/javascript; charset=utf-8"));
        server.Map("GET", "/favicon.ico", _ => WebServer.File(Path.Combine(_wwwroot, "favicon.ico"), "image/x-icon"));

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

        server.MapJson("POST", "/api/ie6/open-directory", _ =>
        {
            var result = _ie6.OpenEngineDirectory();
            return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
        });

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
        ["browserChannels"] = _browsers.Status()["channels"]
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
        ["flashProjectorPath"] = _launcher.Config.FlashProjectorPath ?? ""
    };
}
