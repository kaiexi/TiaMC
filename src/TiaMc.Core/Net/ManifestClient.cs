using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMc.Core.Net;

public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public ManifestLatest? Latest { get; set; }
    [JsonPropertyName("versions")] public List<ManifestVersion> Versions { get; set; } = [];
}

public sealed class ManifestLatest
{
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
}

public sealed class ManifestVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("releaseTime")] public string? ReleaseTime { get; set; }

    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);
    public bool IsSnapshot => string.Equals(Type, "snapshot", StringComparison.OrdinalIgnoreCase);
    public bool IsOld => string.Equals(Type, "old_beta", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Type, "old_alpha", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the official version manifest (optionally through the BMCLAPI mirror)
/// so the launcher can list versions that are not installed yet.
/// </summary>
public sealed class ManifestClient
{
    public const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string BmclApiManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    private readonly HttpClient _http;

    public ManifestClient(HttpClient? http = null)
    {
        _http = http ?? Http.ApiClient;
    }

    public async Task<VersionManifest?> GetManifestAsync(Integrity.DownloadSource source = Integrity.DownloadSource.Official,
        CancellationToken token = default)
    {
        var url = source == Integrity.DownloadSource.BmclApi ? BmclApiManifest : OfficialManifest;
        try
        {
            var json = await _http.GetStringAsync(url, token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<VersionManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception)
        {
            // Fall back to the other endpoint before giving up.
            var fallback = source == Integrity.DownloadSource.BmclApi ? OfficialManifest : BmclApiManifest;
            try
            {
                var json = await _http.GetStringAsync(fallback, token).ConfigureAwait(false);
                return JsonSerializer.Deserialize<VersionManifest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Downloads the vanilla version JSON of <paramref name="version"/> into
    /// versions/&lt;id&gt;/&lt;id&gt;.json plus the client jar, so a fresh install can be
    /// created without any external launcher.
    /// </summary>
    public async Task<bool> InstallVanillaAsync(
        Minecraft.McPaths paths,
        ManifestVersion version,
        Integrity.DownloadSource source,
        IProgress<Integrity.DownloadProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken token = default)
    {
        try
        {
            // BMCLAPI exposes the version JSON and client jar directly (the same
            // short routes PCL2 uses), so the mirror is not asked to proxy the
            // arbitrary piston-* host from the manifest.
            var url = source == Integrity.DownloadSource.BmclApi
                ? $"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(version.Id)}/json"
                : version.Url;

            var json = await _http.GetStringAsync(url, token).ConfigureAwait(false);

            // Guard against an HTML error page being written as a version JSON.
            if (!json.TrimStart().StartsWith('{'))
            {
                log?.Invoke($"[install] {url} 返回的不是版本 JSON，改用原始地址重试");
                json = await _http.GetStringAsync(version.Url, token).ConfigureAwait(false);
            }

            var dir = paths.VersionDir(version.Id);
            Directory.CreateDirectory(dir);
            var target = paths.VersionJsonPath(version.Id);
            await File.WriteAllTextAsync(target, json, token).ConfigureAwait(false);
            log?.Invoke($"[install] 已写入 {target}");

            var model = JsonSerializer.Deserialize<Minecraft.VersionJson>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true });
            var client = model?.Downloads?.GetValueOrDefault("client");
            if (client is null)
            {
                log?.Invoke("[install] 版本 JSON 没有 client 下载项");
                return false;
            }

            var jarPath = paths.VersionJarPath(version.Id);
            if (File.Exists(jarPath) && client.Size > 0 && new FileInfo(jarPath).Length == client.Size)
            {
                log?.Invoke("[install] client jar 已存在，跳过");
                return true;
            }

            var clientUrls = new List<string>();
            if (source == Integrity.DownloadSource.BmclApi)
            {
                clientUrls.Add($"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(version.Id)}/client");
            }

            if (client.Url is { Length: > 0 } original)
            {
                var rewritten = Integrity.IntegrityChecker.RewriteUrl(original, source);
                if (!clientUrls.Contains(rewritten, StringComparer.OrdinalIgnoreCase)) clientUrls.Add(rewritten);
                if (!clientUrls.Contains(original, StringComparer.OrdinalIgnoreCase)) clientUrls.Add(original);
            }

            var downloader = new Integrity.DownloadService(_http);
            var outcome = await downloader.DownloadMissingAsync(
            [
                new Integrity.MissingFile
                {
                    Kind = "client-jar",
                    Path = jarPath,
                    // The URL list is already resolved (mirror route first, then the
                    // original), so it is fed to the downloader through the official
                    // source which performs no further rewriting.
                    Url = clientUrls[0],
                    Size = client.Size,
                    Sha1 = client.Sha1
                }
            ], Integrity.DownloadSource.Official, progress, log, token).ConfigureAwait(false);

            return outcome.Ok;
        }
        catch (Exception e)
        {
            log?.Invoke($"[install] 失败: {e.Message}");
            return false;
        }
    }
}
