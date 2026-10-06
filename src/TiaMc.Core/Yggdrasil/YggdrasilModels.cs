using System.Text.Json.Serialization;

namespace TiaMc.Core.Yggdrasil;

/// <summary>Persisted server settings.</summary>
public sealed class YggdrasilSettings
{
    [JsonPropertyName("port")] public int Port { get; set; } = 25566;
    /// <summary>"local" binds 127.0.0.1 only, "any" binds every interface (needs admin on Windows).</summary>
    [JsonPropertyName("bind")] public string Bind { get; set; } = "local";
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "TiaMC Yggdrasil Server";
    [JsonPropertyName("skinDomains")] public List<string> SkinDomains { get; set; } = [];
    /// <summary>Access token lifetime in days.</summary>
    [JsonPropertyName("tokenDays")] public int TokenDays { get; set; } = 15;
    [JsonPropertyName("nonEmailLogin")] public bool NonEmailLogin { get; set; } = true;
}

public sealed class YggdrasilUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("passwordHash")] public string PasswordHash { get; set; } = "";
    [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    [JsonPropertyName("profiles")] public List<string> Profiles { get; set; } = [];
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class YggdrasilProfile
{
    /// <summary>Unsigned uuid (32 hex characters), derived from the name like the offline launcher.</summary>
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    /// <summary>"default" or "slim".</summary>
    [JsonPropertyName("model")] public string Model { get; set; } = "default";
    [JsonPropertyName("skinHash")] public string? SkinHash { get; set; }
    [JsonPropertyName("capeHash")] public string? CapeHash { get; set; }
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class YggdrasilToken
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("clientToken")] public string ClientToken { get; set; } = "";
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("profileUuid")] public string? ProfileUuid { get; set; }
    [JsonPropertyName("issuedUtc")] public DateTime IssuedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("expiresUtc")] public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow.AddDays(15);
    [JsonPropertyName("valid")] public bool Valid { get; set; } = true;
    [JsonPropertyName("uses")] public int Uses { get; set; }
}

/// <summary>Everything the server persists, one JSON file next to the launcher.</summary>
public sealed class YggdrasilStore
{
    [JsonPropertyName("settings")] public YggdrasilSettings Settings { get; set; } = new();
    [JsonPropertyName("users")] public List<YggdrasilUser> Users { get; set; } = [];
    [JsonPropertyName("profiles")] public List<YggdrasilProfile> Profiles { get; set; } = [];
    [JsonPropertyName("tokens")] public List<YggdrasilToken> Tokens { get; set; } = [];
    /// <summary>PEM PKCS#1 private key used to sign profile properties.</summary>
    [JsonPropertyName("privateKeyPem")] public string? PrivateKeyPem { get; set; }
    [JsonPropertyName("publicKeyPem")] public string? PublicKeyPem { get; set; }
}

// ------------------------------------------------------------------ API DTOs

public sealed class YggAuthenticateRequest
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("clientToken")] public string? ClientToken { get; set; }
    [JsonPropertyName("requestUser")] public bool RequestUser { get; set; }
    [JsonPropertyName("agent")] public YggAgent? Agent { get; set; }
}

public sealed class YggAgent
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public int Version { get; set; }
}

public sealed class YggRefreshRequest
{
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("clientToken")] public string? ClientToken { get; set; }
    [JsonPropertyName("requestUser")] public bool RequestUser { get; set; }
    [JsonPropertyName("selectedProfile")] public YggProfileRef? SelectedProfile { get; set; }
}

public sealed class YggProfileRef
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class YggTokenRequest
{
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("clientToken")] public string? ClientToken { get; set; }
}

public sealed class YggSignoutRequest
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
}

public sealed class YggJoinRequest
{
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("selectedProfile")] public string? SelectedProfile { get; set; }
    [JsonPropertyName("serverId")] public string? ServerId { get; set; }
}

public sealed class YggProperty
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("signature")] public string? Signature { get; set; }
}

public sealed class YggProfileResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("properties")] public List<YggProperty>? Properties { get; set; }
}

public sealed class YggUserResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("properties")] public List<YggProperty> Properties { get; set; } = [];
}

public sealed class YggAuthenticateResponse
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("clientToken")] public string ClientToken { get; set; } = "";
    [JsonPropertyName("availableProfiles")] public List<YggProfileResponse> AvailableProfiles { get; set; } = [];
    [JsonPropertyName("selectedProfile")] public YggProfileResponse? SelectedProfile { get; set; }
    [JsonPropertyName("user")] public YggUserResponse? User { get; set; }
}

public sealed class YggRefreshResponse
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("clientToken")] public string ClientToken { get; set; } = "";
    [JsonPropertyName("selectedProfile")] public YggProfileResponse? SelectedProfile { get; set; }
    [JsonPropertyName("user")] public YggUserResponse? User { get; set; }
}

public sealed class YggError
{
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("errorMessage")] public string ErrorMessage { get; set; } = "";
    [JsonPropertyName("cause")] public string? Cause { get; set; }

    public static YggError Forbidden(string message) => new()
    {
        Error = "ForbiddenOperationException",
        ErrorMessage = message
    };

    public static YggError Illegal(string message) => new()
    {
        Error = "IllegalArgumentException",
        ErrorMessage = message
    };
}

/// <summary>Textures payload that is base64 encoded into the profile "textures" property.</summary>
public sealed class YggTexturesPayload
{
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    [JsonPropertyName("profileId")] public string ProfileId { get; set; } = "";
    [JsonPropertyName("profileName")] public string ProfileName { get; set; } = "";
    [JsonPropertyName("textures")] public Dictionary<string, YggTexture> Textures { get; set; } = [];
}

public sealed class YggTexture
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("metadata")] public Dictionary<string, string>? Metadata { get; set; }
}
