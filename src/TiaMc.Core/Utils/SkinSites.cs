using System.IO;
using System.Text.Json;
using TiaMc.Core.Net;

namespace TiaMc.Core.Utils;

/// <summary>
/// Skins from outside the launcher:
///
///   * classic skin image services (mc-heads, Minotar, Crafatar, Visage …) that map
///     a player name or UUID to a PNG
///   * Blessing Skin / LittleSkin style sites ("外置皮肤站"), which speak the
///     Yggdrasil protocol: /api/profiles/minecraft → profile → textures property →
///     the SKIN url signed by the site
///   * plain URLs pasted by the user
///
/// Everything ends up as a local PNG next to the account, so offline accounts can use
/// it and the built-in Yggdrasil server can serve it to the game.
/// </summary>
public static class SkinSites
{
    public sealed record Site(string Name, string UrlTemplate, bool IsYggdrasil = false, string Hint = "");

    /// <summary>Built in skin services; {0} is the player name or UUID.</summary>
    public static List<Site> Presets { get; } =
    [
        new("mc-heads.net", "https://mc-heads.net/skin/{0}", Hint: "按玩家名或 UUID 取皮肤 PNG"),
        new("Minotar", "https://minotar.net/skin/{0}", Hint: "Mojang 正版玩家皮肤"),
        new("Crafatar", "https://crafatar.com/skins/{0}", Hint: "需要 UUID，支持 ?default=…"),
        new("Visage (正版 UUID)", "https://visage.surgeplay.com/skin/512/{0}", Hint: "按 UUID 取高清皮肤"),
        new("LittleSkin 皮肤站", "https://littleskin.cn", IsYggdrasil: true,
            Hint: "外置登录皮肤站（Blessing Skin），用角色名解析"),
        new("Blessing Skin 自建站", "", IsYggdrasil: true,
            Hint: "填入站点地址，例如 https://skin.example.com"),
        new("自定义图片链接", "{0}", Hint: "直接粘贴皮肤 PNG 的完整链接")
    ];

    public sealed record FetchResult(bool Ok, string Message, byte[]? Png, string SourceUrl);

    /// <summary>True when the bytes look like a Minecraft skin (PNG, 64 wide).</summary>
    public static (bool Ok, string Message) ValidateSkin(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50) return (false, "不是 PNG 图片");

        try
        {
            // Width/height live in the IHDR chunk right after the signature.
            var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];

            if (width != 64)
            {
                return (false, $"皮肤宽度应为 64 像素，实际 {width}（可能是头像或其它图片）");
            }

            return height is 64 or 32
                ? (true, $"皮肤有效：{width}×{height}")
                : (false, $"皮肤高度应为 64 或 32，实际 {height}");
        }
        catch (Exception e)
        {
            return (false, "解析 PNG 失败: " + e.Message);
        }
    }

    /// <summary>Downloads a PNG from a direct url and validates it.</summary>
    public static async Task<FetchResult> FetchUrlAsync(string url, CancellationToken token = default)
    {
        try
        {
            using var response = await Http.Client.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = (int)response.StatusCode switch
                {
                    403 => "该名字在皮肤站上不存在或没有皮肤（403）",
                    404 => "皮肤站上没有这个玩家 / 图片（404）",
                    429 => "请求过于频繁，稍后再试（429）",
                    _ => $"皮肤站返回 {(int)response.StatusCode} {response.ReasonPhrase}"
                };

                return new FetchResult(false, reason, null, url);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            var check = ValidateSkin(bytes);
            return check.Ok
                ? new FetchResult(true, check.Message, bytes, url)
                : new FetchResult(false, check.Message + $"（{url}）", null, url);
        }
        catch (Exception e)
        {
            return new FetchResult(false, "下载失败: " + e.Message, null, url);
        }
    }

    /// <summary>
    /// Resolves a skin from a site: classic services take the name straight into the
    /// URL, Blessing Skin style sites are queried through their Yggdrasil API.
    /// </summary>
    public static async Task<FetchResult> FetchAsync(Site site, string nameOrId, string? customSiteUrl = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return new FetchResult(false, "请填写玩家名、UUID 或图片链接", null, "");

        if (site.IsYggdrasil)
        {
            var root = (customSiteUrl ?? "").Trim().TrimEnd('/');
            if (site.UrlTemplate.Length > 0 && root.Length == 0) root = site.UrlTemplate.TrimEnd('/');
            if (root.Length == 0) return new FetchResult(false, "请填写皮肤站地址", null, "");

            return await FetchFromYggdrasilSiteAsync(root, nameOrId, token).ConfigureAwait(false);
        }

        // A raw link ("{0}") or an input that already is a URL must not be escaped.
        var isRawLink = site.UrlTemplate.Trim() == "{0}";
        var argument = isRawLink || nameOrId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? nameOrId.Trim()
            : Uri.EscapeDataString(nameOrId.Trim());

        var url = site.UrlTemplate.Contains("{0}")
            ? string.Format(site.UrlTemplate, argument)
            : argument;

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return new FetchResult(false, "链接必须以 http 开头", null, url);
        }

        return await FetchUrlAsync(url, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Blessing Skin / LittleSkin: resolve the profile by name, read the signed
    /// "textures" property and download the SKIN url it points at.
    /// </summary>
    public static async Task<FetchResult> FetchFromYggdrasilSiteAsync(string siteRoot, string playerName,
        CancellationToken token = default)
    {
        foreach (var prefix in new[] { "/api/yggdrasil", "" })
        {
            try
            {
                var api = siteRoot + prefix;

                // 1. name → profile (uuid)
                var request = new HttpRequestMessage(HttpMethod.Post, api + "/api/profiles/minecraft")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[] { playerName }),
                        System.Text.Encoding.UTF8, "application/json")
                };

                using var response = await Http.Client.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                {
                    return new FetchResult(false, $"{siteRoot} 上没有找到角色 {playerName}", null, "");
                }

                var id = document.RootElement[0].TryGetProperty("id", out var idElement)
                    ? idElement.GetString()
                    : null;
                if (string.IsNullOrEmpty(id)) continue;

                // 2. profile → textures
                var profileUrl = api + "/sessionserver/session/minecraft/profile/" + id;
                var profileJson = await Http.ApiClient.GetStringAsync(profileUrl, token).ConfigureAwait(false);
                using var profile = JsonDocument.Parse(profileJson);

                if (!profile.RootElement.TryGetProperty("properties", out var properties)) continue;

                foreach (var property in properties.EnumerateArray())
                {
                    if (property.TryGetProperty("name", out var propertyName) &&
                        propertyName.GetString() != "textures") continue;

                    var value = property.GetProperty("value").GetString();
                    if (string.IsNullOrEmpty(value)) continue;

                    var decoded = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(value));
                    using var textures = JsonDocument.Parse(decoded);

                    if (textures.RootElement.TryGetProperty("textures", out var textureRoot) &&
                        textureRoot.TryGetProperty("SKIN", out var skin) &&
                        skin.TryGetProperty("url", out var skinUrl))
                    {
                        var url = skinUrl.GetString() ?? "";
                        var result = await FetchUrlAsync(url, token).ConfigureAwait(false);
                        return result with { SourceUrl = url };
                    }
                }

                return new FetchResult(false, $"{playerName} 在 {siteRoot} 上没有设置皮肤", null, "");
            }
            catch (Exception e)
            {
                if (prefix.Length == 0)
                {
                    return new FetchResult(false, $"{siteRoot} 查询失败: {e.Message}", null, "");
                }
            }
        }

        return new FetchResult(false, $"{siteRoot} 不是可用的外置皮肤站（Yggdrasil API 无响应）", null, "");
    }

    /// <summary>Stores the skin next to the launcher and returns the file path.</summary>
    public static string SaveSkin(string directory, string accountKey, byte[] png)
    {
        AppPaths.EnsureDirectory(directory);
        var safe = new string(accountKey.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        if (safe.Length == 0) safe = "player";
        var path = Path.Combine(directory, safe + ".png");
        File.WriteAllBytes(path, png);
        return path;
    }
}
