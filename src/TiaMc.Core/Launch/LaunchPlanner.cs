using System.IO.Compression;
using TiaMc.Core.Accounts;
using TiaMc.Core.Minecraft;
using TiaMc.Core.Rules;
using TiaMc.Core.Utils;

namespace TiaMc.Core.Launch;

/// <summary>
/// Converts an <see cref="InstalledVersion"/> plus a game configuration into a
/// concrete java process command line. This is the Tia-MC equivalent of
/// ColorMC's GameLaunch.MakeArg / MakeClassPath / ReplaceAll combination.
/// </summary>
public static class LaunchPlanner
{
    /// <summary>Extracts the native libraries a version needs into its natives folder.</summary>
    public static int ExtractNatives(McPaths paths, InstalledVersion version,
        IEnumerable<LibraryResolver.ResolvedLibrary> resolved, Action<string>? log = null)
    {
        var nativesDir = paths.NativesDir(version.Id);
        Directory.CreateDirectory(nativesDir);

        var count = 0;
        foreach (var lib in resolved)
        {
            foreach (var (relative, _, extract) in lib.Natives)
            {
                var file = Path.Combine(paths.LibrariesDir, relative);
                if (!File.Exists(file))
                {
                    log?.Invoke($"[natives] 缺失 {lib.MavenName}");
                    continue;
                }

                try
                {
                    using var archive = ZipFile.OpenRead(file);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        var name = entry.Name;
                        if (extract?.Exclude is { Count: > 0 } &&
                            extract.Exclude.Any(ex => name.StartsWith(ex, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var target = Path.Combine(nativesDir, name);
                        // Never let a hostile archive escape the natives folder.
                        if (!Path.GetFullPath(target).StartsWith(
                                Path.GetFullPath(nativesDir) + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (File.Exists(target)) continue;
                        entry.ExtractToFile(target, overwrite: true);
                        count++;
                    }
                }
                catch (Exception e)
                {
                    log?.Invoke($"[natives] {lib.MavenName}: {e.Message}");
                }
            }
        }

        return count;
    }

    public static LaunchPlan Build(
        McPaths paths,
        InstalledVersion version,
        MinecraftAccount account,
        string javaPath,
        int javaMajorVersion,
        LaunchOptions options,
        Action<string>? log = null)
    {
        var json = version.Json;
        var featureSet = RuleEvaluator.FeatureSet.Default();
        featureSet["is_demo_user"] = options.DemoMode;
        featureSet["has_custom_resolution"] = options.WindowWidth > 0 && options.WindowHeight > 0;

        // The working directory doubles as --gameDir: with isolation enabled each
        // instance gets its own config/saves/mods, exactly like PCL2's 版本隔离.
        var gameDirectory = string.IsNullOrWhiteSpace(options.GameDirectory)
            ? paths.SharedGameDir
            : options.GameDirectory!;
        if (options.Isolated)
        {
            Directory.CreateDirectory(gameDirectory);
        }

        var libraries = VersionRepository.DeduplicateLibraries(json.Libraries);
        var resolved = LibraryResolver.Resolve(libraries, featureSet);

        // 1. native libraries -> natives folder
        ExtractNatives(paths, version, resolved, log);

        // 2. classpath: every artifact, then the game jar itself
        var classpathEntries = new List<string>();
        foreach (var lib in resolved)
        {
            if (lib.ArtifactPath is null) continue;
            classpathEntries.Add(Path.Combine(paths.LibrariesDir, lib.ArtifactPath));
        }

        if (version.HasJar) classpathEntries.Add(version.JarPath);

        if (!string.IsNullOrWhiteSpace(options.ExtraClasspath))
        {
            foreach (var raw in options.ExtraClasspath!.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = raw.Trim()
                    .Replace("%GAME_DIR%", paths.Root, StringComparison.OrdinalIgnoreCase)
                    .Replace("%LAUNCH_DIR%", paths.Root, StringComparison.OrdinalIgnoreCase);
                if (entry.Length > 0) classpathEntries.Add(Path.GetFullPath(entry));
            }
        }

        var classpath = string.Join(Path.PathSeparator, classpathEntries.Distinct(StringComparer.OrdinalIgnoreCase));

        // 3. argument templates
        var jvmTemplates = new List<string>();
        var gameTemplates = new List<string>();

        if (json.Arguments is { } arguments && (arguments.Jvm.Count > 0 || arguments.Game.Count > 0))
        {
            Collect(arguments.Jvm, featureSet, jvmTemplates);
            Collect(arguments.Game, featureSet, gameTemplates);
        }
        else if (!string.IsNullOrWhiteSpace(json.MinecraftArguments))
        {
            foreach (var part in json.MinecraftArguments!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                gameTemplates.Add(part);
            }
        }

        // 4. base jvm arguments (memory, gc, user args)
        var jvm = new List<string>();

        // 内存：以前只传 -Xmx，界面上却能设置"最小内存"（设了没用，日志还写着传了 -Xms）。
        // 现在两件都做对：-Xms 真的传（预分配，减少运行中扩堆造成的卡顿），并且保证 Xms ≤ Xmx
        // —— Xms 大于 Xmx 会让 JVM 直接启动失败（"Initial heap size set to a larger value than the maximum heap size"）。
        var maxMemory = options.MaxMemoryMb;
        var minMemory = options.MinMemoryMb;

        if (maxMemory > 0 && minMemory > maxMemory) minMemory = maxMemory;
        if (minMemory > 0) jvm.Add($"-Xms{minMemory}m");
        if (maxMemory > 0) jvm.Add($"-Xmx{maxMemory}m");

        switch (options.GcMode)
        {
            case "G1GC": jvm.Add("-XX:+UseG1GC"); break;
            case "SerialGC": jvm.Add("-XX:+UseSerialGC"); break;
            case "ParallelGC": jvm.Add("-XX:+UseParallelGC"); break;

            // Open source collectors shipped with OpenJDK builds.
            case "ZGC":
                // Generational ZGC (JDK 21+) is the default mode of -XX:+UseZGC there.
                jvm.Add("-XX:+UseZGC");
                if (options.JavaMajor >= 21) jvm.Add("-XX:+ZGenerational");
                break;

            case "ShenandoahGC":
                // Red Hat's open source low pause collector (JDK 12+).
                jvm.Add("-XX:+UseShenandoahGC");
                jvm.Add("-XX:ShenandoahGCMode=iu");
                break;

            case "EpsilonGC":
                // OpenJDK no-op collector: allocates but never reclaims (benchmarks only).
                jvm.Add("-XX:+UnlockExperimentalVMOptions");
                jvm.Add("-XX:+UseEpsilonGC");
                break;

            case "OpenJ9GenCon":
                // Eclipse OpenJ9 (open source JVM) generational collector.
                jvm.Add("-Xgcpolicy:gencon");
                jvm.Add("-Xshareclasses");
                break;

            case "None": break;
        }

        if (!string.IsNullOrWhiteSpace(options.JavaAgentPath))
        {
            jvm.Add($"-javaagent:{options.JavaAgentPath!.Trim()}");
        }

        if (json.UsesBootstrapLauncher)
        {
            log?.Invoke("[launch] 检测到 BootstrapLauncher (Forge/NeoForge 1.17+)");
        }

        if (json.LoaderKind is "Forge" or "NeoForge" && !json.UsesBootstrapLauncher)
        {
            // Legacy (1.16 and older) Forge reads the library folder from a property.
            jvm.Add($"-DlibraryDirectory={paths.LibrariesDir}");
            log?.Invoke("[launch] 检测到旧版 Forge");
        }

        // 4c. 外置登录：把 authlib-injector 挂上，服务器才会接受这个会话。
        if (account.NeedsAuthlibInjector && !string.IsNullOrWhiteSpace(options.AuthlibInjectorPath))
        {
            jvm.Add($"-javaagent:{options.AuthlibInjectorPath}={account.AuthServer}");
            log?.Invoke($"[launch] 外置登录：已挂载 authlib-injector → {account.AuthServerName ?? account.AuthServer}");
        }

        // 4b. user supplied JVM arguments win over the launcher's own ones: a custom
        // "-Xmx8G" must not be followed by the managed "-Xmx4096m".
        if (JvmArguments.Parse(options.ExtraJvmArgs) is { Count: > 0 } userArgs)
        {
            var (merged, replaced) = JvmArguments.Merge(jvm, userArgs);
            jvm.Clear();
            jvm.AddRange(merged);

            if (replaced.Count > 0)
            {
                log?.Invoke($"[launch] 自定义 JVM 参数覆盖了启动器生成的: {string.Join(" ", replaced)}");
            }

            foreach (var warning in JvmArguments.Validate(options.ExtraJvmArgs))
            {
                log?.Invoke("[launch] JVM 参数提示: " + warning);
            }
        }

        jvm.AddRange(jvmTemplates);

        // 5. game arguments. Resolution arguments may already be present because
        // the "has_custom_resolution" feature rule matched; never add them twice.
        var game = new List<string>(gameTemplates);
        var hasResolutionArg = game.Contains("--width") || game.Contains("--height");

        if (options.Fullscreen && !game.Contains("--fullscreen"))
        {
            game.Add("--fullscreen");
        }
        else if (!options.Fullscreen && !hasResolutionArg)
        {
            if (options.WindowWidth > 0) { game.Add("--width"); game.Add(options.WindowWidth.ToString()); }
            if (options.WindowHeight > 0) { game.Add("--height"); game.Add(options.WindowHeight.ToString()); }
        }

        if (!string.IsNullOrWhiteSpace(options.QuickPlayServer))
        {
            game.Add("--server");
            game.Add(options.QuickPlayServer!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraGameArgs))
        {
            foreach (var line in options.ExtraGameArgs!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var arg = line.Trim();
                if (arg.Length > 0) game.Add(arg);
            }
        }

        // 6. placeholder replacement
        var assetsIndexName = json.AssetIndex?.Id ?? json.Assets ?? "legacy";

        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = account.Name,
            ["${auth_session}"] = account.AccessToken,
            ["${auth_access_token}"] = account.AccessToken,
            // The game expects the uuid without dashes; Microsoft accounts come
            // back that way from the profile endpoint and offline accounts are
            // hashed into that form as well.
            ["${auth_uuid}"] = account.Uuid,
            ["${auth_xuid}"] = string.IsNullOrWhiteSpace(account.XboxUserHash) ? "0" : account.XboxUserHash,
            ["${clientid}"] = "0",
            ["${user_type}"] = account.UserType,
            ["${version_name}"] = version.Id,
            ["${version_type}"] = string.IsNullOrWhiteSpace(json.Type) ? "release" : json.Type,
            ["${game_directory}"] = gameDirectory,
            ["${assets_root}"] = paths.AssetsDir,
            ["${assets_index_name}"] = assetsIndexName,
            ["${game_assets}"] = paths.AssetsDir,
            ["${natives_directory}"] = paths.NativesDir(version.Id),
            ["${library_directory}"] = paths.LibrariesDir,
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
            ["${classpath}"] = classpath,
            ["${launcher_name}"] = AppInfo.LauncherName,
            ["${launcher_version}"] = AppInfo.Version,
            ["${user_properties}"] = "{}",
            ["${resolution_width}"] = options.WindowWidth.ToString(),
            ["${resolution_height}"] = options.WindowHeight.ToString(),
            ["${quickPlayPath}"] = "",
            ["${primary_jar}"] = version.JarPath,
            ["${primary_jar_name}"] = Path.GetFileName(version.JarPath),
            ["${game_jar}"] = version.JarPath,
            ["${minecraft_version}"] = version.Id
        };

        jvm = jvm.Select(a => TextUtil.ReplaceMap(a, placeholders)).ToList();
        game = game.Select(a => TextUtil.ReplaceMap(a, placeholders)).ToList();

        // 7. log4j2 security configuration (1.12 - 1.17)
        if (json.Logging?.Client is { Argument: { Length: > 0 } logArg } loggingClient)
        {
            var logFile = Path.Combine(paths.AssetsDir, "log_configs",
                loggingClient.File?.Sha1 ?? "client-1.12.xml");
            if (File.Exists(logFile))
            {
                jvm.Add(TextUtil.ReplaceMap(logArg, placeholders));
            }
        }

        // 8. environment variables
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(options.EnvironmentVariables))
        {
            foreach (var line in options.EnvironmentVariables!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var index = trimmed.IndexOf('=');
                if (index <= 0) continue;
                environment[trimmed[..index].Trim()] = trimmed[(index + 1)..].Trim();
            }
        }

        return new LaunchPlan
        {
            VersionId = version.Id,
            JavaPath = javaPath,
            MainClass = string.IsNullOrWhiteSpace(options.MainClassOverride)
                ? json.MainClass ?? "net.minecraft.client.main.Main"
                : options.MainClassOverride!.Trim(),
            JvmArguments = jvm,
            GameArguments = game,
            Classpath = classpath,
            NativesDirectory = paths.NativesDir(version.Id),
            GameDirectory = gameDirectory,
            Environment = environment,
            RequiredJavaMajor = json.JavaVersion?.MajorVersion ?? (javaMajorVersion > 0 ? javaMajorVersion : 8)
        };
    }

    private static void Collect(List<ArgumentJson> source, RuleEvaluator.FeatureSet features, List<string> target)
    {
        foreach (var argument in source)
        {
            if (!RuleEvaluator.IsAllowed(argument.Rules, features)) continue;
            target.AddRange(argument.Value);
        }
    }
}
