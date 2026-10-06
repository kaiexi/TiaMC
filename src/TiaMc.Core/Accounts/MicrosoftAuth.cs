using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using TiaMc.Core.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMc.Core.Accounts;

public sealed class MicrosoftAuthException : Exception
{
    public MicrosoftAuthException(string message, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Detail = detail;
    }

    public string? Detail { get; }

    public override string ToString() =>
        Detail is { Length: > 0 } ? $"{Message}{Environment.NewLine}{Detail}" : Message;
}

public sealed class DeviceCodeInfo
{
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresInSeconds { get; set; } = 900;
    [JsonPropertyName("interval")] public int IntervalSeconds { get; set; } = 5;
    [JsonPropertyName("message")] public string? Message { get; set; }

    /// <summary>这张设备码对应的令牌端点（必须与签发设备码的端点配对，否则换票必失败）。</summary>
    [JsonIgnore] public string TokenEndpoint { get; set; } = "";
}

public sealed class MicrosoftAuthResult
{
    public required MinecraftAccount Account { get; init; }
    public string? XboxUserId { get; init; }
}

/// <summary>
/// Microsoft (Xbox Live) authentication for Minecraft: Java Edition.
///
/// The flow follows https://minecraft.wiki/w/Microsoft_authentication :
///   1. OAuth 2.0 device code flow (live.com for the built in client id, or the
///      AAD consumers tenant when a custom Azure application id is configured)
///   2. user.auth.xboxlive.com/user/authenticate                  -> Xbox Live token + user hash
///   3. xsts.auth.xboxlive.com/xsts/authorize                     -> XSTS token
///   4. api.minecraftservices.com/authentication/login_with_xbox  -> Minecraft access token
///   5. api.minecraftservices.com/entitlements/mcstore            -> owns the game?
///   6. api.minecraftservices.com/minecraft/profile               -> uuid, name, skin, cape
///
/// The device code flow needs no embedded browser and no client secret: the user
/// opens microsoft.com/link and types a short code.
/// </summary>
public sealed class MicrosoftAuth
{
    /// <summary>
    /// Built in OAuth client id (the live.com device code client used by the
    /// Minecraft launchers). See the README for how to plug in your own Azure
    /// application id instead.
    /// </summary>
    public const string DefaultClientId = "00000000402b5328";

    private const string LiveScope = "service::user.auth.xboxlive.com::MBI_SSL";
    private const string ConsumersScope = "XboxLive.signin offline_access";

    private const string LiveDeviceCodeEndpoint = "https://login.live.com/oauth20_connect.srf";
    private const string LiveTokenEndpoint = "https://login.live.com/oauth20_token.srf";
    private const string ConsumersDeviceCodeEndpoint =
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string ConsumersTokenEndpoint =
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    private readonly HttpClient _http;
    private readonly string _clientId;

    public MicrosoftAuth(HttpClient? http = null, string? clientId = null)
    {
        _http = http ?? Net.Http.ApiClient;
        _clientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId!;
    }

    public string ClientId => _clientId;

    /// <summary>The legacy endpoint is used for the built in client id.</summary>
    private bool UsesLegacyEndpoint => _clientId == DefaultClientId;

    private string TokenEndpoint => UsesLegacyEndpoint ? LiveTokenEndpoint : ConsumersTokenEndpoint;

    // ------------------------------------------------------------ step 1: code

    /// <summary>Requests a device code; show <see cref="DeviceCodeInfo.UserCode"/> to the user.</summary>
    /// <summary>每一步的诊断输出（设备码 / 换票 / Xbox / XSTS / Minecraft / 授权），界面把它写进日志。</summary>
    public Action<string>? Diagnostics { get; set; }

    private static string legacyOf(string endpoint)
        => endpoint == LiveDeviceCodeEndpoint ? LiveTokenEndpoint : ConsumersTokenEndpoint;

    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken token = default)
    {
        // 顺序很关键：内置客户端 ID（00000000402b5328，官方启动器那个）在
        // login.microsoftonline.com/consumers 端点会被拒（实测 HTTP 400），
        // 而 login.live.com 的 MBI_SSL 流程实测可用 —— 所以**固定先走 live.com**，
        // 再把 consumers 当兜底（换成自建 Azure 应用时它才有用）。
        var endpoints = new[] { LiveDeviceCodeEndpoint, ConsumersDeviceCodeEndpoint };

        Exception? last = null;
        var lastDetail = "";

        foreach (var endpoint in endpoints)
        {
            try
            {
                var info = await RequestDeviceCodeFromAsync(endpoint, token).ConfigureAwait(false);
                {
                    // 换票必须用与设备码**同一个**端点，否则会出现 live.com 拿码 / consumers 换票的认证失败
                    info.TokenEndpoint = legacyOf(endpoint);
                    Diagnostics?.Invoke("设备码已获取（端点 " + endpoint + "）");
                    return info;
                }
            }
            catch (MicrosoftAuthException e)
            {
                last = e;
                lastDetail = e.Detail ?? e.Message;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                last = e;
                lastDetail = e.Message;
            }
        }

        throw new MicrosoftAuthException("无法获取 Microsoft 设备代码，请检查网络连接或客户端 ID。",
            lastDetail, last);
    }

    private async Task<DeviceCodeInfo?> RequestDeviceCodeFromAsync(string endpoint, CancellationToken token)
    {
        var legacy = endpoint == LiveDeviceCodeEndpoint;

        HttpResponseMessage response;
        string body;

        if (legacy)
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = LiveScope,
                ["response_type"] = "device_code"
            });
            response = await _http.PostAsync(endpoint, content, token).ConfigureAwait(false);
        }
        else
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = ConsumersScope
            });
            response = await _http.PostAsync(endpoint, content, token).ConfigureAwait(false);
        }

        using (response)
        {
            body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new MicrosoftAuthException(
                    $"设备代码申请失败 (HTTP {(int)response.StatusCode}): {ExtractErrorDescription(body)}", body);
            }
        }

        DeviceCodeInfo? info = null;

        // live.com answers with form encoding, AAD with JSON.
        if (body.TrimStart().StartsWith('{'))
        {
            info = JsonSerializer.Deserialize<DeviceCodeInfo>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        else
        {
            var parsed = ParseFormEncoded(body);
            if (parsed.TryGetValue("device_code", out var deviceCode))
            {
                info = new DeviceCodeInfo
                {
                    DeviceCode = deviceCode,
                    UserCode = parsed.GetValueOrDefault("user_code", ""),
                    VerificationUri = parsed.GetValueOrDefault("verification_uri", "https://microsoft.com/link"),
                    VerificationUriComplete = parsed.GetValueOrDefault("verification_uri_complete"),
                    ExpiresInSeconds = int.TryParse(parsed.GetValueOrDefault("expires_in"), out var expires) ? expires : 900,
                    IntervalSeconds = int.TryParse(parsed.GetValueOrDefault("interval"), out var interval) ? interval : 5
                };
            }
        }

        if (info is null || info.DeviceCode.Length == 0) return null;

        if (string.IsNullOrWhiteSpace(info.VerificationUri)) info.VerificationUri = "https://microsoft.com/link";
        if (string.IsNullOrWhiteSpace(info.VerificationUriComplete) && info.UserCode.Length > 0)
        {
            info.VerificationUriComplete = info.VerificationUri + "?otc=" + info.UserCode;
        }

        if (info.IntervalSeconds <= 0) info.IntervalSeconds = 5;
        return info;
    }

    private static string ExtractErrorDescription(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error_description", out var description))
            {
                return description.GetString() ?? body;
            }

            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Not JSON (form encoded): fall through.
        }

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;
            if (!pair[..index].Equals("error_description", StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(index + 1)..]).Replace('+', ' ');
        }

        return body;
    }

    private static Dictionary<string, string> ParseFormEncoded(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;
            result[Uri.UnescapeDataString(pair[..index])] = Uri.UnescapeDataString(pair[(index + 1)..]).Replace('+', ' ');
        }

        return result;
    }

    // -------------------------------------------------------- step 1: polling

    /// <summary>
    /// Polls the token endpoint until the user finishes signing in.
    /// <paramref name="onWaiting"/> receives the seconds left before the code expires.
    /// </summary>
    public async Task<(string AccessToken, string RefreshToken, int ExpiresIn)> WaitForTokenAsync(
        DeviceCodeInfo info,
        Action<int>? onWaiting = null,
        CancellationToken token = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(2, info.IntervalSeconds));
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, info.ExpiresInSeconds));

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(interval, token).ConfigureAwait(false);

            var remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds);
            onWaiting?.Invoke(remaining);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = info.DeviceCode
            });

            HttpResponseMessage response;
            string body;
            try
            {
                var tokenEndpoint = string.IsNullOrWhiteSpace(info.TokenEndpoint) ? TokenEndpoint : info.TokenEndpoint;
                response = await _http.PostAsync(tokenEndpoint, content, token).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                continue; // transient network problem: keep polling
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var parsed = MicrosoftTokenResponse.Parse(body);
                    if (parsed is { AccessToken.Length: > 0 })
                    {
                        return (parsed.AccessToken, parsed.RefreshToken ?? "", parsed.ExpiresInSeconds);
                    }

                    continue;
                }

                var error = ReadError(body);
                switch (error.Code)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    case "expired_token":
                        throw new MicrosoftAuthException("设备代码已过期，请重新登录。", body);
                    case "authorization_declined":
                        throw new MicrosoftAuthException("用户取消了登录授权。", body);
                    case "bad_verification_code":
                        throw new MicrosoftAuthException("设备代码无效，请重新登录。", body);
                    default:
                        if (!string.IsNullOrEmpty(error.Code))
                        {
                            throw new MicrosoftAuthException(
                                $"Microsoft 登录失败: {error.Description ?? error.Code}", body);
                        }

                        continue;
                }
            }
        }

        throw new MicrosoftAuthException("登录超时，请重新开始登录流程。");
    }

    /// <summary>Refreshes a Microsoft token pair from a stored refresh token.</summary>
    public async Task<(string AccessToken, string RefreshToken, int ExpiresIn)> RefreshMicrosoftTokenAsync(
        string refreshToken, CancellationToken token = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = UsesLegacyEndpoint ? LiveScope : ConsumersScope,
            ["redirect_uri"] = "https://login.live.com/oauth20_desktop.srf"
        });

        using var response = await _http.PostAsync(TokenEndpoint, content, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = ReadError(body);
            throw new MicrosoftAuthException(
                error.Code is "invalid_grant" or "invalid_request"
                    ? "登录状态已失效，请重新使用 Microsoft 账户登录。"
                    : $"刷新 Microsoft 令牌失败: {error.Description ?? error.Code}",
                body);
        }

        var parsed = MicrosoftTokenResponse.Parse(body);
        if (parsed is null || parsed.AccessToken.Length == 0)
        {
            throw new MicrosoftAuthException("刷新 Microsoft 令牌失败: 响应为空", body);
        }

        return (parsed.AccessToken, parsed.RefreshToken ?? refreshToken, parsed.ExpiresInSeconds);
    }

    // ------------------------------------------- steps 2-6: Xbox and Minecraft

    /// <summary>Full chain: Microsoft access token -&gt; Xbox -&gt; XSTS -&gt; Minecraft -&gt; profile.</summary>
    public async Task<MicrosoftAuthResult> AuthenticateAsync(string microsoftAccessToken, string refreshToken,
        CancellationToken token = default)
    {
        var (xblToken, userHash) = await XboxLiveAuthenticateAsync(microsoftAccessToken, token).ConfigureAwait(false);
        var xsts = await XstsAuthorizeAsync(xblToken, token).ConfigureAwait(false);
        var (mcAccessToken, expiresIn) = await MinecraftLoginAsync(userHash, xsts, token).ConfigureAwait(false);
        var owns = await CheckEntitlementsAsync(mcAccessToken, token).ConfigureAwait(false);
        var profile = await GetProfileAsync(mcAccessToken, token).ConfigureAwait(false);

        var account = new MinecraftAccount
        {
            Kind = AccountKind.Microsoft,
            Name = profile.Name,
            Uuid = profile.Uuid,
            AccessToken = mcAccessToken,
            UserType = "msa",
            RefreshToken = refreshToken,
            TokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn),
            XboxUserHash = userHash,
            OwnsGame = owns,
            SkinUrl = profile.SkinUrl,
            SkinVariant = profile.SkinVariant,
            CapeUrl = profile.CapeUrl,
            LastLoginUtc = DateTime.UtcNow
        };

        return new MicrosoftAuthResult { Account = account };
    }

    /// <summary>End to end login: request a code, wait for the user, resolve the account.</summary>
    public async Task<MicrosoftAuthResult> LoginAsync(
        Action<DeviceCodeInfo> onCodeReady,
        Action<int>? onWaiting = null,
        CancellationToken token = default)
    {
        var info = await RequestDeviceCodeAsync(token).ConfigureAwait(false);
        onCodeReady(info);

        var (accessToken, refreshToken, _) = await WaitForTokenAsync(info, onWaiting, token).ConfigureAwait(false);
        return await AuthenticateAsync(accessToken, refreshToken, token).ConfigureAwait(false);
    }

    /// <summary>Renews an existing Microsoft account (uses its refresh token).</summary>
    public async Task<MicrosoftAuthResult> RefreshAccountAsync(MinecraftAccount account,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            throw new MicrosoftAuthException("该账户没有刷新令牌，请重新登录。");
        }

        var (accessToken, refreshToken, _) = await RefreshMicrosoftTokenAsync(account.RefreshToken!, token)
            .ConfigureAwait(false);
        return await AuthenticateAsync(accessToken, refreshToken, token).ConfigureAwait(false);
    }

    private async Task<(string Token, string UserHash)> XboxLiveAuthenticateAsync(
        string microsoftAccessToken, CancellationToken token)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + microsoftAccessToken
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://user.auth.xboxlive.com/user/authenticate")
        {
            Content = JsonContent(payload)
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new MicrosoftAuthException("Xbox Live 认证失败（步骤 2/6）", body);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var xblToken = root.GetProperty("Token").GetString() ?? "";
        var userHash = ReadUserHash(root);

        if (string.IsNullOrEmpty(xblToken) || string.IsNullOrEmpty(userHash))
        {
            throw new MicrosoftAuthException("Xbox Live 返回的数据不完整（步骤 2/6）", body);
        }

        return (xblToken, userHash);
    }

    private async Task<string> XstsAuthorizeAsync(string xblToken, CancellationToken token)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://xsts.auth.xboxlive.com/xsts/authorize")
        {
            Content = JsonContent(payload)
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if ((int)response.StatusCode == 401)
        {
            throw new MicrosoftAuthException(DescribeXstsError(body), body);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new MicrosoftAuthException("XSTS 授权失败（步骤 3/6）", body);
        }

        using var doc = JsonDocument.Parse(body);
        var xsts = doc.RootElement.GetProperty("Token").GetString() ?? "";
        if (string.IsNullOrEmpty(xsts))
        {
            throw new MicrosoftAuthException("XSTS 返回的令牌为空（步骤 3/6）", body);
        }

        return xsts;
    }

    /// <summary>Turns the numeric XErr reported by XSTS into an actionable message.</summary>
    private static string DescribeXstsError(string body)
    {
        string? xerr = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("XErr", out var value))
            {
                xerr = value.ValueKind == JsonValueKind.Number
                    ? value.GetInt64().ToString()
                    : value.GetString();
            }
        }
        catch (JsonException)
        {
            // Keep the generic message.
        }

        return xerr switch
        {
            "2148916227" => "该账户已被 Xbox Live 封禁。",
            "2148916233" => "该 Microsoft 账户还没有 Xbox 账户，请先在 minecraft.net 登录一次以创建。",
            "2148916235" => "该账户所在国家/地区不支持 Xbox Live。",
            "2148916236" or "2148916237" => "该账户需要在 Xbox 页面完成成年人验证（韩国）。",
            "2148916238" => "该账户是未成年人账户，需要由成年人加入 Microsoft 家庭组后才能登录。",
            "2148916262" => "Xbox Live 返回了未知错误 (2148916262)，请改用其它账户或稍后重试。",
            _ => $"XSTS 授权失败（步骤 3/6）: XErr={xerr ?? "未知"}"
        };
    }

    private async Task<(string AccessToken, int ExpiresIn)> MinecraftLoginAsync(string userHash, string xstsToken,
        CancellationToken token)
    {
        var payload = new
        {
            identityToken = $"XBL3.0 x={userHash};{xstsToken}"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.minecraftservices.com/authentication/login_with_xbox")
        {
            Content = JsonContent(payload)
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode == HttpStatusCode.Forbidden
                ? "Minecraft 服务拒绝登录 (403)，该 OAuth 客户端没有 Minecraft API 权限。"
                : $"Minecraft 登录失败（步骤 4/6, HTTP {(int)response.StatusCode}）";
            throw new MicrosoftAuthException(message, body);
        }

        using var doc = JsonDocument.Parse(body);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 86400;

        if (string.IsNullOrEmpty(accessToken))
        {
            throw new MicrosoftAuthException("Minecraft 返回的访问令牌为空（步骤 4/6）", body);
        }

        return (accessToken, expiresIn);
    }

    /// <summary>Checks the mcstore entitlements; an empty item list means the game is not owned.</summary>
    public async Task<bool> CheckEntitlementsAsync(string minecraftAccessToken, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.minecraftservices.com/entitlements/mcstore");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + minecraftAccessToken);

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;

        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return false;
            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is "game_minecraft" or "product_minecraft") return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把本地皮肤上传到正版账号（Mojang），这样**进游戏/进服务器也是这个皮肤**。
    ///
    /// 官方接口：POST https://api.minecraftservices.com/minecraft/profile/skins
    ///   multipart/form-data：variant = classic|slim，file = PNG
    /// 上传成功后需要一点时间在全球生效（官方文档：最长约 1 分钟）。
    /// </summary>
    public async Task<(bool Ok, string Message)> UploadSkinAsync(string minecraftAccessToken,
        byte[] png, string variant = "classic", CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftAccessToken)) return (false, "没有可用的正版令牌，请先登录");
        if (png is null || png.Length < 64) return (false, "皮肤数据为空");

        try
        {
            using var content = new System.Net.Http.MultipartFormDataContent();
            content.Add(new System.Net.Http.StringContent(
                variant.Equals("slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic"), "variant");

            var file = new System.Net.Http.ByteArrayContent(png);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            content.Add(file, "file", "skin.png");

            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post,
                "https://api.minecraftservices.com/minecraft/profile/skins")
            {
                Content = content
            };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", minecraftAccessToken);

            using var response = await Http.Client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return (false, $"上传失败 HTTP {(int)response.StatusCode}：{Trim(body, 200)}");
            }

            return (true, "皮肤已上传到正版账号，最长约 1 分钟后在游戏与服务器生效");
        }
        catch (Exception e)
        {
            return (false, "上传异常: " + e.Message);
        }
    }

    private static string Trim(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";
    public sealed record MinecraftProfile(string Uuid, string Name, string? SkinUrl, string? SkinVariant,
        string? CapeUrl);

    public async Task<MinecraftProfile> GetProfileAsync(string minecraftAccessToken, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + minecraftAccessToken);

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new MicrosoftAuthException(
                "该账户没有 Minecraft: Java Edition 档案（未购买游戏或从未登录过官方启动器）。", body);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new MicrosoftAuthException($"读取 Minecraft 档案失败（步骤 6/6, HTTP {(int)response.StatusCode}）", body);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var uuid = root.GetProperty("id").GetString() ?? "";
        var name = root.GetProperty("name").GetString() ?? "";

        string? skinUrl = null, skinVariant = null, capeUrl = null;
        if (root.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array)
        {
            foreach (var skin in skins.EnumerateArray())
            {
                var state = skin.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (!string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase)) continue;
                skinUrl = skin.TryGetProperty("url", out var u) ? u.GetString() : null;
                skinVariant = skin.TryGetProperty("variant", out var v) ? v.GetString() : null;
                break;
            }
        }

        if (root.TryGetProperty("capes", out var capes) && capes.ValueKind == JsonValueKind.Array)
        {
            foreach (var cape in capes.EnumerateArray())
            {
                var state = cape.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (!string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase)) continue;
                capeUrl = cape.TryGetProperty("url", out var u) ? u.GetString() : null;
                break;
            }
        }

        return new MinecraftProfile(uuid, name, skinUrl, skinVariant, capeUrl);
    }

    // ------------------------------------------------------------- utilities

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string ReadUserHash(JsonElement root)
    {
        if (!root.TryGetProperty("DisplayClaims", out var claims)) return "";
        if (!claims.TryGetProperty("xui", out var xui) || xui.ValueKind != JsonValueKind.Array) return "";

        foreach (var entry in xui.EnumerateArray())
        {
            if (entry.TryGetProperty("uhs", out var uhs)) return uhs.GetString() ?? "";
        }

        return "";
    }

    private static (string? Code, string? Description) ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return (code, description);
        }
        catch (JsonException)
        {
            var map = ParseFormEncoded(body);
            if (map.Count == 0) return (null, null);
            return (map.GetValueOrDefault("error"), map.GetValueOrDefault("error_description"));
        }
    }
}
