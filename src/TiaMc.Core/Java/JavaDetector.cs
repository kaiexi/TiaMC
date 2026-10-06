using System.Text.Json;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TiaMc.Core.Java;

/// <summary>One detected Java runtime.</summary>
public sealed class JavaInfo
{
    public required string Path { get; init; }
    public required int MajorVersion { get; init; }
    public required string FullVersion { get; init; }
    public string? Architecture { get; init; }
    public string? Vendor { get; init; }
    public string Source { get; init; } = "scan";
    public bool Is64Bit { get; init; }

    public string Display => $"Java {MajorVersion} ({FullVersion})  {(Is64Bit ? "64-bit" : "32-bit")}  {Path}";
    public string ShortDisplay => $"Java {MajorVersion} - {ShortenPath(Path)}";

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    public string GetExecutable() => Path;
}

/// <summary>
/// Finds installed Java runtimes by consulting JAVA_HOME, PATH and the usual
/// install locations (including the JVMs bundled by other launchers), then
/// probing each candidate with "java -version".
/// </summary>
public static class JavaDetector
{
    private static readonly string[] RootedSearchPaths =
    [
        @"C:\Program Files\Java",
        @"C:\Program Files (x86)\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\Microsoft\jdk",
        @"C:\Program Files\Zulu",
        @"C:\Program Files\BellSoft",
        @"C:\Program Files\Amazon Corretto",
        @"C:\Program Files\Semeru",
        @"C:\Program Files\Common Files\Oracle\Java",
        @"C:\Java",
        @"D:\Java",
        @"D:\Program Files\Java"
    ];

    private static readonly string[] UserSearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "jre"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Java"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMC", "jre")
    ];

    /// <summary>
    /// Scans for Java runtimes. <paramref name="extraDirectories"/> lets callers add
    /// folders (for example an instance specific runtime folder).
    /// </summary>
    public static List<JavaInfo> Detect(IEnumerable<string>? extraDirectories = null,
        Action<string>? log = null, bool probe = true)
    {
        // The disk scan plus "java -version" per candidate costs hundreds of
        // milliseconds. The result only changes when the user installs a JDK, so it
        // is cached for a day (and invalidated by touching the cache file).
        var useCache = probe && (extraDirectories is null || !extraDirectories.Any());
        if (useCache && TryReadCache(out var cached))
        {
            log?.Invoke($"[java] 使用缓存的检测结果（{cached.Count} 个运行时）");
            return cached;
        }

        var result = DetectCore(extraDirectories, log, probe);
        if (useCache) WriteCache(result);
        return result;
    }

    private static string CachePath => Utils.AppPaths.JavaCacheFile;

    private static bool TryReadCache(out List<JavaInfo> runtimes)
    {
        runtimes = [];
        try
        {
            if (!File.Exists(CachePath)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > TimeSpan.FromDays(1)) return false;

            var cached = JsonSerializer.Deserialize<List<JavaInfo>>(File.ReadAllText(CachePath));
            if (cached is null || cached.Count == 0) return false;

            // Every path must still exist, otherwise the cache is stale.
            foreach (var info in cached)
            {
                if (!File.Exists(info.Path)) return false;
            }

            runtimes = cached;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteCache(List<JavaInfo> runtimes)
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath)!;
            Utils.AppPaths.EnsureDirectory(directory);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(runtimes));
        }
        catch (Exception)
        {
            // caching is best effort
        }
    }

    /// <summary>Invalidates the cached detection (used by the "re-detect" button).</summary>
    public static void InvalidateCache()
    {
        try
        {
            if (File.Exists(CachePath)) File.Delete(CachePath);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    private static List<JavaInfo> DetectCore(IEnumerable<string>? extraDirectories, Action<string>? log, bool probe)
    {
        var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            AddCandidate(executables, Path.Combine(javaHome!, "bin", "java.exe"));
        }

        // 2. PATH
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                AddCandidate(executables, Path.Combine(dir.Trim(), "java.exe"));
            }
            catch (ArgumentException)
            {
                // Invalid characters in a PATH entry - ignore.
            }
        }

        // 3. well known roots. Vendor folders are searched to depth 2 (vendor ->
        //    jdk-xx), while the runtime folders that other launchers create can
        //    nest one level deeper (runtime -> java-runtime-gamma -> windows-x64).
        foreach (var root in RootedSearchPaths.Where(Directory.Exists))
        {
            log?.Invoke($"[java] 扫描 {root}");
            foreach (var exe in EnumerateJavaExecutables(root, depth: 2))
            {
                AddCandidate(executables, exe);
            }
        }

        foreach (var userPath in UserSearchPaths.Where(Directory.Exists))
        {
            log?.Invoke($"[java] 扫描 {userPath}");
            foreach (var exe in EnumerateJavaExecutables(userPath, depth: 3))
            {
                AddCandidate(executables, exe);
            }
        }

        if (extraDirectories is not null)
        {
            foreach (var extra in extraDirectories.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)))
            {
                foreach (var exe in EnumerateJavaExecutables(extra, depth: 3))
                {
                    AddCandidate(executables, exe);
                }
            }
        }

        // 4. probe
        var result = new List<JavaInfo>();
        foreach (var exe in executables)
        {
            var info = probe ? Probe(exe) : QuickInfo(exe);
            if (info is not null) result.Add(info);
        }

        return result
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.MajorVersion).First())
            .OrderByDescending(i => i.MajorVersion)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddCandidate(HashSet<string> set, string candidate)
    {
        try
        {
            candidate = candidate.Trim().Trim('"');
            if (candidate.Length == 0) return;
            if (!candidate.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.Combine(candidate, "bin", "java.exe");
            }

            if (File.Exists(candidate)) set.Add(Path.GetFullPath(candidate));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Ignore malformed candidates.
        }
    }

    /// <summary>Walks a directory tree looking for bin\java.exe without following symlink loops.</summary>
    private static IEnumerable<string> EnumerateJavaExecutables(string root, int depth)
    {
        var found = new List<string>();
        Walk(root, 0);
        return found;

        void Walk(string dir, int level)
        {
            if (level > depth) return;

            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(dir, "java.exe", SearchOption.TopDirectoryOnly);
                dirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }

            foreach (var file in files)
            {
                if (dir.EndsWith("bin", StringComparison.OrdinalIgnoreCase)) found.Add(file);
            }

            foreach (var sub in dirs)
            {
                var name = Path.GetFileName(sub);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
                Walk(sub, level + 1);
            }
        }
    }

    private static JavaInfo? QuickInfo(string exe)
    {
        var home = ParentOfBin(exe);
        var release = TryReadRelease(home);
        if (release is null) return null;
        return new JavaInfo
        {
            Path = exe,
            MajorVersion = release.Value.Major,
            FullVersion = release.Value.Full,
            Architecture = release.Value.Arch,
            Is64Bit = release.Value.Is64,
            Source = "release-file"
        };
    }

    /// <summary>Runs "java -version" and parses the reported version.</summary>
    public static JavaInfo? Probe(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo(javaExe)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null) return null;

            var text = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }

            var (major, full, arch, vendor, is64) = ParseVersionOutput(text);
            if (major <= 0)
            {
                var quick = QuickInfo(javaExe);
                return quick;
            }

            return new JavaInfo
            {
                Path = javaExe,
                MajorVersion = major,
                FullVersion = full,
                Architecture = arch,
                Vendor = vendor,
                Is64Bit = is64,
                Source = "probe"
            };
        }
        catch (Exception)
        {
            return QuickInfo(javaExe);
        }
    }

    private static (int Major, string Full, string? Arch, string? Vendor, bool Is64) ParseVersionOutput(string text)
    {
        var major = 0;
        var full = "";
        string? arch = null;
        string? vendor = null;
        var is64 = Environment.Is64BitOperatingSystem;

        var match = Regex.Match(text, "version \"([^\"]+)\"");
        if (match.Success)
        {
            full = match.Groups[1].Value;
            var parts = full.Split('.', '_', '-', '+');
            if (parts.Length > 0 && int.TryParse(parts[0], out var first))
            {
                major = first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second) ? second : first;
            }
        }

        var archMatch = Regex.Match(text, @"(?:^|\s)(?:Java HotSpot|OpenJDK|Eclipse OpenJ9)[^\r\n]*?(\d{2})-Bit", RegexOptions.IgnoreCase);
        if (archMatch.Success && int.TryParse(archMatch.Groups[1].Value, out var bits))
        {
            is64 = bits == 64;
            arch = bits == 64 ? "x86_64" : "x86";
        }

        if (text.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase)) vendor = "OpenJDK";
        else if (text.Contains("Java(TM)", StringComparison.OrdinalIgnoreCase)) vendor = "Oracle";

        return (major, full, arch, vendor, is64);
    }

    private static string ParentOfBin(string javaExe)
    {
        var bin = Path.GetDirectoryName(javaExe) ?? "";
        return Directory.GetParent(bin)?.FullName ?? bin;
    }

    /// <summary>Reads the "release" file that ships with every JDK/JRE 9+.</summary>
    private static (int Major, string Full, string? Arch, bool Is64)? TryReadRelease(string javaHome)
    {
        var file = Path.Combine(javaHome, "release");
        if (!File.Exists(file)) return null;

        try
        {
            string? version = null;
            string? arch = null;
            foreach (var line in File.ReadLines(file))
            {
                var index = line.IndexOf('=');
                if (index <= 0) continue;
                var key = line[..index].Trim();
                var value = line[(index + 1)..].Trim().Trim('"');
                if (key.Equals("JAVA_VERSION", StringComparison.OrdinalIgnoreCase)) version = value;
                else if (key.Equals("OS_ARCH", StringComparison.OrdinalIgnoreCase)) arch = value;
            }

            if (string.IsNullOrWhiteSpace(version)) return null;

            var parts = version!.Split('.', '_', '-', '+');
            if (!int.TryParse(parts[0], out var first)) return null;
            var major = first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second) ? second : first;
            var is64 = arch?.Contains("64", StringComparison.Ordinal) == true || Environment.Is64BitOperatingSystem;

            return (major, version, arch, is64);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Keeps only the runtimes whose major version satisfies the requirement.</summary>
    public static List<JavaInfo> Filter(IEnumerable<JavaInfo> all, int requiredMajor)
    {
        var list = all.Where(j => requiredMajor <= 0 || j.MajorVersion == requiredMajor).ToList();
        if (list.Count == 0 && requiredMajor > 0)
        {
            list = all.Where(j => j.MajorVersion >= requiredMajor).ToList();
        }

        return list;
    }
}
