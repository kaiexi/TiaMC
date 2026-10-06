using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMc.Core.Resources;

/// <summary>The kinds of content the download center offers.</summary>
public enum ResourceKind
{
    Mod,
    Modpack,
    ResourcePack,
    Shader,
    Datapack,
    World
}

public static class ResourceKindExtensions
{
    /// <summary>Modrinth project_type facet value.</summary>
    public static string ToFacet(this ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => "mod",
        ResourceKind.Modpack => "modpack",
        ResourceKind.ResourcePack => "resourcepack",
        ResourceKind.Shader => "shader",
        ResourceKind.Datapack => "datapack",
        ResourceKind.World => "world",
        _ => "mod"
    };

    public static string ToChinese(this ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => "模组",
        ResourceKind.Modpack => "整合包",
        ResourceKind.ResourcePack => "资源包",
        ResourceKind.Shader => "光影",
        ResourceKind.Datapack => "数据包",
        ResourceKind.World => "世界",
        _ => "资源"
    };

    /// <summary>Folder inside an instance the files belong to.</summary>
    public static string ToInstanceFolder(this ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => "mods",
        ResourceKind.Modpack => "",
        ResourceKind.ResourcePack => "resourcepacks",
        ResourceKind.Shader => "shaderpacks",
        ResourceKind.Datapack => "datapacks",
        ResourceKind.World => "saves",
        _ => "mods"
    };
}

/// <summary>One search result row.</summary>
public sealed class ResourceHit
{
    public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public long Downloads { get; set; }
    public long Follows { get; set; }
    public string IconUrl { get; set; } = "";
    public List<string> Categories { get; set; } = [];
    public List<string> GameVersions { get; set; } = [];
    public string ProjectType { get; set; } = "";
    public string Updated { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public ResourceKind Kind { get; set; } = ResourceKind.Mod;

    [JsonIgnore] public string DownloadsText => Downloads >= 1_000_000
        ? $"{Downloads / 1_000_000.0:0.#}M"
        : Downloads >= 1000
            ? $"{Downloads / 1000.0:0.#}k"
            : Downloads.ToString();

    [JsonIgnore] public string ChineseName { get; set; } = "";

    [JsonIgnore]
    public string TitleText => ChineseName.Length > 0 ? $"{ChineseName}（{Title}）" : Title;

    [JsonIgnore] public string Subtitle => $"{Author} · {DownloadsText} 次下载 · {string.Join(", ", Categories.Take(3))}";
}

/// <summary>One downloadable file of a project.</summary>
public sealed class ResourceFile
{
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha1 { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string VersionName { get; set; } = "";
    public string VersionType { get; set; } = "";
    public List<string> GameVersions { get; set; } = [];
    public List<string> Loaders { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];
    public DateTime PublishedUtc { get; set; }

    [JsonIgnore] public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024.0 / 1024:F1} MB"
        : $"{Size / 1024.0:F0} KB";

    [JsonIgnore]
    public string Label => $"{VersionNumber}  ·  {SizeText}  ·  {string.Join('/', Loaders)}  " +
                           $"[{string.Join('/', GameVersions.Take(3))}]";
}

/// <summary>
/// Content catalogue for mods, modpacks, resource packs and shaders.
///
/// Modrinth is the primary source (one open API for every project type, see the
/// project_type facet) and the layout is inspired by Axolotl's browse pages:
/// category + game version + loader filters, Chinese name lookup, and a
/// "download into this instance" action per file.
/// </summary>
public sealed class ResourceCatalog
{
    public const string OfficialApi = "https://api.modrinth.com/v2";

    /// <summary>
    /// Modrinth 接口基址：默认官方，可在设置里改成自建/合作镜像（国内直连官方常失败）。
    /// 自定义基址请求失败时会自动回退官方，避免"换个设备就搜不到模组"。
    /// </summary>
    public string BaseUrl { get; set; } = OfficialApi;

    private string Api(string path) => BaseUrl.TrimEnd('/') + path;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Chinese name dictionary (Axolotl compatible format).</summary>
    public ChineseDictionary Chinese { get; }

    public ResourceCatalog(ChineseDictionary? chinese = null) => Chinese = chinese ?? ChineseDictionary.Load();

    // ------------------------------------------------------------- searching

    /// <summary>Searches a project type, optionally filtered by game version and loader.</summary>
    public async Task<List<ResourceHit>> SearchAsync(ResourceKind kind, string query, string? gameVersion = null,
        string? loader = null, int limit = 30, int offset = 0, CancellationToken token = default)
    {
        // A Chinese query is translated through the dictionary first (Axolotl does
        // the same with its bundled WikiEntries table).
        var effectiveQuery = query;
        var titleHint = "";
        if (!string.IsNullOrWhiteSpace(query) && Chinese.ContainsChinese(query))
        {
            var resolved = Chinese.Resolve(query);
            if (resolved.Count > 0)
            {
                effectiveQuery = resolved[0].Slug.Length > 0 ? resolved[0].Slug : resolved[0].English;
                titleHint = resolved[0].English;
            }
        }

        var facets = new List<string> { $"[\"project_type:{kind.ToFacet()}\"]" };
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add($"[\"versions:{gameVersion}\"]");
        if (!string.IsNullOrWhiteSpace(loader) && kind is ResourceKind.Mod or ResourceKind.Modpack or ResourceKind.Datapack)
        {
            facets.Add($"[\"categories:{loader}\"]");
        }

        var url = Api($"/search?query={Uri.EscapeDataString(effectiveQuery)}") +
                  $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}" +
                  $"&limit={Math.Clamp(limit, 1, 100)}&offset={Math.Max(0, offset)}&index=relevance";

        var json = await GetStringAsync(url, token).ConfigureAwait(false);
        var hits = new List<ResourceHit>();
        if (json is null) return hits;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("hits", out var array)) return hits;

            foreach (var element in array.EnumerateArray())
            {
                var hit = new ResourceHit
                {
                    ProjectId = Text(element, "project_id"),
                    Slug = Text(element, "slug"),
                    Title = Text(element, "title"),
                    Description = Text(element, "description"),
                    Author = Text(element, "author"),
                    Downloads = Number(element, "downloads"),
                    Follows = Number(element, "follows"),
                    IconUrl = Text(element, "icon_url"),
                    ProjectType = Text(element, "project_type"),
                    Updated = Text(element, "date_modified"),
                    Kind = kind
                };

                hit.Categories = Strings(element, "display_categories", "categories");
                hit.GameVersions = Strings(element, "versions");
                hit.PageUrl = $"https://modrinth.com/{hit.ProjectType}/{hit.Slug}";
                hit.ChineseName = Chinese.LookupChinese(hit.Slug) is { Length: > 0 } cn ? cn : titleHint;

                // Prefer the newest game version in the list for the UI.
                if (hit.GameVersions.Count > 4) hit.GameVersions = hit.GameVersions.Take(4).ToList();
                hits.Add(hit);
            }
        }
        catch (JsonException)
        {
            // malformed response: return what we have
        }

        return hits;
    }

    /// <summary>Lists the files of a project that fit the game version / loader.</summary>
    public async Task<List<ResourceFile>> GetFilesAsync(string slug, string? gameVersion = null, string? loader = null,
        CancellationToken token = default)
    {
        var url = Api($"/project/{Uri.EscapeDataString(slug)}/version");
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            filters.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            filters.Add($"loaders={Uri.EscapeDataString($"[\"{loader}\"]")}");
        }

        if (filters.Count > 0) url += "?" + string.Join("&", filters);

        var json = await GetStringAsync(url, token).ConfigureAwait(false);
        var files = new List<ResourceFile>();
        if (json is null) return files;

        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("files", out var fileArray) || fileArray.GetArrayLength() == 0) continue;
                var primary = fileArray.EnumerateArray()
                    .FirstOrDefault(f => f.TryGetProperty("primary", out var p) && p.GetBoolean());
                if (primary.ValueKind == JsonValueKind.Undefined) primary = fileArray[0];

                var file = new ResourceFile
                {
                    Url = Text(primary, "url"),
                    FileName = Text(primary, "filename"),
                    Size = Number(primary, "size"),
                    VersionNumber = Text(element, "version_number"),
                    VersionName = Text(element, "name"),
                    VersionType = Text(element, "version_type"),
                    GameVersions = Strings(element, "game_versions"),
                    Loaders = Strings(element, "loaders")
                };

                if (primary.TryGetProperty("hashes", out var hashes)) file.Sha1 = Text(hashes, "sha1");
                if (DateTime.TryParse(Text(element, "date_published"), out var published)) file.PublishedUtc = published;

                if (element.TryGetProperty("dependencies", out var dependencies))
                {
                    foreach (var dependency in dependencies.EnumerateArray())
                    {
                        var projectId = Text(dependency, "project_id");
                        if (projectId.Length > 0) file.Dependencies.Add(projectId);
                    }
                }

                if (file.Url.Length > 0) files.Add(file);
            }
        }
        catch (JsonException)
        {
            // ignore malformed payloads
        }

        return files;
    }

    // ------------------------------------------------------------ installing

    /// <summary>
    /// Downloads one file into the right folder of an instance
    /// (mods / resourcepacks / shaderpacks / datapacks).
    /// </summary>
    public async Task<(bool Ok, string Message)> InstallFileAsync(ResourceKind kind, ResourceFile file, string instanceDirectory,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        var folder = kind.ToInstanceFolder();
        if (folder.Length == 0) return (false, "整合包请用整合包安装流程");

        try
        {
            var target = Path.Combine(instanceDirectory, folder);
            Directory.CreateDirectory(target);
            var destination = Path.Combine(target, file.FileName);

            using var response = await Net.Http.Client
                .GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? file.Size;
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var sink = File.Create(destination + ".tiamc-download"))
            {
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    read += count;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }

            File.Move(destination + ".tiamc-download", destination, overwrite: true);

            // Modrinth sends a sha1; verify it when we have one.
            if (file.Sha1.Length == 40)
            {
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(destination)))
                    .ToLowerInvariant();
                if (!actual.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"{file.FileName} 校验失败（sha1 不匹配）");
                }
            }

            return (true, $"已下载到 {folder}\\{file.FileName}（{file.SizeText}）");
        }
        catch (Exception e)
        {
            return (false, "下载失败: " + e.Message);
        }
    }

    /// <summary>Downloads a project's file into the launcher cache and returns the path.</summary>
    public async Task<(bool Ok, string Path, string Message)> DownloadToCacheAsync(ResourceFile file, string cacheDirectory,
        CancellationToken token = default)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var destination = Path.Combine(cacheDirectory, file.FileName);
            if (File.Exists(destination) && new FileInfo(destination).Length == file.Size) return (true, destination, "已存在");

            using var response = await Net.Http.Client.GetAsync(file.Url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(destination, bytes, token).ConfigureAwait(false);
            return (true, destination, $"{file.FileName}（{file.SizeText}）");
        }
        catch (Exception e)
        {
            return (false, "", "下载失败: " + e.Message);
        }
    }

    // ------------------------------------------------------------- plumbing

    private static async Task<string?> GetStringAsync(string url, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await Net.Http.ApiClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static List<string> Strings(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
            }

            if (list.Count > 0) return list;
        }

        return [];
    }
}

/// <summary>One dictionary entry: Chinese name plus the source slugs.</summary>
public sealed class ChineseEntry
{
    public string Chinese { get; init; } = "";
    public string English { get; init; } = "";
    public string Slug { get; init; } = "";
    public string CurseForgeId { get; init; } = "";
}

/// <summary>
/// Chinese name dictionary for content search, using the same line format as
/// Axolotl's searcher table:
///
///   slug@curseforgeId|中文名 (English name)⌦otherSlug|另一个中文名
///
/// A full table can be dropped at config/search-zh.txt; a compact built-in list
/// keeps Chinese search useful out of the box.
/// </summary>
public sealed class ChineseDictionary
{
    private readonly Dictionary<string, ChineseEntry> _byChinese = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bySlug = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byChinese.Count;

    public static string DictionaryPath => Utils.AppPaths.ChineseSearchFile;

    public static ChineseDictionary Load(string? path = null)
    {
        var dictionary = new ChineseDictionary();
        foreach (var line in BuiltIn.Split('\n'))
        {
            dictionary.AddLine(line);
        }

        var file = path ?? DictionaryPath;
        try
        {
            if (File.Exists(file))
            {
                foreach (var line in File.ReadLines(file)) dictionary.AddLine(line);
            }
        }
        catch (Exception)
        {
            // the built-in table is enough
        }

        return dictionary;
    }

    public void AddLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return;

        // Multiple alternatives are separated by the same separator Axolotl uses.
        foreach (var part in line.Split('⌦', '\u2318'))
        {
            var bar = part.IndexOf('|');
            if (bar < 0) continue;

            var key = part[..bar].Trim();
            var name = part[(bar + 1)..].Trim();
            if (key.Length == 0 || name.Length == 0) continue;

            var at = key.IndexOf('@');
            var slug = at >= 0 ? key[..at] : key;
            var curseForge = at >= 0 ? key[(at + 1)..] : "";

            var english = name;
            var chinese = name;
            var open = name.LastIndexOf('(');
            var close = name.LastIndexOf(')');
            if (open > 0 && close > open)
            {
                chinese = name[..open].Trim(' ', '*');
                english = name[(open + 1)..close].Trim();
            }

            var entry = new ChineseEntry
            {
                Chinese = chinese,
                English = english,
                Slug = slug,
                CurseForgeId = curseForge
            };

            if (chinese.Length > 0) _byChinese[chinese] = entry;
            if (slug.Length > 0) _bySlug[slug] = chinese;
        }
    }

    public bool ContainsChinese(string text) => text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    /// <summary>Finds entries whose Chinese name contains the query.</summary>
    public List<ChineseEntry> Resolve(string query)
    {
        var result = new List<ChineseEntry>();
        var trimmed = query.Trim();

        if (_byChinese.TryGetValue(trimmed, out var exact)) result.Add(exact);

        foreach (var (chinese, entry) in _byChinese)
        {
            if (result.Count >= 8) break;
            if (chinese.Contains(trimmed, StringComparison.OrdinalIgnoreCase) && !result.Contains(entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>Chinese name of a Modrinth slug, when the table knows it.</summary>
    public string LookupChinese(string slug) => _bySlug.TryGetValue(slug, out var chinese) ? chinese : "";

    /// <summary>Compact built-in table (popular content only).</summary>
    private const string BuiltIn = """
        # TiaMC 内置中文名表（格式与 Axolotl 一致：slug|中文名 (English)⌦其它 slug）
        sodium|钠 (Sodium)
        lithium|锂 (Lithium)
        iris|鸢尾花光影加载 (Iris Shaders)
        jei|JEI 物品管理器 (Just Enough Items)
        rei|REI 物品管理器 (Roughly Enough Items)
        emi|EMI 物品管理器 (EMI)
        create|机械动力 (Create)
        mekanism|通用机械 (Mekanism)
        ae2|应用能源2 (Applied Energistics 2)
        appliedenergistics2|应用能源2 (Applied Energistics 2)
        botania|植物魔法 (Botania)
        immersiveengineering|沉浸工程 (Immersive Engineering)
        thermal-expansion|热力膨胀 (Thermal Expansion)
        tconstruct|匠魂 (Tinkers' Construct)
        farmers-delight|农夫乐事 (Farmer's Delight)
        twilightforest|暮色森林 (The Twilight Forest)
        aether|天境 (The Aether)
        jei-bees|JEI 蜜蜂 (Just Enough Bees)
        xaeros-minimap|Xaero的小地图 (Xaero's Minimap)
        xaeros-world-map|Xaero的世界地图 (Xaero's World Map)
        journeymap|旅行地图 (JourneyMap)
        jade|Jade 信息显示 (Jade)
        wthit|WTHIT 信息显示 (What The Hell Is That)
        appleskin|苹果皮 (AppleSkin)
        jei-integration|JEI 集成 (Just Enough Integration)
        cloth-config|布料配置 (Cloth Config)
        architectury|建筑工艺 (Architectury API)
        fabric-api|Fabric API
        forge-config-api-port|配置API (Forge Config API Port)
        kubejs|KubeJS
        ftb-quests|FTB 任务 (FTB Quests)
        ftb-teams|FTB 队伍 (FTB Teams)
        ftb-library|FTB 前置库 (FTB Library)
        ae2wtlib|AE2 无线终端 (AE2 Wireless Terminals)
        iron-chests|更多箱子 (Iron Chests)
        storagedrawers|储物抽屉 (Storage Drawers)
        sophisticated-backpacks|精致背包 (Sophisticated Backpacks)
        curios|护符槽 (Curios API)
        trinkets|饰品栏 (Trinkets)
        geckolib|GeckoLib
        patchouli|帕秋莉手册 (Patchouli)
        bookshelf|书架 (Bookshelf)
        balm|Balm
        waystones|传送石碑 (Waystones)
        comforts|睡袋 (Comforts)
        farmersdelight|农夫乐事 (Farmer's Delight)
        embellishcraft|装饰工艺 (EmbellishCraft)
        chipped|锤凿 (Chipped)
        supplementaries|附属物 (Supplementaries)
        quark|夸克 (Quark)
        autofish|自动钓鱼 (AutoFish)
        sodium-extra|钠扩展 (Sodium Extra)
        indium|铟 (Indium)
        lithium-fabric|锂 (Lithium)
        starlight|星光 (Starlight)
        ferritecore|铁氧体核心 (FerriteCore)
        memoryleakfix|内存泄漏修复 (Memory Leak Fix)
        modernfix|现代修复 (ModernFix)
        entityculling|实体渲染优化 (Entity Culling)
        immediatelyfast|即时渲染优化 (ImmediatelyFast)
        exordium|序章 (Exordium)
        c2me-fabric|并发区块管理 (C2ME)
        noisium|噪声优化 (Noisium)
        very-many-players|更多玩家 (Very Many Players)
        krypton|氪 (Krypton)
        dynamic-fps|动态帧率 (Dynamic FPS)
        betterf3|更好的F3 (BetterF3)
        modmenu|模组菜单 (Mod Menu)
        catalogue|模组目录 (Catalogue)
        roughly-enough-items|REI 物品管理器 (Roughly Enough Items)
        shulkerboxtooltip|潜影盒提示 (ShulkerBoxTooltip)
        inventory-profiles-next|背包配置 (Inventory Profiles Next)
        itemscroller|物品滚动 (Item Scroller)
        litematica|投影 (Litematica)
        minihud|迷你HUD (MiniHUD)
        tweakeroo|Tweakeroo
        fabric-api-lite|Fabric API
        complementary-reimagined|Complementary 光影 (Complementary Reimagined)
        complementary-shaders|Complementary 光影 (Complementary Shaders)
        bsl-shaders|BSL 光影 (BSL Shaders)
        seus-ptgi-shaders|SEUS PTGI 光线追踪光影 (SEUS PTGI)
        chocapic13-shaders|Chocapic13 光影 (Chocapic13)
        soda-shaders|Soda 光影 (Soda Shaders)
        photon-shader|Photon 光影 (Photon Shader)
        makeup-ultra-fast-shaders|MakeUp 光影 (MakeUp Ultra Fast)
        complementary-unbound|Complementary Unbound 光影 (Complementary Unbound)
        """;
}
