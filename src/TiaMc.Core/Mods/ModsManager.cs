using System.Text.Json;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMc.Core.Mods;

public enum ModLoader
{
    Unknown,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    LiteLoader,
    LegacyForge
}

/// <summary>One file in the mods folder, with whatever metadata could be read from it.</summary>
public sealed class ModInfo
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }

    /// <summary>False when the file carries the ".disabled" extension.</summary>
    public bool Enabled { get; set; } = true;

    public string? Name { get; set; }
    public string? ModId { get; set; }
    public string? Version { get; set; }
    public string? Authors { get; set; }
    public string? Description { get; set; }
    public string? License { get; set; }
    public string? Homepage { get; set; }
    public string? Issues { get; set; }
    public ModLoader Loader { get; set; } = ModLoader.Unknown;
    public bool HasMetadata { get; set; }

    /// <summary>Extracted mod icon (cached on disk) so the list is easy to scan.</summary>
    public string IconPath { get; set; } = "";

    public bool HasIcon => IconPath.Length > 0;
    public string? MetadataError { get; set; }

    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }

    public string SizeText => Utils.TextUtil.FormatBytes(Size);
    public string StatusText => Enabled ? "已启用" : "已停用";
    public string LoaderText => Loader switch
    {
        ModLoader.Forge => "Forge",
        ModLoader.NeoForge => "NeoForge",
        ModLoader.Fabric => "Fabric",
        ModLoader.Quilt => "Quilt",
        ModLoader.LiteLoader => "LiteLoader",
        ModLoader.LegacyForge => "Forge(旧)",
        _ => "-"
    };

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FileName : Name!;
    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "-" : Version!;

    public override string ToString() => $"{DisplayName} {Version}";
}

/// <summary>
/// Reads and manages the mods folder of one instance. Loader manifests are
/// parsed straight out of the jar: mods.toml / META-INF/mods.toml (Forge and
/// NeoForge), fabric.mod.json (Fabric) and quilt.mod.json (Quilt).
/// </summary>
public static class ModsManager
{
    public const string DisabledSuffix = ".disabled";

    /// <summary>
    /// Finds the mods folder a version actually uses. "mods" is resolved relative
    /// to the game directory, which is:
    ///   * the instance folder when the version is isolated, or
    ///   * the shared .minecraft root otherwise.
    /// The other candidate is offered as a fallback so a folder created by another
    /// launcher is still picked up.
    /// </summary>
    public static string ResolveDirectory(Minecraft.McPaths paths, string versionId, bool isolated = false)
    {
        var primary = isolated
            ? Path.Combine(paths.IsolatedGameDir(versionId), "mods")
            : Path.Combine(paths.SharedGameDir, "mods");

        if (Directory.Exists(primary)) return primary;

        var alternative = isolated
            ? Path.Combine(paths.SharedGameDir, "mods")
            : Path.Combine(paths.IsolatedGameDir(versionId), "mods");

        if (Directory.Exists(alternative)) return alternative;

        return primary;
    }

    /// <summary>Scans a mods folder. Missing folders simply yield an empty list.</summary>
    public static List<ModInfo> Scan(string modsDirectory, Action<string>? log = null)
    {
        var result = new List<ModInfo>();
        if (!Directory.Exists(modsDirectory)) return result;

        // Reading metadata means opening every jar. Two optimisations:
        //   1. a cache keyed by path + size + mtime, so a rescan is nearly free;
        //   2. parallel parsing, because zip reads are I/O bound.
        var cache = MetadataCache.Load();
        var files = Directory.EnumerateFiles(modsDirectory)
            .Where(IsModFile)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var infos = new ModInfo?[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
        }, i =>
        {
            var path = files[i];
            var name = Path.GetFileName(path);
            FileInfo file;
            try
            {
                file = new FileInfo(path);
                if (!file.Exists) return;
            }
            catch (Exception)
            {
                return;
            }

            var info = new ModInfo
            {
                FilePath = path,
                FileName = name,
                Enabled = !name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase),
                Size = file.Length,
                ModifiedUtc = file.LastWriteTimeUtc
            };

            if (cache.TryGet(info, out var cached))
            {
                info.ModId = cached.ModId;
                info.Name = cached.Name;
                info.Version = cached.Version;
                info.Loader = cached.Loader;
                info.Authors = cached.Authors;
                info.Description = cached.Description;
                info.HasMetadata = cached.HasMetadata;
                info.IconPath = cached.IconPath ?? "";
            }
            else
            {
                TryReadMetadata(info, log);
                cache.Put(info);
            }

            infos[i] = info;
        });

        foreach (var info in infos)
        {
            if (info is not null) result.Add(info);
        }

        cache.Save();
        return result;
    }

    /// <summary>
    /// Metadata cache: path + size + last write time is the key, so a mod is only
    /// re-parsed when the file actually changed.
    /// </summary>
    private sealed class MetadataCache
    {
        private readonly Dictionary<string, CacheEntry> _entries;
        private int _dirty;

        private MetadataCache(Dictionary<string, CacheEntry> entries) => _entries = entries;

        public static string CachePath => Utils.AppPaths.ModMetadataFile;

        public static MetadataCache Load()
        {
            try
            {
                if (File.Exists(CachePath))
                {
                    var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath));
                    if (entries is not null)
                    {
                        return new MetadataCache(new Dictionary<string, CacheEntry>(entries,
                            StringComparer.OrdinalIgnoreCase));
                    }
                }
            }
            catch (Exception)
            {
                // a broken cache is not an error: it gets rebuilt
            }

            return new MetadataCache(new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase));
        }

        public bool TryGet(ModInfo info, out CacheEntry entry)
        {
            entry = new CacheEntry();
            if (!_entries.TryGetValue(info.FilePath, out var found) || found is null) return false;
            if (found.Size != info.Size || found.ModifiedTicks != info.ModifiedUtc.Ticks) return false;
            entry = found;
            return true;
        }

        public void Put(ModInfo info)
        {
            lock (_entries)
            {
                _entries[info.FilePath] = new CacheEntry
                {
                    Size = info.Size,
                    ModifiedTicks = info.ModifiedUtc.Ticks,
                    ModId = info.ModId,
                    Name = info.Name,
                    Version = info.Version,
                    Loader = info.Loader,
                    Authors = info.Authors,
                    Description = info.Description,
                    HasMetadata = info.HasMetadata,
                    IconPath = info.IconPath
                };
            }

            Interlocked.Exchange(ref _dirty, 1);
        }

        public void Save()
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;

            try
            {
                var directory = Path.GetDirectoryName(CachePath)!;
                Directory.CreateDirectory(directory);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(_entries,
                    new JsonSerializerOptions { WriteIndented = false }));
            }
            catch (Exception)
            {
                // caching is best effort
            }
        }

        public sealed class CacheEntry
        {
            public long Size { get; set; }
            public long ModifiedTicks { get; set; }
            public string? ModId { get; set; }
            public string? Name { get; set; }
            public string? Version { get; set; }
            public ModLoader Loader { get; set; }
            public string? Authors { get; set; }
            public string? Description { get; set; }
            public bool HasMetadata { get; set; }
            public string? IconPath { get; set; }
        }
    }

    /// <summary>True for files the game would load (or that we disabled ourselves).</summary>
    public static bool IsModFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.')) return false;
        return name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".zip" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path a mod file has when it is enabled / disabled.</summary>
    public static string PathFor(ModInfo mod, bool enabled) =>
        enabled
            ? mod.FilePath
            : mod.FilePath + DisabledSuffix;

    /// <summary>Enables or disables a mod by renaming the file, then updates the record.</summary>
    public static ModInfo SetEnabled(ModInfo mod, bool enabled, Action<string>? log = null)
    {
        var current = mod.FilePath;
        if (enabled == mod.Enabled)
        {
            return mod;
        }

        var target = enabled
            ? current[..^DisabledSuffix.Length]
            : current + DisabledSuffix;

        if (File.Exists(target))
        {
            throw new IOException($"目标文件已存在: {Path.GetFileName(target)}");
        }

        File.Move(current, target);
        log?.Invoke($"[mods] {(enabled ? "已启用" : "已停用")} {Path.GetFileName(target)}");

        var updated = new ModInfo
        {
            FilePath = target,
            FileName = Path.GetFileName(target),
            Enabled = enabled,
            Name = mod.Name,
            ModId = mod.ModId,
            Version = mod.Version,
            Authors = mod.Authors,
            Description = mod.Description,
            License = mod.License,
            Homepage = mod.Homepage,
            Issues = mod.Issues,
            Loader = mod.Loader,
            HasMetadata = mod.HasMetadata,
            MetadataError = mod.MetadataError,
            Size = mod.Size,
            ModifiedUtc = mod.ModifiedUtc
        };

        return updated;
    }

    public static void Delete(ModInfo mod, Action<string>? log = null)
    {
        if (File.Exists(mod.FilePath))
        {
            File.Delete(mod.FilePath);
            log?.Invoke($"[mods] 已删除 {mod.FileName}");
        }
    }

    /// <summary>
    /// Copies jar files (or whole folders of jars) into the mods folder.
    /// Returns the number of files copied; existing files are not overwritten.
    /// </summary>
    public static int Import(IEnumerable<string> sources, string modsDirectory, Action<string>? log = null)
    {
        Directory.CreateDirectory(modsDirectory);
        var copied = 0;

        foreach (var source in sources)
        {
            if (Directory.Exists(source))
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                             .Where(IsModFile))
                {
                    copied += CopyOne(file, modsDirectory, log);
                }

                continue;
            }

            if (File.Exists(source))
            {
                copied += CopyOne(source, modsDirectory, log);
            }
        }

        return copied;
    }

    private static int CopyOne(string file, string modsDirectory, Action<string>? log)
    {
        var name = Path.GetFileName(file);
        var target = Path.Combine(modsDirectory, name);

        if (File.Exists(target))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            var index = 1;
            do
            {
                target = Path.Combine(modsDirectory, $"{stem} ({index}){extension}");
                index++;
            } while (File.Exists(target));
        }

        File.Copy(file, target);
        log?.Invoke($"[mods] 已导入 {Path.GetFileName(target)}");
        return 1;
    }

    // ------------------------------------------------------------- metadata

/// <summary>
    /// Finds the mod logo inside a jar (Fabric pack.png / icon, Forge-NeoForge
    /// mods.toml logoFile, assets/&lt;modid&gt;/icon.png) and caches it on disk.
    /// </summary>
    private static string ExtractIcon(ModInfo info, ZipArchive archive)
    {
        try
        {
            var entry = FindIconEntry(info, archive);
            if (entry is null) return "";

            var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif")) extension = ".png";

            var directory = Utils.AppPaths.ModIconDirectory;
            Utils.AppPaths.EnsureDirectory(directory);

            // The key covers the jar identity and the icon entry, so replacing a mod
            // file or its logo produces a fresh cache file.
            var key = $"{info.FilePath}|{info.ModifiedUtc.Ticks}|{entry.FullName}";
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..24];
            var target = Path.Combine(directory, hash + extension);
            if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

            using var source = entry.Open();
            using var sink = File.Create(target);
            source.CopyTo(sink);
            return new FileInfo(target).Length > 0 ? target : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Icon entry lookup order used by the big launchers.</summary>
    private static ZipArchiveEntry? FindIconEntry(ModInfo info, ZipArchive archive)
    {
        foreach (var candidate in new[] { "pack.png", "icon.png", "logo.png", "icon.jpg", "logo.jpg" })
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) return entry;
        }

        // Forge / NeoForge declare logoFile in mods.toml.
        foreach (var meta in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
        {
            var toml = archive.Entries.FirstOrDefault(e => e.FullName.Equals(meta, StringComparison.OrdinalIgnoreCase));
            if (toml is null) continue;

            var text = ReadEntry(toml);
            var match = System.Text.RegularExpressions.Regex.Match(text, "logoFile\\s*=\\s*\"([^\"]+)\"");
            if (!match.Success) continue;

            var wanted = match.Groups[1].Value.TrimStart('/');
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                e.Name.Equals(Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase));
            if (entry is not null) return entry;
        }

        // Fabric / Quilt declare "icon" (string or per-size map).
        foreach (var meta in new[] { "fabric.mod.json", "quilt.mod.json" })
        {
            var file = archive.Entries.FirstOrDefault(e => e.FullName.Equals(meta, StringComparison.OrdinalIgnoreCase));
            if (file is null) continue;

            var json = ReadEntry(file);
            var match = System.Text.RegularExpressions.Regex.Match(json, "\"icon\"\\s*:\\s*(\"[^\"]+\"|\\{[^}]*\\})");
            if (!match.Success) continue;

            var raw = match.Groups[1].Value;
            var pathMatch = System.Text.RegularExpressions.Regex.Match(raw, "\"([^\"]+\\.(png|jpg|jpeg))\"");
            if (!pathMatch.Success) continue;

            var wanted = pathMatch.Groups[1].Value.TrimStart('/');
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) return entry;
        }

        // Last resort: the mod's own asset namespace.
        var modId = info.ModId ?? "";
        if (modId.Length > 0)
        {
            foreach (var name in new[] { "icon.png", "logo.png" })
            {
                var wanted = $"assets/{modId}/{name}";
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                if (entry is not null) return entry;
            }
        }

        return null;
    }

    private static void TryReadMetadata(ModInfo info, Action<string>? log)
    {
        try
        {
            using var archive = ZipFile.OpenRead(info.FilePath);

            // Mods ship their own logo (pack.png, icon.png, mods.toml logoFile);
            // it is extracted to a cache folder so the list can show it.
            info.IconPath = ExtractIcon(info, archive);

            // Fabric / Quilt first: they are unambiguous.
            var fabric = archive.GetEntry("fabric.mod.json");
            if (fabric is not null)
            {
                var json = ReadEntry(fabric);
                info.Loader = ModLoader.Fabric;
                info.ModId = JsonString(json, "id");
                info.Name = JsonString(json, "name") ?? info.ModId;
                info.Version = JsonString(json, "version");
                info.Description = JsonString(json, "description");
                info.License = JsonString(json, "license");
                info.Homepage = JsonString(json, "homepage") ?? Nested(json, "contact", "homepage");
                info.Issues = Nested(json, "contact", "issues");
                info.Authors = JsonAuthors(json);
                info.HasMetadata = true;
                return;
            }

            var quilt = archive.GetEntry("quilt.mod.json");
            if (quilt is not null)
            {
                var json = ReadEntry(quilt);
                info.Loader = ModLoader.Quilt;
                info.ModId = JsonString(json, "id");
                var loaderBlock = JsonObject(json, "quilt_loader") ?? json;
                var metadata = JsonObject(loaderBlock, "metadata") ?? loaderBlock;
                info.Name = JsonString(metadata, "name") ?? info.ModId;
                info.Version = JsonString(loaderBlock, "version");
                info.Description = JsonString(metadata, "description");
                info.Authors = JsonAuthors(metadata);
                info.HasMetadata = true;
                return;
            }

            // Forge / NeoForge
            var modsToml = archive.GetEntry("META-INF/mods.toml")
                           ?? archive.GetEntry("mods.toml")
                           ?? archive.GetEntry("META-INF/neoforge.mods.toml");

            if (modsToml is not null)
            {
                var toml = ReadEntry(modsToml);
                var isNeo = info.FilePath.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ||
                            archive.GetEntry("META-INF/neoforge.mods.toml") is not null;
                info.Loader = isNeo ? ModLoader.NeoForge : ModLoader.Forge;
                info.ModId = TomlValue(toml, "modId");
                info.Name = TomlValue(toml, "displayName") ?? info.ModId;
                info.Version = TomlValue(toml, "version");
                info.Description = TomlValue(toml, "description");
                info.License = TomlValue(toml, "license") ?? TomlRegexValue(toml, "license");
                info.Authors = TomlValue(toml, "authors") ?? TomlValue(toml, "authorList");
                info.Homepage = TomlValue(toml, "displayURL");

                // mods.toml usually carries ${file.jarVersion}; prefer the jar manifest.
                if (string.IsNullOrWhiteSpace(info.Version) || info.Version!.Contains("${"))
                {
                    info.Version = JarManifestVersion(archive) ?? info.Version;
                }

                info.HasMetadata = true;
                return;
            }

            // Very old Forge / LiteLoader use mcmod.info
            var mcmod = archive.GetEntry("mcmod.info");
            if (mcmod is not null)
            {
                var json = ReadEntry(mcmod);
                info.Loader = ModLoader.LegacyForge;
                info.ModId = JsonString(json, "modid");
                info.Name = JsonString(json, "name") ?? info.ModId;
                info.Version = JsonString(json, "version");
                info.Description = JsonString(json, "description");
                info.Authors = JsonString(json, "authorList");
                info.HasMetadata = true;
                return;
            }

            info.Loader = ModLoader.Unknown;
            info.MetadataError = "jar 里没有找到 mods.toml / fabric.mod.json";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            info.MetadataError = e.Message;
            log?.Invoke($"[mods] 无法读取 {info.FileName}: {e.Message}");
        }
        catch (Exception e)
        {
            info.MetadataError = e.Message;
        }
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? JarManifestVersion(ZipArchive archive)
    {
        var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
        if (manifest is null) return null;
        var text = ReadEntry(manifest);
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf(':');
            if (index <= 0) continue;
            var key = line[..index].Trim();
            if (key.Equals("Implementation-Version", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Specification-Version", StringComparison.OrdinalIgnoreCase))
            {
                return line[(index + 1)..].Trim();
            }
        }

        return null;
    }

    // --------------------------------------------------------------- parsers

    private static string? TomlValue(string toml, string key)
    {
        // key = "value"  |  key="value"  | key = 'value'
        var match = Regex.Match(toml,
            $@"^\s*{Regex.Escape(key)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value.Trim() : null;
    }

    private static string? TomlRegexValue(string toml, string key)
    {
        var match = Regex.Match(toml, $@"{Regex.Escape(key)}\s*=\s*""(?<v>[^""]*)""",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value.Trim() : null;
    }

    private static string? JsonString(string json, string key)
    {
        var match = Regex.Match(json, $@"""{Regex.Escape(key)}""\s*:\s*""(?<v>(?:[^""\\]|\\.)*)""",
            RegexOptions.IgnoreCase);
        return match.Success ? Regex.Unescape(match.Groups["v"].Value) : null;
    }

    private static string? Nested(string json, string outer, string inner)
    {
        var block = JsonObject(json, outer);
        return block is null ? null : JsonString(block, inner);
    }

    /// <summary>Extracts the raw body of a nested JSON object (one level, brace counted).</summary>
    private static string? JsonObject(string json, string key)
    {
        var start = Regex.Match(json, $@"""{Regex.Escape(key)}""\s*:\s*\{{", RegexOptions.IgnoreCase);
        if (!start.Success) return null;

        var index = start.Index + start.Length;
        var depth = 1;
        var inString = false;
        var escaped = false;

        for (var i = index; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return json[index..i];
                    break;
            }
        }

        return null;
    }

    private static string? JsonAuthors(string json)
    {
        var match = Regex.Match(json, @"""authors""\s*:\s*\[(?<body>.*?)\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var names = Regex.Matches(match.Groups["body"].Value, @"""(?<v>(?:[^""\\]|\\.)*)""")
            .Select(m => Regex.Unescape(m.Groups["v"].Value))
            .ToList();

        return names.Count == 0 ? null : string.Join(", ", names.Take(4));
    }
}
