using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMc.Core.Accounts;

/// <summary>OAuth token payload; also used to read the live.com form encoded replies.</summary>
internal sealed class MicrosoftTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresInSeconds { get; set; } = 3600;
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }

    /// <summary>Parses either the JSON (AAD) or form encoded (live.com) token reply.</summary>
    public static MicrosoftTokenResponse? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        if (body.TrimStart().StartsWith('{'))
        {
            try
            {
                return JsonSerializer.Deserialize<MicrosoftTokenResponse>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;
            map[Uri.UnescapeDataString(pair[..index])] = Uri.UnescapeDataString(pair[(index + 1)..]).Replace('+', ' ');
        }

        if (!map.TryGetValue("access_token", out var accessToken)) return null;

        return new MicrosoftTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = map.GetValueOrDefault("refresh_token"),
            ExpiresInSeconds = int.TryParse(map.GetValueOrDefault("expires_in"), out var expires) ? expires : 3600,
            TokenType = map.GetValueOrDefault("token_type"),
            Scope = map.GetValueOrDefault("scope")
        };
    }
}
