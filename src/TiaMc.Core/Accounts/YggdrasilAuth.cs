using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMc.Core.Net;

namespace TiaMc.Core.Accounts;

/// <summary>
/// Client side of the Yggdrasil protocol, used to log into third party
/// authentication servers ("外置登录", typically a Blessing Skin / LittleSkin skin
/// site). Many Chinese servers only accept players who authenticated against such a
/// site, so the launcher has to:
///
///   1. authenticate (email + password → accessToken + selected profile)
///   2. refresh the token before a launch when it expired
///   3. read the profile's textures so the avatar shows the site skin
///   4. hand the server URL to authlib-injector when starting the game
/// </summary>
public static class YggdrasilAuth
{
    public sealed record Profile(string Id, string Name);

    public sealed record Session(string AccessToken, string ClientToken, Profile Profile, string ServerUrl);

    public sealed record Outcome(bool Ok, string Message, Session? Session, string? SkinUrl, string? CapeUrl);

    /// <summary>Normalises a user supplied site address to its Yggdrasil API root.</summary>
    public static string NormalizeServer(string server)
    {
        var text = (server ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) return "";

        // https://littleskin.cn → https://littleskin.cn/api/yggdrasil
        if (!text.EndsWith("/api/yggdrasil", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
        {
            text += "/api/yggdrasil";
        }

        return text;
    }

    /// <summary>Site metadata (used by authlib-injector and to show the site name).</summary>
    public static async Task<(string Name, string Raw)> FetchMetadataAsync(string server,
        CancellationToken token = default)
    {
        try
        {
            var response = await Http.Client.GetAsync(server, token).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(raw);
            var name = document.RootElement.TryGetProperty("meta", out var meta) &&
                       meta.TryGetProperty("serverName", out var serverName)
                ? serverName.GetString() ?? server
                : server;
            return (name, raw);
        }
        catch (Exception)
        {
            return (server, "");
        }
    }

    /// <summary>Email + password login.</summary>
    public static async Task<Outcome> AuthenticateAsync(string server, string email, string password,
        string? clientToken = null, CancellationToken token = default)
    {
        var root = NormalizeServer(server);
        if (root.Length == 0) return new Outcome(false, "请填写认证服务器地址", null, null, null);

        try
        {
            var payload = new JsonObject
            {
                ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
                ["username"] = email,
                ["password"] = password,
                ["clientToken"] = clientToken ?? Guid.NewGuid().ToString("N"),
                ["requestUser"] = true
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, root + "/authserver/authenticate")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            using var response = await Http.Client.SendAsync(request, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new Outcome(false, DescribeFailure(response.StatusCode, body), null, null, null);
            }

            return ParseSession(body, root, clientToken);
        }
        catch (Exception e)
        {
            return new Outcome(false, "外置登录失败: " + e.Message, null, null, null);
        }
    }

    /// <summary>Token refresh (the Yggdrasil spec requires it after ~24h).</summary>
    public static async Task<Outcome> RefreshAsync(string server, string accessToken, string clientToken,
        CancellationToken token = default)
    {
        var root = NormalizeServer(server);
        try
        {
            var payload = new JsonObject
            {
                ["accessToken"] = accessToken,
                ["clientToken"] = clientToken,
                ["requestUser"] = true
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, root + "/authserver/refresh")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            using var response = await Http.Client.SendAsync(request, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new Outcome(false, DescribeFailure(response.StatusCode, body), null, null, null);
            }

            return ParseSession(body, root, clientToken);
        }
        catch (Exception e)
        {
            return new Outcome(false, "刷新外置登录令牌失败: " + e.Message, null, null, null);
        }
    }

    /// <summary>Checks whether a token is still valid.</summary>
    public static async Task<bool> ValidateAsync(string server, string accessToken, string clientToken,
        CancellationToken token = default)
    {
        try
        {
            var payload = new JsonObject { ["accessToken"] = accessToken, ["clientToken"] = clientToken };
            using var request = new HttpRequestMessage(HttpMethod.Post,
                NormalizeServer(server) + "/authserver/validate")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            using var response = await Http.Client.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads the skin / cape urls of a profile (signed textures property).</summary>
    public static async Task<(string? Skin, string? Cape)> FetchTexturesAsync(string server, string uuid,
        CancellationToken token = default)
    {
        try
        {
            var root = NormalizeServer(server);
            var url = root + "/sessionserver/session/minecraft/profile/" + uuid;
            var json = await Http.ApiClient.GetStringAsync(url, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("properties", out var properties)) return (null, null);

            foreach (var property in properties.EnumerateArray())
            {
                var name = property.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (name != "textures") continue;

                var value = property.GetProperty("value").GetString();
                if (string.IsNullOrEmpty(value)) continue;

                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                using var textures = JsonDocument.Parse(decoded);
                if (!textures.RootElement.TryGetProperty("textures", out var textureRoot)) continue;

                string? skin = null;
                string? cape = null;
                if (textureRoot.TryGetProperty("SKIN", out var skinNode) &&
                    skinNode.TryGetProperty("url", out var skinUrl))
                {
                    skin = skinUrl.GetString();
                }

                if (textureRoot.TryGetProperty("CAPE", out var capeNode) &&
                    capeNode.TryGetProperty("url", out var capeUrl))
                {
                    cape = capeUrl.GetString();
                }

                return (skin, cape);
            }
        }
        catch (Exception)
        {
            // a missing texture is not an error
        }

        return (null, null);
    }

    private static Outcome ParseSession(string body, string root, string? clientToken)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var accessToken = document.RootElement.GetProperty("accessToken").GetString() ?? "";
            var token = document.RootElement.TryGetProperty("clientToken", out var client)
                ? client.GetString() ?? clientToken ?? ""
                : clientToken ?? "";

            var profileNode = document.RootElement.TryGetProperty("selectedProfile", out var selected)
                ? selected
                : default;

            var id = profileNode.ValueKind == JsonValueKind.Object
                ? profileNode.GetProperty("id").GetString() ?? ""
                : "";
            var name = profileNode.ValueKind == JsonValueKind.Object
                ? profileNode.GetProperty("name").GetString() ?? ""
                : "";

            if (id.Length == 0 || name.Length == 0)
            {
                return new Outcome(false, "服务器没有返回角色（可能还没有创建角色）", null, null, null);
            }

            return new Outcome(true, $"登录成功：{name}", new Session(accessToken, token, new Profile(id, name), root),
                null, null);
        }
        catch (Exception e)
        {
            return new Outcome(false, "解析服务器响应失败: " + e.Message, null, null, null);
        }
    }

    /// <summary>Turns a Yggdrasil error response into a readable Chinese message.</summary>
    private static string DescribeFailure(System.Net.HttpStatusCode status, string body)
    {
        var detail = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            detail = document.RootElement.TryGetProperty("errorMessage", out var message)
                ? message.GetString() ?? ""
                : document.RootElement.TryGetProperty("error", out var error)
                    ? error.GetString() ?? ""
                    : "";
        }
        catch (Exception)
        {
            detail = body.Length > 120 ? body[..120] : body;
        }

        var hint = (int)status switch
        {
            401 or 403 => "邮箱或密码不正确",
            404 => "认证服务器地址不正确（应指向皮肤站，例如 https://littleskin.cn）",
            429 => "请求过于频繁，稍后再试",
            _ => $"服务器返回 {(int)status}"
        };

        return detail.Length > 0 ? $"{hint}：{detail}" : hint;
    }
}
