using TiaMc.Core.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMc.Core.Mods;

/// <summary>A downloadable mod version offered by Modrinth.</summary>
public sealed class ModrinthFile
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string FileName { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = [];
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("date_published")] public DateTime? DatePublished { get; set; }
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = [];

    /// <summary>The file that should be installed (primary, or the first one).</summary>
    [JsonIgnore]
    public ModrinthFile? MainFile =>
        Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();

    [JsonIgnore]
    public string LoadersText => Loaders.Count == 0 ? "-" : string.Join(", ", Loaders);

    [JsonIgnore]
    public string SizeText => MainFile is null ? "-" : Utils.TextUtil.FormatBytes(MainFile.Size);
}

/// <summary>
/// Minimal Modrinth client (https://docs.modrinth.com/api/).
/// Only the two read-only endpoints needed by the mod browser are implemented:
/// the version list of a project and the search endpoint.
/// </summary>
public sealed class ModrinthClient
{
    public const string OfficialBaseUrl = "https://api.modrinth.com/v2";

    /// <summary>当前使用的 Modrinth 接口基址（可在设置里换成自建/合作镜像）。</summary>
    private readonly string _baseUrl;

    /// <summary>回退基址：主基址失败时再试一次（例如自定义镜像挂了就回官方）。</summary>
    private readonly string? _fallbackBaseUrl;

    public ModrinthClient(string? baseUrl = null, string? fallbackBaseUrl = null, HttpClient? http = null)
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? OfficialBaseUrl : baseUrl!.TrimEnd('/');
        _fallbackBaseUrl = string.IsNullOrWhiteSpace(fallbackBaseUrl) ? null : fallbackBaseUrl!.TrimEnd('/');
        _http = http ?? Net.Http.Client;

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            // Modrinth asks every client to identify itself.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("TiaMC/1.0 (Minecraft launcher; +https://github.com/)");
        }
    }

    private readonly HttpClient _http;

    /// <summary>
    /// Lists the versions of a project (slug or id) filtered by game version and
    /// mod loader. Loader names are Modrinth's own: forge, neoforge, fabric, quilt.
    /// </summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(string project, string? gameVersion,
        string? loader, CancellationToken token = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));
        }

        if (!string.IsNullOrWhiteSpace(loader) && !loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
        {
            query.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() })));
        }

        var url = $"{_baseUrl}/project/{Uri.EscapeDataString(project.Trim())}/version";
        if (query.Count > 0) url += "?" + string.Join("&", query);

        using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? $"Modrinth 上找不到项目 “{project}”"
                    : $"Modrinth 返回 HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<List<ModrinthVersion>>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        return list
            .OrderByDescending(v => v.DatePublished ?? DateTime.MinValue)
            .ToList();
    }

    /// <summary>Downloads one mod file into the mods folder (never overwrites).</summary>
    public async Task<string> DownloadAsync(ModrinthFile file, string modsDirectory,
        IProgress<long>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(modsDirectory);
        var target = Path.Combine(modsDirectory, file.FileName);

        if (File.Exists(target))
        {
            var stem = Path.GetFileNameWithoutExtension(file.FileName);
            var extension = Path.GetExtension(file.FileName);
            var index = 1;
            do
            {
                target = Path.Combine(modsDirectory, $"{stem} ({index}){extension}");
                index++;
            } while (File.Exists(target));
        }

        using var response = await _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var temp = target + ".tiamc-download";
        await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var destination = File.Create(temp))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
        }

        if (File.Exists(target)) File.Delete(target);
        File.Move(temp, target);
        return target;
    }

    /// <summary>Maps a launcher loader name to Modrinth's loader facet.</summary>
    public static string? LoaderFacet(string loaderText) => loaderText.ToLowerInvariant() switch
    {
        "forge" => "forge",
        "neoforge" => "neoforge",
        "fabric" => "fabric",
        "quilt" => "quilt",
        _ => null
    };
}

public sealed class ModrinthSearchHit
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = [];

    [JsonIgnore] public string Display => $"{Title}  ({Slug})";
}
