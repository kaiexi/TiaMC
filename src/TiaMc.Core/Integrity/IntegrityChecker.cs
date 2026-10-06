using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMc.Core.Java;
using TiaMc.Core.Launch;
using TiaMc.Core.Minecraft;
using TiaMc.Core.Rules;
using TiaMc.Core.Utils;

namespace TiaMc.Core.Integrity;

/// <summary>A file the game needs but which is missing (or the wrong size) on disk.</summary>
public sealed class MissingFile
{
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public required string Url { get; init; }
    public long Size { get; init; }
    public string? Sha1 { get; init; }
    public string Display => $"{Kind}: {TextUtil.Shorten(Path, 120)}";
}

public sealed class CheckResult
{
    public List<MissingFile> Missing { get; init; } = [];
    public int LibraryCount { get; init; }
    public int AssetCount { get; init; }
    public int PresentCount { get; init; }
    public long MissingBytes => Missing.Sum(m => m.Size);
    public bool IsComplete => Missing.Count == 0;

    public string Summary =>
        $"库 {LibraryCount} | 资源 {AssetCount} | 已存在 {PresentCount} | 缺失 {Missing.Count} ({TextUtil.FormatBytes(MissingBytes)})";
}

public sealed class AssetIndex
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = [];
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>
/// Verifies that everything a version needs is present locally, and reports what
/// has to be downloaded. Mirrors ColorMC's CheckGameFile step but is read-only:
/// the caller decides whether to download.
/// </summary>
public static class IntegrityChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static CheckResult Check(McPaths paths, InstalledVersion version, bool includeAssets = true,
        Action<string>? log = null)
    {
        var missing = new List<MissingFile>();
        var present = 0;

        var featureSet = RuleEvaluator.FeatureSet.Default();
        var libraries = VersionRepository.DeduplicateLibraries(version.Json.Libraries);
        var resolved = LibraryResolver.Resolve(libraries, featureSet);

        var libraryCount = 0;
        foreach (var lib in resolved)
        {
            // 本地库（`:natives-windows` 这类）的 ArtifactPath 为 null，但 Natives 里有真实文件。
            // 以前这里直接 continue，导致这些 jar 既不算缺失也不下载，natives 目录永远为空——
            // 表现就是 UnsatisfiedLinkError: Failed to locate library: lwjgl.dll。
            if (lib.ArtifactPath is null)
            {
                foreach (var (relative, nativeDownload, _) in lib.Natives)
                {
                    libraryCount++;
                    var nativeTarget = Path.Combine(paths.LibrariesDir, relative);
                    if (File.Exists(nativeTarget))
                    {
                        present++;
                        continue;
                    }

                    var nativeUrl = nativeDownload.Url;
                    if (string.IsNullOrWhiteSpace(nativeUrl)) nativeUrl = LibraryResolver.DefaultUrl(lib.MavenName, null);
                    if (string.IsNullOrWhiteSpace(nativeUrl))
                    {
                        log?.Invoke($"[check] 无法确定本地库下载地址: {lib.MavenName}");
                        continue;
                    }

                    missing.Add(new MissingFile
                    {
                        Kind = "natives",
                        Path = nativeTarget,
                        Url = nativeUrl!,
                        Size = nativeDownload.Size,
                        Sha1 = nativeDownload.Sha1
                    });
                }

                continue;
            }

            libraryCount++;

            var target = Path.Combine(paths.LibrariesDir, lib.ArtifactPath);
            var download = lib.Artifact;

            if (!File.Exists(target))
            {
                var url = download?.Url;
                if (string.IsNullOrWhiteSpace(url)) url = LibraryResolver.DefaultUrl(lib.MavenName, null);
                if (string.IsNullOrWhiteSpace(url))
                {
                    log?.Invoke($"[check] 无法确定下载地址: {lib.MavenName}");
                    continue;
                }

                missing.Add(new MissingFile
                {
                    Kind = "library",
                    Path = target,
                    Url = url!,
                    Size = download?.Size ?? 0,
                    Sha1 = download?.Sha1
                });
            }
            else
            {
                present++;
            }
        }

        // client jar
        if (!version.HasJar)
        {
            var download = version.Json.Downloads?.GetValueOrDefault("client");
            if (download?.Url is { Length: > 0 } url)
            {
                missing.Add(new MissingFile
                {
                    Kind = "client-jar",
                    Path = paths.VersionJarPath(version.JarId),
                    Url = url,
                    Size = download.Size,
                    Sha1 = download.Sha1
                });
            }
        }
        else
        {
            present++;
        }

        // asset index + objects
        var assetCount = 0;
        if (includeAssets)
        {
            var indexId = version.Json.AssetIndex?.Id ?? version.Json.Assets;
            if (!string.IsNullOrWhiteSpace(indexId))
            {
                var indexPath = paths.AssetsIndexPath(indexId!);
                if (!File.Exists(indexPath))
                {
                    var indexDownload = version.Json.AssetIndex;
                    if (indexDownload?.Url is { Length: > 0 } indexUrl)
                    {
                        missing.Add(new MissingFile
                        {
                            Kind = "asset-index",
                            Path = indexPath,
                            Url = indexUrl,
                            Size = indexDownload.Size,
                            Sha1 = indexDownload.Sha1
                        });
                    }
                }
                else
                {
                    present++;
                    var index = ReadAssetIndex(indexPath, log);
                    if (index is not null)
                    {
                        foreach (var (name, obj) in index.Objects)
                        {
                            assetCount++;
                            var target = AssetObjectPath(paths, obj.Hash);
                            if (File.Exists(target))
                            {
                                present++;
                            }
                            else
                            {
                                missing.Add(new MissingFile
                                {
                                    Kind = "asset",
                                    Path = target,
                                    Url = $"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}",
                                    Size = obj.Size,
                                    Sha1 = obj.Hash
                                });
                            }
                        }
                    }
                }
            }
        }

        return new CheckResult
        {
            Missing = missing,
            LibraryCount = libraryCount,
            AssetCount = assetCount,
            PresentCount = present
        };
    }

    public static string AssetObjectPath(McPaths paths, string hash) =>
        Path.Combine(paths.AssetObjectsDir, hash[..2], hash);

    public static AssetIndex? ReadAssetIndex(string path, Action<string>? log = null)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<AssetIndex>(stream, JsonOptions);
        }
        catch (Exception e)
        {
            log?.Invoke($"[check] 资源索引解析失败 {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the download URLs to try for one missing file, best first.
    ///
    /// The BMCLAPI mirror only proxies some of the hosts a version JSON can
    /// reference (libraries.minecraft.net, maven.minecraftforge.net, the Mojang
    /// download hosts, resources.download.minecraft.net). Libraries that come
    /// from a different Maven repository - NeoForge, Fabric, third party mod
    /// loaders - would answer 403 through the mirror, so the original URL is
    /// always appended as a fallback.
    /// </summary>
    public static List<string> BuildDownloadUrls(MissingFile file, DownloadSource source)
    {
        var urls = new List<string>();

        // 三种下载源（PCL 的默认行为是"自动"）：
        //   BmclApi —— 镜像优先，失败回官方
        //   Auto    —— 官方优先，失败回镜像
        //   Official—— 只走官方
        if (source is DownloadSource.BmclApi or DownloadSource.Custom)
        {
            var mirrored = RewriteUrl(file.Url, source);
            if (!string.IsNullOrWhiteSpace(mirrored)) urls.Add(mirrored);
        }

        // The original URL doubles as the fallback (and as the only URL for the
        // official source). Never ask the same host twice.
        if (!urls.Any(u => string.Equals(u, file.Url, StringComparison.OrdinalIgnoreCase)))
        {
            urls.Add(file.Url);
        }

        // Auto：官方排在前面，镜像作为回退接在后面
        if (source == DownloadSource.Auto)
        {
            var mirrored = RewriteUrl(file.Url, DownloadSource.BmclApi);
            if (!string.IsNullOrWhiteSpace(mirrored) &&
                !urls.Any(u => string.Equals(u, mirrored, StringComparison.OrdinalIgnoreCase)))
            {
                urls.Add(mirrored);
            }
        }

        return urls;
    }

    /// <summary>Asset URLs point at resources.download.minecraft.net; mirror them (PCL2 compatible).</summary>
    /// <summary>自定义下载源基址，例如 https://你的反代（路径规则同 BMCLAPI）。</summary>
    public static string? CustomBaseUrl { get; set; }

    public static string RewriteAssetUrl(string url, DownloadSource source) => RewriteUrl(url, source);

    /// <summary>Library URLs are Maven artifacts; mirror them (PCL2 compatible).</summary>
    public static string RewriteLibraryUrl(string url, DownloadSource source) => RewriteUrl(url, source);

    /// <summary>
    /// Rewrites any URL to the download source, using the same URL table PCL2
    /// uses for the BMCLAPI mirror:
    ///
    ///   piston-meta / launchermeta / piston-data     -> bmclapi2 / (same path)
    ///   launcher.mojang.com                          -> bmclapi2 / (same path)
    ///   resources.download.minecraft.net/&lt;aa&gt;/&lt;hash&gt;   -> bmclapi2 / assets/&lt;aa&gt;/&lt;hash&gt;
    ///   any Maven artifact URL                       -> bmclapi2 / maven/&lt;path&gt;
    ///
    /// Maven artifacts of third party loaders (NeoForge, Fabric, Maven Central,
    /// JitPack, ...) are proxied through the same <c>/maven/</c> route, which is
    /// why BMCLAPI works for those as well.
    /// </summary>
    public static string RewriteUrl(string url, DownloadSource source)
    {
        if (source == DownloadSource.Custom)
        {
            if (string.IsNullOrWhiteSpace(CustomBaseUrl)) return url;
            var mirroredPath = RewriteUrl(url, DownloadSource.BmclApi);
            var bmclHost = "https://bmclapi2.bangbang93.com/";
            if (mirroredPath.StartsWith(bmclHost, StringComparison.OrdinalIgnoreCase))
            {
                return CustomBaseUrl!.TrimEnd('/') + "/" + mirroredPath[bmclHost.Length..];
            }
            return url;
        }

        if (source != DownloadSource.BmclApi) return url;
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (url.StartsWith(BmclHost, StringComparison.OrdinalIgnoreCase)) return url;

        // Mojang infrastructure: same path on the mirror.
        foreach (var host in MojangHosts)
        {
            if (!url.StartsWith(host, StringComparison.OrdinalIgnoreCase)) continue;
            return BmclHost + url[host.Length..];
        }

        // Assets: resources.download.minecraft.net/<aa>/<hash> -> /assets/<aa>/<hash>
        if (url.StartsWith(AssetHost, StringComparison.OrdinalIgnoreCase))
        {
            return BmclHost + "assets/" + url[AssetHost.Length..];
        }

        // Everything else that looks like a Maven repository artifact.
        if (LooksLikeMavenArtifact(url, out var baseUrl))
        {
            return BmclHost + "maven/" + url[baseUrl.Length..];
        }

        return url;
    }

    /// <summary>True when the URL ends with a Maven repository path (group/artifact/version/file).</summary>
    private static bool LooksLikeMavenArtifact(string url, out string baseUrl)
    {
        baseUrl = "";

        foreach (var prefix in MavenHostPrefixes)
        {
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            baseUrl = prefix;
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        // Known Maven repository paths on hosts we do not enumerate.
        if (uri.AbsolutePath.StartsWith("/maven2/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/repository/", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = $"{uri.Scheme}://{uri.Authority}";
            return true;
        }

        return false;
    }

    private const string BmclHost = "https://bmclapi2.bangbang93.com/";
    private const string AssetHost = "https://resources.download.minecraft.net/";

    /// <summary>Mojang hosts whose path can be appended to the mirror root unchanged.</summary>
    private static readonly string[] MojangHosts =
    [
        "https://piston-meta.mojang.com/",
        "https://piston-data.mojang.com/",
        "https://launchermeta.mojang.com/",
        "https://launcher.mojang.com/",
        "https://launchercontent.mojang.com/",
        "https://api.minecraftservices.com/"
    ];

    /// <summary>Hosts that publish Minecraft libraries (all of them are Maven repositories).</summary>
    private static readonly string[] MavenHostPrefixes =
    [
        "https://libraries.minecraft.net/",
        "https://maven.minecraftforge.net/",
        "https://maven.neoforged.net/releases/",
        "https://maven.fabricmc.net/",
        "https://maven.quiltmc.org/repository/release/",
        "https://repo.spongepowered.org/repository/maven-public/",
        "https://repo1.maven.org/maven2/",
        "https://repo.maven.apache.org/maven2/",
        "https://files.minecraftforge.net/maven/",
        "https://maven.parchmentmc.org/",
        "https://maven.blamejared.com/",
        "https://maven.shedaniel.me/",
        "https://maven.terraformersmc.com/releases/",
        "https://jitpack.io/"
    ];
}

public enum DownloadSource
{
    /// <summary>自动：先用官方地址，失败再换镜像（PCL 的默认行为）。</summary>
    Auto,

    /// <summary>只走官方（Mojang）地址。</summary>
    Official,

    /// <summary>优先走 BMCLAPI 镜像，失败再回官方。</summary>
    BmclApi,

    /// <summary>优先走自定义镜像基址（自建/合作反代，路径规则与 BMCLAPI 相同）。</summary>
    Custom
}
