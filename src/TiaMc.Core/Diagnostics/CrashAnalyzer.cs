using System.Text;
using System.Text.RegularExpressions;

namespace TiaMc.Core.Diagnostics;

public enum CrashSeverity
{
    Info,
    Warning,
    Error,
    Fatal
}

/// <summary>One conclusion of the log analysis.</summary>
public sealed class CrashHint
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Advice { get; init; } = "";
    public CrashSeverity Severity { get; init; } = CrashSeverity.Warning;
    /// <summary>The log line that triggered this hint.</summary>
    public string Evidence { get; init; } = "";

    public string SeverityText => Severity switch
    {
        CrashSeverity.Fatal => "致命",
        CrashSeverity.Error => "错误",
        CrashSeverity.Warning => "警告",
        _ => "提示"
    };

    public override string ToString() =>
        $"[{SeverityText}] {Title}" + (Advice.Length > 0 ? $" → {Advice}" : "");
}

/// <summary>Result of analysing a game log, a crash report or both.</summary>
public sealed class CrashAnalysis
{
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public int ExitCode { get; set; }
    public List<CrashHint> Hints { get; set; } = [];
    /// <summary>Package keywords taken from the stack trace (HMCL style).</summary>
    public List<string> Keywords { get; set; } = [];
    /// <summary>Installed mod files whose names contain one of the keywords.</summary>
    public List<string> SuspectMods { get; set; } = [];
    public string? CrashReportPath { get; set; }
    public string AnalysedAt { get; set; } = "";

    public bool HasProblem => Hints.Any(h => h.Severity >= CrashSeverity.Error);
    public CrashSeverity Worst => Hints.Count == 0 ? CrashSeverity.Info : Hints.Max(h => h.Severity);

    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("===== 诊断结论 =====");
        builder.AppendLine(Summary);
        if (CrashReportPath is not null) builder.AppendLine($"崩溃报告: {CrashReportPath}");

        foreach (var hint in Hints.OrderByDescending(x => x.Severity))
        {
            builder.AppendLine();
            builder.AppendLine($"● [{hint.SeverityText}] {hint.Title}");
            if (hint.Detail.Length > 0) builder.AppendLine("  " + hint.Detail);
            if (hint.Advice.Length > 0) builder.AppendLine("  建议: " + hint.Advice);
            if (hint.Evidence.Length > 0) builder.AppendLine("  证据: " + Ellipsis(hint.Evidence));
        }

        if (SuspectMods.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("疑似相关的模组（按堆栈关键词匹配）:");
            foreach (var mod in SuspectMods) builder.AppendLine("  · " + mod);
        }

        if (Keywords.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("堆栈关键词: " + string.Join(", ", Keywords.Take(20)));
        }

        return builder.ToString();

        static string Ellipsis(string text) => text.Length <= 220 ? text : text[..220] + "…";
    }
}

/// <summary>
/// Log / crash report analysis, following the approach of HMCL's
/// CrashReportAnalyzer: rule matching over the log plus stack trace keywords that
/// are matched against the installed mod files to name a likely culprit.
/// </summary>
public static class CrashAnalyzer
{
    /// <summary>Number of log lines kept for the analysis.</summary>
    public const int MaxLogLines = 4000;

    private sealed record Rule(
        string Id,
        string Title,
        string Advice,
        CrashSeverity Severity,
        string Pattern,
        string Detail = "");

    private static readonly Rule[] Rules =
    [
        new("oom", "内存不足（OutOfMemoryError）",
            "把「最大内存」调大（本机物理内存充足时可用 6–8 GB），或减少同时安装的模组；32 位 Java 最多只能用到约 1.5 GB。",
            CrashSeverity.Fatal,
            @"java\.lang\.OutOfMemoryError|OutOfMemoryError|Java heap space|GC overhead limit exceeded|Could not reserve enough space"),

        new("java-too-old", "Java 版本过低",
            "该版本/模组需要更高的 Java，请在「Java 运行时」里换用对应版本（启动器可自动下载）。",
            CrashSeverity.Fatal,
            @"UnsupportedClassVersionError|class file version (?<major>\d+)|has been compiled by a more recent version"),

        new("mod-missing-dep", "模组依赖缺失",
            "缺少前置模组：按提示里的名称补装对应前置（Forge 需要同版本；Fabric 需要同 MC 版本）。",
            CrashSeverity.Fatal,
            @"ModResolutionException|Missing or unsupported mandatory dependencies|requires (?<dep>[\w\-.]+) which is missing|Could not find required mod|Missing dependency"),

        new("mod-duplicate", "模组重复安装",
            "同一个模组有多个版本：删掉 mods 目录里重复/旧版本的文件，只保留一个。",
            CrashSeverity.Error,
            @"DuplicateModsFoundException|Duplicate mods|Found duplicate mod"),

        new("mixin", "Mixin 冲突（模组之间冲突）",
            "多为两个模组改同一处代码：逐个禁用最近新增的模组定位，或升级其中一方。",
            CrashSeverity.Fatal,
            @"MixinApplyError|InvalidMixinException|Mixin apply failed|MixinTransformerError"),

        new("mod-load-failed", "模组加载失败",
            "看下方关键词对应的模组，先把它禁用再启动，确认是该模组后去更新或换版本。",
            CrashSeverity.Fatal,
            @"LoaderExceptionModCrash|Failed to load mod|Exception loading mods|FabricLoader.*Exception|mod loading error|Incompatible mod set"),

        new("graphics", "显卡 / OpenGL 驱动问题",
            "更新显卡驱动；笔记本切换到独显；或加 JVM 参数 -Dfml.earlyprogresswindow=false；必要时改用软件渲染 (Mesa)。",
            CrashSeverity.Fatal,
            @"Pixel format not accelerated|Failed to create display|GLFW error|WGL|OpenGL.*(unsupported|failed)|Could not create GL context|libGL error"),

        new("version-broken", "版本文件缺失或损坏",
            "在「版本」页选中该实例，点「补全缺失文件」重新校验下载；玩家的 mods/config 不会被删除。",
            CrashSeverity.Fatal,
            @"Could not find or load main class|ClassNotFoundException: net\.minecraft|NoClassDefFoundError.*net/minecraft|zip file is empty|invalid LOC header"),

        new("mod-incompatible", "模组与当前游戏/装载器版本不匹配",
            "检查模组支持的 MC 版本与装载器版本（Forge/Fabric/NeoForge），换成对应版本。",
            CrashSeverity.Error,
            @"NoSuchMethodError|NoSuchFieldError|AbstractMethodError|IncompatibleClassChangeError|UnsupportedClassVersionError.*mod"),

        new("network", "网络连接失败",
            "换下载源（BMCLAPI 镜像）或重试；离线模式下也可以直接启动。",
            CrashSeverity.Warning,
            @"UnknownHostException|ConnectException|SocketTimeoutException|Connection reset|Failed to download|503 Service Unavailable"),

        new("auth", "账户认证失败",
            "在「账户」页刷新登录状态或重新登录（正版账户令牌过期会自动刷新）。",
            CrashSeverity.Error,
            @"Invalid credentials|ForbiddenOperationException|401 Unauthorized|403 Forbidden|authentication failed|Failed to verify username"),

        new("world-lock", "存档被占用 / 无法写入",
            "关掉另一个正在运行的游戏实例；确认存档目录未被网盘同步或杀毒软件锁定。",
            CrashSeverity.Error,
            @"session\.lock|The directory is not empty|Access is denied.*saves|already running"),

        new("port", "端口被占用",
            "换一个端口，或结束占用该端口的进程（服务端默认 25565）。",
            CrashSeverity.Warning,
            @"Address already in use|BindException|failed to bind"),

        new("native-crash", "JVM 原生崩溃（驱动或原生库）",
            "查看游戏目录下的 hs_err_pid*.log；更新显卡驱动、去掉有问题的 JVM 参数或换个 Java 运行大版本。",
            CrashSeverity.Fatal,
            @"EXCEPTION_ACCESS_VIOLATION|A fatal error has been detected by the Java Runtime|SIGSEGV|hs_err_pid"),

        new("sound", "音频设备初始化失败",
            "检查默认音频设备；必要时加 -Dorg.lwjgl.openal.libname=soft_oal 或换一个声卡。",
            CrashSeverity.Warning,
            @"OpenAL|Failed to initialize audio|No audio device|AL lib"),

        new("fabric-java", "Fabric 需要 Java 17 或更高",
            "Fabric 1.18+ 需要 Java 17，1.20.5+ 需要 Java 21；在 Java 运行时里切换。",
            CrashSeverity.Fatal,
            @"fabric.*Unsupported class file major version|net\.fabricmc.*UnsupportedClassVersionError"),

        new("forge-install", "Forge/NeoForge 安装不完整",
            "重新运行安装器（本启动器「版本」页可重新安装该版本），或删除该实例后重装。",
            CrashSeverity.Fatal,
            @"Failed to find (main class|Minecraft)|Unable to launch|forge.*installer|BootstrapLauncher.*not found")
    ];

    private static readonly Regex[] CompiledRules = Rules
        .Select(r => new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    /// <summary>HMCL's package keyword blacklist, plus a few launcher specific ones.</summary>
    private static readonly HashSet<string> PackageKeywordBlackList = new(StringComparer.OrdinalIgnoreCase)
    {
        "net", "minecraft", "item", "setup", "block", "assist", "optifine", "player", "unimi", "fastutil",
        "tileentity", "events", "common", "blockentity", "client", "entity", "mojang", "main", "gui", "world",
        "server", "dedicated", "map", "dsi",
        "renderer", "chunk", "model", "loading", "color", "pipeline", "inventory", "launcher", "physics",
        "particle", "gen", "registry", "worldgen", "texture", "biomes", "biome",
        "monster", "passive", "ai", "integrated", "tile", "state", "play", "override", "transformers",
        "structure", "nbt", "pathfinding", "audio", "entities", "items", "renderers",
        "storage", "universal", "oshi", "platform", "base", "native", "method", "array", "arrays",
        "java", "lang", "util", "nio", "io", "sun", "reflect", "zip", "jar", "jdk", "nashorn", "scripts",
        "runtime", "internal",
        "mods", "mod", "impl", "org", "com", "cn", "cc", "jp",
        "core", "config", "registries", "lib", "ruby", "mc", "codec", "recipe", "channel", "embedded", "done",
        "netty", "network", "load", "github", "handler", "content", "feature",
        "file", "machine", "shader", "general", "helper", "init", "library", "api", "integration", "engine",
        "preload", "preinit", "tiamc",
        "fml", "minecraftforge", "forge", "cpw", "modlauncher", "launchwrapper", "objectweb", "asm", "event",
        "eventhandler", "handshake", "modapi", "kcauldron",
        "fabricmc", "loader", "game", "knot", "launch", "mixin"
    };

    private static readonly Regex CrashLocationPattern =
        new(@"#@!@# Game crashed! Crash report saved to: #@!@# (?<location>.+)", RegexOptions.Compiled);

    private static readonly Regex DescriptionPattern =
        new(@"Description:\s*(?<description>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StackTraceLinePattern =
        new(@"at (?<method>.*?)\((?<sourcefile>.*?)\)", RegexOptions.Compiled);

    /// <summary>Analyses a game log (and the newest crash report when present).</summary>
    public static CrashAnalysis Analyze(string? logText, int exitCode, string? gameDirectory,
        int maxMemoryMb = 0, int modCount = 0, IReadOnlyList<string>? modFiles = null)
    {
        var analysis = new CrashAnalysis
        {
            ExitCode = exitCode,
            AnalysedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        var text = logText ?? "";

        // 1. newest crash report in the game directory carries the real description
        string? reportText = null;
        if (gameDirectory is not null)
        {
            foreach (var candidate in CandidateReports(gameDirectory))
            {
                try
                {
                    reportText = File.ReadAllText(candidate);
                    analysis.CrashReportPath = candidate;
                    break;
                }
                catch (Exception)
                {
                    // unreadable report: keep going
                }
            }
        }

        var combined = text + "\n" + (reportText ?? "");
        var description = DescriptionPattern.Match(combined);
        if (description.Success) analysis.Description = description.Groups["description"].Value.Trim();

        // 2. rules
        foreach (var rule in Rules)
        {
            var regex = CompiledRules[Array.IndexOf(Rules, rule)];
            var match = regex.Match(combined);
            if (!match.Success) continue;

            var detail = rule.Detail;
            if (rule.Id == "java-too-old" && match.Groups["major"].Success &&
                int.TryParse(match.Groups["major"].Value, out var classMajor))
            {
                var needed = JavaVersionFromClassMajor(classMajor);
                if (needed > 0) detail = $"该 class 文件需要 Java {needed}（class 版本 {classMajor}）。";
            }

            if (rule.Id == "mod-missing-dep" && match.Groups["dep"].Success)
            {
                detail = "缺少前置: " + match.Groups["dep"].Value;
            }

            analysis.Hints.Add(new CrashHint
            {
                Id = rule.Id,
                Title = rule.Title,
                Detail = detail,
                Advice = rule.Advice,
                Severity = rule.Severity,
                Evidence = Line(match.Value)
            });
        }

        // 3. memory heuristic (HMCL shows an explicit hint for this)
        if (maxMemoryMb > 0 && maxMemoryMb < 4096 && modCount >= 40 &&
            !analysis.Hints.Any(h => h.Id == "oom"))
        {
            analysis.Hints.Add(new CrashHint
            {
                Id = "memory-tight",
                Title = "内存偏小（模组较多）",
                Detail = $"当前最大内存 {maxMemoryMb} MB，已安装 {modCount} 个模组。",
                Advice = "建议把最大内存调到 4096 MB 以上，或减少模组数量。",
                Severity = CrashSeverity.Warning
            });
        }

        // 4. stack trace keywords + suspect mods (HMCL's CrashReportAnalyzer idea)
        // Keywords come from both the crash report and the console log.
        var keywords = FindKeywords((reportText ?? "") + "\n" + text);
        analysis.Keywords = keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        var files = modFiles ?? (gameDirectory is null ? [] : ListModFiles(gameDirectory));
        analysis.SuspectMods = MatchSuspectMods(analysis.Keywords, files);

        analysis.Summary = BuildSummary(analysis, exitCode);
        return analysis;
    }

    /// <summary>Analyses an existing crash report file directly.</summary>
    public static CrashAnalysis AnalyzeReport(string reportPath, int maxMemoryMb = 0, int modCount = 0)
    {
        var directory = Path.GetDirectoryName(reportPath);
        var text = File.Exists(reportPath) ? File.ReadAllText(reportPath) : "";
        return Analyze(text, -1, directory, maxMemoryMb, modCount);
    }

    /// <summary>Maps a class file major version to the Java version (HMCL helper).</summary>
    public static int JavaVersionFromClassMajor(int major) => major >= 46 ? major - 44 : -1;

    /// <summary>Extracts package keywords from every "at ..." line, HMCL style.</summary>
    public static HashSet<string> FindKeywords(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var match = StackTraceLinePattern.Match(line);
            if (!match.Success) continue;

            var parts = match.Groups["method"].Value.Split('.');
            // Skip the class and method name: only the package segments are useful.
            for (var i = 0; i < parts.Length - 2; i++)
            {
                var part = parts[i].Trim();
                if (part.Length < 3 || PackageKeywordBlackList.Contains(part)) continue;
                result.Add(part);
            }
        }

        return result;
    }

    /// <summary>Matches the keywords against installed mod file names.</summary>
    public static List<string> MatchSuspectMods(IEnumerable<string> keywords, IEnumerable<string> modFiles)
    {
        var list = keywords.Where(k => k.Length >= 3).ToList();
        var result = new List<string>();

        foreach (var file in modFiles)
        {
            var name = Path.GetFileName(file);
            var normalized = name.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
            foreach (var keyword in list)
            {
                var key = keyword.ToLowerInvariant();
                if (key.Length < 4 || !normalized.Contains(key)) continue;
                if (!result.Contains(name)) result.Add(name);
                break;
            }
        }

        return result;
    }

    /// <summary>Lists the mod files of a game directory (isolated or not).</summary>
    public static List<string> ListModFiles(string gameDirectory)
    {
        var mods = Path.Combine(gameDirectory, "mods");
        if (!Directory.Exists(mods)) return [];
        try
        {
            return Directory.GetFiles(mods)
                .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Newest crash reports / JVM crash logs of a game directory.</summary>
    public static IEnumerable<string> CandidateReports(string gameDirectory)
    {
        var reports = new List<string>();

        try
        {
            var crashReports = Path.Combine(gameDirectory, "crash-reports");
            if (Directory.Exists(crashReports))
            {
                reports.AddRange(Directory.GetFiles(crashReports, "crash-*.txt")
                    .OrderByDescending(File.GetLastWriteTimeUtc));
            }

            reports.AddRange(Directory.GetFiles(gameDirectory, "hs_err_pid*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc));
            reports.AddRange(Directory.GetFiles(gameDirectory, "crash-*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }
        catch (Exception)
        {
            // directory may not exist
        }

        return reports;
    }

    private static string BuildSummary(CrashAnalysis analysis, int exitCode)
    {
        if (analysis.Hints.Count == 0)
        {
            return exitCode == 0
                ? "日志里没有发现明显问题（进程正常退出）。"
                : $"进程以代码 {exitCode} 退出，但没有匹配到已知故障特征；" +
                  "可以在「日志」页把最后 50 行发出来，或查看游戏目录 crash-reports 下的报告。";
        }

        var worst = analysis.Hints.OrderByDescending(h => h.Severity).First();
        var summary = $"最可能的原因: {worst.Title}" +
                      (analysis.Description.Length > 0 ? $"（游戏报告: {analysis.Description}）" : "");
        if (analysis.SuspectMods.Count > 0)
        {
            summary += $"；疑似相关模组: {string.Join(", ", analysis.SuspectMods.Take(3))}";
        }

        return summary;
    }

    private static string Line(string value)
    {
        var trimmed = value.Trim();
        var index = trimmed.IndexOf('\n');
        return index < 0 ? trimmed : trimmed[..index];
    }
}
