using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TiaMc.Core.Accounts;

public enum AccountKind
{
    /// <summary>Offline / cracked login: the UUID is derived from the name.</summary>
    Offline,

    /// <summary>A real Microsoft (Xbox Live) account, authenticated through the OAuth device code flow.</summary>
    Microsoft,

    /// <summary>
    /// 外置登录: authenticated against a third party Yggdrasil server (a skin site
    /// such as LittleSkin). Servers that require it are joined with authlib-injector.
    /// </summary>
    Yggdrasil
}

/// <summary>
/// A launcher account, ready to be turned into game arguments. The launch
/// pipeline only needs Name / Uuid / AccessToken / UserType, which is why
/// adding a new authentication scheme does not touch the argument builder
/// (this is the same abstraction ColorMC uses with its LoginObj).
/// </summary>
public sealed class MinecraftAccount
{
    [JsonPropertyName("kind")] public AccountKind Kind { get; set; } = AccountKind.Offline;

    /// <summary>In-game name (for Microsoft accounts this is the Minecraft profile name).</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "Player";

    /// <summary>UUID without dashes, exactly as the launcher passes it to --uuid.</summary>
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";

    /// <summary>Minecraft access token, or "0" for offline accounts.</summary>
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "0";

    /// <summary>"msa" for Microsoft accounts, "legacy" (or "mojang") otherwise.</summary>
    [JsonPropertyName("userType")] public string UserType { get; set; } = "legacy";

    /// <summary>Microsoft refresh token (Microsoft accounts only).</summary>
    [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }

    /// <summary>Yggdrasil API root of a 外置登录 account (…/api/yggdrasil).</summary>
    [JsonPropertyName("authServer")] public string? AuthServer { get; set; }

    /// <summary>Client token of the Yggdrasil session (needed for refresh/validate).</summary>
    [JsonPropertyName("clientToken")] public string? ClientToken { get; set; }

    /// <summary>Login name (email) of a 外置登录 account, kept for the UI only.</summary>
    [JsonPropertyName("loginName")] public string? LoginName { get; set; }

    /// <summary>Display name of the skin site, e.g. LittleSkin.</summary>
    [JsonPropertyName("authServerName")] public string? AuthServerName { get; set; }

    [JsonPropertyName("tokenExpiresUtc")] public DateTime? TokenExpiresUtc { get; set; }

    /// <summary>Xbox user hash, needed to build the XBL3.0 identity token when refreshing.</summary>
    [JsonPropertyName("xboxUserHash")] public string? XboxUserHash { get; set; }

    /// <summary>Whether the account owns Minecraft (checked through the entitlements endpoint).</summary>
    [JsonPropertyName("ownsGame")] public bool OwnsGame { get; set; } = true;

    /// <summary>Skin texture URL from the Minecraft profile.</summary>
    [JsonPropertyName("skinUrl")] public string? SkinUrl { get; set; }

    /// <summary>"CLASSIC" or "SLIM".</summary>
    [JsonPropertyName("skinVariant")] public string? SkinVariant { get; set; }

    [JsonPropertyName("capeUrl")] public string? CapeUrl { get; set; }

    /// <summary>
    /// Local skin file of an offline account (64x64 or 64x32 PNG). Offline accounts
    /// cannot upload to Mojang, so the launcher keeps the file itself and serves it
    /// through the built-in Yggdrasil server (authlib-injector) and the avatar tiles.
    /// </summary>
    [JsonPropertyName("skinPath")] public string? SkinPath { get; set; }

    /// <summary>"default" (classic) or "slim".</summary>
    [JsonPropertyName("skinModel")] public string SkinModel { get; set; } = "default";

    /// <summary>Local cape file.</summary>
    [JsonPropertyName("capePath")] public string? CapePath { get; set; }

    /// <summary>Source used by the avatar: the online URL first, else the local skin.</summary>
    [JsonIgnore]
    public string? SkinSource => !string.IsNullOrWhiteSpace(SkinUrl) ? SkinUrl
        : !string.IsNullOrWhiteSpace(SkinPath) && File.Exists(SkinPath) ? SkinPath
        : null;

    [JsonIgnore]
    public bool HasLocalSkin => !string.IsNullOrWhiteSpace(SkinPath) && File.Exists(SkinPath);
    [JsonPropertyName("lastLoginUtc")] public DateTime? LastLoginUtc { get; set; }

    /// <summary>True when the access token is missing or within five minutes of expiry.</summary>
    [JsonIgnore]
    public bool NeedsRefresh =>
        Kind == AccountKind.Microsoft &&
        (string.IsNullOrWhiteSpace(AccessToken)
         || TokenExpiresUtc is null
         || TokenExpiresUtc.Value <= DateTime.UtcNow.AddMinutes(5));

    [JsonIgnore] public string KindText => Kind switch
    {
        AccountKind.Microsoft => "正版",
        AccountKind.Yggdrasil => "外置登录",
        _ => "离线"
    };

    /// <summary>True for accounts that must start the game with authlib-injector.</summary>
    [JsonIgnore]
    public bool NeedsAuthlibInjector => Kind == AccountKind.Yggdrasil && !string.IsNullOrWhiteSpace(AuthServer);

    [JsonIgnore]
    public string Display =>
        $"{Name}  ({(Kind == AccountKind.Microsoft ? "Microsoft 正版" : "离线")})";

    [JsonIgnore]
    public string ShortUuid => Uuid.Length >= 8 ? Uuid[..8] : Uuid;

    [JsonIgnore]
    public string Subtitle => Kind == AccountKind.Microsoft
        ? $"Microsoft  ·  {ShortUuid}  ·  {(OwnsGame ? "已拥有游戏" : "无游戏许可")}"
        : $"离线账户  ·  {ShortUuid}";

    /// <summary>Creates an offline account, deriving a stable UUID from the name.</summary>
    public static MinecraftAccount CreateOffline(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        return new MinecraftAccount
        {
            Kind = AccountKind.Offline,
            Name = name,
            Uuid = OfflineUuid(name).ToString("N"),
            AccessToken = "0",
            UserType = "legacy",
            OwnsGame = true,
            LastLoginUtc = DateTime.UtcNow
        };
    }

    /// <summary>Deterministic UUID shared with the official launcher for offline profiles.</summary>
    public static Guid OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        // Set the version/variant bits so the value is a well formed UUID.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    /// <summary>Formatted uuid (8-4-4-4-12) for display; --uuid uses <see cref="Uuid"/>.</summary>
    [JsonIgnore]
    public string FormattedUuid =>
        Guid.TryParse(Uuid, out var guid) ? guid.ToString() : OfflineUuid(Name).ToString();

    /// <summary>Key used to find the same account again (name + kind).</summary>
    [JsonIgnore]
    public string Key => $"{Kind}:{Name}".ToLowerInvariant();

    public MinecraftAccount Clone() => new()
    {
        Kind = Kind,
        Name = Name,
        Uuid = Uuid,
        AccessToken = AccessToken,
        UserType = UserType,
        RefreshToken = RefreshToken,
        TokenExpiresUtc = TokenExpiresUtc,
        XboxUserHash = XboxUserHash,
        OwnsGame = OwnsGame,
        SkinUrl = SkinUrl,
        SkinVariant = SkinVariant,
        CapeUrl = CapeUrl,
        LastLoginUtc = LastLoginUtc
    };
}
