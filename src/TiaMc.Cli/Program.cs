using TiaMc.Core.Resources;
using System.Text.Json;
using TiaMc.Core.Accounts;
using TiaMc.Core.Integrity;
using TiaMc.Core.Java;
using TiaMc.Core.Launch;
using TiaMc.Core.Minecraft;
using TiaMc.Core.Modpacks;
using TiaMc.Core.Mods;
using TiaMc.Core.Net;
using TiaMc.Core.Yggdrasil;

namespace TiaMc.Cli;

/// <summary>
/// Head-less harness for the launcher core. Used to verify version merging,
/// classpath assembly and command generation without starting the GUI:
///   tiamc-cli list   ["--mc C:\path\.minecraft"]
///   tiamc-cli check  &lt;versionId&gt;
///   tiamc-cli plan   &lt;versionId&gt; [--java path]
///   tiamc-cli launch &lt;versionId&gt; [--user name]
///   tiamc-cli manifest
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) &&
                i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[args[i][2..]] = args[++i];
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                // Value-less flags such as --isolate / --fast.
                options[args[i][2..]] = "true";
            }
            else
            {
                positionals.Add(args[i]);
            }
        }

        var paths = new McPaths(options.TryGetValue("mc", out var mc) ? mc : McPaths.DetectRoot(null));
        Console.WriteLine($"Minecraft: {paths.Root}");
        Console.WriteLine();

        // caches live next to the launcher data, not in %LOCALAPPDATA%
        TiaMc.Core.Utils.AppPaths.CacheRoot = Path.Combine(paths.Root, "cache");
        var repository = new VersionRepository(paths);

        try
        {
            switch (command)
            {
                case "list": return List(paths, repository);
                case "check": return Check(paths, repository, positionals);
                case "plan": return Plan(paths, repository, positionals, options, dryRun: true);
                case "launch": return Plan(paths, repository, positionals, options, dryRun: false);
                case "manifest": return Manifest(options);
                case "java": return JavaCommandDispatch(positionals, options);
                case "login": return Login(options);
                case "install": return Install(paths, options);
                case "mods": return Mods(paths, repository, positionals, options);
                case "detect": return Detect(paths, options);
                case "yggdrasil": return YggdrasilSelfTest(options);
                case "packs": return Modpacks(paths, positionals, options);
                case "trim": return TrimMemory(options);
                case "diagnose": return Diagnose(positionals, options);
                case "bench": return Bench(paths, repository, positionals, options);
                case "search": return SearchContent(positionals, options);
                case "cache": return CacheInfo(positionals);
                case "get": return GetContent(paths, positionals, options);
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"错误: {e}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            TiaMC 命令行内核测试工具
              list                        列出已安装版本
              check <versionId>           校验文件完整性
              plan  <versionId>           打印启动命令（不运行）
              launch <versionId>          真正启动游戏
              manifest                    列出可下载的原版版本
              java                        列出检测到的 Java
              login [--offline <名字>]    账户（Microsoft 正版 / 离线）
              install <版本号>            下载并补全一个原版版本（用于排查下载问题）
              mods list <versionId>       列出 mods 目录并解析元数据
              mods enable|disable <versionId> <文件名>
              mods import <versionId> <文件或目录>...
              detect                      自动检测本机所有 Minecraft 目录
              yggdrasil [--port n]        启动认证服务端并自测全部 Yggdrasil 接口
              packs list                  列出已安装的客户端 / 服务端整合包
              packs info <文件|目录>      识别整合包（.mrpack / CurseForge / 服务端安装器 / zip）
              packs install <文件>        安装整合包（--server 装成服务端整合包）
              packs start <名称> [--java x] 生成服务端启动脚本并打印启动命令
              diagnose [--log <文件>] [--dir <游戏目录>] [--max <MB>]
                                          分析游戏日志 / crash-reports，给出结论与嫌疑模组
              java get --major 17         自动下载并安装缺失的 Java 运行库到 runtime\\java-17
              cache [clean]               查看 / 清理启动器缓存（模组图标、元数据、Java 清单）
              search <模组|整合包|资源包|光影> <关键词> [--game 1.20.1] [--loader fabric] [--limit 10]
                                          在 Modrinth 上搜索资源（支持中文名，见内置中文表）
              get <模组|资源包|光影|整合包> <slug> [--game 1.20.1] [--loader fabric] [--instance 版本ID] [--dest 目录]
                                          下载某个项目到实例目录（mods / resourcepacks / shaderpacks）
              bench [版本] [--mc 目录]     跑一遍启动关键路径的性能基准（缓存冷/热对比）
              trim [--standby] [--modified] [--cache] [--all]
                                          内存回收（工作集 / 待机列表 / 文件缓存），打印前后可用内存
              install <版本号>            下载并补全一个原版版本（用于排查下载问题）

            通用选项: --mc <路径>  --java <java.exe 路径>  --user <离线用户名>
                      --account <正版用户名>  --min <MB>  --max <MB>  --source official|bmclapi
                      --isolate (版本隔离: --gameDir 指向 versions\&lt;版本&gt;)
            """);
    }

    /// <summary>Downloads the JRE a version needs (auto provisioning).</summary>
    /// <summary>java / java get 分发。</summary>
    private static int JavaCommandDispatch(List<string> positionals, Dictionary<string, string> options)
    {
        if (positionals.Count > 0 && positionals[0].Equals("get", StringComparison.OrdinalIgnoreCase))
        {
            return JavaInstall(positionals.Skip(1).ToList(), options);
        }

        return Java();
    }
    private static int JavaInstall(List<string> positionals, Dictionary<string, string> options)
    {
        var major = int.TryParse(options.GetValueOrDefault("major"), out var parsed) ? parsed : 17;
        var root = options.GetValueOrDefault("mc") ?? TiaMc.Core.Minecraft.McPaths.DefaultRoot;
        var runtimeRoot = Path.Combine(root, "runtime");

        Console.WriteLine($"Java {major} → {runtimeRoot}");
        var progress = new Progress<double>(p => Console.Write($"\r  下载中 {p * 100,5:F1}%"));
        var result = TiaMc.Core.Java.JavaRuntimeInstaller
            .InstallAsync(major, runtimeRoot, Console.WriteLine, progress)
            .GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine(result.Ok ? $"成功: {result.Message} → {result.JavaPath}" : $"失败: {result.Message}");
        return result.Ok ? 0 : 1;
    }
    /// <summary>Shows or clears the launcher caches (disk usage control).</summary>
    private static int CacheInfo(List<string> positionals)
    {
        var action = positionals.Count > 0 ? positionals[0].ToLowerInvariant() : "info";
        var paths = TiaMc.Core.Utils.AppPaths.CacheRoot;
        var icons = TiaMc.Core.Utils.AppPaths.IconCacheSizeBytes();
        var metadata = new FileInfo(TiaMc.Core.Utils.AppPaths.ModMetadataFile);

        Console.WriteLine($"缓存目录: {paths}");
        Console.WriteLine($"  模组图标: {TiaMc.Core.Utils.TextUtil.FormatBytes(icons)}" +
                          $"（上限 {TiaMc.Core.Utils.TextUtil.FormatBytes(TiaMc.Core.Utils.AppPaths.IconBudgetBytes)}）");
        Console.WriteLine($"  模组元数据: {(metadata.Exists ? TiaMc.Core.Utils.TextUtil.FormatBytes(metadata.Length) : "无")}");

        if (action is "clean" or "clear")
        {
            var (files, bytes) = TiaMc.Core.Utils.AppPaths.ClearCaches();
            Console.WriteLine();
            Console.WriteLine($"已清理 {files} 个文件，释放 {TiaMc.Core.Utils.TextUtil.FormatBytes(bytes)}");
            return 0;
        }

        var trimmed = TiaMc.Core.Utils.AppPaths.EnforceIconBudget();
        Console.WriteLine();
        Console.WriteLine(trimmed.Deleted > 0
            ? $"已按上限清理 {trimmed.Deleted} 个文件，当前图标占用 {TiaMc.Core.Utils.TextUtil.FormatBytes(trimmed.TotalBytes)}"
            : $"图标占用在预算内（{TiaMc.Core.Utils.TextUtil.FormatBytes(icons)}），无需清理");
        Console.WriteLine("提示: cache clean 可以清空全部缓存");
        return 0;
    }

    /// <summary>Content search + download (mods / modpacks / resource packs / shaders).</summary>
    private static int SearchContent(List<string> positionals, Dictionary<string, string> options)
    {
        if (positionals.Count < 2)
        {
            Console.Error.WriteLine("用法: search <模组|整合包|资源包|光影> <关键词> [--game 1.20.1] [--loader fabric] [--limit 10]");
            return 2;
        }

        var kind = ParseKind(positionals[0]);
        var query = positionals[1];
        var gameVersion = options.GetValueOrDefault("game") ?? "";
        var loader = options.GetValueOrDefault("loader") ?? "";
        var limit = options.TryGetValue("limit", out var rawLimit) && int.TryParse(rawLimit, out var parsed) ? parsed : 10;

        var catalog = new TiaMc.Core.Resources.ResourceCatalog();
        Console.WriteLine($"搜索 {kind.ToChinese()}: \"{query}\"  （中文名表 {catalog.Chinese.Count} 条）" +
                          (catalog.Chinese.ContainsChinese(query) ? "  → 命中中文名，已翻译为英文检索" : ""));

        var hits = catalog.SearchAsync(kind, query, gameVersion, loader, limit).GetAwaiter().GetResult();
        if (hits.Count == 0)
        {
            Console.WriteLine("没有结果（检查网络，或换关键词 / 去掉版本过滤）");
            return 3;
        }

        foreach (var hit in hits)
        {
            Console.WriteLine();
            Console.WriteLine($"  {hit.TitleText}");
            Console.WriteLine($"    slug={hit.Slug}  作者={hit.Author}  下载={hit.DownloadsText}  分类={string.Join('/', hit.Categories.Take(4))}");
            Console.WriteLine($"    支持版本: {string.Join(", ", hit.GameVersions)}");
            Console.WriteLine($"    {TiaMc.Core.Utils.TextUtil.Shorten(hit.Description, 110)}");
        }

        Console.WriteLine();
        Console.WriteLine($"共 {hits.Count} 条。用 get 下载，例如: get {positionals[0]} {hits[0].Slug} --game {gameVersion} --loader {loader}");
        return 0;
    }

    /// <summary>Downloads one project file into an instance folder.</summary>
    private static int GetContent(McPaths paths, List<string> positionals, Dictionary<string, string> options)
    {
        if (positionals.Count < 2)
        {
            Console.Error.WriteLine("用法: get <模组|资源包|光影|整合包> <slug> [--game 1.20.1] [--loader fabric] [--instance 版本ID]");
            return 2;
        }

        var kind = ParseKind(positionals[0]);
        var slug = positionals[1];
        var gameVersion = options.GetValueOrDefault("game") ?? "";
        var loader = options.GetValueOrDefault("loader") ?? "";
        var instanceId = options.GetValueOrDefault("instance") ?? "";

        var catalog = new TiaMc.Core.Resources.ResourceCatalog();
        var files = catalog.GetFilesAsync(slug, gameVersion, loader).GetAwaiter().GetResult();
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"没有匹配的文件（slug={slug}, mc={gameVersion}, loader={loader}）");
            return 3;
        }

        var file = files[0];
        Console.WriteLine($"项目 {slug} 的最新文件: {file.FileName}（{file.SizeText}）版本 {file.VersionNumber}");
        Console.WriteLine($"游戏 {string.Join('/', file.GameVersions.Take(4))}  装载器 {string.Join('/', file.Loaders)}");

        var instanceDir = instanceId.Length > 0
            ? Path.Combine(paths.VersionsDir, instanceId)
            : options.GetValueOrDefault("dest", Path.Combine(paths.Root, kind.ToInstanceFolder()));

        if (kind == TiaMc.Core.Resources.ResourceKind.Modpack)
        {
            var cache = Path.Combine(paths.Root, "modpack-cache");
            var (ok, path, message) = catalog.DownloadToCacheAsync(file, cache).GetAwaiter().GetResult();
            Console.WriteLine(ok ? $"已下载整合包: {path}" : message);
            if (!ok) return 4;

            var pack = TiaMc.Core.Modpacks.ModpackManager.Inspect(path);
            if (pack is null)
            {
                Console.Error.WriteLine("下载的文件不是可识别的整合包（.mrpack 才支持自动安装）");
                return 5;
            }

            var manager = new TiaMc.Core.Modpacks.ModpackManager(paths.Root);
            var installed = manager.InstallAsync(pack, new Progress<string>(l => Console.WriteLine("  " + l)))
                .GetAwaiter().GetResult();
            Console.WriteLine($"整合包已安装: {installed.Path}");
            return 0;
        }

        var progress = new Progress<double>(p =>
        {
            if (p is > 0 and < 1) Console.Write($"\r  下载中 {p * 100:F0}%   ");
        });

        var result = catalog.InstallFileAsync(kind, file, instanceDir, progress).GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine(result.Message);
        Console.WriteLine($"目标目录: {Path.Combine(instanceDir, kind.ToInstanceFolder())}");
        return result.Ok ? 0 : 4;
    }

    private static TiaMc.Core.Resources.ResourceKind ParseKind(string text) => text.ToLowerInvariant() switch
    {
        "mod" or "mods" or "模组" => TiaMc.Core.Resources.ResourceKind.Mod,
        "modpack" or "modpacks" or "整合包" => TiaMc.Core.Resources.ResourceKind.Modpack,
        "resourcepack" or "resourcepacks" or "资源包" => TiaMc.Core.Resources.ResourceKind.ResourcePack,
        "shader" or "shaders" or "光影" => TiaMc.Core.Resources.ResourceKind.Shader,
        "datapack" or "数据包" => TiaMc.Core.Resources.ResourceKind.Datapack,
        _ => TiaMc.Core.Resources.ResourceKind.Mod
    };

    /// <summary>Performance benchmark of the startup critical path.</summary>
    private static int Bench(McPaths paths, VersionRepository repository, List<string> positionals,
        Dictionary<string, string> options)
    {
        Console.WriteLine($"Minecraft 目录: {paths.Root}");
        Console.WriteLine($"物理内存: {TiaMc.Core.Utils.SystemInfo.MemorySummary()}");
        Console.WriteLine();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var versions = repository.LoadAll(out var warnings);
        watch.Stop();
        Console.WriteLine($"{"版本仓库加载",-28} {watch.ElapsedMilliseconds,6} ms   ({versions.Count} 个版本, {warnings} 个警告)");

        // Java detection: cold (no cache) then warm (cache hit).
        TiaMc.Core.Java.JavaDetector.InvalidateCache();
        watch.Restart();
        var java = TiaMc.Core.Java.JavaDetector.Detect(log: null);
        watch.Stop();
        var coldJava = watch.ElapsedMilliseconds;
        Console.WriteLine($"{"Java 检测（冷缓存）",-28} {coldJava,6} ms   ({java.Count} 个运行时)");

        watch.Restart();
        java = TiaMc.Core.Java.JavaDetector.Detect(log: null);
        watch.Stop();
        Console.WriteLine($"{"Java 检测（命中缓存）",-28} {watch.ElapsedMilliseconds,6} ms   ({java.Count} 个运行时)");

        var versionId = positionals.Count > 0 ? positionals[0] : versions.FirstOrDefault()?.Id;
        if (versionId is null)
        {
            Console.WriteLine("没有已安装版本，跳过版本相关基准");
            return 0;
        }

        var version = repository.Load(versionId);
        if (version is null)
        {
            Console.Error.WriteLine($"未找到版本 {versionId}");
            return 2;
        }

        watch.Restart();
        var check = TiaMc.Core.Integrity.IntegrityChecker.Check(paths, version, log: null);
        watch.Stop();
        Console.WriteLine($"{"文件完整性校验",-28} {watch.ElapsedMilliseconds,6} ms   " +
                          $"{check.Summary}");

        watch.Restart();
        var benchAccount = new TiaMc.Core.Accounts.MinecraftAccount { Name = "Bench", Kind = TiaMc.Core.Accounts.AccountKind.Offline };
        var plan = TiaMc.Core.Launch.LaunchPlanner.Build(paths, version, benchAccount,
            java.FirstOrDefault()?.Path ?? "java", java.FirstOrDefault()?.MajorVersion ?? 17,
            new TiaMc.Core.Launch.LaunchOptions(), log: null);
        watch.Stop();
        Console.WriteLine($"{"生成启动方案",-28} {watch.ElapsedMilliseconds,6} ms   ({plan.JvmArguments.Count} 个 JVM 参数)");

        var modsDir = TiaMc.Core.Mods.ModsManager.ResolveDirectory(paths, versionId, isolated: true);
        watch.Restart();
        var mods = TiaMc.Core.Mods.ModsManager.Scan(modsDir, log: null);
        watch.Stop();
        Console.WriteLine($"{"扫描实例模组（命中缓存）",-28} {watch.ElapsedMilliseconds,6} ms   ({mods.Count} 个)");

        watch.Restart();
        var packs = new TiaMc.Core.Modpacks.ModpackManager(paths.Root).Scan();
        watch.Stop();
        Console.WriteLine($"{"扫描整合包",-28} {watch.ElapsedMilliseconds,6} ms   ({packs.Count} 个)");

        watch.Restart();
        var found = TiaMc.Core.Minecraft.MinecraftFinder.FindAll(log: null);
        watch.Stop();
        Console.WriteLine($"{"自动检测 Minecraft 目录",-28} {watch.ElapsedMilliseconds,6} ms   ({found.Count} 个目录)");

        Console.WriteLine();
        Console.WriteLine("提示：Java 检测与模组元数据都会落盘缓存，第二次运行应明显更快。");
        return 0;
    }

    /// <summary>Log / crash report diagnostics (HMCL inspired).</summary>
    private static int Diagnose(List<string> positionals, Dictionary<string, string> options)
    {
        var logPath = options.GetValueOrDefault("log", positionals.Count > 0 ? positionals[0] : "");
        var directory = options.GetValueOrDefault("dir", "");
        var maxMemory = options.TryGetValue("max", out var max) && int.TryParse(max, out var parsed) ? parsed : 0;

        string? logText = null;
        if (logPath.Length > 0)
        {
            if (!File.Exists(logPath))
            {
                Console.Error.WriteLine("日志文件不存在: " + logPath);
                return 2;
            }

            logText = File.ReadAllText(logPath);
            Console.WriteLine($"读取日志: {logPath}（{logText.Length} 字符）");
        }

        if (directory.Length > 0)
        {
            Console.WriteLine($"游戏目录: {directory}");
            var mods = TiaMc.Core.Diagnostics.CrashAnalyzer.ListModFiles(directory);
            Console.WriteLine($"模组数量: {mods.Count}");
        }

        var analysis = TiaMc.Core.Diagnostics.CrashAnalyzer.Analyze(logText, -1,
            directory.Length > 0 ? directory : null, maxMemory,
            directory.Length > 0 ? TiaMc.Core.Diagnostics.CrashAnalyzer.ListModFiles(directory).Count : 0);

        Console.WriteLine();
        Console.Write(analysis.ToText());
        return analysis.HasProblem ? 1 : 0;
    }

    /// <summary>Memory reclaim (Mem Reduct style) with before/after numbers.</summary>
    private static int TrimMemory(Dictionary<string, string> options)
    {
        var trimmer = new TiaMc.Core.Utils.MemoryTrimmer.Options
        {
            EmptyWorkingSets = true,
            PurgeStandbyList = options.ContainsKey("standby") || options.ContainsKey("all"),
            FlushModifiedList = options.ContainsKey("modified") || options.ContainsKey("all"),
            ClearFileCache = options.ContainsKey("cache") || options.ContainsKey("all")
        };

        Console.WriteLine($"物理内存: {TiaMc.Core.Utils.SystemInfo.MemorySummary()}");
        Console.WriteLine($"管理员权限: {(TiaMc.Core.Utils.MemoryTrimmer.IsElevated ? "是" : "否（待机列表/文件缓存需要管理员）")}");
        Console.WriteLine("回收内容: 工作集" +
                          (trimmer.PurgeStandbyList ? " + 待机列表" : "") +
                          (trimmer.FlushModifiedList ? " + 已修改页面" : "") +
                          (trimmer.ClearFileCache ? " + 文件缓存" : ""));

        var result = TiaMc.Core.Utils.MemoryTrimmer.Trim(trimmer, line => Console.WriteLine("  " + line));
        Console.WriteLine();
        Console.WriteLine(result.Summary);
        Console.WriteLine($"工作集: 处理 {result.ProcessesTrimmed} 个进程，跳过 {result.ProcessesSkipped} 个");
        Console.WriteLine($"待机列表: {(result.StandbyPurged ? "已清理" : "未执行")}    " +
                          $"已修改页面: {(result.ModifiedListFlushed ? "已刷新" : "未执行")}    " +
                          $"文件缓存: {(result.FileCacheCleared ? "已清空" : "未执行")}");

        foreach (var note in result.Notes) Console.WriteLine("  注意: " + note);
        return 0;
    }

    /// <summary>Lists, inspects and installs client / server modpacks.</summary>
    private static int Modpacks(McPaths paths, List<string> positionals, Dictionary<string, string> options)
    {
        var manager = new ModpackManager(paths.Root);
        var action = positionals.Count > 0 ? positionals[0].ToLowerInvariant() : "list";

        switch (action)
        {
            case "list":
            {
                var packs = manager.Scan();
                Console.WriteLine($"客户端整合包目录: {manager.ClientRoot}");
                Console.WriteLine($"服务端整合包目录: {manager.ServerRoot}");
                Console.WriteLine();

                if (packs.Count == 0)
                {
                    Console.WriteLine("还没有安装任何整合包。用 packs install <文件> 安装。");
                    return 0;
                }

                Console.WriteLine($"{"类型",-14} {"名称",-28} {"游戏",-10} {"装载器",-16} {"模组",5} {"大小",-10}");
                Console.WriteLine(new string('-', 96));
                foreach (var pack in packs)
                {
                    var kind = pack.IsServer ? "服务端整合包" : "客户端整合包";
                    Console.WriteLine($"{kind,-14} {TiaMc.Core.Utils.TextUtil.Shorten(pack.Name, 28),-28} {pack.GameVersion,-10} " +
                                      $"{pack.Loader,-16} {pack.ModCount,5} {pack.SizeText,-10}");
                }

                Console.WriteLine();
                Console.WriteLine($"共 {packs.Count} 个（其中服务端整合包 {packs.Count(p => p.IsServer)} 个）");
                return 0;
            }

            case "info":
            {
                var source = positionals.Count > 1 ? positionals[1] : "";
                if (source.Length == 0)
                {
                    Console.Error.WriteLine("用法: packs info <文件|目录>");
                    return 2;
                }

                var pack = ModpackManager.Inspect(source);
                if (pack is null)
                {
                    Console.Error.WriteLine("无法识别为整合包: " + source);
                    return 3;
                }

                Console.WriteLine($"名称:       {pack.Name}");
                Console.WriteLine($"类型:       {pack.KindText}{(pack.IsServer ? "  ★ 橙色高亮" : "")}");
                Console.WriteLine($"格式:       {pack.Format}");
                Console.WriteLine($"版本:       {pack.Version}");
                Console.WriteLine($"游戏版本:   {pack.GameVersion}");
                Console.WriteLine($"装载器:     {pack.LoaderText}");
                Console.WriteLine($"模组数:     {pack.ModCount}");
                Console.WriteLine($"清单文件:   {pack.Files.Count}（需下载 {pack.Files.Count(f => f.Url.StartsWith("http"))}）");
                Console.WriteLine($"服务端核心: {(pack.ServerJar.Length == 0 ? "-" : pack.ServerJar)}");
                Console.WriteLine($"来源:       {pack.SourceFile}");
                Console.WriteLine($"说明:       {pack.Summary}");
                return 0;
            }

            case "install":
            case "add":
            {
                var source = positionals.Count > 1 ? positionals[1] : "";
                if (source.Length == 0)
                {
                    Console.Error.WriteLine("用法: packs install <文件> [--server]");
                    return 2;
                }

                var pack = ModpackManager.Inspect(source);
                if (pack is null)
                {
                    Console.Error.WriteLine("无法识别为整合包: " + source);
                    return 3;
                }

                if (options.ContainsKey("server")) pack.Kind = ModpackKind.Server;

                Console.WriteLine($"安装 {pack.KindText}: {pack.Name}（{pack.LoaderText} {pack.GameVersion}）");
                var progress = new Progress<string>(line => Console.WriteLine("  " + line));
                var installed = manager.InstallAsync(pack, progress).GetAwaiter().GetResult();

                Console.WriteLine();
                Console.WriteLine($"已安装到: {installed.Path}");
                Console.WriteLine($"模组 {installed.ModCount} 个 · 缺失 {installed.MissingFiles} 个 · {installed.SizeText}");

                // --instance <版本 ID>: also copy the pack into that instance game dir.
                if (options.TryGetValue("instance", out var instanceId) && instanceId.Length > 0)
                {
                    var gameDir = Path.Combine(paths.VersionsDir, instanceId);
                    var (files, mods, message) = manager.DeployToGameDir(installed.Path, gameDir, progress);
                    manager.SetInstance(installed, instanceId, gameDir);
                    Console.WriteLine($"{message}");
                    Console.WriteLine($"游戏目录: {gameDir}");
                    Console.WriteLine($"该实例 mods 目录: {Path.Combine(gameDir, "mods")}（{mods} 个模组，复制 {files} 个文件）");
                }
                else if (!installed.IsServer)
                {
                    var match = ModpackManager.MatchInstance(pack,
                        Directory.Exists(paths.VersionsDir)
                            ? Directory.GetDirectories(paths.VersionsDir).Select(d => Path.GetFileName(d))
                                .Select(id =>
                                {
                                    var parsed = ModpackReader.ParseVersionId(id);
                                    return (Id: id, GameVersion: parsed.Game.Length > 0 ? parsed.Game : id, Loader: id);
                                })
                            : []
                    );

                    Console.WriteLine(match is null
                        ? "提示: 没有可用实例，加 --instance <版本ID> 可把整合包装入某个实例"
                        : $"提示: 可加 --instance {match} 把整合包加入该实例");
                }

                if (installed.IsServer)
                {
                    Console.WriteLine("启动命令: " + ModpackManager.BuildServerCommand(installed.Path));
                    var installer = ModpackManager.FindInstaller(installed.Path);
                    if (installer is not null)
                    {
                        Console.WriteLine("需要先运行安装器: java -jar " + Path.GetFileName(installer) + " --installServer");
                    }
                }

                return 0;
            }

            case "start":
            {
                var name = positionals.Count > 1 ? positionals[1] : "";
                if (name.Length == 0)
                {
                    Console.Error.WriteLine("用法: packs start <名称> [--java <java.exe>] [--mem 4096]");
                    return 2;
                }

                var pack = manager.Scan().FirstOrDefault(p =>
                    p.IsServer && p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (pack is null)
                {
                    Console.Error.WriteLine($"没有找到服务端整合包: {name}");
                    return 3;
                }

                var java = options.GetValueOrDefault("java", "java");
                var memory = options.TryGetValue("mem", out var mem) && int.TryParse(mem, out var parsed) ? parsed : 4096;

                ModpackManager.AcceptEula(pack.Path);
                ModpackManager.ConfigureServer(pack.Path, 25565, 20, "TiaMC Server", onlineMode: false);
                manager.WriteServerScripts(pack.Path, pack, new Progress<string>(l => Console.WriteLine("  " + l)), memory);

                var installer = ModpackManager.FindInstaller(pack.Path);
                if (installer is not null)
                {
                    Console.WriteLine("检测到服务端安装器，正在安装（java -jar ... --installServer）...");
                    var ok = manager.RunInstallerAsync(pack.Path, java, new Progress<string>(l => Console.WriteLine("  " + l)))
                        .GetAwaiter().GetResult();
                    Console.WriteLine(ok ? "安装器执行完成" : "安装器执行失败");
                }

                var command = ModpackManager.BuildServerCommand(pack.Path, java, memory);
                Console.WriteLine($"目录:   {pack.Path}");
                Console.WriteLine($"命令:   {command}");
                Console.WriteLine($"脚本:   {Path.Combine(pack.Path, "tiamc-start.cmd")}");
                return 0;
            }

            default:
                Console.Error.WriteLine("用法: packs list|info|install|start");
                return 2;
        }
    }

    /// <summary>Starts the Yggdrasil server on a test port and walks the whole documented
    /// API surface, so the implementation can be checked against the spec without
    /// a Minecraft client.
    /// </summary>
    private static int YggdrasilSelfTest(Dictionary<string, string> options)
    {
        var port = options.TryGetValue("port", out var p) && int.TryParse(p, out var parsed) ? parsed : 25567;
        var root = options.TryGetValue("root", out var r)
            ? r
            : Path.Combine(Environment.CurrentDirectory, "yggdrasil-test");

        if (Directory.Exists(root) && !options.ContainsKey("keep")) Directory.Delete(root, recursive: true);

        using var server = new YggdrasilServer(root);
        server.Log += message => Console.WriteLine("  [server] " + message);
        Console.WriteLine($"测试目录: {root}");

        if (!server.Start(port))
        {
            Console.Error.WriteLine("服务端启动失败");
            return 7;
        }

        var fails = 0;
        var api = server.ApiRoot;

        try
        {
            var http = new HttpClient { BaseAddress = new Uri(api), Timeout = TimeSpan.FromSeconds(15) };

            // 1. metadata
            var metadata = http.GetStringAsync("/").GetAwaiter().GetResult();
            Check("GET / 元数据", metadata.Contains("skinDomains") && metadata.Contains("signaturePublickey"),
                TiaMc.Core.Utils.TextUtil.Shorten(metadata, 120));

            // 2. create a user and a placeholder skin
            var name = options.TryGetValue("user", out var u) ? u : "Steve";
            var password = options.TryGetValue("pass", out var pw) ? pw : "secret123";
            Console.WriteLine($"  add user: {server.AddUser(name, password).Message}");
            Console.WriteLine($"  skin:     {server.SetPlaceholderSkin(name).Message}");

            // 3. authenticate (login by profile name, non_email_login)
            var authBody = JsonSerializer.Serialize(new
            {
                username = name,
                password,
                requestUser = true,
                agent = new { name = "Minecraft", version = 1 }
            });
            var auth = Post(http, "/authserver/authenticate", authBody, out var authStatus);
            var accessToken = Json(auth, "accessToken");
            var clientToken = Json(auth, "clientToken");
            var profileId = Json(auth, "selectedProfile.id");
            Check("POST /authserver/authenticate", authStatus == 200 && accessToken.Length > 0,
                $"accessToken={accessToken[..Math.Min(8, accessToken.Length)]}… profile={profileId}");

            // 4. validate
            var validate = Post(http, "/authserver/validate",
                System.Text.Json.JsonSerializer.Serialize(new { accessToken, clientToken }), out var validateStatus);
            Check("POST /authserver/validate", validateStatus == 204, "HTTP " + validateStatus);

            // 5. profile with signature and verify it with the published key
            var profileJson = http.GetStringAsync(
                $"/sessionserver/session/minecraft/profile/{profileId}?unsigned=false").GetAwaiter().GetResult();
            var signed = Json(profileJson, "properties[0].signature");
            var value = Json(profileJson, "properties[0].value");
            var signatureOk = VerifySignature(server.Store.PublicKeyPem!, value, signed);
            Check("GET /sessionserver/session/minecraft/profile (签名校验)", signatureOk,
                $"signature={(signed.Length > 16 ? signed[..16] + "…" : signed)}");

            var texturesJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
            Check("textures 属性内容", texturesJson.Contains("SKIN") && texturesJson.Contains("/textures/"),
                TiaMc.Core.Utils.TextUtil.Shorten(texturesJson, 140));

            // 6. texture bytes are served as image/png
            var hash = Json(texturesJson, "textures.SKIN.url").Split('/').Last();
            var texture = http.GetAsync($"/textures/{hash}").GetAwaiter().GetResult();
            var textureBytes = texture.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Check("GET /textures/{hash}", texture.IsSuccessStatusCode &&
                                          texture.Content.Headers.ContentType?.MediaType == "image/png" &&
                                          textureBytes.Length > 8 && textureBytes[1] == 0x50,
                $"{textureBytes.Length} 字节 {texture.Content.Headers.ContentType}");

            // 7. join + hasJoined (the server side handshake)
            var join = Post(http, "/sessionserver/session/minecraft/join",
                System.Text.Json.JsonSerializer.Serialize(new { accessToken, selectedProfile = profileId, serverId = "tiamc-test" }),
                out var joinStatus);
            Check("POST /sessionserver/session/minecraft/join", joinStatus == 204, "HTTP " + joinStatus);

            var hasJoined = http.GetStringAsync(
                $"/sessionserver/session/minecraft/hasJoined?username={name}&serverId=tiamc-test")
                .GetAwaiter().GetResult();
            Check("GET /sessionserver/session/minecraft/hasJoined",
                hasJoined.Contains(name) && hasJoined.Contains("signature"),
                TiaMc.Core.Utils.TextUtil.Shorten(hasJoined, 100));

            // 8. batch profile lookup
            var lookup = Post(http, "/api/profiles/minecraft",
                JsonSerializer.Serialize(new[] { name, "ExamplePlayer" }), out var lookupStatus);
            Check("POST /api/profiles/minecraft", lookupStatus == 200 && lookup.Contains(profileId),
                TiaMc.Core.Utils.TextUtil.Shorten(lookup, 100));

            // 9. refresh (revokes the old token, issues a new one)
            var refresh = Post(http, "/authserver/refresh",
                System.Text.Json.JsonSerializer.Serialize(new { accessToken, clientToken }), out var refreshStatus);
            var newToken = Json(refresh, "accessToken");
            Check("POST /authserver/refresh", refreshStatus == 200 && newToken.Length > 0 && newToken != accessToken,
                "新令牌 " + newToken[..Math.Min(8, newToken.Length)] + "…");

            var oldTokenRejected = Post(http, "/authserver/validate",
                System.Text.Json.JsonSerializer.Serialize(new { accessToken }),
                out var oldStatus);
            Check("吊销后的旧令牌被拒绝", oldStatus == 403, "HTTP " + oldStatus);

            // 10. invalidate + signout
            Post(http, "/authserver/invalidate", JsonSerializer.Serialize(new { accessToken = newToken }),
                out var invalidateStatus);
            Check("POST /authserver/invalidate", invalidateStatus == 204, "HTTP " + invalidateStatus);

            var signout = Post(http, "/authserver/signout",
                System.Text.Json.JsonSerializer.Serialize(new { username = name, password }), out var signoutStatus);
            Check("POST /authserver/signout", signoutStatus == 204, "HTTP " + signoutStatus);

            Check("错误密码被拒绝", LoginFails(http, name, "wrong-password"), "403 ForbiddenOperationException");
        }
        finally
        {
            server.Stop();
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "全部接口自测通过 ✔" : $"有 {fails} 项失败 ✘");
        Console.WriteLine($"authlib-injector 用法: -javaagent:authlib-injector.jar={api}");
        return fails == 0 ? 0 : 8;

        void Check(string title, bool ok, string detail)
        {
            if (!ok) fails++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {title,-52} {detail}");
        }
    }

    private static bool LoginFails(HttpClient http, string user, string password)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            username = user,
            password,
            agent = new { name = "Minecraft", version = 1 }
        });
        Post(http, "/authserver/authenticate", body, out var status);
        return status == 403;
    }

    private static string Post(HttpClient http, string path, string json, out int status)
    {
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = http.PostAsync(path, content).GetAwaiter().GetResult();
        status = (int)response.StatusCode;
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    /// <summary>Very small JSON path reader: "a.b", "a[0].b" style, for the self test.</summary>
    private static string Json(string json, string path)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var element = doc.RootElement;
            foreach (var rawPart in path.Split('.'))
            {
                var part = rawPart;
                var index = -1;
                var bracket = part.IndexOf('[');
                if (bracket >= 0)
                {
                    index = int.Parse(part[(bracket + 1)..].TrimEnd(']'));
                    part = part[..bracket];
                }

                if (part.Length > 0) element = element.GetProperty(part);
                if (index >= 0) element = element[index];
            }

            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => element.GetString() ?? "",
                System.Text.Json.JsonValueKind.Null => "",
                _ => element.GetRawText()
            };
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Verifies a base64 SHA1withRSA signature with the published public key.</summary>
    private static bool VerifySignature(string publicKeyPem, string value, string signatureBase64)
    {
        if (signatureBase64.Length == 0) return false;
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(System.Text.Encoding.UTF8.GetBytes(value),
                Convert.FromBase64String(signatureBase64),
                System.Security.Cryptography.HashAlgorithmName.SHA1,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Prints every Minecraft installation found on this machine.</summary>
    private static int Detect(McPaths paths, Dictionary<string, string> options)
    {
        var scan = !options.ContainsKey("fast");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = MinecraftFinder.FindAll(paths.Root, Console.WriteLine, scanDrives: scan);
        watch.Stop();

        Console.WriteLine();
        Console.WriteLine($"共检测到 {found.Count} 个 Minecraft 目录（耗时 {watch.ElapsedMilliseconds} ms，" +
                          $"磁盘扫描: {(scan ? "开" : "关")}）:");
        foreach (var candidate in found)
        {
            Console.WriteLine($"  {candidate.Path}");
            Console.WriteLine($"      来源: {candidate.SourceText}    版本: {candidate.Summary}");

            var versions = new VersionRepository(new McPaths(candidate.Path)).LoadAll(out _);
            foreach (var version in versions.Take(6))
            {
                Console.WriteLine($"        - {version.Id}  [{version.Loader}]");
            }

            if (versions.Count > 6) Console.WriteLine($"        ... 其余 {versions.Count - 6} 个版本");
        }

        return 0;
    }

    /// <summary>
    /// Downloads a vanilla version into a (possibly empty) .minecraft folder:
    /// version JSON + client jar, then every library and asset the version needs.
    /// Mirrors exactly what the GUI install button does, so download problems can
    /// be reproduced from a terminal.
    /// </summary>
    private static int Install(McPaths paths, Dictionary<string, string> options)
    {
        var versionId = options.TryGetValue("version", out var v) ? v : "1.21.4";
        var source = options.TryGetValue("source", out var s) && s.Equals("official", StringComparison.OrdinalIgnoreCase)
            ? DownloadSource.Official
            : DownloadSource.BmclApi;

        Console.WriteLine($"目标目录: {paths.Root}");
        Console.WriteLine($"下载源:   {(source == DownloadSource.BmclApi ? "BMCLAPI 镜像" : "Mojang 官方")}");

        var manifestClient = new ManifestClient();
        var manifest = manifestClient.GetManifestAsync(source).GetAwaiter().GetResult();
        if (manifest is null)
        {
            Console.Error.WriteLine("无法获取版本清单");
            return 4;
        }

        var entry = manifest.Versions.FirstOrDefault(x =>
            string.Equals(x.Id, versionId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Console.Error.WriteLine($"清单中没有版本 {versionId}");
            return 4;
        }

        Console.WriteLine($"版本清单: {entry.Id} ({entry.Type}) json={entry.Url}");

        var progress = new Progress<DownloadProgress>(p =>
        {
            Console.Write($"\r  {p.Summary,-100}");
        });

        var installed = manifestClient.InstallVanillaAsync(paths, entry, source, progress, Console.WriteLine)
            .GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine($"版本 JSON + 客户端 Jar: {(installed ? "成功" : "失败")}");
        if (!installed) return 5;

        // caches live next to the launcher data, not in %LOCALAPPDATA%
        TiaMc.Core.Utils.AppPaths.CacheRoot = Path.Combine(paths.Root, "cache");
        var repository = new VersionRepository(paths);
        var version = repository.Load(entry.Id);
        if (version is null)
        {
            Console.Error.WriteLine("版本 JSON 写入后无法解析");
            return 5;
        }

        Console.WriteLine($"已解析: loader={version.Loader} libs={version.Json.Libraries.Count} jar={version.HasJar}");

        // Installer pass: repeat check+download until nothing is missing, because
        // the asset index itself has to be downloaded before its objects can be
        // enumerated.
        var attempt = 0;
        var downloader = new DownloadService();
        while (attempt < 6)
        {
            attempt++;
            var check = IntegrityChecker.Check(paths, version, includeAssets: true, Console.WriteLine);
            Console.WriteLine($"第 {attempt} 轮: {check.Summary}");
            if (check.Missing.Count == 0) break;

            if (attempt == 1)
            {
                Console.WriteLine("缺失明细（前 10 项）:");
                foreach (var file in check.Missing.Take(10))
                {
                    Console.WriteLine($"  [{file.Kind,-12}] {TiaMc.Core.Utils.TextUtil.FormatBytes(file.Size),10}  " +
                                      string.Join(" | ", IntegrityChecker.BuildDownloadUrls(file, source)));
                }
            }

            var outcome = downloader.DownloadMissingAsync(check.Missing, source, progress, Console.WriteLine)
                .GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine($"下载结果: 成功 {outcome.Succeeded} / 跳过 {outcome.Skipped} / 失败 {outcome.Failed}");
            foreach (var error in outcome.Errors.Take(10)) Console.Error.WriteLine("  ! " + error);
            if (outcome.Failed > 0) return 6;
        }

        var final = IntegrityChecker.Check(paths, version, includeAssets: true);
        Console.WriteLine("最终校验: " + final.Summary);
        return final.IsComplete ? 0 : 6;
    }


    /// <summary>
    /// Mods folder harness: list (with metadata parsed out of the jar), toggle a
    /// single mod, or import jars from the file system.
    /// </summary>
    private static int Mods(McPaths paths, VersionRepository repository, List<string> positionals,
        Dictionary<string, string> options)
    {
        if (positionals.Count < 1)
        {
            PrintUsage();
            return 1;
        }

        var action = positionals[0].ToLowerInvariant();
        var versionId = positionals.Count > 1
            ? positionals[1]
            : repository.LoadAll(out _).FirstOrDefault()?.Id;

        var hasExplicitDir = options.TryGetValue("dir", out var explicitDirOption) && explicitDirOption.Length > 0;

        if (!hasExplicitDir && string.IsNullOrWhiteSpace(versionId))
        {
            Console.Error.WriteLine("请指定版本号，例如: mods list 1.20.1-forge");
            return 1;
        }

        if (!hasExplicitDir && repository.Load(versionId!) is null)
        {
            Console.Error.WriteLine($"未找到版本 {versionId}");
            return 1;
        }

        // --dir points the scan at any folder (benchmarks, other launchers).
        var modsDir = hasExplicitDir
            ? explicitDirOption!
            : ModsManager.ResolveDirectory(paths, versionId!);
        Console.WriteLine($"mods 目录: {modsDir}");

        switch (action)
        {
            case "list":
            {
                var mods = ModsManager.Scan(modsDir, Console.WriteLine);
                Console.WriteLine($"共 {mods.Count} 个文件，其中启用 {mods.Count(m => m.Enabled)} 个");
                foreach (var mod in mods)
                {
                    Console.WriteLine($"  [{(mod.Enabled ? "ON " : "OFF")}] {mod.DisplayName,-38} " +
                                      $"{mod.DisplayVersion,-14} {mod.LoaderText,-10} {mod.SizeText,10}");
                    Console.WriteLine($"        {mod.FileName}");
                    if (!string.IsNullOrWhiteSpace(mod.Description))
                    {
                        Console.WriteLine($"        {TiaMc.Core.Utils.TextUtil.Shorten(mod.Description, 100)}");
                    }
                }

                return 0;
            }

            case "enable":
            case "disable":
            {
                if (positionals.Count < 3)
                {
                    Console.Error.WriteLine("用法: mods enable|disable <versionId> <文件名>");
                    return 1;
                }

                var wanted = positionals[2];
                var mod = ModsManager.Scan(modsDir).FirstOrDefault(m =>
                    m.FileName.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                    m.DisplayName.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                if (mod is null)
                {
                    Console.Error.WriteLine($"mods 目录里没有 {wanted}");
                    return 1;
                }

                var updated = ModsManager.SetEnabled(mod, action == "enable", Console.WriteLine);
                Console.WriteLine($"现在文件: {updated.FileName} ({updated.StatusText})");
                return 0;
            }

            case "import":
            {
                if (positionals.Count < 3)
                {
                    Console.Error.WriteLine("用法: mods import <versionId> <文件或目录>...");
                    return 1;
                }

                var copied = ModsManager.Import(positionals.Skip(2), modsDir, Console.WriteLine);
                Console.WriteLine($"已导入 {copied} 个文件");
                return 0;
            }

            default:
                PrintUsage();
                return 1;
        }
    }

    /// <summary>
    /// Exercises the account layer from the console: with --account it runs the
    /// full Microsoft device code login, otherwise it creates/refreshes an
    /// offline account and prints the launch arguments that would be used.
    /// </summary>
    private static int Login(Dictionary<string, string> options)
    {
        var storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TiaMC", "accounts.json");
        var store = AccountStore.Load(storePath);
        Console.WriteLine($"账户文件: {store.FilePath}");

        if (options.TryGetValue("account", out var target))
        {
            var auth = new MicrosoftAuth();
            Console.WriteLine($"OAuth 客户端: {auth.ClientId}");
            Console.WriteLine("正在申请设备代码...");

            var info = auth.RequestDeviceCodeAsync().GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine($"  请在浏览器打开: {info.VerificationUri}");
            Console.WriteLine($"  并输入代码:     {info.UserCode}");
            Console.WriteLine($"  有效期:         {info.ExpiresInSeconds / 60} 分钟");
            Console.WriteLine("==================================================");
            Console.WriteLine();

            try
            {
                var result = auth.LoginAsync(
                    _ => { },
                    seconds => Console.WriteLine($"  等待用户完成登录... 剩余 {seconds}s")).GetAwaiter().GetResult();

                var account = result.Account;
                if (!string.IsNullOrWhiteSpace(target) && !target.StartsWith("--", StringComparison.Ordinal))
                {
                    // The Minecraft profile name wins; the argument is only a hint.
                }

                store.AddOrUpdate(account);
                Console.WriteLine();
                Console.WriteLine($"登录成功: {account.Name}  ({account.Uuid})");
                Console.WriteLine($"  游戏许可: {(account.OwnsGame ? "已拥有" : "未拥有")}");
                Console.WriteLine($"  皮肤: {account.SkinVariant} {account.SkinUrl}");
                Console.WriteLine($"  令牌有效期至: {account.TokenExpiresUtc:u} (UTC)");
                Console.WriteLine($"  已写入: {store.FilePath}");

                // Prove the token also produces launch arguments.
                var accountArg = $"--username {account.Name} --uuid {account.Uuid} --accessToken *** --userType {account.UserType}";
                Console.WriteLine($"  启动参数: {accountArg}");
                return 0;
            }
            catch (MicrosoftAuthException e)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("登录失败: " + e.Message);
                if (!string.IsNullOrWhiteSpace(e.Detail))
                {
                    Console.Error.WriteLine("服务端响应: " + e.Detail);
                }

                return 5;
            }
        }

        var name = options.TryGetValue("offline", out var offlineName) ? offlineName : "Steve";
        var offline = MinecraftAccount.CreateOffline(name);
        store.AddOrUpdate(offline);
        Console.WriteLine($"离线账户: {offline.Name}  uuid={offline.Uuid}");
        Console.WriteLine($"  启动参数: --username {offline.Name} --uuid {offline.Uuid} --accessToken 0 --userType legacy");
        Console.WriteLine($"  已写入: {store.FilePath}");
        Console.WriteLine();
        Console.WriteLine("提示: 使用 --account <任意值> 可以执行 Microsoft 正版登录流程。");
        return 0;
    }

    private static int List(McPaths paths, VersionRepository repository)
    {
        var versions = repository.LoadAll(out var errors);
        Console.WriteLine($"已安装版本 {versions.Count} 个:");
        foreach (var version in versions)
        {
            var memory = version.Json.JavaVersion?.MajorVersion ?? 0;
            Console.WriteLine($"  {version.Id,-34} loader={version.Loader,-9} java={memory,-3} " +
                              $"libs={version.Json.Libraries.Count,-4} jar={(version.HasJar ? "yes" : "NO"),-4} " +
                              $"chain=[{string.Join(" <- ", version.Chain)}]");
        }

        foreach (var error in errors)
        {
            Console.WriteLine($"  ! {error}");
        }

        return 0;
    }

    private static int Check(McPaths paths, VersionRepository repository, List<string> positionals)
    {
        if (positionals.Count == 0) { PrintUsage(); return 1; }
        var version = repository.Load(positionals[0]) ?? throw new Exception($"未找到版本 {positionals[0]}");
        var result = IntegrityChecker.Check(paths, version, includeAssets: true, Console.WriteLine);
        Console.WriteLine(result.Summary);
        foreach (var file in result.Missing.Take(50))
        {
            Console.WriteLine($"  缺失 [{file.Kind}] {file.Path}  ({TiaMc.Core.Utils.TextUtil.FormatBytes(file.Size)})");
        }

        if (result.Missing.Count > 50)
        {
            Console.WriteLine($"  ... 其余 {result.Missing.Count - 50} 项省略");
        }

        return result.IsComplete ? 0 : 0;
    }

    private static int Plan(McPaths paths, VersionRepository repository, List<string> positionals,
        Dictionary<string, string> options, bool dryRun)
    {
        if (positionals.Count == 0) { PrintUsage(); return 1; }

        var version = repository.Load(positionals[0]) ?? throw new Exception($"未找到版本 {positionals[0]}");
        var requiredJava = version.Json.JavaVersion?.MajorVersion ?? 8;

        JavaInfo? java = null;
        if (options.TryGetValue("java", out var javaPath))
        {
            java = JavaDetector.Probe(javaPath) ?? new JavaInfo
            {
                Path = javaPath,
                MajorVersion = 0,
                FullVersion = "unknown"
            };
        }
        else
        {
            var all = JavaDetector.Detect(log: null);
            java = JavaDetector.Filter(all, requiredJava).FirstOrDefault() ?? all.FirstOrDefault();
        }

        if (java is null)
        {
            Console.WriteLine("未检测到任何 Java 运行时。请使用 --java 指定。");
            return 3;
        }

        Console.WriteLine($"Java: {java.Display}");
        var account = MinecraftAccount.CreateOffline(options.TryGetValue("user", out var user) ? user : "Steve");

        var isolated = options.ContainsKey("isolate");
        var launchOptions = new LaunchOptions
        {
            // -Xms is no longer passed; only the maximum heap is set.
            MinMemoryMb = 0,
            MaxMemoryMb = options.TryGetValue("max", out var max) && int.TryParse(max, out var maxValue)
                ? maxValue
                : TiaMc.Core.Utils.SystemInfo.RecommendedMaxMemoryMb,
            // --gc picks the collector (G1GC / ShenandoahGC / ZGC / EpsilonGC / OpenJ9GenCon / ...).
            GcMode = options.GetValueOrDefault("gc", "G1GC"),
            JavaMajor = java?.MajorVersion ?? 17,
            // --jvm "..." 追加自定义 JVM 参数（会覆盖启动器生成的同名参数）
            ExtraJvmArgs = options.GetValueOrDefault("jvm") ?? "",
            // --isolate gives the version its own game directory (版本隔离).
            GameDirectory = isolated ? paths.IsolatedGameDir(version.Id) : null,
            Isolated = isolated
        };

        if (isolated)
        {
            Console.WriteLine($"版本隔离: 游戏目录 = {launchOptions.GameDirectory}");
        }

        var plan = LaunchPlanner.Build(paths, version, account, java.Path, java.MajorVersion,
            launchOptions, Console.WriteLine);

        Console.WriteLine();
        Console.WriteLine($"主类     : {plan.MainClass}");
        Console.WriteLine($"natives  : {plan.NativesDirectory}");
        Console.WriteLine($"游戏目录 : {plan.GameDirectory}");
        Console.WriteLine($"classpath: {plan.Classpath.Split(Path.PathSeparator).Length} 项");
        Console.WriteLine($"JVM 参数 : {plan.JvmArguments.Count} 项");
        Console.WriteLine($"游戏参数 : {plan.GameArguments.Count} 项");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine(GameProcess.ToCommandLine(plan));

        if (dryRun) return 0;

        using var process = new GameProcess();
        var finished = new ManualResetEventSlim(false);
        process.OutputLine += line => Console.WriteLine(line);
        process.Exited += (_, e) =>
        {
            Console.WriteLine($"[exit] code={e.ExitCode} reason={e.Reason} {e.Error}");
            finished.Set();
        };

        process.Start(plan, Console.WriteLine);
        // Give the game two minutes to boot before the harness detaches.
        finished.Wait(TimeSpan.FromMinutes(2));
        if (process.IsRunning)
        {
            Console.WriteLine("[harness] 游戏仍在运行，测试工具退出（不会关闭游戏进程）");
        }

        return 0;
    }

    private static int Manifest(Dictionary<string, string> options)
    {
        var source = options.TryGetValue("source", out var s) && s.Equals("bmclapi", StringComparison.OrdinalIgnoreCase)
            ? DownloadSource.BmclApi
            : DownloadSource.Official;

        var client = new ManifestClient();
        var manifest = client.GetManifestAsync(source).GetAwaiter().GetResult();
        if (manifest is null)
        {
            Console.WriteLine("无法获取版本清单（网络不可用）");
            return 4;
        }

        Console.WriteLine($"latest release={manifest.Latest?.Release} snapshot={manifest.Latest?.Snapshot}");
        Console.WriteLine($"共 {manifest.Versions.Count} 个版本，前 20 个:");
        foreach (var version in manifest.Versions.Take(20))
        {
            Console.WriteLine($"  {version.Id,-20} {version.Type,-12} {version.ReleaseTime}");
        }

        return 0;
    }

    private static int Java()
    {
        var list = JavaDetector.Detect(log: Console.WriteLine);
        Console.WriteLine($"检测到 {list.Count} 个 Java:");
        foreach (var java in list)
        {
            Console.WriteLine($"  {java.Display}");
        }

        return 0;
    }
}
