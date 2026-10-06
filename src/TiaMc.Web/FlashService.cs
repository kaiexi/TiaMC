using TiaMc.App.Services;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMc.Core.Net;
using TiaMc.Core.Utils;

namespace TiaMc.Web;

/// <summary>
/// Flash 支持（浏览器 GUI 版）。
///
/// Flash Player 插件（NPAPI/PPAPI）已于 2020-12-31 停止支持，现代浏览器不再加载它，
/// 因此这里提供两条可用路线：
///
///   1. **Ruffle**（开源 Flash 模拟器，MIT/Apache-2.0，WASM）：下载 self-hosted 包到
///      &lt;config&gt;\flash\ruffle，网页里加载 ruffle.js 后，页面中的 &lt;object&gt;/&lt;embed&gt;
///      SWF 会在浏览器内直接播放——不需要任何插件。
///   2. **Flash 投影播放器**（flashplayer_*.exe，Adobe 已停止分发）：如果用户本机已有，
///      启动器可以检测到并直接把 SWF / SWF 链接交给它播放；也可以在界面里手动指定路径。
/// </summary>
internal sealed class FlashService
{
    private const string RuffleApi = "https://api.github.com/repos/ruffle-rs/ruffle/releases/latest";

    public string BaseDirectory { get; } = Path.Combine(AppConfig.ConfigDirectory, "flash");
    public string RuffleDirectory => Path.Combine(BaseDirectory, "ruffle");
    public string SwfDirectory => Path.Combine(BaseDirectory, "swf");
    public string RuffleScriptPath => Path.Combine(RuffleDirectory, "ruffle.js");

    public bool RuffleReady => File.Exists(RuffleScriptPath);

    /// <summary>Ruffle / 投影播放器 / SWF 列表的状态。</summary>
    public JsonObject Status()
    {
        var swfs = new JsonArray();
        try
        {
            if (Directory.Exists(SwfDirectory))
            {
                foreach (var file in new DirectoryInfo(SwfDirectory).GetFiles("*.swf").OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    swfs.Add(new JsonObject
                    {
                        ["name"] = file.Name,
                        ["size"] = TextUtil.FormatBytes(file.Length),
                        ["modified"] = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                    });
                }
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return new JsonObject
        {
            ["ruffleReady"] = RuffleReady,
            ["ruffleVersion"] = ReadRuffleVersion(),
            ["directory"] = BaseDirectory,
            ["swfDirectory"] = SwfDirectory,
            ["projector"] = ProjectorPath(),
            ["projectorFound"] = ProjectorPath().Length > 0,
            ["note"] = "现代浏览器已不支持 Flash 插件（2020-12-31 EOL）：网页内播放请用 Ruffle，真实 Flash 内容可用投影播放器。",
            ["swfs"] = swfs
        };
    }

    private static string ReadRuffleVersion()
    {
        try
        {
            var path = Path.Combine(AppConfig.ConfigDirectory, "flash", "ruffle", "version.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ Ruffle

    public sealed record RuffleResult(bool Ok, string Message, string Version);

    /// <summary>下载并解压 Ruffle 的 self-hosted 包（含 ruffle.js / .wasm）。</summary>
    public async Task<RuffleResult> EnsureRuffleAsync(CancellationToken token = default)
    {
        try
        {
            if (RuffleReady)
            {
                return new RuffleResult(true, "Ruffle 已就绪", ReadRuffleVersion());
            }

            AppPaths.EnsureDirectory(RuffleDirectory);

            // 1) 找到最新的 nightly self-hosted 包
            var json = await Http.ApiClient.GetStringAsync(RuffleApi, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var tag = document.RootElement.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "" : "";
            string? downloadUrl = null;
            foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("web-selfhosted.zip", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                return new RuffleResult(false, "没有找到 Ruffle 的 self-hosted 包（GitHub API 返回异常）", "");
            }

            LogService.Info($"正在下载 Ruffle {tag}：{Path.GetFileName(downloadUrl)}", "Flash");

            var archive = Path.Combine(BaseDirectory, "ruffle.zip");
            await using (var source = await Http.Client.GetStreamAsync(downloadUrl, token).ConfigureAwait(false))
            await using (var sink = File.Create(archive))
            {
                await source.CopyToAsync(sink, token).ConfigureAwait(false);
            }

            LogService.Info("正在解压 Ruffle…", "Flash");
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    var target = Path.Combine(RuffleDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            try
            {
                File.Delete(archive);
            }
            catch (Exception)
            {
                // ignore
            }

            // 有些包会多一层目录（ruffle-nightly-…/ruffle.js）
            if (!RuffleReady)
            {
                var nested = Directory.GetFiles(RuffleDirectory, "ruffle.js", SearchOption.AllDirectories).FirstOrDefault();
                if (nested is not null)
                {
                    var nestedRoot = Path.GetDirectoryName(nested)!;
                    foreach (var file in Directory.GetFiles(nestedRoot))
                    {
                        File.Copy(file, Path.Combine(RuffleDirectory, Path.GetFileName(file)), overwrite: true);
                    }

                    foreach (var dir in Directory.GetDirectories(nestedRoot))
                    {
                        var target = Path.Combine(RuffleDirectory, Path.GetFileName(dir));
                        if (!Directory.Exists(target)) Directory.CreateDirectory(target);
                        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            var relative = Path.GetRelativePath(dir, file);
                            var destination = Path.Combine(target, relative);
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            File.Copy(file, destination, overwrite: true);
                        }
                    }
                }
            }

            if (!RuffleReady)
            {
                return new RuffleResult(false, "解压完成但没有找到 ruffle.js", tag);
            }

            File.WriteAllText(Path.Combine(RuffleDirectory, "version.txt"), tag);
            LogService.Ok($"Ruffle {tag} 已就绪：{RuffleScriptPath}", "Flash");
            return new RuffleResult(true, $"Ruffle {tag} 已下载完成", tag);
        }
        catch (Exception e)
        {
            LogService.Error("下载 Ruffle 失败: " + e.Message, "Flash");
            return new RuffleResult(false, "下载失败: " + e.Message, "");
        }
    }

    // ------------------------------------------------------------- 投影播放器

    /// <summary>配置里的路径，或常见安装位置里找到的 flashplayer 可执行文件。</summary>
    public string ProjectorPath()
    {
        try
        {
            var configured = TiaMc.App.Services.AppConfig.Load().FlashProjectorPath ?? "";
            if (configured.Length > 0 && File.Exists(configured)) return configured;
        }
        catch (Exception)
        {
            // ignore
        }

        var roots = new[]
        {
            BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashCenter"),
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var found = Directory.GetFiles(root, "flashplayer*.exe", SearchOption.AllDirectories).FirstOrDefault()
                            ?? Directory.GetFiles(root, "FlashPlayer*.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) return found;
            }
            catch (Exception)
            {
                // ignore
            }
        }

        return "";
    }

    public sealed record ActionResult(bool Ok, string Message, string File = "");

    /// <summary>把本地 SWF 或远程 SWF/SWF 链接交给投影播放器打开。</summary>
    public ActionResult Open(string target)
    {
        if (target.Length == 0) return new ActionResult(false, "请填写 SWF 文件或链接");

        var projector = ProjectorPath();
        if (projector.Length == 0)
        {
            return new ActionResult(false,
                "没有找到 Flash 投影播放器（flashplayer_*.exe）。请在设置里指定路径，" +
                "或直接在网页里用 Ruffle 播放。");
        }

        // 本地文件名 → 转到 flash/swf 目录
        var candidate = Path.Combine(SwfDirectory, target);
        var argument = File.Exists(candidate) ? candidate
            : File.Exists(target) ? target
            : target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? target
            : "";

        if (argument.Length == 0) return new ActionResult(false, "找不到目标: " + target);

        try
        {
            Process.Start(new ProcessStartInfo { FileName = projector, Arguments = $"\"{argument}\"", UseShellExecute = false });
            LogService.User($"用投影播放器打开 Flash: {argument}", "Flash");
            return new ActionResult(true, "已用投影播放器打开");
        }
        catch (Exception e)
        {
            return new ActionResult(false, "启动投影播放器失败: " + e.Message);
        }
    }

    public ActionResult OpenProjector()
    {
        var projector = ProjectorPath();
        if (projector.Length == 0) return new ActionResult(false, "没有找到投影播放器");
        try
        {
            Process.Start(new ProcessStartInfo { FileName = projector, UseShellExecute = false });
            return new ActionResult(true, "投影播放器已启动");
        }
        catch (Exception e)
        {
            return new ActionResult(false, "启动失败: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ SWF 库

    public ActionResult SaveSwf(string name, byte[] data)
    {
        try
        {
            if (data.Length < 8) return new ActionResult(false, "文件太小，可能不是 SWF");

            // FWS / CWS / ZWS 是 SWF 的三种头部
            var signature = System.Text.Encoding.ASCII.GetString(data, 0, 3);
            if (signature is not ("FWS" or "CWS" or "ZWS"))
            {
                return new ActionResult(false, "不是 SWF 文件（缺少 FWS/CWS/ZWS 头）");
            }

            AppPaths.EnsureDirectory(SwfDirectory);
            var safe = Path.GetFileName(name);
            if (safe.Length == 0) safe = $"flash-{DateTime.Now:yyyyMMdd-HHmmss}.swf";
            if (!safe.EndsWith(".swf", StringComparison.OrdinalIgnoreCase)) safe += ".swf";

            var path = Path.Combine(SwfDirectory, safe);
            File.WriteAllBytes(path, data);
            LogService.Ok($"已导入 SWF: {path}（{TextUtil.FormatBytes(data.Length)}）", "Flash");
            return new ActionResult(true, $"已导入 {safe}", safe);
        }
        catch (Exception e)
        {
            return new ActionResult(false, "保存 SWF 失败: " + e.Message);
        }
    }

    public string? ResolveSwf(string name)
    {
        try
        {
            var safe = Path.GetFileName(name);
            var path = Path.Combine(SwfDirectory, safe);
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
