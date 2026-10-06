using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using TiaMc.App.Services;
using TiaMc.Core.Net;
using TiaMc.Core.Utils;

namespace TiaMc.Web;

/// <summary>
/// 把开源浏览器内核 **miniblink**（weolar/miniblink49，Apache-2.0）塞进启动器。
///
/// miniblink 是一个"给应用嵌入用"的 Blink 内核：单个 DLL（`mb132_x64.dll` 67 MB / `mb132_x32.dll` 55 MB），
/// 导出 `mb*` C API（`mbCreateWebWindow` / `mbLoadURL` / `mbMoveWindow` …）。
/// 与前面那些"IE6 兼容"尝试不同，这是**真正可用的开源浏览器内核**，可以嵌进我们自己的窗口里显示网页。
///
/// 内核按需下载（Apache-2.0，允许再分发，但 60 MB 不适合入库），放在 &lt;配置目录&gt;\miniblink\。
/// </summary>
internal sealed class MiniblinkService
{
    private const string ReleaseApi = "https://api.github.com/repos/weolar/miniblink49/releases/latest";

    /// <summary>内核目录（配置里的 MiniblinkPath 优先）。</summary>
    public string Directory
    {
        get
        {
            try
            {
                var configured = AppConfig.Load().MiniblinkPath ?? "";
                if (configured.Length > 0 && System.IO.Directory.Exists(configured)) return configured;
            }
            catch (Exception)
            {
                // ignore
            }

            return Path.Combine(AppConfig.ConfigDirectory, "miniblink");
        }
    }

    /// <summary>当前进程位数对应的内核 DLL。</summary>
    public string DllPath()
    {
        var name = Environment.Is64BitProcess ? "mb132_x64.dll" : "mb132_x32.dll";
        var direct = Path.Combine(Directory, name);
        if (File.Exists(direct)) return direct;

        try
        {
            return System.IO.Directory.GetFiles(Directory, name, SearchOption.AllDirectories).FirstOrDefault() ?? direct;
        }
        catch (Exception)
        {
            return direct;
        }
    }

    public bool Ready => File.Exists(DllPath());

    public JsonObject Status()
    {
        var dll = DllPath();
        var version = "";
        try
        {
            var stamp = Path.Combine(Directory, "version.txt");
            if (File.Exists(stamp)) version = File.ReadAllText(stamp).Trim();
        }
        catch (Exception)
        {
            // ignore
        }

        return new JsonObject
        {
            ["ready"] = Ready,
            ["version"] = version,
            ["directory"] = Directory,
            ["dll"] = dll,
            ["processBits"] = Environment.Is64BitProcess ? 64 : 32,
            ["license"] = "Apache-2.0（weolar/miniblink49）",
            ["note"] = Ready
                ? "已就绪：可以在启动器窗口里用开源 Blink 内核显示网页"
                : "未下载：点「获取内核」从 GitHub 下载 miniblink（约 62 MB，Apache-2.0）并解包"
        };
    }

    public sealed record ActionResult(bool Ok, string Message, string Detail = "");

    /// <summary>下载并解包 miniblink 内核（运行时获取，不随仓库分发）。</summary>
    public async Task<ActionResult> EnsureAsync(CancellationToken token = default)
    {
        try
        {
            if (Ready) return new ActionResult(true, "miniblink 内核已就绪", DllPath());

            AppPaths.EnsureDirectory(Directory);

            var json = await Http.ApiClient.GetStringAsync(ReleaseApi, token).ConfigureAwait(false);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var tag = document.RootElement.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "" : "";
            string? url = null;
            foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (string.IsNullOrEmpty(url)) return new ActionResult(false, "没有找到 miniblink 发行包（GitHub API 返回异常）");

            var archive = Path.Combine(Directory, Path.GetFileName(url));
            LogService.Info($"正在下载 miniblink {tag}：{Path.GetFileName(url)}（Apache-2.0）", "浏览器内核");

            await using (var source = await Http.Client.GetStreamAsync(url, token).ConfigureAwait(false))
            await using (var sink = File.Create(archive))
            {
                await source.CopyToAsync(sink, token).ConfigureAwait(false);
            }

            LogService.Info("正在解包 miniblink（.7z，用系统 7-Zip / NanaZip）…", "浏览器内核");
            var (code, output) = Run7z($"x \"{archive}\" -o\"{Directory}\" -y");
            if (code != 0)
            {
                return new ActionResult(false, "解包失败（需要 7-Zip / NanaZip）：" + output.Split('\n').LastOrDefault(l => l.Trim().Length > 0));
            }

            File.WriteAllText(Path.Combine(Directory, "version.txt"), tag);
            try
            {
                File.Delete(archive);
            }
            catch (Exception)
            {
                // ignore
            }

            if (!Ready) return new ActionResult(false, "解包完成但没有找到 " + (Environment.Is64BitProcess ? "mb132_x64.dll" : "mb132_x32.dll"));

            LogService.Ok($"miniblink {tag} 已就绪：{DllPath()}", "浏览器内核");
            return new ActionResult(true, $"miniblink {tag} 已就绪", DllPath());
        }
        catch (Exception e)
        {
            return new ActionResult(false, "获取 miniblink 失败: " + e.Message);
        }
    }

    private static (int ExitCode, string Output) Run7z(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("7z", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return (-1, "无法启动 7z");

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(300000);
            return (process.HasExited ? process.ExitCode : -1, output);
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }

    /// <summary>在启动器自己的窗口里用 miniblink 打开页面。</summary>
    public ActionResult Open(string url)
    {
        if (!Ready)
        {
            return new ActionResult(false, "miniblink 内核还没有下载：先点「获取内核」（Apache-2.0，约 62 MB）");
        }

        try
        {
            var target = url.Length > 0 ? url : "http://127.0.0.1/";
            var thread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MiniblinkWindow(DllPath(), target));
            })
            {
                IsBackground = true,
                Name = "miniblink-window"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            LogService.User($"用内置 miniblink 内核打开: {target}", "浏览器内核");
            return new ActionResult(true, "已用 miniblink（开源 Blink 内核）打开窗口", DllPath());
        }
        catch (Exception e)
        {
            return new ActionResult(false, "打开 miniblink 窗口失败: " + e.Message);
        }
    }
}
