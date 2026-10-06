using System.IO;
using System.Text.Json;
using TiaMc.Core.Net;
using TiaMc.Core.Utils;

namespace TiaMc.Core.Java;

/// <summary>
/// Downloads and locates authlib-injector, the java agent that lets a vanilla client
/// talk to a third party authentication server (LittleSkin, Blessing Skin, …).
///
/// Usage when launching: <c>-javaagent:authlib-injector.jar=&lt;api root&gt;</c> plus the
/// access token / uuid / name the launcher already passes to the game.
/// </summary>
public static class AuthlibInjector
{
    public const string FileName = "authlib-injector.jar";

    /// <summary>Filename in the runtime folder that holds the downloaded agent.</summary>
    public static string PathIn(string runtimeRoot) => Path.Combine(runtimeRoot, FileName);

    /// <summary>Finds an already downloaded agent (next to the launcher or in runtime).</summary>
    public static string Locate(string root, string runtimeRoot)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(runtimeRoot, FileName),
                     Path.Combine(root, FileName),
                     Path.Combine(root, "authlib-injector", FileName),
                     Path.Combine(AppContext.BaseDirectory, FileName),
                     Path.Combine(AppPaths.CacheRoot, FileName)
                 })
        {
            try
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 100_000) return candidate;
            }
            catch (Exception)
            {
                // ignore
            }
        }

        return "";
    }

    /// <summary>Downloads the newest release from the official mirror list.</summary>
    public static async Task<(bool Ok, string Message, string Path)> EnsureAsync(string runtimeRoot,
        Action<string>? log = null, IProgress<double>? progress = null, CancellationToken token = default)
    {
        void Say(string message) => log?.Invoke(message);

        try
        {
            AppPaths.EnsureDirectory(runtimeRoot);
            var target = PathIn(runtimeRoot);
            if (File.Exists(target) && new FileInfo(target).Length > 100_000)
            {
                return (true, "authlib-injector 已存在", target);
            }

            // The official build server and its GitHub mirror; the "latest" endpoints
            // redirect to the current artifact.
            var mirrors = new[]
            {
                "https://authlib-injector.yushi.moe/artifact/latest.json",
                "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json"
            };

            string? downloadUrl = null;
            string version = "";
            foreach (var mirror in mirrors)
            {
                try
                {
                    var json = await Http.ApiClient.GetStringAsync(mirror, token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("download_url", out var url))
                    {
                        downloadUrl = url.GetString();
                        version = document.RootElement.TryGetProperty("version", out var v)
                            ? v.GetString() ?? ""
                            : "";
                        break;
                    }
                }
                catch (Exception e)
                {
                    Say($"[authlib] 镜像不可用 {mirror}: {e.Message}");
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                // Hard fallback: the GitHub release asset.
                downloadUrl = "https://github.com/yushijinhun/authlib-injector/releases/latest/download/authlib-injector.jar";
                Say("[authlib] 使用 GitHub 备用地址");
            }

            Say($"[authlib] 正在下载 authlib-injector {version}…");

            using (var response = await Http.Client
                       .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var sink = File.Create(target);

                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            var size = new FileInfo(target).Length;
            if (size < 100_000)
            {
                File.Delete(target);
                return (false, $"下载的 authlib-injector 不完整（{TextUtil.FormatBytes(size)}）", "");
            }

            Say($"[authlib] 已就绪: {target}（{TextUtil.FormatBytes(size)}）");
            return (true, $"authlib-injector {version} 已下载", target);
        }
        catch (Exception e)
        {
            return (false, "下载 authlib-injector 失败: " + e.Message, "");
        }
    }
}
