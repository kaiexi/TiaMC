using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMc.Core.Modpacks;

/// <summary>
/// Reads modpack metadata from every layout we support: Modrinth .mrpack,
/// CurseForge zips, Forge/NeoForge/Fabric server installer jars, plain zips and
/// already unpacked folders. Nothing is downloaded here, the reader only inspects.
/// </summary>
public static class ModpackReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads a pack from a file or a folder; returns null when it is not a pack.</summary>
    public static Modpack? Read(string path)
    {
        try
        {
            if (Directory.Exists(path)) return ReadFolder(path);
            if (!File.Exists(path)) return null;

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".jar") return ReadInstallerJar(path);
            if (extension is ".zip" or ".mrpack") return ReadArchive(path);
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------- archives

    public static Modpack? ReadArchive(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var fileName = Path.GetFileNameWithoutExtension(archivePath);

        var mrpack = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase));
        if (mrpack is not null) return ReadModrinthIndex(archivePath, fileName, mrpack);

        var curseForge = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (curseForge is not null) return ReadCurseForgeManifest(archivePath, fileName, curseForge);

        // An installer jar may be shipped inside a server pack zip.
        var installer = zip.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
            (e.Name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
             e.Name.Contains("forge-", StringComparison.OrdinalIgnoreCase) ||
             e.Name.Contains("neoforge-", StringComparison.OrdinalIgnoreCase) ||
             e.Name.Contains("fabric-server", StringComparison.OrdinalIgnoreCase)));

        var pack = new Modpack
        {
            Name = fileName,
            Format = ModpackFormat.Archive,
            SourceFile = archivePath,
            // Entry names may use backslashes when the zip was written on Windows.
            ArchiveEntries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList()
        };

        // A start script inside the pack names the server jar, which gives us the
        // game version and the loader even when the file name does not.
        foreach (var script in new[] { "run.bat", "run.sh", "start.bat", "start.sh", "user_jvm_args.txt" })
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(script, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;

            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            var (scriptLoader, scriptGame, scriptVersion) = ParseInstallerName(text);
            if (scriptLoader.Length > 0)
            {
                pack.Loader = scriptLoader;
                pack.GameVersion = scriptGame;
                pack.LoaderVersion = scriptVersion;
                break;
            }
        }

        var looksLikeServer = pack.ArchiveEntries.Any(e =>
            e.Equals("server.properties", StringComparison.OrdinalIgnoreCase) ||
            e.Equals("eula.txt", StringComparison.OrdinalIgnoreCase) ||
            e.Equals("run.bat", StringComparison.OrdinalIgnoreCase) ||
            e.Equals("run.sh", StringComparison.OrdinalIgnoreCase) ||
            e.EndsWith("user_jvm_args.txt", StringComparison.OrdinalIgnoreCase) ||
            e.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("server", StringComparison.OrdinalIgnoreCase) && e.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

        pack.Kind = looksLikeServer || LooksLikeServerName(fileName) ? ModpackKind.Server : ModpackKind.Client;

        if (installer is not null)
        {
            pack.ServerJar = installer.FullName;
            var (loader, game, loaderVersion) = ParseInstallerName(installer.Name);
            pack.Loader = loader;
            pack.GameVersion = game;
            pack.LoaderVersion = loaderVersion;
            pack.Kind = ModpackKind.Server;
        }

        // The forge installer jar name also carries the versions.
        if (string.IsNullOrEmpty(pack.GameVersion))
        {
            var (loader, game, loaderVersion) = ParseInstallerName(fileName);
            pack.Loader = loader;
            pack.GameVersion = game;
            pack.LoaderVersion = loaderVersion;
        }

        // Archive entry count is a good enough mod count for a plain zip.
        pack.ArchiveModCount = pack.ArchiveEntries.Count(e =>
            e.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && e.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

        return pack;
    }

    private static Modpack ReadModrinthIndex(string archivePath, string fileName, ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var index = JsonSerializer.Deserialize<ModrinthIndex>(stream, JsonOptions) ?? new ModrinthIndex();

        var pack = new Modpack
        {
            Name = string.IsNullOrWhiteSpace(index.Name) ? fileName : index.Name!,
            Version = index.VersionId,
            Summary = index.Summary ?? "",
            Format = ModpackFormat.Modrinth,
            SourceFile = archivePath,
            Loader = LoaderFromDependencies(index.Dependencies),
            LoaderVersion = LoaderVersionFromDependencies(index.Dependencies),
            GameVersion = index.Dependencies.GetValueOrDefault("minecraft", ""),
            OverrideFolder = "overrides"
        };

        foreach (var file in index.Files)
        {
            pack.Files.Add(new ModpackFile
            {
                Path = file.Path.Replace('\\', '/'),
                Url = file.Downloads.FirstOrDefault() ?? "",
                Size = file.FileSize,
                Sha1 = file.Hashes.GetValueOrDefault("sha1", ""),
                Sha512 = file.Hashes.GetValueOrDefault("sha512", ""),
                Side = SideFromEnv(file.Env)
            });
        }

        // A .mrpack is a client pack unless it is explicitly named as a server pack.
        pack.Kind = LooksLikeServerName(pack.Name) || LooksLikeServerName(fileName)
            ? ModpackKind.Server
            : ModpackKind.Client;

        return pack;
    }

    private static Modpack ReadCurseForgeManifest(string archivePath, string fileName, ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<CurseForgeManifest>(stream, JsonOptions) ?? new CurseForgeManifest();

        var loaderId = manifest.Minecraft?.ModLoaders.FirstOrDefault(l => l.Primary)?.Id
                       ?? manifest.Minecraft?.ModLoaders.FirstOrDefault()?.Id
                       ?? "";
        var loader = loaderId.Split('-').FirstOrDefault() ?? "";
        var loaderVersion = loaderId.Contains('-') ? loaderId[(loaderId.IndexOf('-') + 1)..] : "";

        var pack = new Modpack
        {
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? fileName : manifest.Name,
            Version = manifest.Version,
            Summary = $"CurseForge 整合包（作者 {manifest.Author}）",
            Format = ModpackFormat.CurseForge,
            SourceFile = archivePath,
            GameVersion = manifest.Minecraft?.Version ?? "",
            Loader = loader,
            LoaderVersion = loaderVersion,
            OverrideFolder = manifest.Overrides ?? "overrides"
        };

        // CurseForge downloads need an API key, so only the plan is recorded: the
        // pack is imported with its overrides and the missing files are reported.
        foreach (var file in manifest.Files)
        {
            pack.Files.Add(new ModpackFile
            {
                Path = $"mods/curseforge-{file.ProjectId}-{file.FileId}.jar.pending",
                Url = $"https://www.curseforge.com/minecraft/mc-mods/{file.ProjectId}/files/{file.FileId}",
                Side = "both"
            });
        }

        pack.Kind = LooksLikeServerName(pack.Name) || LooksLikeServerName(fileName)
            ? ModpackKind.Server
            : ModpackKind.Client;

        return pack;
    }

    // --------------------------------------------------------- installer jar

    /// <summary>Reads a Forge / NeoForge / Fabric server installer or launcher jar.</summary>
    public static Modpack? ReadInstallerJar(string jarPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(jarPath);
        using var zip = ZipFile.OpenRead(jarPath);

        var versionEntry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("version.json", StringComparison.OrdinalIgnoreCase));
        var installProfile = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("install_profile.json", StringComparison.OrdinalIgnoreCase));

        if (versionEntry is null && installProfile is null &&
            !fileName.Contains("server", StringComparison.OrdinalIgnoreCase))
        {
            return null; // a plain mod, not an installer
        }

        var (loader, game, loaderVersion) = ParseInstallerName(fileName);

        if (versionEntry is not null)
        {
            try
            {
                using var stream = versionEntry.Open();
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } versionId)
                {
                    var parsed = ParseVersionId(versionId);
                    loader = parsed.Loader.Length > 0 ? parsed.Loader : loader;
                    loaderVersion = parsed.LoaderVersion.Length > 0 ? parsed.LoaderVersion : loaderVersion;
                    game = parsed.Game.Length > 0 ? parsed.Game : game;
                }

                if (game.Length == 0 && root.TryGetProperty("inheritsFrom", out var inherits))
                {
                    game = inherits.GetString() ?? "";
                }
            }
            catch (Exception)
            {
                // keep the file name based guess
            }
        }

        return new Modpack
        {
            Name = fileName,
            Format = ModpackFormat.InstallerJar,
            Kind = ModpackKind.Server,
            SourceFile = jarPath,
            Loader = loader,
            LoaderVersion = loaderVersion,
            GameVersion = game,
            ServerJar = Path.GetFileName(jarPath),
            Summary = loader switch
            {
                "neoforge" => "NeoForge 服务端安装器",
                "forge" => "Forge 服务端安装器",
                "fabric" => "Fabric 服务端启动器",
                _ => "服务端安装器 / 启动器"
            },
            ArchiveEntries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList()
        };
    }

    // ---------------------------------------------------------------- folder

    public static Modpack? ReadFolder(string folder)
    {
        var name = new DirectoryInfo(folder).Name;

        var index = Path.Combine(folder, "modrinth.index.json");
        if (File.Exists(index))
        {
            using var stream = File.OpenRead(index);
            var parsed = JsonSerializer.Deserialize<ModrinthIndex>(stream, JsonOptions);
            if (parsed is not null)
            {
                return new Modpack
                {
                    Name = string.IsNullOrWhiteSpace(parsed.Name) ? name : parsed.Name,
                    Version = parsed.VersionId,
                    Summary = parsed.Summary ?? "",
                    Format = ModpackFormat.Modrinth,
                    SourceFile = folder,
                    Loader = LoaderFromDependencies(parsed.Dependencies),
                    LoaderVersion = LoaderVersionFromDependencies(parsed.Dependencies),
                    GameVersion = parsed.Dependencies.GetValueOrDefault("minecraft", ""),
                    Kind = LooksLikeServerName(name) ? ModpackKind.Server : ModpackKind.Client
                };
            }
        }

        var mods = Directory.Exists(Path.Combine(folder, "mods"))
            ? Directory.GetFiles(Path.Combine(folder, "mods"), "*.jar").Length
            : 0;

        var hasServerMarkers = File.Exists(Path.Combine(folder, "server.properties")) ||
                               File.Exists(Path.Combine(folder, "eula.txt")) ||
                               File.Exists(Path.Combine(folder, "run.bat")) ||
                               File.Exists(Path.Combine(folder, "run.sh")) ||
                               Directory.Exists(Path.Combine(folder, "libraries"));

        var serverJar = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.jar").FirstOrDefault(f =>
                Path.GetFileName(f).Contains("forge", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f).Contains("neoforge", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f).Contains("fabric-server", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f).Equals("server.jar", StringComparison.OrdinalIgnoreCase))
            : null;

        if (mods == 0 && !hasServerMarkers && serverJar is null && !Directory.Exists(Path.Combine(folder, "config")))
        {
            return null;
        }

        var (loader, game, loaderVersion) = serverJar is null
            ? ("", "", "")
            : ParseInstallerName(Path.GetFileName(serverJar));

        return new Modpack
        {
            Name = name,
            Format = ModpackFormat.Folder,
            SourceFile = folder,
            Kind = hasServerMarkers || serverJar is not null || LooksLikeServerName(name)
                ? ModpackKind.Server
                : ModpackKind.Client,
            Loader = loader,
            LoaderVersion = loaderVersion,
            GameVersion = game,
            ServerJar = serverJar is null ? "" : Path.GetFileName(serverJar),
            Summary = $"{mods} 个模组"
        };
    }

    // --------------------------------------------------------------- helpers

    /// <summary>Guesses loader, game version and loader version from a file name.</summary>
    public static (string Loader, string Game, string LoaderVersion) ParseInstallerName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        // fabric-server-mc.1.20.1-loader.0.14.21-launcher.0.11.2
        var fabric = Regex.Match(name, @"mc\.(?<mc>[\d.]+)-loader\.(?<loader>[\d.]+)");
        if (fabric.Success)
        {
            return ("fabric", fabric.Groups["mc"].Value, fabric.Groups["loader"].Value);
        }

        // forge-1.20.1-47.2.0-installer  /  neoforge-20.4.237-installer
        var modern = Regex.Match(name, @"(?<loader>neoforge|forge)-(?<mc>\d+\.\d+(?:\.\d+)?)-(?<ver>[\d.]+)");
        if (modern.Success)
        {
            return (modern.Groups["loader"].Value, modern.Groups["mc"].Value, modern.Groups["ver"].Value);
        }

        // forge-1.12.2-14.23.5.2859-installer (same shape) and 1.20.1-forge-47.2.0
        var reversed = Regex.Match(name, @"(?<mc>\d+\.\d+(?:\.\d+)?)-(?<loader>neoforge|forge)-(?<ver>[\d.]+)");
        if (reversed.Success)
        {
            return (reversed.Groups["loader"].Value, reversed.Groups["mc"].Value, reversed.Groups["ver"].Value);
        }

        var neo = Regex.Match(name, @"neoforge-(?<ver>\d+\.\d+\.\d+)");
        if (neo.Success)
        {
            return ("neoforge", "", neo.Groups["ver"].Value);
        }

        var forge = Regex.Match(name, @"forge-(?<ver>\d+\.\d+[\d.]*)");
        if (forge.Success)
        {
            return ("forge", "", forge.Groups["ver"].Value);
        }

        var fabricLoader = Regex.Match(name, @"fabric.*?(?<ver>\d+\.\d+\.\d+)");
        if (fabricLoader.Success)
        {
            return ("fabric", "", fabricLoader.Groups["ver"].Value);
        }

        return ("", "", "");
    }

    /// <summary>Splits a version id like "1.20.1-forge-47.2.0" or "1.20.1-neoforge-20.4.237".</summary>
    public static (string Game, string Loader, string LoaderVersion) ParseVersionId(string versionId)
    {
        var match = Regex.Match(versionId, @"^(?<mc>[\d.]+)-(?<loader>forge|neoforge|fabric|quilt)-(?<ver>.+)$",
            RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return (match.Groups["mc"].Value, match.Groups["loader"].Value.ToLowerInvariant(),
                match.Groups["ver"].Value);
        }

        return (versionId, "", "");
    }

    private static string LoaderFromDependencies(Dictionary<string, string> dependencies)
    {
        foreach (var key in new[] { "neoforge", "forge", "fabric-loader", "quilt-loader" })
        {
            if (dependencies.ContainsKey(key))
            {
                return key switch
                {
                    "fabric-loader" => "fabric",
                    "quilt-loader" => "quilt",
                    _ => key
                };
            }
        }

        return "";
    }

    private static string LoaderVersionFromDependencies(Dictionary<string, string> dependencies)
    {
        foreach (var key in new[] { "neoforge", "forge", "fabric-loader", "quilt-loader" })
        {
            if (dependencies.TryGetValue(key, out var value)) return value;
        }

        return "";
    }

    private static string SideFromEnv(Dictionary<string, string>? env)
    {
        if (env is null) return "both";
        var client = env.GetValueOrDefault("client", "required");
        var server = env.GetValueOrDefault("server", "required");

        if (client == "unsupported") return "server";
        if (server == "unsupported") return "client";
        return "both";
    }

    private static bool LooksLikeServerName(string name) =>
        name.Contains("server", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("服务端", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("服务端整合包", StringComparison.OrdinalIgnoreCase);
}
