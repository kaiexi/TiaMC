using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMc.Core.Modpacks;

/// <summary>
/// Installs and manages modpacks (client packs and server packs).
///
/// Layout under the launcher root:
///   modpacks/&lt;name&gt;/        client packs (mods, config, resourcepacks, ...)
///   serverpacks/&lt;name&gt;/      server packs (mods, config, libraries, run scripts, ...)
///
/// Every installed pack gets a modpack.json so the manager can list it without
/// re-reading the original archive.
/// </summary>
public sealed class ModpackManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ModpackManager(string minecraftRoot)
    {
        MinecraftRoot = minecraftRoot;
        ClientRoot = Path.Combine(minecraftRoot, "modpacks");
        ServerRoot = Path.Combine(minecraftRoot, "serverpacks");
    }

    public string MinecraftRoot { get; }

    /// <summary>Client packs live here.</summary>
    public string ClientRoot { get; }

    /// <summary>Server packs live here.</summary>
    public string ServerRoot { get; }

    public string DirectoryFor(ModpackKind kind) => kind == ModpackKind.Server ? ServerRoot : ClientRoot;

    /// <summary>Lists every installed pack, server packs first, then by name.</summary>
    public List<InstalledModpack> Scan()
    {
        var result = new List<InstalledModpack>();

        foreach (var (root, kind) in new[] { (ServerRoot, ModpackKind.Server), (ClientRoot, ModpackKind.Client) })
        {
            if (!Directory.Exists(root)) continue;

            foreach (var directory in Directory.GetDirectories(root))
            {
                var metadata = Path.Combine(directory, "modpack.json");
                InstalledModpack? pack = null;

                if (File.Exists(metadata))
                {
                    try
                    {
                        pack = JsonSerializer.Deserialize<InstalledModpack>(File.ReadAllText(metadata), JsonOptions);
                    }
                    catch (Exception)
                    {
                        pack = null;
                    }
                }

                pack ??= new InstalledModpack
                {
                    Name = Path.GetFileName(directory),
                    Kind = kind == ModpackKind.Server ? "server" : "client",
                    Format = "Folder",
                    Path = directory
                };

                pack.Path = directory;
                // 文件夹决定类型：整合包放在哪个目录就是哪种。
                pack.Kind = kind == ModpackKind.Server ? "server" : "client";

                // 名字优先取"包内清单里声明的名字"，而不是文件夹名
                // （下载下来的包常是 <slug>-<版本>.mrpack 这种，直接当名字很难看、也对不上）。
                var declared = ReadDeclaredName(directory);
                if (declared.Length > 0) pack.DeclaredName = declared;
                else if (string.IsNullOrWhiteSpace(pack.DeclaredName)) pack.DeclaredName = pack.Name;

                pack.ModCount = CountMods(directory);
                pack.SizeText = FormatSize(DirectorySize(directory));

                if (!File.Exists(metadata))
                {
                    try
                    {
                        File.WriteAllText(metadata, JsonSerializer.Serialize(pack, JsonOptions));
                    }
                    catch (Exception)
                    {
                        // listing still works
                    }
                }

                result.Add(pack);
            }
        }

        return result
            .OrderByDescending(p => p.IsServer)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 读整合包内清单里声明的名字（Modrinth 的 modrinth.index.json、CurseForge 的 manifest.json、
    /// MultiMC 的 mmc-pack.json）。读不到就返回空串，调用方退回文件夹名。
    /// </summary>
    public static string ReadDeclaredName(string directory)
    {
        foreach (var (file, property) in new[]
                 {
                     ("modrinth.index.json", "name"),
                     ("manifest.json", "name"),
                     ("mmc-pack.json", "name")
                 })
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty(property, out var name) &&
                    name.ValueKind == JsonValueKind.String)
                {
                    var text = name.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text!.Trim();
                }
            }
            catch (Exception)
            {
                // 清单坏了就继续找下一个
            }
        }

        return "";
    }

    public static int CountMods(string directory)
    {
        var mods = Path.Combine(directory, "mods");
        if (!Directory.Exists(mods)) return 0;
        return Directory.GetFiles(mods).Count(f =>
            f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase));
    }

    public static long DirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f =>
            {
                try
                {
                    return new FileInfo(f).Length;
                }
                catch (Exception)
                {
                    return 0L;
                }
            });
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };

    // -------------------------------------------------------------- install

    /// <summary>
    /// Installs a pack: creates the target folder, unpacks the archive, downloads
    /// the files listed in the manifest and writes modpack.json.
    /// </summary>
    public async Task<InstalledModpack> InstallAsync(Modpack pack, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var kind = pack.Kind;
        var name = Sanitize(pack.Name);
        var target = UniqueDirectory(DirectoryFor(kind), name);

        progress?.Report($"{(kind == ModpackKind.Server ? "服务端整合包" : "客户端整合包")}: {pack.Name}" +
                         $"（{pack.LoaderText} {pack.GameVersion}）");
        progress?.Report($"目标目录: {target}");

        Directory.CreateDirectory(target);

        // 1. unpack the archive (or copy the folder / installer jar)
        if (pack.Format == ModpackFormat.InstallerJar && File.Exists(pack.SourceFile))
        {
            File.Copy(pack.SourceFile, Path.Combine(target, Path.GetFileName(pack.SourceFile)), overwrite: true);
            progress?.Report("已复制服务端安装器");
        }
        else if (Directory.Exists(pack.SourceFile))
        {
            CopyDirectory(pack.SourceFile, target);
            progress?.Report("已复制整合包目录");
        }
        else if (File.Exists(pack.SourceFile))
        {
            ExtractArchive(pack.SourceFile, target, pack.OverrideFolder, progress);
        }

        // 2. download the files listed in the manifest
        var (downloaded, missing) = await DownloadFilesAsync(pack, target, progress, token).ConfigureAwait(false);

        // 3. remember what this folder is
        var installed = new InstalledModpack
        {
            Name = Path.GetFileName(target),
            Version = pack.Version,
            Kind = kind == ModpackKind.Server ? "server" : "client",
            Format = pack.Format.ToString(),
            GameVersion = pack.GameVersion,
            Loader = pack.Loader,
            LoaderVersion = pack.LoaderVersion,
            Path = target,
            SourceFile = pack.SourceFile,
            ModCount = CountMods(target),
            MissingFiles = missing,
            ServerJar = pack.ServerJar,
            Summary = pack.Summary,
            SizeText = FormatSize(DirectorySize(target))
        };

        WriteMetadata(target, installed);
        progress?.Report($"安装完成: 模组 {installed.ModCount} 个，文件 {downloaded} 个下载，{missing} 个缺失");

        if (kind == ModpackKind.Server)
        {
            WriteServerScripts(target, installed, progress);
        }

        return installed;
    }

    private async Task<(int Downloaded, int Missing)> DownloadFilesAsync(Modpack pack, string target,
        IProgress<string>? progress, CancellationToken token)
    {
        var wanted = pack.Files
            .Where(f => f.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            // Only the side being installed: a client pack skips server-only files and vice versa.
            .Where(f => f.Side == "both" || (pack.Kind == ModpackKind.Server ? f.Side == "server" : f.Side == "client"))
            .Where(f => !f.Path.EndsWith(".pending", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var missing = pack.Files.Count(f => f.Path.EndsWith(".pending", StringComparison.OrdinalIgnoreCase));
        if (wanted.Count == 0) return (0, missing);

        progress?.Report($"需要下载 {wanted.Count} 个文件（{(int)(wanted.Sum(f => f.Size) / 1024 / 1024)} MB）");

        // Shared client: pooled connections, one TLS handshake per host.
        var http = Net.Http.Client;

        var downloaded = 0;
        var failed = 0;
        var done = 0;

        var parallel = Math.Clamp(Environment.ProcessorCount, 2, 6);
        await Parallel.ForEachAsync(wanted, new ParallelOptions
        {
            MaxDegreeOfParallelism = parallel,
            CancellationToken = token
        }, async (file, ct) =>
        {
            var destination = Path.Combine(target, file.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(destination) && new FileInfo(destination).Length > 0 &&
                    (file.Size <= 0 || new FileInfo(destination).Length == file.Size))
                {
                    Interlocked.Increment(ref downloaded);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var bytes = await http.GetByteArrayAsync(file.Url, ct).ConfigureAwait(false);

                if (bytes.Length == 0)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                var temporary = destination + ".tiamc-download";
                await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
                Interlocked.Increment(ref downloaded);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref failed);
                if (failed <= 5) progress?.Report($"下载失败 {file.FileName}: {e.Message}");
            }
            finally
            {
                var current = Interlocked.Increment(ref done);
                if (current % 25 == 0 || current == wanted.Count)
                {
                    progress?.Report($"进度 {current}/{wanted.Count}");
                }
            }
        }).ConfigureAwait(false);

        progress?.Report($"下载结束: 成功 {downloaded} / 失败 {failed}");
        return (downloaded, missing + failed);
    }

    private static int ExtractArchive(string archivePath, string target, string overrideFolder,
        IProgress<string>? progress)
    {
        var count = 0;
        var overridePrefix = overrideFolder.TrimEnd('/') + "/";

        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');

            // Directory entries (they may end with "/" or with "\" on Windows) are skipped.
            if (relative.EndsWith("/", StringComparison.Ordinal) || relative.Length == 0) continue;

            // Modrinth / CurseForge keep the playable files under overrides/
            if (relative.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
            {
                relative = relative["overrides/".Length..];
            }
            else if (overrideFolder.Length > 0 &&
                     relative.StartsWith(overridePrefix, StringComparison.OrdinalIgnoreCase))
            {
                relative = relative[overridePrefix.Length..];
            }
            else if (relative is "modrinth.index.json" or "manifest.json" ||
                     relative.StartsWith("modrinth.index.json", StringComparison.OrdinalIgnoreCase))
            {
                continue; // the manifest itself is not copied
            }

            if (relative.Length == 0) continue;

            var destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!IsInside(target, destination)) continue; // zip-slip guard

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }

        progress?.Report($"已解压 {count} 个文件");
        return count;
    }

    private static bool IsInside(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidate);
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    // -------------------------------------------------------- server scripts

    /// <summary>
    /// Writes start scripts and a jvm args file for a server pack, and installs
    /// Forge / NeoForge by running the shipped installer with --installServer.
    /// </summary>
    public void WriteServerScripts(string directory, InstalledModpack pack, IProgress<string>? progress = null,
        int memoryMb = 4096)
    {
        var jvmArgs = Path.Combine(directory, "user_jvm_args.txt");
        if (!File.Exists(jvmArgs))
        {
            File.WriteAllText(jvmArgs,
                $"# TiaMC 生成的服务端 JVM 参数（不设置 -Xms，堆按需增长）{Environment.NewLine}" +
                $"-Xmx{memoryMb}M{Environment.NewLine}" +
                "-Dfile.encoding=UTF-8" + Environment.NewLine);
        }

        var command = BuildServerCommand(directory);
        var startCmd = Path.Combine(directory, "tiamc-start.cmd");
        File.WriteAllText(startCmd,
            "@echo off" + Environment.NewLine +
            "chcp 65001 >nul" + Environment.NewLine +
            "cd /d \"%~dp0\"" + Environment.NewLine +
            command.Replace("java", "java", StringComparison.Ordinal) + Environment.NewLine +
            "pause" + Environment.NewLine);

        var startSh = Path.Combine(directory, "tiamc-start.sh");
        File.WriteAllText(startSh, "#!/bin/sh" + Environment.NewLine + "cd \"$(dirname \"$0\")\"" +
                                   Environment.NewLine + command + Environment.NewLine);

        progress?.Report("启动命令: " + command);
        progress?.Report($"已生成 tiamc-start.cmd（内存 {memoryMb} MB）");

        pack.ServerJar = string.IsNullOrEmpty(pack.ServerJar) ? DetectServerJar(directory) ?? "" : pack.ServerJar;
        WriteMetadata(directory, pack);
    }

    /// <summary>Builds the java command line for whatever server files are present.</summary>
    public static string BuildServerCommand(string directory, string java = "java", int memoryMb = 4096)
    {
        // Modern Forge / NeoForge: argument files written by the installer.
        foreach (var argsFile in new[]
                 {
                     Path.Combine("libraries", "net", "neoforged", "neoforge"),
                     Path.Combine("libraries", "net", "minecraftforge", "forge")
                 })
        {
            var root = Path.Combine(directory, argsFile);
            if (!Directory.Exists(root)) continue;

            var versionDir = Directory.GetDirectories(root).OrderByDescending(d => d).FirstOrDefault();
            if (versionDir is null) continue;

            var winArgs = Path.Combine(versionDir, "win_args.txt");
            var unixArgs = Path.Combine(versionDir, "unix_args.txt");
            var relative = Path.GetRelativePath(directory, File.Exists(winArgs) ? winArgs : unixArgs);

            if (File.Exists(winArgs) || File.Exists(unixArgs))
            {
                return $"{java} @user_jvm_args.txt @{relative.Replace('\\', '/')} --nogui";
            }
        }

        // Legacy Forge: a forge-<mc>-<ver>.jar next to the pack.
        var legacyForge = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "forge-*.jar")
                .Concat(Directory.GetFiles(directory, "neoforge-*.jar"))
                .FirstOrDefault(f => !Path.GetFileName(f).Contains("installer", StringComparison.OrdinalIgnoreCase))
            : null;
        if (legacyForge is not null)
        {
            return $"{java} -Xmx{memoryMb}M -jar \"{Path.GetFileName(legacyForge)}\" nogui";
        }

        var jar = DetectServerJar(directory);
        return jar is null
            ? $"{java} -Xmx{memoryMb}M -jar server.jar nogui    # 还没有服务端核心，先运行安装器"
            : $"{java} -Xmx{memoryMb}M -jar \"{Path.GetFileName(jar)}\" nogui";
    }

    public static string? DetectServerJar(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        foreach (var candidate in new[] { "server.jar", "fabric-server-launch.jar", "fabric-server-launcher.jar" })
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path)) return path;
        }

        return Directory.GetFiles(directory, "*.jar")
            .FirstOrDefault(f =>
            {
                var name = Path.GetFileName(f);
                return name.Contains("fabric-server", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("forge", StringComparison.OrdinalIgnoreCase) &&
                       !name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                       !name.Contains("installer", StringComparison.OrdinalIgnoreCase);
            });
    }

    /// <summary>True when the pack still needs its Forge/NeoForge installer to be run.</summary>
    public static string? FindInstaller(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        return Directory.GetFiles(directory, "*.jar").FirstOrDefault(f =>
            Path.GetFileName(f).Contains("installer", StringComparison.OrdinalIgnoreCase));
    }

    public static void WriteMetadata(string directory, InstalledModpack pack)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "modpack.json"), JsonSerializer.Serialize(pack, JsonOptions));
        }
        catch (Exception)
        {
            // metadata is best effort
        }
    }

    /// <summary>Writes eula.txt so the server may start (the user asked for it explicitly).</summary>
    public static void AcceptEula(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "eula.txt"),
            $"# 由 TiaMC 写入，表示已同意 Minecraft EULA（https://aka.ms/MinecraftEULA）{Environment.NewLine}" +
            $"eula=true{Environment.NewLine}");
    }

    /// <summary>Creates or patches server.properties.</summary>
    public static void ConfigureServer(string directory, int port, int maxPlayers, string motd, bool onlineMode)
    {
        var path = Path.Combine(directory, "server.properties");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];

        void Set(string key, string value)
        {
            var index = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            if (index >= 0) lines[index] = $"{key}={value}";
            else lines.Add($"{key}={value}");
        }

        Set("server-port", port.ToString());
        Set("max-players", maxPlayers.ToString());
        Set("motd", motd);
        Set("online-mode", onlineMode ? "true" : "false");
        Set("enable-command-block", "true");

        File.WriteAllLines(path, lines);
    }

    /// <summary>Runs the Forge/NeoForge installer that ships inside a server pack.</summary>
    public async Task<bool> RunInstallerAsync(string directory, string javaPath, IProgress<string>? progress,
        CancellationToken token = default)
    {
        var installer = FindInstaller(directory);
        if (installer is null)
        {
            progress?.Report("没有找到安装器，跳过安装步骤");
            return false;
        }

        progress?.Report("运行服务端安装器: " + Path.GetFileName(installer));
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(Path.GetFileName(installer));
        startInfo.ArgumentList.Add("--installServer");

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null) return false;

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(token).ConfigureAwait(false);

        progress?.Report($"安装器退出码 {process.ExitCode}");
        return process.ExitCode == 0;
    }

    // ---------------------------------------------------------- instances

    /// <summary>
    /// Folders that are copied when a pack is deployed into an instance game
    /// directory. Only Minecraft content, never launcher metadata.
    /// </summary>
    private static readonly string[] DeployFolders =
    [
        "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "texturepacks",
        "kubejs", "scripts", "datapacks", "patchouli_books", "openloader", "disabled"
    ];

    private static readonly string[] DeployFiles =
    [
        "options.txt", "servers.dat", "optionsof.txt", "iris.properties", "sodium-options.json"
    ];

    /// <summary>
    /// Copies an installed client pack into a game directory so the pack becomes
    /// playable through that instance (versions/&lt;id&gt; when isolation is on).
    /// Existing files are overwritten, everything else in the instance is kept.
    /// </summary>
    public (int Files, int Mods, string Message) DeployToGameDir(string packDirectory, string gameDirectory,
        IProgress<string>? progress = null)
    {
        if (!Directory.Exists(packDirectory)) return (0, 0, "整合包目录不存在: " + packDirectory);

        try
        {
            Directory.CreateDirectory(gameDirectory);
            var files = 0;

            foreach (var folder in DeployFolders)
            {
                var source = Path.Combine(packDirectory, folder);
                if (!Directory.Exists(source)) continue;

                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(packDirectory, file);
                    var destination = Path.Combine(gameDirectory, relative);
                    if (!IsInside(gameDirectory, destination)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: true);
                    files++;
                }

                progress?.Report($"已加入实例: {folder}/（{Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length} 个文件）");
            }

            foreach (var name in DeployFiles)
            {
                var source = Path.Combine(packDirectory, name);
                if (!File.Exists(source)) continue;

                File.Copy(source, Path.Combine(gameDirectory, name), overwrite: true);
                files++;
            }

            var mods = CountMods(gameDirectory);
            progress?.Report($"实例 {Path.GetFileName(gameDirectory)} 现在有 {mods} 个模组");
            return (files, mods, $"已加入实例: {files} 个文件，mods 目录共 {mods} 个模组");
        }
        catch (Exception e)
        {
            return (0, 0, "加入实例失败: " + e.Message);
        }
    }

    /// <summary>
    /// Picks the instance that best matches a pack: same game version and the same
    /// loader first, then same game version, then any vanilla instance.
    /// </summary>
    public static string? MatchInstance(Modpack pack, IEnumerable<(string Id, string GameVersion, string Loader)> instances)
    {
        var candidates = instances.ToList();
        if (candidates.Count == 0) return null;

        bool LoaderMatches(string loader) =>
            pack.Loader.Length > 0 && loader.Contains(pack.Loader, StringComparison.OrdinalIgnoreCase);

        var exact = candidates.FirstOrDefault(i =>
            i.GameVersion.Equals(pack.GameVersion, StringComparison.OrdinalIgnoreCase) && LoaderMatches(i.Loader));
        if (exact.Id is not null) return exact.Id;

        var game = candidates.FirstOrDefault(i =>
            i.GameVersion.Equals(pack.GameVersion, StringComparison.OrdinalIgnoreCase));
        if (game.Id is not null) return game.Id;

        var anyLoader = candidates.FirstOrDefault(i => LoaderMatches(i.Loader));
        if (anyLoader.Id is not null) return anyLoader.Id;

        return candidates[0].Id;
    }

    /// <summary>Records which instance a pack was deployed into.</summary>
    public void SetInstance(InstalledModpack pack, string? instanceId, string? gameDirectory)
    {
        pack.InstanceId = instanceId ?? "";
        pack.GameDirectory = gameDirectory ?? "";
        WriteMetadata(pack.Path, pack);
    }

    // ------------------------------------------------------------- deletion

    public (bool Ok, string Message) Delete(InstalledModpack pack)
    {
        try
        {
            if (Directory.Exists(pack.Path)) Directory.Delete(pack.Path, recursive: true);
            return (true, $"已删除 {pack.Name}");
        }
        catch (Exception e)
        {
            return (false, "删除失败: " + e.Message);
        }
    }

    // -------------------------------------------------------------- helpers

    /// <summary>Guesses the pack a dropped file or folder belongs to, without installing.</summary>
    public static Modpack? Inspect(string path) => ModpackReader.Read(path);

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Length == 0 ? "modpack" : cleaned;
    }

    private static string UniqueDirectory(string root, string name)
    {
        var candidate = Path.Combine(root, name);
        var index = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(root, $"{name} ({index})");
            index++;
        }

        return candidate;
    }
}
