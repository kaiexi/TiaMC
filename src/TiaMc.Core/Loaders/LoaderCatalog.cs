using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMc.Core.Loaders;

/// <summary>支持的模组加载器。</summary>
public enum LoaderKind
{
    Vanilla,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    OptiFine
}

/// <summary>一个可安装的加载器版本。</summary>
public sealed record LoaderVersion(string Loader, string Version, string GameVersion, bool Recommended, string Note = "")
{
    public string Display => Version + (Recommended ? "  （推荐）" : "") + (Note.Length > 0 ? "  " + Note : "");
}

/// <summary>
/// 加载器版本目录（就像 HMCL/PCL 安装版本时能选 Forge / NeoForge / Fabric / OptiFine 的版本）。
///
/// 四家的来源（均已实测可用）：
///   Fabric    https://meta.fabricmc.net/v2/versions/loader                     253 个版本
///   NeoForge  https://maven.neoforged.net/api/maven/versions/releases/...      1783 个版本
///   Forge     https://files.minecraftforge.net/.../promotions_slim.json        每游戏版本的 latest/recommended
///             + https://maven.minecraftforge.net/.../maven-metadata.xml        完整版本列表
///   OptiFine  https://optifine.net/downloads                                   （无官方 API，抓取页面里的版本号）
/// </summary>
public sealed class LoaderCatalog
{
    private readonly HttpClient _http;

    public LoaderCatalog(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("TiaMC/1.0"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "TiaMC/1.0");
        }
    }

    public static string NameOf(LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.OptiFine => "optifine",
        _ => "vanilla"
    };

    /// <summary>列出某个加载器在指定游戏版本下可安装的版本（新的在前）。</summary>
    public async Task<List<LoaderVersion>> GetVersionsAsync(LoaderKind kind, string gameVersion,
        CancellationToken token = default)
    {
        try
        {
            return kind switch
            {
                LoaderKind.Fabric => await FabricAsync(gameVersion, token).ConfigureAwait(false),
                LoaderKind.Quilt => await QuiltAsync(gameVersion, token).ConfigureAwait(false),
                LoaderKind.NeoForge => await NeoForgeAsync(gameVersion, token).ConfigureAwait(false),
                LoaderKind.Forge => await ForgeAsync(gameVersion, token).ConfigureAwait(false),
                LoaderKind.OptiFine => await OptiFineAsync(gameVersion, token).ConfigureAwait(false),
                _ => []
            };
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>整合包清单里写的加载器名 → 枚举（"fabric-loader" / "neoforge" / "forge" 等）。</summary>
    public static LoaderKind Parse(string? name) => (name ?? "").ToLowerInvariant() switch
    {
        var s when s.Contains("neoforge") => LoaderKind.NeoForge,
        var s when s.Contains("forge") => LoaderKind.Forge,
        var s when s.Contains("quilt") => LoaderKind.Quilt,
        var s when s.Contains("optifine") => LoaderKind.OptiFine,
        var s when s.Contains("fabric") => LoaderKind.Fabric,
        _ => LoaderKind.Vanilla
    };

    private async Task<List<LoaderVersion>> FabricAsync(string gameVersion, CancellationToken token)
    {
        var jar = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/loader/" +
                                    Uri.EscapeDataString(gameVersion), token).ConfigureAwait(false);
        var result = new List<LoaderVersion>();
        if (jar is null) return result;

        foreach (var item in jar.Value.EnumerateArray())
        {
            if (!item.TryGetProperty("loader", out var loader)) continue;
            var version = loader.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var stable = loader.TryGetProperty("stable", out var st) && st.ValueKind == JsonValueKind.True;
            if (version.Length > 0)
            {
                result.Add(new LoaderVersion("fabric", version, gameVersion, stable));
            }
        }

        return result;
    }

    private async Task<List<LoaderVersion>> QuiltAsync(string gameVersion, CancellationToken token)
    {
        var jar = await GetJsonAsync("https://meta.quiltmc.org/v3/versions/loader", token).ConfigureAwait(false);
        var result = new List<LoaderVersion>();
        if (jar is null) return result;

        foreach (var item in jar.Value.EnumerateArray())
        {
            var version = item.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            if (version.Length > 0) result.Add(new LoaderVersion("quilt", version, gameVersion, false));
        }

        return result;
    }

    private async Task<List<LoaderVersion>> NeoForgeAsync(string gameVersion, CancellationToken token)
    {
        // NeoForge 的版本号形如 21.4.123（前两段对应 MC 1.21.4）；26.x 起改用新的版本方案。
        var jar = await GetJsonAsync(
            "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", token)
            .ConfigureAwait(false);
        var result = new List<LoaderVersion>();
        if (jar is null || !jar.Value.TryGetProperty("versions", out var versions)) return result;

        var prefix = NeoForgePrefix(gameVersion);
        foreach (var item in versions.EnumerateArray())
        {
            var version = item.GetString() ?? "";
            if (version.Length == 0) continue;
            if (prefix.Length > 0 && !version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var isBeta = version.Contains("beta", StringComparison.OrdinalIgnoreCase);
            result.Add(new LoaderVersion("neoforge", version, gameVersion, !isBeta));
        }

        result.Reverse();   // 新的在前
        return result;
    }

    /// <summary>把 1.21.4 / 26.3 这类游戏版本换算成 NeoForge 的版本前缀。</summary>
    public static string NeoForgePrefix(string gameVersion)
    {
        var parts = gameVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        if (parts[0] == "1" && parts.Length >= 3) return $"{parts[1]}.{parts[2]}.";
        if (parts[0] == "1" && parts.Length == 2) return $"{parts[1]}.0.";
        return gameVersion + ".";
    }

    private async Task<List<LoaderVersion>> ForgeAsync(string gameVersion, CancellationToken token)
    {
        var result = new List<LoaderVersion>();

        // ① promotions：每个游戏版本的 latest / recommended（最常用）
        var promos = await GetJsonAsync(
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", token)
            .ConfigureAwait(false);
        if (promos is not null && promos.Value.TryGetProperty("promos", out var map))
        {
            foreach (var property in map.EnumerateObject())
            {
                if (!property.Name.StartsWith(gameVersion + "-", StringComparison.OrdinalIgnoreCase)) continue;
                var channel = property.Name[(gameVersion.Length + 1)..];
                var version = property.Value.GetString() ?? "";
                if (version.Length == 0) continue;
                result.Add(new LoaderVersion("forge", version, gameVersion,
                    channel.Equals("recommended", StringComparison.OrdinalIgnoreCase),
                    channel.Equals("latest", StringComparison.OrdinalIgnoreCase) ? "最新" : "推荐"));
            }
        }

        // ② maven-metadata：完整版本列表（可能较多，只在 promotions 命中很少时补）
        if (result.Count < 3)
        {
            var text = await GetTextAsync(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", token)
                .ConfigureAwait(false);
            if (text is not null)
            {
                foreach (Match match in Regex.Matches(text, $@"<version>{Regex.Escape(gameVersion)}-([^<]+)</version>"))
                {
                    var version = match.Groups[1].Value;
                    if (result.All(r => r.Version != version))
                    {
                        result.Add(new LoaderVersion("forge", version, gameVersion, false));
                    }
                }

                result.Reverse();   // 新的在前
            }
        }

        return result;
    }

    private async Task<List<LoaderVersion>> OptiFineAsync(string gameVersion, CancellationToken token)
    {
        // OptiFine 没有官方 API：从下载页里抓 "OptiFine_<游戏版本>_HD_U_<x>_<mc>.jar" 这类文件名。
        var html = await GetTextAsync("https://optifine.net/downloads", token).ConfigureAwait(false);
        var result = new List<LoaderVersion>();
        if (html is null) return result;

        var pattern = $@"OptiFine_{Regex.Escape(gameVersion)}_([A-Za-z0-9_\.]+)\.jar";
        foreach (Match match in Regex.Matches(html, pattern))
        {
            var version = match.Groups[1].Value.TrimEnd('.');
            if (result.All(r => r.Version != version))
            {
                result.Add(new LoaderVersion("optifine", version, gameVersion, false, "需要图形界面安装"));
            }
        }

        return result;
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken token)
    {
        var text = await GetTextAsync(url, token).ConfigureAwait(false);
        if (text is null) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> GetTextAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
