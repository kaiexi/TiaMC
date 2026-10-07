using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TiaMc.Core.Loaders;

/// <summary>
/// 安装模组加载器（就像 HMCL/PCL 安装版本时选 Forge/NeoForge/Fabric/OptiFine 再装）。
///
/// 三家的安装方式不一样，这里按各家**官方**的做法：
///   Fabric / Quilt  拿官方 profile JSON（自带 libraries 与 mainClass，继承原版）→ 写进 versions/&lt;id&gt;/
///   Forge / NeoForge 下载 installer jar，跑 ``java -jar ... --installClient``（安装器自己写 versions/）
///   OptiFine        下载 jar 后开**图形安装器**（官方没有静默安装参数，必须用户点一下）
/// </summary>
public sealed class LoaderInstaller
{
    private readonly HttpClient _http;

    public LoaderInstaller(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("TiaMC/1.0"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "TiaMC/1.0");
        }
    }

    public sealed record InstallResult(bool Ok, string Message, string VersionId, string Detail);

    /// <summary>安装加载器到指定的 Minecraft 根目录。</summary>
    public async Task<InstallResult> InstallAsync(LoaderKind kind, string gameVersion, string loaderVersion,
        string minecraftRoot, string javaPath, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        return kind switch
        {
            LoaderKind.Fabric => await FabricAsync("fabric", gameVersion, loaderVersion, minecraftRoot, progress, token)
                .ConfigureAwait(false),
            LoaderKind.Quilt => await FabricAsync("quilt", gameVersion, loaderVersion, minecraftRoot, progress, token)
                .ConfigureAwait(false),
            LoaderKind.Forge => await InstallerJarAsync(kind, gameVersion, loaderVersion, minecraftRoot, javaPath,
                progress, token).ConfigureAwait(false),
            LoaderKind.NeoForge => await InstallerJarAsync(kind, gameVersion, loaderVersion, minecraftRoot, javaPath,
                progress, token).ConfigureAwait(false),
            LoaderKind.OptiFine => await OptiFineAsync(gameVersion, loaderVersion, minecraftRoot, javaPath, progress,
                token).ConfigureAwait(false),
            _ => new InstallResult(false, "原版不需要安装加载器", "", "")
        };
    }

    // ---------------------------------------------------------------- Fabric / Quilt

    private async Task<InstallResult> FabricAsync(string loader, string gameVersion, string loaderVersion,
        string minecraftRoot, IProgress<string>? progress, CancellationToken token)
    {
        var url = loader == "quilt"
            ? $"https://meta.quiltmc.org/v3/versions/loader/{gameVersion}/{loaderVersion}/profile/json"
            : $"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}/{loaderVersion}/profile/json";

        progress?.Report($"获取 {loader} 配置: {url}");

        string json;
        try
        {
            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new InstallResult(false, $"{loader} 配置获取失败（HTTP {(int)response.StatusCode}）", "", url);
            }

            json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new InstallResult(false, $"{loader} 配置获取失败：{e.Message}", "", url);
        }

        var id = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("id", out var idElement)) id = idElement.GetString() ?? "";
        }
        catch (Exception)
        {
            // 用下面的兜底 id
        }

        if (id.Length == 0) id = $"{loader}-loader-{loaderVersion}-{gameVersion}";

        var directory = Path.Combine(minecraftRoot, "versions", id);
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, id + ".json");
        await File.WriteAllTextAsync(jsonPath, json, new UTF8Encoding(false), token).ConfigureAwait(false);

        progress?.Report($"已写入 {jsonPath}");
        return new InstallResult(true, $"{loader} {loaderVersion} 已安装（{id}）", id, jsonPath);
    }

    // ---------------------------------------------------------------- Forge / NeoForge

    /// <summary>Forge / NeoForge 安装器的下载地址。</summary>
    public static string InstallerUrl(LoaderKind kind, string gameVersion, string loaderVersion) => kind switch
    {
        LoaderKind.Forge =>
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{gameVersion}-{loaderVersion}/" +
            $"forge-{gameVersion}-{loaderVersion}-installer.jar",
        LoaderKind.NeoForge =>
            $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/" +
            $"neoforge-{loaderVersion}-installer.jar",
        _ => ""
    };

    /// <summary>Forge / NeoForge 安装后产生的版本 id（用于提示用户）。</summary>
    public static string InstalledVersionId(LoaderKind kind, string gameVersion, string loaderVersion) => kind switch
    {
        LoaderKind.Forge => $"{gameVersion}-forge-{loaderVersion}",
        LoaderKind.NeoForge => $"neoforge-{loaderVersion}",
        _ => ""
    };

    private async Task<InstallResult> InstallerJarAsync(LoaderKind kind, string gameVersion, string loaderVersion,
        string minecraftRoot, string javaPath, IProgress<string>? progress, CancellationToken token)
    {
        var url = InstallerUrl(kind, gameVersion, loaderVersion);
        var name = LoaderCatalog.NameOf(kind);
        var cache = Path.Combine(minecraftRoot, "loaders");
        Directory.CreateDirectory(cache);
        var jar = Path.Combine(cache, Path.GetFileName(url));

        progress?.Report($"下载 {name} 安装器: {url}");
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new InstallResult(false, $"{name} 安装器下载失败（HTTP {(int)response.StatusCode}）", "", url);
            }

            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var target = File.Create(jar);
            await source.CopyToAsync(target, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new InstallResult(false, $"{name} 安装器下载失败：{e.Message}", "", url);
        }

        progress?.Report($"安装器已下载（{new FileInfo(jar).Length / 1024 / 1024} MB），开始执行 --installClient");

        var info = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(javaPath) ? "java" : javaPath,
            WorkingDirectory = minecraftRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-jar");
        info.ArgumentList.Add(jar);
        info.ArgumentList.Add("--installClient");
        info.ArgumentList.Add(minecraftRoot);

        try
        {
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report("  " + e.Data); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report("  " + e.Data); };

            if (!process.Start())
            {
                return new InstallResult(false, $"{name} 安装器无法启动", "", jar);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var id = InstalledVersionId(kind, gameVersion, loaderVersion);
            var installed = Directory.Exists(Path.Combine(minecraftRoot, "versions", id));

            return process.ExitCode == 0 || installed
                ? new InstallResult(true, $"{name} {loaderVersion} 安装完成（{id}）", id, jar)
                : new InstallResult(false, $"{name} 安装器退出码 {process.ExitCode}（看日志）", id, jar);
        }
        catch (Exception e)
        {
            return new InstallResult(false, $"{name} 安装器执行失败：{e.Message}", "", jar);
        }
    }

    // ---------------------------------------------------------------- OptiFine

    /// <summary>OptiFine 的下载页（需要 token 才能拿直链）。</summary>
    public const string OptiFineDownloads = "https://optifine.net/downloads";

    private async Task<InstallResult> OptiFineAsync(string gameVersion, string loaderVersion,
        string minecraftRoot, string javaPath, IProgress<string>? progress, CancellationToken token)
    {
        var fileName = $"OptiFine_{gameVersion}_{loaderVersion}.jar";
        var cache = Path.Combine(minecraftRoot, "loaders");
        Directory.CreateDirectory(cache);
        var jar = Path.Combine(cache, fileName);

        progress?.Report($"获取 OptiFine 下载链接: {fileName}");
        try
        {
            using var page = await _http.GetAsync(OptiFineDownloads, token).ConfigureAwait(false);
            var html = await page.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            // 页面里的下载链接形如 downloadx?f=OptiFine_1.21.4_HD_U_J4_pre2.jar&x=<token>
            var pattern = $@"downloadx\?f={System.Text.RegularExpressions.Regex.Escape(fileName)}&(?:amp;)?x=([A-Za-z0-9]+)";
            var match = System.Text.RegularExpressions.Regex.Match(html, pattern);
            if (!match.Success)
            {
                return new InstallResult(false,
                    "OptiFine 没有可直接下载的链接（可能未适配该版本，或需要先去下载页点一次广告）", "", OptiFineDownloads);
            }

            var url = $"https://optifine.net/downloadx?f={fileName}&x={match.Groups[1].Value}";
            progress?.Report($"下载 {url}");
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new InstallResult(false, $"OptiFine 下载失败（HTTP {(int)response.StatusCode}）", "", url);
            }

            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var target = File.Create(jar);
            await source.CopyToAsync(target, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new InstallResult(false, "OptiFine 下载失败：" + e.Message, "", fileName);
        }

        // OptiFine 官方安装器没有静默参数：这里把它**打开**，由用户在界面里点 Install。
        progress?.Report("OptiFine 需要图形界面安装：已打开安装器，请在窗口里点 Install");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(javaPath) ? "java" : javaPath,
                Arguments = $"-jar \"{jar}\"",
                WorkingDirectory = minecraftRoot,
                UseShellExecute = false
            });
        }
        catch (Exception e)
        {
            return new InstallResult(false, "OptiFine 安装器无法打开：" + e.Message, "", jar);
        }

        return new InstallResult(true,
            "OptiFine 安装器已打开：请在窗口里选择游戏目录并点 Install（官方没有静默安装参数）",
            $"OptiFine_{loaderVersion}", jar);
    }
}
