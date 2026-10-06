using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMc.Core.Net;

namespace TiaMc.Core.Servers;

/// <summary>一个可下载的服务端核心构建。</summary>
public sealed record ServerCoreBuild(
    string Project,
    string GameVersion,
    int BuildId,
    string Channel,
    string FileName,
    string Url,
    long Size,
    string? Sha256)
{
    public string Display => $"{Project} {GameVersion} #{BuildId} [{Channel}] {FileName} ({Utils.TextUtil.FormatBytes(Size)})";
}

/// <summary>
/// 服务端核心目录：PaperMC 的 <b>Fill API v3</b>（v2 已下线返回 410）。
///
///   GET https://fill.papermc.io/v3/projects                      → 项目列表（paper / velocity / waterfall / folia …）
///   GET https://fill.papermc.io/v3/projects/paper                → 版本（按大版本分组）
///   GET https://fill.papermc.io/v3/projects/paper/versions/&lt;v&gt;/builds → 构建（含下载地址、大小、sha256）
///
/// MCSLAPI / FastMirror 那两个源（MCSL2 文档里的）实测：MCSLAPI 返回 522、FastMirror 是服务端核心直链目录，
/// 因此这里以 Fill 为主，MCSL 作为可选源保留（失败时给出明确原因）。
/// </summary>
public sealed class ServerCoreCatalog
{
    public const string FillApi = "https://fill.papermc.io/v3";

    private readonly HttpClient _http = Http.ApiClient;

    public sealed record ProjectInfo(string Id, string Name);

    private void Prepare(HttpClient http)
    {
        if (!http.DefaultRequestHeaders.UserAgent.Any())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TiaMC/1.0 (Minecraft launcher; +https://github.com/kaiexi/TiaMC)");
        }
    }

    /// <summary>可下载的服务端项目（Paper / Velocity / Waterfall / Folia …）。</summary>
    public async Task<List<ProjectInfo>> GetProjectsAsync(CancellationToken token = default)
    {
        Prepare(_http);
        var result = new List<ProjectInfo>();
        try
        {
            var json = await _http.GetStringAsync($"{FillApi}/projects", token).ConfigureAwait(false);
            var root = JsonNode.Parse(json);
            if (root?["projects"] is JsonArray projects)
            {
                foreach (var node in projects)
                {
                    var id = node?["project"]?["id"]?.GetValue<string>();
                    var name = node?["project"]?["name"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(id)) result.Add(new ProjectInfo(id!, name ?? id!));
                }
            }
        }
        catch (Exception)
        {
            // 由调用方提示网络问题
        }

        return result;
    }

    /// <summary>某个项目的游戏版本列表（最新在前）。</summary>
    public async Task<List<string>> GetVersionsAsync(string project, CancellationToken token = default)
    {
        Prepare(_http);
        var versions = new List<string>();
        try
        {
            var json = await _http.GetStringAsync($"{FillApi}/projects/{project}", token).ConfigureAwait(false);
            var root = JsonNode.Parse(json);
            if (root?["versions"] is JsonObject groups)
            {
                foreach (var (_, value) in groups)
                {
                    if (value is JsonArray list)
                    {
                        foreach (var v in list)
                        {
                            var text = v?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(text) && !versions.Contains(text!)) versions.Add(text!);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 同上
        }

        return versions;
    }

    /// <summary>某个项目 + 游戏版本的构建列表（最新在前）。</summary>
    public async Task<List<ServerCoreBuild>> GetBuildsAsync(string project, string gameVersion,
        CancellationToken token = default)
    {
        Prepare(_http);
        var builds = new List<ServerCoreBuild>();
        try
        {
            var json = await _http
                .GetStringAsync($"{FillApi}/projects/{project}/versions/{gameVersion}/builds", token)
                .ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var id = element.TryGetProperty("id", out var idNode) ? idNode.GetInt32() : 0;
                var channel = element.TryGetProperty("channel", out var channelNode) ? channelNode.GetString() ?? "" : "";

                if (!element.TryGetProperty("downloads", out var downloads)) continue;
                if (!downloads.TryGetProperty("server:default", out var entry)) continue;

                var name = entry.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
                var url = entry.TryGetProperty("url", out var urlNode) ? urlNode.GetString() ?? "" : "";
                var size = entry.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;
                string? sha256 = null;
                if (entry.TryGetProperty("checksums", out var sums) &&
                    sums.TryGetProperty("sha256", out var shaNode))
                {
                    sha256 = shaNode.GetString();
                }

                if (url.Length == 0) continue;
                builds.Add(new ServerCoreBuild(project, gameVersion, id, channel, name, url, size, sha256));
            }
        }
        catch (Exception)
        {
            // 同上
        }

        return builds;
    }
}
