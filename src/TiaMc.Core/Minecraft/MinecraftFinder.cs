using System.IO;

namespace TiaMc.Core.Minecraft;

/// <summary>Where a candidate installation came from.</summary>
public enum McRootSource
{
    /// <summary>The launcher's own configuration.</summary>
    Configured,
    /// <summary>%APPDATA%\.minecraft (the official launcher and most modpacks).</summary>
    AppData,
    /// <summary>A .minecraft folder inside the user profile.</summary>
    UserProfile,
    /// <summary>Registered by the official Minecraft launcher.</summary>
    Registry,
    /// <summary>Data folder of another launcher (PCL2, HMCL, BakaXL, MultiMC, Prism, ...).</summary>
    OtherLauncher,
    /// <summary>Found by scanning drives.</summary>
    DriveScan,
    /// <summary>Created by this launcher next to its executable (portable mode).</summary>
    LauncherFolder
}

/// <summary>A Minecraft folder found on this machine.</summary>
public sealed class McRootCandidate
{
    public required string Path { get; init; }
    public required McRootSource Source { get; init; }
    /// <summary>Human readable origin, e.g. "PCL2" or "注册表".</summary>
    public string Origin { get; init; } = "";
    public int VersionCount { get; set; }
    public bool IsValid => VersionCount > 0 || Directory.Exists(System.IO.Path.Combine(Path, "assets"));
    public bool Exists => Directory.Exists(Path);

    public string SourceText => Source switch
    {
        McRootSource.Configured => "当前配置",
        McRootSource.AppData => "%APPDATA%\\.minecraft",
        McRootSource.UserProfile => "用户目录",
        McRootSource.Registry => "注册表",
        McRootSource.OtherLauncher => Origin.Length > 0 ? Origin : "其它启动器",
        McRootSource.DriveScan => "磁盘扫描",
        McRootSource.LauncherFolder => "程序目录",
        _ => Origin
    };

    /// <summary>True when this folder sits next to the launcher executable.</summary>
    public bool IsPortable => Source == McRootSource.LauncherFolder;

    public string Summary => VersionCount > 0
        ? $"{VersionCount} 个版本"
        : Exists ? "无版本（缺 versions 目录）" : "不存在";

    public string Display => $"{Path}    [{SourceText} · {Summary}]";

    public override string ToString() => Display;
}

/// <summary>
/// Finds Minecraft installations on this machine. Covered locations:
///   * the currently configured folder (kept even when it looks unusual)
///   * %APPDATA%\.minecraft and %USERPROFILE%\.minecraft
///   * the folder registered by the official launcher (HKCU\Software\Mojang\...)
///   * data folders of other launchers (PCL2, HMCL, BakaXL, MultiMC, Prism, ...)
///   * .minecraft folders found while scanning the fixed drives (depth limited)
/// </summary>
public static class MinecraftFinder
{
    /// <summary>Registry keys the official launcher writes the game directory to.</summary>
    private static readonly (string SubKey, string ValueName)[] RegistryLocations =
    [
        (@"Software\Mojang\InstalledProducts\Minecraft", "GameDir"),
        (@"Software\Mojang\InstalledProducts\MinecraftJava", "GameDir"),
        (@"Software\Mojang\Launcher", "GameDir"),
        (@"Software\Classes\minecraft\DefaultIcon", ""),
        (@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Minecraft", "InstallLocation")
    ];

    /// <summary>Data folders used by other launchers; the search also looks one level below.</summary>
    private static readonly (string Folder, string Name)[] OtherLauncherFolders =
    [
        (@"%APPDATA%\.minecraft", "官方启动器数据"),
        (@"%APPDATA%\PCL", "PCL2 数据"),
        (@"%APPDATA%\PCL2", "PCL2 数据"),
        (@"%LOCALAPPDATA%\PCL", "PCL2 数据"),
        (@"%APPDATA%\HMCL", "HMCL 数据"),
        (@"%APPDATA%\BakaXL", "BakaXL 数据"),
        (@"%APPDATA%\MultiMC", "MultiMC 数据"),
        (@"%APPDATA%\PrismLauncher", "Prism Launcher 数据"),
        (@"%LOCALAPPDATA%\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\game", "微软商店版"),
        (@"%USERPROFILE%\curseforge\minecraft", "CurseForge"),
        (@"%USERPROFILE%\AppData\Roaming\.minecraft", "官方启动器数据")
    ];

    /// <summary>Names that count as a Minecraft folder during the drive scan.</summary>
    private static readonly string[] ScanFolderNames = [".minecraft", "minecraft"];

    /// <summary>
    /// Returns every installation found, newest-evidence first: configured folder,
    /// well known locations, registry, other launchers, then a drive scan.
    /// </summary>
    public static List<McRootCandidate> FindAll(string? configuredRoot = null, Action<string>? log = null,
        bool scanDrives = true, CancellationToken token = default)
    {
        var found = new Dictionary<string, McRootCandidate>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? path, McRootSource source, string origin = "", bool forceInclude = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            string full;
            try
            {
                full = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
            }
            catch (Exception)
            {
                return;
            }

            if (!Directory.Exists(full)) return;
            if (found.ContainsKey(full)) return;

            // A folder only counts when it looks like a Minecraft installation,
            // unless the caller insists (the configured folder is always listed).
            var looksLikeMc = Directory.Exists(System.IO.Path.Combine(full, "versions"))
                              || Directory.Exists(System.IO.Path.Combine(full, "assets"))
                              || File.Exists(System.IO.Path.Combine(full, "launcher_profiles.json"))
                              || Directory.Exists(System.IO.Path.Combine(full, "libraries"));

            if (!forceInclude && !looksLikeMc) return;

            found[full] = new McRootCandidate
            {
                Path = full,
                Source = source,
                Origin = origin,
                VersionCount = CountVersions(full)
            };
        }

        // 1. the configured folder (and the historical default)
        Consider(configuredRoot, McRootSource.Configured, "settings", forceInclude: true);
        Consider(McPaths.DefaultRoot, McRootSource.AppData);
        Consider(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft"), McRootSource.UserProfile);

        // The portable folder next to the launcher executable (created on request).
        Consider(System.IO.Path.Combine(AppContext.BaseDirectory, ".minecraft"), McRootSource.LauncherFolder,
            "程序目录（便携）");
        Consider(System.IO.Path.Combine(AppContext.BaseDirectory, "minecraft"), McRootSource.LauncherFolder,
            "程序目录（便携）");

        // 2. official launcher registry entries (Windows only; the registry API is
        //    reached through reflection free code that the JIT only compiles on
        //    Windows, so the core library still builds for other platforms).
        if (OperatingSystem.IsWindows())
        {
            foreach (var (subKey, valueName) in RegistryLocations)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
                    if (key is null) continue;
                    var value = key.GetValue(valueName) as string;
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    // DefaultIcon values look like "<path>\<exe>,0"; keep the directory only.
                    if (value.Contains(',')) value = value[..value.IndexOf(',')];
                    if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        value = System.IO.Path.GetDirectoryName(value) ?? value;
                    }

                    Consider(value, McRootSource.Registry, "官方启动器注册表");
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    // Sandboxes and locked down accounts can refuse registry reads.
                }
            }
        }

        // 3. other launchers' data folders (and a .minecraft inside them)
        foreach (var (template, name) in OtherLauncherFolders)
        {
            var expanded = Environment.ExpandEnvironmentVariables(template);
            Consider(expanded, McRootSource.OtherLauncher, name);
            Consider(System.IO.Path.Combine(expanded, ".minecraft"), McRootSource.OtherLauncher, name);
            Consider(System.IO.Path.Combine(expanded, "minecraft"), McRootSource.OtherLauncher, name);
        }

        // Prism / MultiMC keep one folder per instance below "instances".
        foreach (var baseFolder in new[]
                 {
                     Environment.ExpandEnvironmentVariables(@"%APPDATA%\PrismLauncher\instances"),
                     Environment.ExpandEnvironmentVariables(@"%APPDATA%\MultiMC\instances")
                 })
        {
            if (!Directory.Exists(baseFolder)) continue;
            try
            {
                foreach (var instance in Directory.EnumerateDirectories(baseFolder))
                {
                    Consider(System.IO.Path.Combine(instance, ".minecraft"), McRootSource.OtherLauncher,
                        System.IO.Path.GetFileName(instance));
                    Consider(System.IO.Path.Combine(instance, "minecraft"), McRootSource.OtherLauncher,
                        System.IO.Path.GetFileName(instance));
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Ignore unreadable folders.
            }
        }

        // 4. drive scan
        if (scanDrives)
        {
            foreach (var candidate in ScanDrives(log, token))
            {
                Consider(candidate, McRootSource.DriveScan, "磁盘扫描");
            }
        }

        return found.Values
            .OrderByDescending(c => c.Source == McRootSource.Configured)
            .ThenByDescending(c => c.VersionCount)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Looks for ".minecraft" / "minecraft" folders on every fixed drive. The walk
    /// is depth limited and skips Windows, Program Files and other system trees so
    /// it stays fast enough to run at start-up.
    /// </summary>
    private static IEnumerable<string> ScanDrives(Action<string>? log, CancellationToken token)
    {
        var results = new List<string>();

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToArray();
        }
        catch (Exception)
        {
            return results;
        }

        foreach (var drive in drives)
        {
            token.ThrowIfCancellationRequested();
            var root = drive.RootDirectory.FullName;
            log?.Invoke($"[detect] 扫描 {root}");

            try
            {
                Walk(root, 0);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Keep going with the next drive.
            }
        }

        return results;

        void Walk(string directory, int depth)
        {
            if (depth > 3 || token.IsCancellationRequested) return;

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return;
            }

            foreach (var sub in subdirectories)
            {
                var name = System.IO.Path.GetFileName(sub);
                var depthOfChild = depth + 1;

                // The folder itself may be an installation.
                if (ScanFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase) && LooksLikeInstallation(sub))
                {
                    results.Add(sub);
                    continue;
                }

                if (IsSkipped(name)) continue;

                // "minecraft" folders nested in launcher data folders are the norm,
                // so the walk is allowed one level deeper there.
                var nextDepth = ScanFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? depth : depthOfChild;
                Walk(sub, nextDepth);
            }
        }

        static bool IsSkipped(string name) =>
            name.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AppData", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeInstallation(string directory) =>
        Directory.Exists(System.IO.Path.Combine(directory, "versions")) ||
        Directory.Exists(System.IO.Path.Combine(directory, "assets")) ||
        File.Exists(System.IO.Path.Combine(directory, "launcher_profiles.json"));

    /// <summary>
    /// Portable mode: creates "&lt;程序目录&gt;\minecraft" (when missing) and returns it, so
    /// downloaded versions, libraries, assets and instances live next to the
    /// executable instead of in %APPDATA%.
    /// </summary>
    public static string CreatePortableRoot(Action<string>? log = null)
    {
        var root = System.IO.Path.Combine(AppContext.BaseDirectory, "minecraft");
        Directory.CreateDirectory(root);
        foreach (var folder in new[] { "versions", "libraries", "assets", "mods" })
        {
            Directory.CreateDirectory(System.IO.Path.Combine(root, folder));
        }

        log?.Invoke($"[portable] 已创建程序目录下的 Minecraft 目录: {root}");
        return root;
    }

    /// <summary>Path of the portable root, whether or not it exists yet.</summary>
    public static string PortableRoot => System.IO.Path.Combine(AppContext.BaseDirectory, "minecraft");

    /// <summary>Counts the version folders that actually contain a JSON file.</summary>
    public static int CountVersions(string root)
    {
        var versionsDir = System.IO.Path.Combine(root, "versions");
        if (!Directory.Exists(versionsDir)) return 0;

        try
        {
            var count = 0;
            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                var id = System.IO.Path.GetFileName(dir);
                if (File.Exists(System.IO.Path.Combine(dir, id + ".json"))) count++;
            }

            return count;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }
}
