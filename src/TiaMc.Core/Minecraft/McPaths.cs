namespace TiaMc.Core.Minecraft;

/// <summary>
/// The directory layout of a Minecraft installation. Every path the launcher
/// touches is derived from <see cref="Root"/>, so a single folder can be shared
/// with PCL2 / HMCL / the official launcher.
/// </summary>
public sealed class McPaths
{
    public McPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>The ".minecraft" folder (game root).</summary>
    public string Root { get; }

    public string VersionsDir => Path.Combine(Root, "versions");
    public string LibrariesDir => Path.Combine(Root, "libraries");
    public string AssetsDir => Path.Combine(Root, "assets");
    public string AssetIndexesDir => Path.Combine(AssetsDir, "indexes");
    public string AssetObjectsDir => Path.Combine(AssetsDir, "objects");
    public string ModsDir => Path.Combine(Root, "mods");

    /// <summary>Where extracted native libraries live, one folder per game version.</summary>
    public string NativesDir(string versionId) => Path.Combine(VersionsDir, versionId, versionId + "-natives");

    public string VersionDir(string versionId) => Path.Combine(VersionsDir, versionId);

    public string VersionJsonPath(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".json");

    public string VersionJarPath(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".jar");

    public string AssetsIndexPath(string indexId) => Path.Combine(AssetIndexesDir, indexId + ".json");

    /// <summary>
    /// Folder used by a version that is not isolated. Modern loaders (Forge 1.17+,
    /// NeoForge, Fabric) resolve "mods", "config", "saves" and "resourcepacks"
    /// relative to the *game directory*, which is normally the .minecraft root.
    /// </summary>
    public string SharedGameDir => Root;

    /// <summary>The per-version folder, used as the game directory when isolating.</summary>
    public string IsolatedGameDir(string versionId) => VersionDir(versionId);

    /// <summary>config folder of an isolated instance (same rule the game uses).</summary>
    public string IsolatedConfigDir(string versionId) => Path.Combine(IsolatedGameDir(versionId), "config");

    /// <summary>Where an isolated instance keeps its worlds.</summary>
    public string IsolatedSavesDir(string versionId) => Path.Combine(IsolatedGameDir(versionId), "saves");

    /// <summary>
    /// Resolves a relative game path (mods, saves, logs, ...) the same way the game
    /// and the loader do: relative to a given game directory.
    /// </summary>
    public static string Under(string gameDirectory, string relative) => Path.Combine(gameDirectory, relative);

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    /// <summary>Picks the best guess for an existing installation folder.</summary>
    public static string DetectRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured!;
        }

        var candidates = new[]
        {
            // Portable folder next to the executable wins: the launcher creates it
            // by default so it never has to touch %APPDATA%.
            Path.Combine(AppContext.BaseDirectory, "minecraft"),
            Path.Combine(AppContext.BaseDirectory, ".minecraft"),
            DefaultRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return DefaultRoot;
    }

    public override string ToString() => Root;
}
