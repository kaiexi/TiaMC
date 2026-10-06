using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMc.Core.Integrity;
using TiaMc.Core.Launch;
using TiaMc.Core.Minecraft;
namespace TiaMc.App.Services;

/// <summary>
/// Per-instance launch settings. Every field is nullable: null means "inherit the
/// global value", which keeps config.json short and lets a version override only
/// what it needs (版本隔离 also covers the launcher side).
/// </summary>
/// <summary>皮肤库里的一条皮肤。</summary>
public sealed class SavedSkin
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("section")] public string Section { get; set; } = "默认";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "classic";
    [JsonPropertyName("source")] public string Source { get; set; } = "custom";
    [JsonPropertyName("equipped")] public bool Equipped { get; set; }
    [JsonPropertyName("addedUtc")] public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class InstanceSettings
{
    [JsonPropertyName("minMemoryMb")] public int? MinMemoryMb { get; set; }
    [JsonPropertyName("maxMemoryMb")] public int? MaxMemoryMb { get; set; }
    [JsonPropertyName("gcMode")] public string? GcMode { get; set; }
    [JsonPropertyName("javaPath")] public string? JavaPath { get; set; }
    [JsonPropertyName("windowWidth")] public int? WindowWidth { get; set; }
    [JsonPropertyName("windowHeight")] public int? WindowHeight { get; set; }
    [JsonPropertyName("fullscreen")] public bool? Fullscreen { get; set; }
    [JsonPropertyName("extraJvmArgs")] public string? ExtraJvmArgs { get; set; }
    [JsonPropertyName("extraGameArgs")] public string? ExtraGameArgs { get; set; }
    [JsonPropertyName("extraClasspath")] public string? ExtraClasspath { get; set; }
    [JsonPropertyName("environmentVariables")] public string? EnvironmentVariables { get; set; }
    [JsonPropertyName("gameDir")] public string? GameDir { get; set; }
    /// <summary>Isolation override for this instance; null inherits the global switch.</summary>
    [JsonPropertyName("isolate")] public bool? Isolate { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        MinMemoryMb is null && MaxMemoryMb is null && GcMode is null && JavaPath is null &&
        WindowWidth is null && WindowHeight is null && Fullscreen is null && ExtraJvmArgs is null &&
        ExtraGameArgs is null && ExtraClasspath is null && EnvironmentVariables is null &&
        GameDir is null && Isolate is null;

    /// <summary>Counts the overridden fields, for the UI hint.</summary>
    [JsonIgnore]
    public int OverrideCount =>
        new object?[]
        {
            MinMemoryMb, MaxMemoryMb, GcMode, JavaPath, WindowWidth, WindowHeight, Fullscreen,
            ExtraJvmArgs, ExtraGameArgs, ExtraClasspath, EnvironmentVariables, GameDir, Isolate
        }.Count(v => v is not null);
}

/// <summary>
/// Persisted launcher configuration (TIA Portal would call this the project
/// settings). Stored next to the executable in config.json.
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("minecraftRoot")] public string? MinecraftRoot { get; set; }
    /// <summary>Legacy single-account name, kept so old config files still load (accounts.json wins).</summary>
    [JsonPropertyName("playerName")] public string PlayerName { get; set; } = "Steve";
    [JsonPropertyName("javaPath")] public string? JavaPath { get; set; }
    [JsonPropertyName("minMemoryMb")] public int MinMemoryMb { get; set; } = 512;

    /// <summary>
    /// When true (default) the maximum heap is derived from the physical memory of
    /// this machine; dragging the slider turns it off so the user choice sticks.
    /// </summary>
    [JsonPropertyName("memoryAuto")] public bool MemoryAuto { get; set; } = true;

    /// <summary>Major version of the Java runtime that is currently selected.</summary>
    [JsonPropertyName("javaMajor")] public int JavaMajor { get; set; } = 17;
    [JsonPropertyName("maxMemoryMb")] public int MaxMemoryMb { get; set; } = 4096;
    [JsonPropertyName("gcMode")] public string GcMode { get; set; } = "G1GC";
    [JsonPropertyName("windowWidth")] public int WindowWidth { get; set; } = 854;
    [JsonPropertyName("windowHeight")] public int WindowHeight { get; set; } = 480;
    [JsonPropertyName("fullscreen")] public bool Fullscreen { get; set; }
    [JsonPropertyName("extraJvmArgs")] public string ExtraJvmArgs { get; set; } = "";
    [JsonPropertyName("extraGameArgs")] public string ExtraGameArgs { get; set; } = "";
    [JsonPropertyName("extraClasspath")] public string ExtraClasspath { get; set; } = "";
    [JsonPropertyName("environmentVariables")] public string EnvironmentVariables { get; set; } = "";
    [JsonPropertyName("javaAgentPath")] public string JavaAgentPath { get; set; } = "";
    [JsonPropertyName("downloadSource")] public DownloadSource DownloadSource { get; set; } = DownloadSource.Auto;

    /// <summary>自定义下载源基址（选「自定义」时用，路径规则同 BMCLAPI），例如 https://你的反代 。</summary>
    [JsonPropertyName("downloadSourceCustom")] public string DownloadSourceCustom { get; set; } = "";

    /// <summary>单文件分段下载的线程数（NeatDM 式多连接）。1 = 单连接。</summary>
    [JsonPropertyName("downloadThreads")] public int DownloadThreads { get; set; } = 8;

    /// <summary>输出窗口代码高亮（按级别/来源上色）。</summary>
    [JsonPropertyName("logHighlight")] public bool LogHighlight { get; set; } = true;

    /// <summary>皮肤库（学 Axolotl：可保存多条、带分区与"已应用"标记）。</summary>
    [JsonPropertyName("savedSkins")] public List<SavedSkin> SavedSkins { get; set; } = [];

    /// <summary>暗黑模式（代码高亮配色）。</summary>
    [JsonPropertyName("darkMode")] public bool DarkMode { get; set; }

    /// <summary>皮肤（强调色预设名）。</summary>
    [JsonPropertyName("skin")] public string Skin { get; set; } = "工程蓝（默认）";

    /// <summary>资源（模组/资源包）搜索来源：modrinth（官方）或 custom（自定义镜像基址）。</summary>
    [JsonPropertyName("resourceSource")] public string ResourceSource { get; set; } = "modrinth";

    /// <summary>自定义 Modrinth 镜像基址，例如 https://你的反代/v2 。</summary>
    [JsonPropertyName("resourceMirror")] public string ResourceMirror { get; set; } = "";
    [JsonPropertyName("activeInstance")] public string? ActiveInstance { get; set; }
    [JsonPropertyName("autoCheckFiles")] public bool AutoCheckFiles { get; set; } = true;
    [JsonPropertyName("autoDownloadMissing")] public bool AutoDownloadMissing { get; set; }
    [JsonPropertyName("closeConsoleOnExit")] public bool CloseConsoleOnExit { get; set; }

    /// <summary>单个会话日志文件的 MB 上限（超过后只保留界面显示，不再写文件）。</summary>
    [JsonPropertyName("logMaxFileMb")] public int LogMaxFileMb { get; set; } = 8;

    /// <summary>保留多少个会话日志文件（从最旧的开始删）。</summary>
    [JsonPropertyName("logKeepFiles")] public int LogKeepFiles { get; set; } = 20;

    /// <summary>日志目录总占用上限（MB）。</summary>
    [JsonPropertyName("logMaxTotalMb")] public int LogMaxTotalMb { get; set; } = 64;
    /// <summary>Mods folder override; empty means "resolve per instance".</summary>
    [JsonPropertyName("modsPath")] public string ModsPath { get; set; } = "";
    /// <summary>Isolate each version into its own game directory (独立的 config/saves/mods).</summary>
    [JsonPropertyName("isolateInstances")] public bool IsolateInstances { get; set; } = true;
    /// <summary>Custom game directory used when <see cref="IsolateInstances"/> is false.</summary>
    [JsonPropertyName("customGameDir")] public string CustomGameDir { get; set; } = "";
    /// <summary>
    /// Portable mode (on by default): everything - versions, libraries, assets,
    /// instances - lives in a "minecraft" folder next to the executable instead of
    /// %APPDATA%\.minecraft.
    /// </summary>
    [JsonPropertyName("portableRoot")] public bool PortableRoot { get; set; } = true;
    /// <summary>
    /// The authentication server switch: when it was left on, the Yggdrasil server
    /// is started again the next time the launcher runs.
    /// </summary>
    [JsonPropertyName("autoStartYggdrasil")] public bool AutoStartYggdrasil { get; set; }

    /// <summary>Download the JRE a version needs into &lt;root&gt;\runtime when missing.</summary>
    [JsonPropertyName("autoProvisionJava")] public bool AutoProvisionJava { get; set; } = true;
    /// <summary>Per-version launch settings, keyed by version id.</summary>
    [JsonPropertyName("instances")]
    public Dictionary<string, InstanceSettings> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("lastLaunchUtc")] public DateTime? LastLaunchUtc { get; set; }

    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string? _configDirectory;

    /// <summary>Folder that holds config.json, accounts.json and the log files.</summary>
    public static string ConfigDirectory
    {
        get
        {
            if (_configDirectory is not null) return _configDirectory;

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TiaMC"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMC"),
                Path.Combine(AppContext.BaseDirectory, "config"),
                Path.Combine(Path.GetTempPath(), "TiaMC")
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    Directory.CreateDirectory(candidate);
                    // Prove the folder is actually writable before committing to it.
                    var probe = Path.Combine(candidate, ".write-probe");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    _configDirectory = candidate;
                    return candidate;
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException or NotSupportedException)
                {
                    // Try the next candidate.
                }
            }

            _configDirectory = Path.GetTempPath();
            return _configDirectory;
        }
    }

    /// <summary>Path of the account file (offline + Microsoft accounts).</summary>
    public static string AccountsFilePath => Path.Combine(ConfigDirectory, "accounts.json");

    public static string DiagnosticsFilePath => Path.Combine(ConfigDirectory, "diagnostics.log");

    /// <summary>Cache directory used by the core (mod icons, metadata, Java list).</summary>
    public static void ApplyCacheRoot()
    {
        TiaMc.Core.Utils.AppPaths.CacheRoot = Path.Combine(ConfigDirectory, "cache");
    }

    public static AppConfig Load()
    {
        var path = Path.Combine(ConfigDirectory, "config.json");
        AppConfig config;

        try
        {
            if (File.Exists(path))
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) ?? new AppConfig();
                config.FilePath = path;
            }
            else
            {
                config = new AppConfig { FilePath = path };
            }
        }
        catch (Exception e)
        {
            LogService.Warn($"配置文件读取失败，使用默认值: {e.Message}");
            config = new AppConfig { FilePath = path };
        }

        // Portable mode is on by default: the game folder lives next to the
        // executable so a fresh copy of the launcher never touches %APPDATA%.
        // The folder structure is created right away, because otherwise the path
        // would not exist yet and the default detection would fall back to
        // %APPDATA%\.minecraft.
        if (config.PortableRoot)
        {
            config.MinecraftRoot = MinecraftFinder.CreatePortableRoot(
                message => LogService.Info(message, "Portable"));
        }
        else if (string.IsNullOrWhiteSpace(config.MinecraftRoot))
        {
            config.MinecraftRoot = McPaths.DetectRoot(null);
        }

        // Write the file on first run so the settings are visible and editable.
        if (!File.Exists(path)) config.Save();

        return config;
    }

    public void Save()
    {
        try
        {
            var path = string.IsNullOrEmpty(FilePath)
                ? Path.Combine(ConfigDirectory, "config.json")
                : FilePath;
            FilePath = path;
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception e)
        {
            LogService.Error($"配置保存失败: {e.Message}");
        }
    }

    /// <summary>Settings of one instance, created on demand.</summary>
    public InstanceSettings GetInstance(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return new InstanceSettings();
        if (!Instances.TryGetValue(versionId!, out var settings))
        {
            settings = new InstanceSettings();
            Instances[versionId!] = settings;
        }

        return settings;
    }

    /// <summary>Drops every override of one instance (back to the global values).</summary>
    public void ResetInstance(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return;
        Instances.Remove(versionId!);
    }

    /// <summary>Maps the persisted settings onto the core launch options.</summary>
    public LaunchOptions ToLaunchOptions(string? versionId = null)
    {
        var instance = versionId is null ? new InstanceSettings() : GetInstance(versionId);

        return new LaunchOptions
        {
            // -Xms is never passed any more; the maximum is the only heap setting.
            MinMemoryMb = 0,
            MaxMemoryMb = instance.MaxMemoryMb ?? MaxMemoryMb,
            GcMode = instance.GcMode ?? GcMode,
            JavaMajor = JavaMajor,
            WindowWidth = instance.WindowWidth ?? WindowWidth,
            WindowHeight = instance.WindowHeight ?? WindowHeight,
            Fullscreen = instance.Fullscreen ?? Fullscreen,
            ExtraJvmArgs = Normalize(instance.ExtraJvmArgs, ExtraJvmArgs),
            ExtraGameArgs = Normalize(instance.ExtraGameArgs, ExtraGameArgs),
            ExtraClasspath = Normalize(instance.ExtraClasspath, ExtraClasspath),
            EnvironmentVariables = Normalize(instance.EnvironmentVariables, EnvironmentVariables),
            JavaAgentPath = string.IsNullOrWhiteSpace(JavaAgentPath) ? null : JavaAgentPath,
            GameDirectory = ResolveGameDirectory(versionId),
            Isolated = instance.Isolate ?? IsolateInstances
        };
    }

    private static string? Normalize(string? instanceValue, string globalValue)
    {
        var value = instanceValue ?? globalValue;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Game directory (--gameDir) of the instance that is about to be launched.
    /// Isolating gives every version its own folder inside versions/.
    /// </summary>
    public string? ResolveGameDirectory(string? versionId = null)
    {
        if (versionId is null) return null;

        var instance = GetInstance(versionId);
        if (instance.GameDir is { Length: > 0 } custom) return custom;

        var isolated = instance.Isolate ?? IsolateInstances;
        if (!isolated)
        {
            return string.IsNullOrWhiteSpace(CustomGameDir) ? null : CustomGameDir;
        }

        var root = MinecraftRoot ?? McPaths.DefaultRoot;
        return Path.Combine(root, "versions", versionId);
    }

    public void CopyFrom(AppConfig other)
    {
        MinecraftRoot = other.MinecraftRoot;
        PlayerName = other.PlayerName;
        JavaPath = other.JavaPath;
        MinMemoryMb = other.MinMemoryMb;
        MaxMemoryMb = other.MaxMemoryMb;
        GcMode = other.GcMode;
        WindowWidth = other.WindowWidth;
        WindowHeight = other.WindowHeight;
        Fullscreen = other.Fullscreen;
        ExtraJvmArgs = other.ExtraJvmArgs;
        ExtraGameArgs = other.ExtraGameArgs;
        ExtraClasspath = other.ExtraClasspath;
        EnvironmentVariables = other.EnvironmentVariables;
        JavaAgentPath = other.JavaAgentPath;
        DownloadSource = other.DownloadSource;
        ActiveInstance = other.ActiveInstance;
        AutoCheckFiles = other.AutoCheckFiles;
        AutoDownloadMissing = other.AutoDownloadMissing;
        CloseConsoleOnExit = other.CloseConsoleOnExit;
    }
}
