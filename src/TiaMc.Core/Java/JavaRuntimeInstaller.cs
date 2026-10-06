using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMc.Core.Net;
using TiaMc.Core.Utils;

namespace TiaMc.Core.Java;

/// <summary>
/// Automatically provisions the Java runtime a Minecraft version needs.
///
/// A version JSON states its requirement ("javaVersion": {"component":
/// "java-runtime-gamma", "majorVersion": 17}). When no installed runtime satisfies
/// it the launcher downloads a matching JRE into &lt;root&gt;\runtime\java-&lt;major&gt;,
/// exactly where <see cref="JavaDetector"/> looks for runtimes.
///
/// Source order: the Adoptium API (official Eclipse Temurin builds, gives a
/// checksum) and the TUNA Adoptium mirror as a fallback for slow networks.
/// </summary>
public static class JavaRuntimeInstaller
{
    public sealed record Result(bool Ok, string Message, string JavaPath, int Major);

    public sealed record Candidate(string Name, string Url, string Sha256, long Size, string Source);

    /// <summary>Major version required by a version JSON, with a sensible fallback.</summary>
    public static int RequiredMajor(int? fromJson, string versionId, string? loader = null)
    {
        if (fromJson is > 0) return fromJson.Value;

        // Older manifests carry no javaVersion: infer from the game version.
        var id = versionId ?? "";
        if (id.StartsWith("1.21", StringComparison.Ordinal) ||
            id.StartsWith("1.20.5", StringComparison.Ordinal) ||
            id.StartsWith("1.20.6", StringComparison.Ordinal))
        {
            return 21;
        }

        if (id.StartsWith("1.18", StringComparison.Ordinal) ||
            id.StartsWith("1.19", StringComparison.Ordinal) ||
            id.StartsWith("1.20", StringComparison.Ordinal) ||
            id.StartsWith("1.17", StringComparison.Ordinal))
        {
            return 17;
        }

        // Modded 1.17+ always needs 17+, and mod loaders state it in their installer.
        if (!string.IsNullOrEmpty(loader) && loader.Contains("1.1", StringComparison.Ordinal)) return 17;

        return 8;
    }

    /// <summary>Path of a runtime installed by this launcher, if any.</summary>
    public static string InstalledJavaPath(string runtimeRoot, int major)
    {
        var directory = Path.Combine(runtimeRoot, $"java-{major}");
        if (!Directory.Exists(directory)) return "";

        // Adoptium zips contain a top level folder (jdk-17.0.x-jre); the extractor
        // strips it, but be tolerant of both layouts.
        var direct = Path.Combine(directory, "bin", "java.exe");
        if (File.Exists(direct)) return direct;

        try
        {
            foreach (var candidate in Directory.GetFiles(directory, "java.exe", SearchOption.AllDirectories))
            {
                if (candidate.Contains(Path.Combine("bin", "java.exe"), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return "";
    }

    /// <summary>Downloads and extracts the required JRE. Safe to call repeatedly.</summary>
    public static async Task<Result> InstallAsync(int major, string runtimeRoot, Action<string>? log = null,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        void Say(string message)
        {
            log?.Invoke(message);
        }

        try
        {
            if (major <= 0) return new Result(false, "无法确定需要的 Java 版本", "", major);

            var existing = InstalledJavaPath(runtimeRoot, major);
            if (existing.Length > 0)
            {
                // 已装过：再校验一次主版本，避免旧版本残留被误用
                var probe = JavaDetector.Probe(existing);
                if (probe is not null && probe.MajorVersion >= major)
                {
                    return new Result(true, $"Java {probe.MajorVersion} 已存在", existing, probe.MajorVersion);
                }

                Say($"[java] 已有的 {existing} 版本不符合要求（需要 {major}+），将继续安装");
            }

            // 首选：Mojang 官方给这个版本配的运行时（与官方启动器一致，版本对应最准）
            Say($"[java] 尝试安装官方运行时 {MojangJavaRuntime.ComponentFor(major)}（Java {major}）…");
            var official = await MojangJavaRuntime.InstallAsync(null, major, runtimeRoot, Say, progress, token)
                .ConfigureAwait(false);
            if (official.Ok && official.JavaPath.Length > 0)
            {
                var probed = JavaDetector.Probe(official.JavaPath);
                if (probed is not null && probed.MajorVersion >= major)
                {
                    Say($"[java] 官方运行时可用：{probed.ShortDisplay}");
                    return new Result(true, $"官方 {probed.ShortDisplay} 已安装", official.JavaPath, probed.MajorVersion);
                }

                Say($"[java] 官方运行时探测不通过（{(probed is null ? "无法执行" : "Java " + probed.MajorVersion)}），改用 Adoptium");
            }
            else if (!official.Ok)
            {
                Say($"[java] 官方运行时不可用（{official.Message}），改用 Adoptium");
            }

            var candidate = await FindCandidateAsync(major, Say, token).ConfigureAwait(false);
            if (candidate is null)
            {
                return new Result(false, $"没有找到 Java {major} 的下载源（网络不可用？）", "", major);
            }

            Say($"[java] 开始下载 Java {major}: {candidate.Name}（{TextUtil.FormatBytes(candidate.Size)}，来源 {candidate.Source}）");

            var downloadDirectory = Path.Combine(runtimeRoot, "downloads");
            AppPaths.EnsureDirectory(downloadDirectory);
            var archive = Path.Combine(downloadDirectory, candidate.Name);

            await DownloadAsync(candidate.Url, archive, candidate.Size, progress, token).ConfigureAwait(false);

            if (candidate.Sha256.Length > 0)
            {
                var actual = await Sha256Async(archive, token).ConfigureAwait(false);
                if (!actual.Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(archive);
                    return new Result(false, $"校验失败（期望 {candidate.Sha256[..8]}…，实际 {actual[..8]}…）", "", major);
                }

                Say("[java] SHA-256 校验通过");
            }

            var target = Path.Combine(runtimeRoot, $"java-{major}");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

            Say($"[java] 正在解压到 {target}");
            Extract(archive, target);

            var javaPath = InstalledJavaPath(runtimeRoot, major);
            if (javaPath.Length == 0)
            {
                return new Result(false, "解压完成但没找到 bin\\java.exe", "", major);
            }

            try
            {
                File.Delete(archive);
            }
            catch (Exception)
            {
                // the archive may stay behind, it is only a few tens of MB
            }

            // The detection cache must forget the old result.
            JavaDetector.InvalidateCache();

            Say($"[java] Java {major} 自动补齐完成: {javaPath}");
            return new Result(true, $"Java {major} 已就绪", javaPath, major);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, "已取消", "", major);
        }
        catch (Exception e)
        {
            return new Result(false, "自动补齐 Java 失败: " + e.Message, "", major);
        }
    }

    // ------------------------------------------------------------- download sources

    private static async Task<Candidate?> FindCandidateAsync(int major, Action<string> say, CancellationToken token)
    {
        // 1. Adoptium API: exact file name, size and SHA-256.
        try
        {
            var api = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                      "?os=windows&architecture=x64&image_type=jre&vendor=eclipse";
            var json = await Http.ApiClient.GetStringAsync(api, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            foreach (var asset in document.RootElement.EnumerateArray())
            {
                if (!asset.TryGetProperty("binary", out var binary)) continue;

                var package = binary.GetProperty("package");
                var name = package.GetProperty("name").GetString() ?? $"java-{major}.zip";
                var link = package.GetProperty("link").GetString();
                if (string.IsNullOrEmpty(link)) continue;

                var checksum = package.TryGetProperty("checksum", out var sum)
                    ? sum.GetString() ?? ""
                    : "";
                var size = package.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;

                return new Candidate(name, link, checksum, size, "Adoptium");
            }
        }
        catch (Exception e)
        {
            say("[java] Adoptium API 不可用: " + e.Message);
        }

        // 2. TUNA mirror of Adoptium (fast in China): scrape the directory listing.
        try
        {
            var mirror = $"https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{major}/jre/x64/windows/";
            var html = await Http.ApiClient.GetStringAsync(mirror, token).ConfigureAwait(false);
            var matches = System.Text.RegularExpressions.Regex.Matches(html,
                "href=\"([^\"]+(_windows_hotspot_[^\"]+|x64_windows_hotspot[^\"]+)\\.zip)\"");

            var files = matches
                .Select(m => m.Groups[1].Value)
                .Where(name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .OrderByDescending(name => name)
                .ToList();

            if (files.Count > 0)
            {
                return new Candidate(files[0], mirror + files[0], "", 0, "TUNA 镜像");
            }
        }
        catch (Exception e)
        {
            say("[java] TUNA 镜像不可用: " + e.Message);
        }

        return null;
    }

    private static async Task DownloadAsync(string url, string target, long expectedSize,
        IProgress<double>? progress, CancellationToken token)
    {
        using var response = await Http.Client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var sink = File.Create(target);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await sink.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Report((double)done / total);
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Extracts a zip, flattening the single top level directory.</summary>
    private static void Extract(string archive, string target)
    {
        Directory.CreateDirectory(target);
        using var zip = ZipFile.OpenRead(archive);

        // Adoptium zips wrap everything in "jdk-17.0.x-jre/"; strip that prefix.
        var prefix = "";
        var first = zip.Entries.FirstOrDefault(e => e.FullName.Contains('/'));
        if (first is not null)
        {
            var slash = first.FullName.IndexOf('/');
            var root = first.FullName[..(slash + 1)];
            if (zip.Entries.All(e => e.FullName.StartsWith(root, StringComparison.Ordinal))) prefix = root;
        }

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (prefix.Length > 0) name = name[prefix.Length..];
            if (name.Length == 0) continue;

            var destination = Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Zip slip protection.
            var full = Path.GetFullPath(destination);
            if (!full.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;

            entry.ExtractToFile(full, overwrite: true);
        }
    }
}
