using System.IO;

namespace TiaMc.Core.Utils;

/// <summary>
/// Where the launcher keeps its caches (mod metadata, extracted mod icons, the
/// Java detection result, the Chinese search table).
///
/// The portable launcher overrides <see cref="CacheRoot"/> with
/// "&lt;config&gt;\cache" so nothing is written outside the program directory;
/// the default keeps %LOCALAPPDATA%\TiaMC for the non portable case.
/// </summary>
public static class AppPaths
{
    private static string _cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMC");

    /// <summary>Cache root; set by the host (app / CLI) once at startup.</summary>
    public static string CacheRoot
    {
        get => _cacheRoot;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _cacheRoot = value;
            try
            {
                Directory.CreateDirectory(_cacheRoot);
            }
            catch (Exception)
            {
                // the caller falls back to the previous root when this fails
            }
        }
    }

    /// <summary>Extracted mod icons.</summary>
    public static string ModIconDirectory => Path.Combine(CacheRoot, "mod-icons");

    /// <summary>Mod jar metadata cache.</summary>
    public static string ModMetadataFile => Path.Combine(CacheRoot, "mod-cache.json");

    /// <summary>Java runtime detection result.</summary>
    public static string JavaCacheFile => Path.Combine(CacheRoot, "java-cache.json");

    /// <summary>Optional Chinese name table for content search (Axolotl format).</summary>
    public static string ChineseSearchFile => Path.Combine(CacheRoot, "search-zh.txt");

    /// <summary>Ensures a directory exists, returning false when it cannot be created.</summary>
    public static bool EnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
    /// <summary>Default budget for extracted mod icons (24 MB).</summary>
    public const long IconBudgetBytes = 24L * 1024 * 1024;

    /// <summary>
    /// Hard disk protection for the image cache: when the icon folder grows past
    /// the budget the oldest files are deleted until it fits again. Called after
    /// every extraction and once at startup, so a folder with hundreds of mods can
    /// never fill the drive.
    /// </summary>
    public static (int Deleted, long FreedBytes, long TotalBytes) EnforceIconBudget(long budgetBytes = IconBudgetBytes)
    {
        var deleted = 0;
        long freed = 0;
        try
        {
            var directory = ModIconDirectory;
            if (!Directory.Exists(directory)) return (0, 0, 0);

            var files = new DirectoryInfo(directory).GetFiles()
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var total = files.Sum(f => f.Length);

            // Zero byte leftovers are always removed.
            foreach (var broken in files.Where(f => f.Length == 0).ToList())
            {
                try
                {
                    broken.Delete();
                    files.Remove(broken);
                    deleted++;
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            foreach (var file in files)
            {
                if (total <= budgetBytes) break;
                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                    freed += length;
                    deleted++;
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            return (deleted, freed, total);
        }
        catch (Exception)
        {
            return (deleted, freed, 0);
        }
    }

    /// <summary>Current size of the icon cache in bytes.</summary>
    public static long IconCacheSizeBytes()
    {
        try
        {
            var directory = ModIconDirectory;
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).GetFiles().Sum(f => f.Length)
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Removes every cache file (mod icons, metadata, Java list).</summary>
    public static (int Files, long Bytes) ClearCaches()
    {
        var files = 0;
        long bytes = 0;
        foreach (var directory in new[] { ModIconDirectory, CacheRoot })
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var file in new DirectoryInfo(directory).GetFiles())
                {
                    // Keep the optional Chinese dictionary, it is user data.
                    if (file.FullName.Equals(ChineseSearchFile, StringComparison.OrdinalIgnoreCase)) continue;
                    bytes += file.Length;
                    file.Delete();
                    files++;
                }
            }
            catch (Exception)
            {
                // best effort
            }
        }

        return (files, bytes);
    }
}
