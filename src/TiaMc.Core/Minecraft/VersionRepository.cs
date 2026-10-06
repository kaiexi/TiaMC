using System.Text.Json;
using TiaMc.Core.Json;
using TiaMc.Core.Launch;

namespace TiaMc.Core.Minecraft;

/// <summary>One installed version found under versions/&lt;id&gt;/&lt;id&gt;.json.</summary>
public sealed class InstalledVersion
{
    public required string Id { get; init; }
    public required VersionJson Json { get; init; }
    /// <summary>Ids from the version itself up to the root parent (self first).</summary>
    public required List<string> Chain { get; init; }
    public required string JsonPath { get; init; }

    /// <summary>The version whose jar file should be put on the classpath.</summary>
    public required string JarId { get; init; }

    public string JarPath { get; init; } = "";
    public bool HasJar => !string.IsNullOrEmpty(JarPath);

    public string Loader => Json.LoaderKind;
    public string? InheritsFrom => Json.InheritsFrom;

    public override string ToString() => Id;
}

/// <summary>
/// Reads, merges and caches the version JSON files of a Minecraft folder.
/// Merging follows the official launcher semantics: a version that declares
/// "inheritsFrom" contributes only the fields it overrides, everything else
/// (libraries, arguments, asset index, java version) is taken from the parent.
/// </summary>
public sealed class VersionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly McPaths _paths;

    public VersionRepository(McPaths paths)
    {
        _paths = paths;
    }

    public McPaths Paths => _paths;

    /// <summary>All installed versions, newest first, skipping folders without a JSON.</summary>
    public List<InstalledVersion> LoadAll(out List<string> errors)
    {
        errors = [];
        var result = new List<InstalledVersion>();

        if (!Directory.Exists(_paths.VersionsDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(_paths.VersionsDir))
        {
            var id = Path.GetFileName(dir);
            try
            {
                var version = Load(id);
                if (version is not null) result.Add(version);
            }
            catch (Exception e)
            {
                errors.Add($"{id}: {e.Message}");
            }
        }

        return result
            .OrderByDescending(v => v.Json.ReleaseTime is { } t && DateTime.TryParse(t, out var d) ? d : DateTime.MinValue)
            .ThenByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Loads one version and resolves its inheritance chain.</summary>
    public InstalledVersion? Load(string versionId)
    {
        var chain = new List<string>();
        var current = versionId;
        VersionJson? merged = null;
        var jsonPath = "";

        while (!string.IsNullOrWhiteSpace(current))
        {
            var path = _paths.VersionJsonPath(current);
            if (!File.Exists(path))
            {
                if (merged is null) return null;
                break;
            }

            var json = ReadJson(path);
            if (json is null) break;

            if (merged is null)
            {
                merged = json;
                jsonPath = path;
                if (string.IsNullOrWhiteSpace(merged.Id)) merged.Id = current;
            }
            else
            {
                merged = Merge(parent: json, child: merged);
            }

            chain.Add(current);

            var parentId = json.InheritsFrom;
            if (string.IsNullOrWhiteSpace(parentId) || string.Equals(parentId, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parentId!;
        }

        if (merged is null) return null;

        var jarId = ResolveJarId(chain);
        return new InstalledVersion
        {
            Id = versionId,
            Json = merged,
            Chain = chain,
            JsonPath = jsonPath,
            JarId = jarId,
            JarPath = string.IsNullOrEmpty(jarId) ? "" : _paths.VersionJarPath(jarId)
        };
    }

    private string ResolveJarId(List<string> chain)
    {
        foreach (var id in chain)
        {
            if (File.Exists(_paths.VersionJarPath(id))) return id;
        }

        return chain.Count > 0 ? chain[^1] : "";
    }

    private static VersionJson? ReadJson(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<VersionJson>(stream, JsonOptions);
        }
        catch (JsonException)
        {
            // Some installers write a JSON with a UTF-8 BOM plus trailing garbage.
            var text = File.ReadAllText(path).TrimStart('\uFEFF');
            return JsonSerializer.Deserialize<VersionJson>(text, JsonOptions);
        }
    }

    /// <summary>
    /// Merges a parent version into a child version. Scalar fields present on the
    /// child win; the library and argument lists are appended in the order
    /// required by the JVM (parent libraries first, then the loader libraries).
    /// </summary>
    public static VersionJson Merge(VersionJson parent, VersionJson child)
    {
        var libraries = new List<LibraryJson>(parent.Libraries.Count + child.Libraries.Count);
        libraries.AddRange(parent.Libraries);
        libraries.AddRange(child.Libraries);

        var gameArgs = new List<ArgumentJson>(parent.Arguments?.Game.Count ?? 0 + child.Arguments?.Game.Count ?? 0);
        gameArgs.AddRange(parent.Arguments?.Game ?? []);
        gameArgs.AddRange(child.Arguments?.Game ?? []);

        var jvmArgs = new List<ArgumentJson>(parent.Arguments?.Jvm.Count ?? 0 + child.Arguments?.Jvm.Count ?? 0);
        jvmArgs.AddRange(parent.Arguments?.Jvm ?? []);
        jvmArgs.AddRange(child.Arguments?.Jvm ?? []);

        return new VersionJson
        {
            Id = child.Id,
            InheritsFrom = null,
            Type = child.Type,
            MainClass = string.IsNullOrWhiteSpace(child.MainClass) ? parent.MainClass : child.MainClass,
            MinecraftArguments = string.IsNullOrWhiteSpace(child.MinecraftArguments)
                ? parent.MinecraftArguments
                : child.MinecraftArguments,
            Assets = string.IsNullOrWhiteSpace(child.Assets) ? parent.Assets : child.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Downloads = MergeDownloads(parent.Downloads, child.Downloads),
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Logging = child.Logging ?? parent.Logging,
            ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
            Time = child.Time ?? parent.Time,
            MinimumLauncherVersion = Math.Max(parent.MinimumLauncherVersion, child.MinimumLauncherVersion),
            ComplianceLevel = child.ComplianceLevel ?? parent.ComplianceLevel,
            Libraries = libraries,
            Arguments = new ArgumentsJson { Game = gameArgs, Jvm = jvmArgs }
        };
    }

    private static Dictionary<string, DownloadJson>? MergeDownloads(
        Dictionary<string, DownloadJson>? parent,
        Dictionary<string, DownloadJson>? child)
    {
        if (parent is null || parent.Count == 0) return child;
        if (child is null || child.Count == 0) return parent;

        var merged = new Dictionary<string, DownloadJson>(parent, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in child) merged[key] = value;
        return merged;
    }

    /// <summary>
    /// Libraries may appear twice (once from vanilla, once from a loader). The
    /// classpath must contain exactly one artifact per group:artifact pair; for
    /// duplicates the higher version wins, matching the official launcher.
    /// </summary>
    public static List<LibraryJson> DeduplicateLibraries(IEnumerable<LibraryJson> libraries)
    {
        var byKey = new Dictionary<string, LibraryJson>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var lib in libraries)
        {
            if (string.IsNullOrWhiteSpace(lib.Name)) continue;
            var key = GroupArtifact(lib.Name);
            if (key is null) continue;

            if (byKey.TryGetValue(key, out var existing))
            {
                if (CompareMavenVersion(lib.Name, existing.Name) > 0)
                {
                    byKey[key] = lib;
                }
            }
            else
            {
                byKey[key] = lib;
                order.Add(key);
            }
        }

        return order.Select(k => byKey[k]).ToList();
    }

    /// <summary>
    /// "group:artifact[:classifier]" of a maven coordinate, dropping only the version and extension.
    ///
    /// 关键：**必须保留 classifier**。现代版本 JSON（1.19+）里本地库是
    /// `org.lwjgl:lwjgl:3.3.3:natives-windows` 这种带 classifier 的条目，
    /// 如果按 "group:artifact" 去重，它们会被当成与主 jar 重复而整批丢弃，
    /// 结果就是 natives 永远不下、永远不解压，游戏启动时找不到 LWJGL 本地库直接秒退。
    /// </summary>
    public static string? GroupArtifact(string mavenName)
    {
        var name = mavenName;
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];

        var parts = name.Split(':');
        if (parts.Length < 3) return null;

        var key = parts[0] + ":" + parts[1];
        if (parts.Length >= 4 && parts[3].Length > 0)
        {
            key += ":" + parts[3];
        }

        return key;
    }

    /// <summary>Version part of a maven coordinate (empty when malformed).</summary>
    public static string MavenVersion(string mavenName)
    {
        var name = mavenName;
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var parts = name.Split(':');
        return parts.Length >= 3 ? parts[2] : "";
    }

    /// <summary>
    /// Compares two maven coordinates by version, following the same rules the
    /// official launcher uses: numeric dotted parts first, then a lexicographic
    /// fallback that understands Forge style "1.20.1-47.4.26" build numbers.
    /// </summary>
    public static int CompareMavenVersion(string a, string b)
    {
        var va = MavenVersion(a);
        var vb = MavenVersion(b);
        if (string.Equals(va, vb, StringComparison.OrdinalIgnoreCase)) return 0;
        return CompareVersions(va, vb);
    }

    public static int CompareVersions(string a, string b)
    {
        var ta = Tokenize(a);
        var tb = Tokenize(b);
        var n = Math.Max(ta.Count, tb.Count);

        for (var i = 0; i < n; i++)
        {
            var sa = i < ta.Count ? ta[i] : "";
            var sb = i < tb.Count ? tb[i] : "";

            var na = long.TryParse(sa, out var la);
            var nb = long.TryParse(sb, out var lb);

            int cmp;
            if (na && nb) cmp = la.CompareTo(lb);
            else if (na) cmp = 1;   // a numeric part is newer than a text part
            else if (nb) cmp = -1;
            else cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);

            if (cmp != 0) return cmp;
        }

        return 0;
    }

    private static List<string> Tokenize(string version)
    {
        var tokens = new List<string>();
        var buffer = new System.Text.StringBuilder();

        foreach (var ch in version)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(ch);
            }
            else
            {
                if (buffer.Length > 0)
                {
                    tokens.Add(buffer.ToString());
                    buffer.Clear();
                }
            }
        }

        if (buffer.Length > 0) tokens.Add(buffer.ToString());
        return tokens;
    }
}
