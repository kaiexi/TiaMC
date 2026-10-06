using TiaMc.Core.Minecraft;
using TiaMc.Core.Rules;

namespace TiaMc.Core.Launch;

/// <summary>
/// Turns version JSON library entries into concrete files on disk:
/// classpath artifacts plus the native DLLs that must be unpacked into the
/// per-launch natives directory.
/// </summary>
public static class LibraryResolver
{
    public sealed record ResolvedLibrary(
        string MavenName,
        string? ArtifactPath,
        DownloadJson? Artifact,
        List<(string RelativePath, DownloadJson Download, ExtractJson? Extract)> Natives)
    {
        public bool IsNativeOnly => ArtifactPath is null && Natives.Count > 0;
    }

    /// <summary>All playable artifacts of a version, filtered by rules.</summary>
    public static List<ResolvedLibrary> Resolve(IEnumerable<LibraryJson> libraries,
        RuleEvaluator.FeatureSet? features = null)
    {
        var result = new List<ResolvedLibrary>();

        foreach (var lib in libraries)
        {
            if (string.IsNullOrWhiteSpace(lib.Name)) continue;
            if (!RuleEvaluator.IsAllowed(lib.Rules, features)) continue;

            var artifact = lib.Downloads?.Artifact;
            var path = artifact?.Path ?? MavenToPath(lib.Name, lib.Url);
            string? artifactPath = null;

            // Native-only libraries (e.g. org.lwjgl:lwjgl:3.3.1:natives-windows) have no artifact.
            var isNativeOnly = lib.Name.Contains(":natives-", StringComparison.OrdinalIgnoreCase)
                               || (artifact is null && lib.Natives is { Count: > 0 });
            if (!isNativeOnly && !string.IsNullOrWhiteSpace(path))
            {
                artifactPath = path.Replace('/', Path.DirectorySeparatorChar);
            }

            var natives = new List<(string, DownloadJson, ExtractJson?)>();
            if (lib.Natives is { Count: > 0 })
            {
                var classifier = ClassifierFor(lib.Natives);
                if (classifier is not null && lib.Downloads?.Classifiers is { } classifiers &&
                    classifiers.TryGetValue(classifier, out var native) &&
                    !string.IsNullOrWhiteSpace(native.Path))
                {
                    natives.Add((native.Path!.Replace('/', Path.DirectorySeparatorChar), native, lib.Extract));
                }
            }
            else if (isNativeOnly && artifact is not null && !string.IsNullOrWhiteSpace(path))
            {
                // Old format: the artifact itself is the native archive.
                natives.Add((path.Replace('/', Path.DirectorySeparatorChar), artifact, lib.Extract));
                artifactPath = null;
            }
            else if (isNativeOnly && !string.IsNullOrWhiteSpace(path))
            {
                // 精简过的版本 JSON 里，`:natives-windows` 这类条目常常只有名字、没有 downloads 段。
                // 这里按 Maven 坐标合成下载项（路径 + 官方/镜像地址），否则本地库永远下不到，
                // 游戏启动时会因为找不到 LWJGL 本地库直接秒退。
                var synthesized = new DownloadJson
                {
                    Path = path,
                    Url = DefaultUrl(lib.Name, lib.Url),
                    Size = 0,
                    Sha1 = null
                };
                natives.Add((path.Replace('/', Path.DirectorySeparatorChar), synthesized, lib.Extract));
                artifactPath = null;
            }

            if (artifactPath is null && natives.Count == 0) continue;

            result.Add(new ResolvedLibrary(lib.Name, artifactPath, artifact, natives));
        }

        return result;
    }

    /// <summary>Maps a "natives" map onto the classifier key for the current OS.</summary>
    public static string? ClassifierFor(Dictionary<string, string> natives)
    {
        var os = RuleEvaluator.OsName;
        var archSuffix = Environment.Is64BitProcess ? "-64" : "-32";

        // Most specific key first: natives-windows-64, natives-windows, natives-windows-32 ...
        string[] candidates =
        [
            $"natives-{os}{archSuffix}",
            $"natives-{os}",
            os
        ];

        foreach (var key in candidates)
        {
            if (natives.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
            }
        }

        return null;
    }

    /// <summary>"group:artifact:version[:classifier][@ext]" to a repository relative path.</summary>
    public static string? MavenToPath(string name, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var extension = "jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            extension = name[(at + 1)..];
            name = name[..at];
        }

        var parts = name.Split(':');
        if (parts.Length < 3) return null;

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;

        var file = classifier is null
            ? $"{artifact}-{version}.{extension}"
            : $"{artifact}-{version}-{classifier}.{extension}";

        return $"{group}/{artifact}/{version}/{file}";
    }

    /// <summary>Builds a default download url for a maven coordinate when the JSON omits one.</summary>
    public static string? DefaultUrl(string mavenName, string? declaredUrl)
    {
        var relative = MavenToPath(mavenName);
        if (relative is null) return null;

        var root = string.IsNullOrWhiteSpace(declaredUrl) ? "https://libraries.minecraft.net/" : declaredUrl!;
        if (!root.EndsWith('/')) root += "/";
        return root + relative;
    }
}
