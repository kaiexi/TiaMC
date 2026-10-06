using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using TiaMc.App.Mvvm;
using TiaMc.App.Services;
using TiaMc.Core.Accounts;
using TiaMc.Core.Integrity;
using TiaMc.Core.Java;
using TiaMc.Core.Launch;
using TiaMc.Core.Minecraft;
using TiaMc.Core.Modpacks;
using TiaMc.Core.Resources;
using TiaMc.Core.Mods;
using TiaMc.Core.Net;
using TiaMc.Core.Yggdrasil;
using TiaMc.Core.Utils;

namespace TiaMc.App.ViewModels;

/// <summary>A version entry shown in the launch combo box.</summary>
public sealed class VersionChoice
{
    public required string Id { get; init; }
    public required string Loader { get; init; }
    public string Display => $"{Id}   [{Loader}]";
}

/// <summary>One row of the launch plan table (TIA style "device overview").</summary>
public sealed class PlanRow
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Value { get; init; } = "";
    public string Status { get; init; } = "就绪";
}

public sealed class RemoteVersionRow
{
    public required ManifestVersion Source { get; init; }
    public string Id => Source.Id;
    public string Type => Source.Type;
    public string ReleaseTime => Source.ReleaseTime is { Length: >= 10 } t ? t[..10] : "";
    public bool Installed { get; init; }

    /// <summary>正式版 / 快照（测试版）/ 旧版 —— 用来把正式版和测试版分开显示。</summary>
    public string TypeText => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照（测试版）",
        "old_beta" => "旧版 Beta",
        "old_alpha" => "旧版 Alpha",
        _ => Type
    };

    public bool IsSnapshot => Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase);

    public bool IsLegacy => Type.StartsWith("old_", StringComparison.OrdinalIgnoreCase);

    public bool IsRelease => Type.Equals("release", StringComparison.OrdinalIgnoreCase);

    /// <summary>Short badge shown in the type column.</summary>
    public string BadgeText => IsSnapshot ? "测试" : IsLegacy ? "旧版" : "正式";
}

/// <summary>
/// The single view model behind the main window. It maps the launcher core onto
/// the TIA Portal style shell: project tree, property grid, workspace tabs and
/// the diagnostics log.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly LaunchService _launcher;

    private InstalledVersion? _selectedVersion;
    private VersionChoice? _activeChoice;
    private JavaInfo? _selectedJava;
    private TreeNode? _selectedNode;
    private LauncherState _state = LauncherState.Idle;
    private string _stateMessage = "就绪";
    private string _downloadStatus = "";
    private double _downloadPercent;
    private bool _isBusy;
    private int _workspaceTab;
    private string _selectedLogLevel = "全部";
    private bool _autoScrollConsole = true;
    private CheckResult? _lastCheck;
    private LaunchPlan? _lastPlan;

    public MainViewModel()
    {
        _launcher = new LaunchService(AppConfig.Load());
        _launcher.StateChanged += OnStateChanged;
        _launcher.GameOutput += line => Console.Append(line);
        _launcher.GameExited += OnGameExited;
        _launcher.DiagnosisReady += analysis => ApplyDiagnosis(analysis, manual: false);

        LogService.EntryAdded += OnLogEntry;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => SelectedVersion is not null && !IsGameRunning);
        StopCommand = new RelayCommand(() => _launcher.Stop(), () => IsGameRunning);
        ImportModpackCommand = new AsyncRelayCommand(() => ImportModpackAsync(server: false));
        ImportServerModpackCommand = new AsyncRelayCommand(() => ImportModpackAsync(server: true));
        ScanModpacksCommand = new RelayCommand(RefreshModpacks);
        StartServerPackCommand = new AsyncRelayCommand(StartServerPackAsync, () => ModpackServerStartEnabled && !_serverRunning);
        StopServerPackCommand = new RelayCommand(StopServerPack, () => _serverRunning);
        DeployModpackCommand = new AsyncRelayCommand(DeploySelectedModpackAsync);
        ApplyRecommendedMemoryCommand = new RelayCommand(ApplyRecommendedMemory);
        PickSkinFileCommand = new RelayCommand(PickSkinFile);
        GenerateSkinCommand = new RelayCommand(GenerateSkin);
        RandomSkinCommand = new RelayCommand(RandomSkin);
        ClearSkinCommand = new RelayCommand(ClearSkin);
        FetchSkinCommand = new AsyncRelayCommand(FetchSkinFromSiteAsync);
        PreviewSkin3DCommand = new AsyncRelayCommand(PreviewSkin3DAsync);
        PushSkinCommand = new RelayCommand(() => PushSkinToServer());
        foreach (var site in TiaMc.Core.Utils.SkinSites.Presets) SkinSites.Add(site.Name);
        if (SkinSites.Count > 0) _skinSite = SkinSites[0];

        RestartElevatedCommand = new RelayCommand(RestartElevated);
        SaveJvmArgsCommand = new RelayCommand(SaveJvmArgs);
        UndoJvmArgsCommand = new RelayCommand(UndoJvmArgs);
        ResetJvmArgsCommand = new RelayCommand(ResetJvmArgs);
        InheritJvmArgsCommand = new RelayCommand(InheritJvmArgs);
        ApplyJvmArgsAsGlobalCommand = new RelayCommand(ApplyJvmArgsAsGlobal);
        SearchResourcesCommand = new AsyncRelayCommand(SearchResourcesAsync);
        DownloadResourceCommand = new AsyncRelayCommand(DownloadResourceAsync);
        RecheckJavaCommand = new RelayCommand(() =>
        {
            JavaDetector.InvalidateCache();
            _ = DetectJavaAsync();
        });
        TrimMemoryCommand = new RelayCommand(TrimMemory);
        ClampMemoryToMachine();

        // Every machine is different: a fresh profile starts from this machine's memory.
        if (Config.MemoryAuto)
        {
            Config.MaxMemoryMb = TiaMc.Core.Utils.SystemInfo.RecommendedMaxMemoryMb;
            Config.MinMemoryMb = Config.MaxMemoryMb;
        }
        OpenModpackFolderCommand = new RelayCommand(OpenModpackFolder);
        DeleteModpackCommand = new RelayCommand(DeleteSelectedModpack);
        SelectSkinCommand = new RelayCommand(p => SelectedSkin = p as string ?? "");
        UploadSkinCommand = new AsyncRelayCommand(UploadSkinToGameAsync);
        // 删除本地版本（删除 versions/<id> 整个目录，共享的存档/模组不动）
        DeleteVersionCommand = new RelayCommand(DeleteSelectedVersion,
            () => SelectedVersion is not null && !_launcher.IsRunning);
        CopyModpackCommandCommand = new RelayCommand(() =>
        {
            if (ModpackServerCommand.Length > 0) System.Windows.Clipboard.SetText(ModpackServerCommand);
        });
        CheckFilesCommand = new AsyncRelayCommand(CheckFilesAsync, () => SelectedVersion is not null && !IsBusy);
        DownloadMissingCommand = new AsyncRelayCommand(DownloadMissingAsync, () => _lastCheck is { IsComplete: false });
        BuildPlanCommand = new RelayCommand(BuildPlan, () => SelectedVersion is not null);
        SaveConfigCommand = new RelayCommand(SaveConfig);
        BrowseRootCommand = new RelayCommand(BrowseRoot);
        AutoDetectRootCommand = new RelayCommand(AutoDetectRoot);
        ApplyInstanceAsGlobalCommand = new RelayCommand(ApplyInstanceAsGlobal, () => SelectedVersion is not null);
        ResetInstanceCommand = new RelayCommand(ResetInstanceOverrides,
            () => SelectedVersion is not null && HasInstanceOverrides);
        BrowseJavaCommand = new RelayCommand(BrowseJava);
        CopyConsoleCommand = new RelayCommand(() => CopyToClipboard(Console.Text));
        ClearConsoleCommand = new RelayCommand(() => Console.Clear());
        CopyCommandLineCommand = new RelayCommand(CopyCommandLine, () => _lastPlan is not null);
        RefreshManifestCommand = new AsyncRelayCommand(RefreshManifestAsync);
        InstallVersionCommand = new AsyncRelayCommand(InstallVersionAsync, p => p is RemoteVersionRow);
        OpenGameFolderCommand = new RelayCommand(OpenGameFolder);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        AddOfflineAccountCommand = new AsyncRelayCommand(AddOfflineAccountAsync);
        AddMicrosoftAccountCommand = new AsyncRelayCommand(AddMicrosoftAccountAsync, () => !IsBusy);
        RefreshAccountCommand = new AsyncRelayCommand(RefreshAccountAsync, () => SelectedAccount is not null && !IsBusy);
        // 允许删除任意账户（包括最后一个）：以前要求 >1，只有一个账户时按钮永远是灰的
        RemoveAccountCommand = new RelayCommand(RemoveAccount, () => _launcher.Accounts.Accounts.Count > 0);
        RefreshModsCommand = new AsyncRelayCommand(RefreshModsAsync);
        ToggleModCommand = new RelayCommand(ToggleMod, () => SelectedMod is not null);
        DeleteModCommand = new RelayCommand(DeleteMod, () => SelectedMod is not null);
        ImportModsCommand = new AsyncRelayCommand(ImportModsAsync);
        OpenModsFolderCommand = new RelayCommand(OpenModsFolder);
        BrowseModsFolderCommand = new RelayCommand(BrowseModsFolder);
        OpenModPageCommand = new RelayCommand(OpenModPage,
            () => SelectedMod is { Homepage.Length: > 0 } or { Issues.Length: > 0 });
        SearchModrinthCommand = new AsyncRelayCommand(SearchModrinthAsync, () => !IsModBusy);
        InstallModrinthCommand = new AsyncRelayCommand(InstallModrinthAsync,
            p => !IsModBusy && p is ModrinthVersion);

        // Open source collectors (OpenJDK / OpenJ9) first, then the classic ones.
        foreach (var mode in new[]
                 {
                     "G1GC",
                     "ShenandoahGC",   // Red Hat 开源低延迟回收器（JDK 12+）
                     "ZGC",            // OpenJDK 开源低延迟回收器（JDK 15+，21+ 支持分代）
                     "EpsilonGC",      // OpenJDK 开源无操作回收器（仅测试）
                     "OpenJ9GenCon",   // Eclipse OpenJ9 开源 JVM 的分代回收器
                     "ParallelGC",
                     "SerialGC",
                     "None"
                 })
        {
            GcModes.Add(mode);
        }

        _launcher.ReloadInstallation();
        BuildPalette();
        RebuildTree();
        RefreshInstalled();
        RefreshAccounts();
        RefreshJavaChoices();
        _ = InitializeAsync();
    }

    // ---------------------------------------------------------------- state

    public LaunchService Launcher => _launcher;
    public AppConfig Config => _launcher.Config;
    public ConsoleViewModel Console { get; } = new();

    public ObservableCollection<TreeNode> Navigation { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<string> LogLevels { get; } = ["全部", "INFO", "CMD", "OK", "WARN", "ERR", "GAME"];
    public ObservableCollection<VersionChoice> VersionChoices { get; } = [];
    public ObservableCollection<JavaInfo> JavaChoices { get; } = [];
    public ObservableCollection<string> GcModes { get; } = [];
    public ObservableCollection<PlanRow> PlanRows { get; } = [];
    public ObservableCollection<RemoteVersionRow> RemoteVersions { get; } = [];
    public ObservableCollection<ModInfo> Mods { get; } = [];
    public ObservableCollection<ModrinthSearchHit> ModrinthHits { get; } = [];

    // ------------------------------------------------ TIA style shell state

    /// <summary>Instruction-palette equivalent: collapsible groups of launcher actions.</summary>
    public ObservableCollection<PaletteSection> PaletteSections { get; } = [];

    /// <summary>Project tree filter box (TIA has "在项目中搜索").</summary>
    private string _treeFilter = "";

    public string TreeFilter
    {
        get => _treeFilter;
        set
        {
            if (Set(ref _treeFilter, value)) RebuildTree();
        }
    }

    /// <summary>Right hand pane selector, mirroring TIA's vertical pane tabs.</summary>
    private int _rightPane;

    public int RightPane
    {
        get => _rightPane;
        set
        {
            if (!Set(ref _rightPane, value)) return;
            Raise(nameof(ShowProperties));
            Raise(nameof(ShowPalette));
        }
    }

    public bool ShowProperties => RightPane == 0;
    public bool ShowPalette => RightPane == 1;

    public string BreadcrumbText =>
        $"{Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar))} " +
        $"/ 实例 / {DetailVersion}";

    // ------------------------------------------------------------------ mods

    private ModInfo? _selectedMod;
    private string _modSearch = "";
    private string _modsPath = "";
    private string _modrinthProject = "";
    private int _modCountEnabled;
    private int _modCountDisabled;

    /// <summary>The mod file selected in the mods table.</summary>
    public ModInfo? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (!Set(ref _selectedMod, value)) return;
            Raise(nameof(SelectedModTitle));
            Raise(nameof(SelectedModDetail));
            Raise(nameof(SelectedModLinks));
            (ToggleModCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteModCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteVersionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenModPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string SelectedModTitle => SelectedMod is null
        ? "(未选择模组)"
        : $"{SelectedMod.DisplayName}  {SelectedMod.DisplayVersion}";

    public string SelectedModDetail => SelectedMod is null
        ? "在左侧列表中选择一个模组查看详情"
        : $"文件: {SelectedMod.FileName}\n" +
          $"Mod ID: {SelectedMod.ModId ?? "-"}\n" +
          $"装载器: {SelectedMod.LoaderText}    状态: {SelectedMod.StatusText}\n" +
          $"作者: {SelectedMod.Authors ?? "-"}\n" +
          $"大小: {SelectedMod.SizeText}    修改时间: {SelectedMod.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
          (string.IsNullOrWhiteSpace(SelectedMod.Description) ? "" : $"\n{SelectedMod.Description}");

    public string SelectedModLinks => SelectedMod switch
    {
        null => "",
        { Homepage: { Length: > 0 } page, Issues: { Length: > 0 } issues } => $"主页: {page}\n问题反馈: {issues}",
        { Homepage: { Length: > 0 } page } => $"主页: {page}",
        { Issues: { Length: > 0 } issues } => $"问题反馈: {issues}",
        _ => ""
    };

    /// <summary>Current mods folder (resolved from the instance unless overridden).</summary>
    public string ModsPath
    {
        get => _modsPath;
        private set => Set(ref _modsPath, value);
    }

    // ------------------------------------------------------- version isolation

    /// <summary>
    /// When enabled every version runs inside its own folder under versions/, with
    /// its own config, saves, mods and resource packs (版本隔离).
    /// </summary>
    public bool IsolateInstances
    {
        get => Config.IsolateInstances;
        set
        {
            if (Config.IsolateInstances == value) return;
            Config.IsolateInstances = value;
            Config.Save();
            Raise();
            RaiseIsolationState();
            LogService.Info(value
                ? "已开启版本隔离：每个版本使用独立的 config/saves/mods"
                : "已关闭版本隔离：所有版本共享 .minecraft 根目录", "Isolation");
            _ = RefreshModsAsync();
        }
    }

    /// <summary>The game directory (--gameDir) the next launch will use.</summary>
    public string GameDirText => SelectedVersion is null
        ? (Config.IsolateInstances ? "versions/<实例>" : RootPath)
        : Config.ResolveGameDirectory(SelectedVersion.Id) ?? RootPath;

    public string IsolationText => (Current.Isolate ?? Config.IsolateInstances)
        ? $"本实例: 隔离（游戏目录 {GameDirText}）"
        : $"本实例: 共享根目录（{RootPath}）";

    /// <summary>
    /// Portable mode: keep everything (versions, libraries, assets, instances) in a
    /// "minecraft" folder next to the launcher executable.
    /// </summary>
    public bool PortableRoot
    {
        get => Config.PortableRoot;
        set
        {
            if (Config.PortableRoot == value) return;

            if (value)
            {
                var target = MinecraftFinder.PortableRoot;
                var answer = System.Windows.MessageBox.Show(
                    System.Windows.Application.Current.MainWindow,
                    $"将在程序目录下创建并切换到便携目录：\n{target}\n\n" +
                    "之后下载的版本、库、资源与实例都会放在这里（已存在的版本不会被移动）。\n\n是否继续？",
                    "启用便携模式", System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxImage.Question);
                if (answer != System.Windows.MessageBoxResult.OK) return;

                Config.PortableRoot = true;
                Config.Save();
                ApplyRoot(MinecraftFinder.CreatePortableRoot(message => LogService.Info(message, "Portable")),
                    "便携模式");
            }
            else
            {
                Config.PortableRoot = false;
                Config.Save();
                ApplyRoot(McPaths.DetectRoot(null), "退出便携模式");
            }

            Raise();
        }
    }

    public string PortableText => Config.PortableRoot
        ? $"开启（{MinecraftFinder.PortableRoot}）"
        : "关闭（使用 %APPDATA%\\.minecraft）";

    /// <summary>Raises every property that depends on the isolation / folder settings.</summary>
    private void RaiseIsolationState()
    {
        Raise(nameof(GameDirText));
        Raise(nameof(IsolationText));
        Raise(nameof(PortableText));
        Raise(nameof(RootPath));
    }

    public string ModSearch
    {
        get => _modSearch;
        set
        {
            if (Set(ref _modSearch, value)) Raise(nameof(FilteredMods));
        }
    }

    /// <summary>Mods after applying the search box.</summary>
    public IEnumerable<ModInfo> FilteredMods =>
        string.IsNullOrWhiteSpace(ModSearch)
            ? Mods
            : Mods.Where(m =>
                m.DisplayName.Contains(ModSearch, StringComparison.OrdinalIgnoreCase) ||
                (m.ModId ?? "").Contains(ModSearch, StringComparison.OrdinalIgnoreCase) ||
                m.FileName.Contains(ModSearch, StringComparison.OrdinalIgnoreCase) ||
                (m.Authors ?? "").Contains(ModSearch, StringComparison.OrdinalIgnoreCase));

    public string ModsSummary =>
        $"共 {Mods.Count} 个模组  ·  启用 {_modCountEnabled}  ·  停用 {_modCountDisabled}" +
        (Mods.Count(m => !m.HasMetadata) is var unknown && unknown > 0 ? $"  ·  无法识别 {unknown}" : "");

    /// <summary>Used by the empty-state hint that fills the leftover space.</summary>
    public bool HasMods => Mods.Count > 0;

    public bool HasAccounts => _accountList.Count > 0;

    public bool HasInstalledVersions => VersionChoices.Count > 0;

    public bool HasRemoteVersions => RemoteVersions.Count > 0;

    // ------------------------------------------------- Yggdrasil auth server

    private YggdrasilServer? _yggServer;
    private string _yggInput = "";
    private bool _yggBannerShown;

    /// <summary>The whole "认证服务端" page is this terminal.</summary>
    public ConsoleViewModel YggConsole { get; } = new();

    /// <summary>Command line of the terminal (typed by the user).</summary>
    public string YggInput
    {
        get => _yggInput;
        set => Set(ref _yggInput, value);
    }

    public string YggPrompt => _yggServer?.IsRunning == true ? $"ygg:{_yggServer.Port}>" : "ygg>";

    /// <summary>
    /// The on/off switch of the authentication server. It is bound one way on
    /// purpose: a two way binding writes the initial (false) value back into the
    /// view model, which would stop a server that a command line switch just started.
    /// </summary>
    public bool YggServerOn => _yggServer?.IsRunning == true;

    /// <summary>Turns the authentication server on or off (used by the switch and commands).</summary>
    public void SetYggServer(bool on)
    {
        if (on == YggServerOn) return;

        if (Environment.GetEnvironmentVariable("TIAMC_YGG_TRACE") == "1")
        {
            YggConsole.Append($"[trace] SetYggServer({on}) 调用栈: {Environment.StackTrace.Replace("\n", " | ")}");
        }

        if (on)
        {
            if (!Ygg.Start())
            {
                RaiseYggState();
                return;
            }
        }
        else
        {
            Ygg.Stop();
        }

        Config.AutoStartYggdrasil = on;
        Config.Save();
        RaiseYggState();
    }

    /// <summary>Flips the switch; the toolbar toggle calls this on click.</summary>
    public void ToggleYggServer() => SetYggServer(!YggServerOn);

    // ------------------------------------------------------ modpack manager

    private ModpackManager? _modpackManager;
    private InstalledModpack? _selectedModpack;
    private System.Diagnostics.Process? _serverProcess;
    private bool _serverRunning;

    private ModpackManager ModpackSvc => _modpackManager ??= new ModpackManager(_launcher.Paths.Root);

    /// <summary>Installed client and server packs (server packs first).</summary>
    public ObservableCollection<InstalledModpack> ModpackList { get; } = [];

    public InstalledModpack? SelectedModpack
    {
        get => _selectedModpack;
        set
        {
            if (!Set(ref _selectedModpack, value)) return;
            Raise(nameof(ModpackDetail));
            Raise(nameof(ModpackServerCommand));
            Raise(nameof(ModpackKindText));
            Raise(nameof(ModpackStartText));
            Raise(nameof(HasSelectedModpack));
        }
    }

    public bool HasSelectedModpack => _selectedModpack is not null;

    public bool HasModpacks => ModpackList.Count > 0;

    public bool IsServerPackSelected => _selectedModpack?.IsServer == true;

    // ------------------------------------------------------- 诊断（HMCL 式崩溃分析）

    public ObservableCollection<TiaMc.Core.Diagnostics.CrashHint> DiagnosisHints { get; } = [];

    private string _diagnosisSummary = "还没有诊断结果。点击「诊断上次启动」分析游戏日志与 crash-reports。";
    private string _diagnosisSuspects = "";
    private string _diagnosisReportPath = "";
    private string _diagnosisKeywords = "";

    public string DiagnosisSummary
    {
        get => _diagnosisSummary;
        private set => Set(ref _diagnosisSummary, value);
    }

    public string DiagnosisSuspects
    {
        get => _diagnosisSuspects;
        private set => Set(ref _diagnosisSuspects, value);
    }

    public string DiagnosisReportPath
    {
        get => _diagnosisReportPath;
        private set => Set(ref _diagnosisReportPath, value);
    }

    public string DiagnosisKeywords
    {
        get => _diagnosisKeywords;
        private set => Set(ref _diagnosisKeywords, value);
    }

    /// <summary>Runs the analysis on demand (toolbar / menu).</summary>
    public void DiagnoseLastLaunch()
    {
        WorkspaceTab = 3;
        try
        {
            var versionId = ActiveChoice?.Id ?? Config.ActiveInstance;
            var gameDir = string.IsNullOrEmpty(versionId)
                ? null
                : Config.ResolveGameDirectory(versionId) ?? Path.Combine(_launcher.Paths.Root, "versions", versionId);

            LogService.Info($"诊断目标实例: {versionId ?? "-"}  游戏目录: {gameDir ?? "-"}", "Diagnose");
            var analysis = _launcher.Diagnose(gameDirectory: gameDir, notify: false);
            ApplyDiagnosis(analysis, manual: true);
        }
        catch (Exception e)
        {
            LogService.Error("诊断失败: " + e.Message, "Diagnose");
        }
    }

    private void ApplyDiagnosis(TiaMc.Core.Diagnostics.CrashAnalysis analysis, bool manual)
    {
        Ui.Post(() =>
        {
            DiagnosisHints.Clear();
            foreach (var hint in analysis.Hints.OrderByDescending(h => h.Severity)) DiagnosisHints.Add(hint);

            DiagnosisSummary = analysis.Summary;
            DiagnosisSuspects = analysis.SuspectMods.Count > 0
                ? "疑似相关模组: " + string.Join(", ", analysis.SuspectMods)
                : "没有匹配到具体模组（关键词不足以定位）";
            DiagnosisReportPath = analysis.CrashReportPath is null
                ? "没有找到 crash-reports 报告（日志里也没有崩溃标记）"
                : "崩溃报告: " + analysis.CrashReportPath;
            DiagnosisKeywords = analysis.Keywords.Count > 0
                ? "堆栈关键词: " + string.Join(", ", analysis.Keywords.Take(20))
                : "";

            LogService.Write(analysis.HasProblem ? LogLevel.Error : LogLevel.Info,
                (manual ? "手动诊断: " : "启动失败自动诊断: ") + analysis.Summary, "Diagnose");

            foreach (var hint in analysis.Hints)
            {
                LogService.Write(hint.Severity >= TiaMc.Core.Diagnostics.CrashSeverity.Error
                        ? LogLevel.Error
                        : LogLevel.Warning,
                    $"  [{hint.SeverityText}] {hint.Title} → {hint.Advice}", "Diagnose");
            }

            foreach (var mod in analysis.SuspectMods) LogService.Warn("  疑似模组: " + mod, "Diagnose");
            if (manual && !analysis.HasProblem) LogService.Ok("没有发现明显问题", "Diagnose");
        });
    }

    // ------------------------------------------- 资源下载中心（模组/整合包/资源包/光影）

    private ResourceCatalog? _catalog;
    private string _resourceKindName = "模组";
    private string _resourceQuery = "";
    private string _resourceGameVersion = "";
    private string _resourceLoader = "";
    private string _resourceStatus = "输入关键词后点「搜索」；支持中文名（内置中文表，可放到 config/search-zh.txt 扩展）。";
    private ResourceHitVm? _selectedResourceHit;
    private ResourceFile? _selectedResourceFile;
    private bool _resourceBusy;

    private ResourceCatalog ResourceSvc
    {
        get
        {
            _catalog ??= new ResourceCatalog();

            // 来源按设置走：自定义镜像（国内常用自建反代）失败时回退官方，
            // 避免"换台设备就搜不到模组"。
            var custom = Config.ResourceSource == "custom" && !string.IsNullOrWhiteSpace(Config.ResourceMirror);
            _catalog.BaseUrl = custom ? Config.ResourceMirror.Trim() : ResourceCatalog.OfficialApi;
            return _catalog;
        }
    }

    public ObservableCollection<string> ResourceKinds { get; } = ["模组", "整合包", "资源包", "光影"];
    public ObservableCollection<string> ResourceGameVersions { get; } = [];
    public ObservableCollection<string> ResourceLoaders { get; } = ["（不限）", "fabric", "forge", "neoforge", "quilt"];
    public ObservableCollection<ResourceHitVm> ResourceHits { get; } = [];
    public ObservableCollection<ResourceFile> ResourceFiles { get; } = [];

    public string ResourceKindName
    {
        get => _resourceKindName;
        set
        {
            if (!Set(ref _resourceKindName, value)) return;
            Raise(nameof(ResourceFolderHint));
            ResourceHits.Clear();
            ResourceFiles.Clear();
        }
    }

    public string ResourceQuery
    {
        get => _resourceQuery;
        set => Set(ref _resourceQuery, value);
    }

    public string ResourceGameVersion
    {
        get => _resourceGameVersion;
        set => Set(ref _resourceGameVersion, value);
    }

    public string ResourceLoader
    {
        get => _resourceLoader;
        set => Set(ref _resourceLoader, value);
    }

    public string ResourceStatus
    {
        get => _resourceStatus;
        private set => Set(ref _resourceStatus, value);
    }

    public ResourceHitVm? SelectedResourceHit
    {
        get => _selectedResourceHit;
        set
        {
            if (!Set(ref _selectedResourceHit, value)) return;
            Raise(nameof(ResourceDetail));
            if (value is not null)
            {
                value.EnsureIcon();
                _ = LoadResourceFilesAsync(value.Hit);
            }
        }
    }

    public ResourceFile? SelectedResourceFile
    {
        get => _selectedResourceFile;
        set => Set(ref _selectedResourceFile, value);
    }

    public bool ResourceBusy
    {
        get => _resourceBusy;
        private set => Set(ref _resourceBusy, value);
    }

    public string ResourceFolderHint
    {
        get
        {
            var kind = CurrentResourceKind;
            return kind == ResourceKind.Modpack
                ? "整合包会下载 .mrpack 并安装，可选择同时把内容复制进当前实例"
                : $"文件会下载到当前实例的 {kind.ToInstanceFolder()} 目录";
        }
    }

    public string ResourceDetail => _selectedResourceHit is null
        ? "选择搜索结果查看详情；左侧图标可以直接看出是模组、整合包、资源包还是光影。"
        : $"{_selectedResourceHit.Title}\\n{_selectedResourceHit.Hit.Description}\\n\\n" +
          $"类型: {_selectedResourceHit.KindBadge}    作者: {_selectedResourceHit.Hit.Author}    " +
          $"下载量: {_selectedResourceHit.Hit.DownloadsText}    关注: {_selectedResourceHit.Hit.Follows}\\n" +
          $"支持版本: {string.Join(", ", _selectedResourceHit.Hit.GameVersions)}\\n" +
          $"分类: {string.Join(", ", _selectedResourceHit.Hit.Categories)}\\n" +
          $"页面: {_selectedResourceHit.Hit.PageUrl}";

    private ResourceKind CurrentResourceKind => _resourceKindName switch
    {
        "整合包" => ResourceKind.Modpack,
        "资源包" => ResourceKind.ResourcePack,
        "光影" => ResourceKind.Shader,
        _ => ResourceKind.Mod
    };

    /// <summary>
    /// Fills the game version dropdown shared by 资源下载 and 模组在线下载.
    ///
    /// The list is no longer limited to the installed instances: it carries every
    /// version from the manifest, ordered as 实例 → 正式版（新→旧）→ 快照 → 旧版, so any
    /// Minecraft version can be targeted without installing it first.
    /// </summary>
    public void RefreshResourceVersions()
    {
        var expected = RemoteVersions.Count;
        if (ResourceGameVersions.Count > 0 && _gameVersionsFilledFor == expected) return;
        _gameVersionsFilledFor = expected;

        var chosen = ResourceGameVersion;
        ResourceGameVersions.Clear();

        foreach (var version in VersionChoices.Select(c => c.Id).Distinct())
        {
            var parsed = ModpackReader.ParseVersionId(version);
            var game = parsed.Game.Length > 0 ? parsed.Game : version;
            if (game.Length > 0 && !ResourceGameVersions.Contains(game)) ResourceGameVersions.Add(game);
        }

        foreach (var row in RemoteVersions.Where(r => r.IsRelease))
        {
            if (!ResourceGameVersions.Contains(row.Id)) ResourceGameVersions.Add(row.Id);
        }

        foreach (var row in RemoteVersions.Where(r => r.IsSnapshot))
        {
            if (!ResourceGameVersions.Contains(row.Id)) ResourceGameVersions.Add(row.Id);
        }

        foreach (var row in RemoteVersions.Where(r => r.IsLegacy))
        {
            if (!ResourceGameVersions.Contains(row.Id)) ResourceGameVersions.Add(row.Id);
        }

        if (chosen.Length > 0 && ResourceGameVersions.Contains(chosen)) ResourceGameVersion = chosen;
        else if (ResourceGameVersions.Count > 0 && ResourceGameVersion.Length == 0)
            ResourceGameVersion = ResourceGameVersions[0];

        var previous = ModGameVersion;
        ModGameVersions.Clear();
        ModGameVersions.Add("（跟随实例）");
        foreach (var version in ResourceGameVersions) ModGameVersions.Add(version);
        ModGameVersion = ModGameVersions.Contains(previous) ? previous : "（跟随实例）";

        Raise(nameof(ResourceVersionHint));
        Raise(nameof(ModSearchScopeText));
    }

    private int _gameVersionsFilledFor = -1;

    /// <summary>Shown next to the version dropdown so the list size is obvious.</summary>
    public string ResourceVersionHint =>
        $"可选游戏版本 {ResourceGameVersions.Count} 个（全部正式版 / 快照 / 旧版）";
    /// <summary>Searches the catalog (Chinese queries are translated first).</summary>
    private async Task SearchResourcesAsync()
    {
        if (ResourceBusy) return;
        ResourceBusy = true;
        ResourceStatus = "正在搜索…";

        try
        {
            var kind = CurrentResourceKind;
            var loader = _resourceLoader is "（不限）" or "" ? null : _resourceLoader;
            var hits = await ResourceSvc.SearchAsync(kind, ResourceQuery, _resourceGameVersion, loader, 40)
                .ConfigureAwait(false);

            Ui.Post(() =>
            {
                ResourceHits.Clear();
                ResourceFiles.Clear();
                foreach (var hit in hits) ResourceHits.Add(new ResourceHitVm(hit));

                ResourceStatus = hits.Count == 0
                    ? "没有结果：换关键词，或把游戏版本/装载器改成「不限」"
                    : $"找到 {hits.Count} 个{kind.ToChinese()}（中文名表 {ResourceSvc.Chinese.Count} 条）" +
                      (ResourceSvc.Chinese.ContainsChinese(ResourceQuery) ? "，已按中文名翻译检索" : "");
                foreach (var row in ResourceHits) row.EnsureIcon();
                SelectedResourceHit = ResourceHits.FirstOrDefault();
            });
        }
        catch (Exception e)
        {
            ResourceStatus = "搜索失败: " + e.Message;
        }
        finally
        {
            ResourceBusy = false;
        }
    }

    private async Task LoadResourceFilesAsync(ResourceHit hit)
    {
        try
        {
            var loader = _resourceLoader is "（不限）" or "" ? null : _resourceLoader;
            var files = await ResourceSvc.GetFilesAsync(hit.Slug, _resourceGameVersion, loader).ConfigureAwait(false);

            Ui.Post(() =>
            {
                ResourceFiles.Clear();
                foreach (var file in files.Take(30)) ResourceFiles.Add(file);
                SelectedResourceFile = ResourceFiles.FirstOrDefault();
                ResourceStatus = files.Count == 0
                    ? $"{hit.Title} 没有匹配 {_resourceGameVersion} / {_resourceLoader} 的文件"
                    : $"{hit.Title}: {ResourceFiles.Count} 个可用文件";
            });
        }
        catch (Exception e)
        {
            ResourceStatus = "读取文件列表失败: " + e.Message;
        }
    }

    /// <summary>Downloads the selected file into the active instance.</summary>
    private async Task DownloadResourceAsync()
    {
        var hit = _selectedResourceHit?.Hit;
        var file = _selectedResourceFile ?? ResourceFiles.FirstOrDefault();
        if (hit is null || file is null)
        {
            ResourceStatus = "先搜索并选中一个项目与文件";
            return;
        }

        var versionId = ActiveChoice?.Id ?? Config.ActiveInstance;
        if (string.IsNullOrEmpty(versionId))
        {
            ResourceStatus = "没有选中的实例：先在「版本」页选择一个版本";
            return;
        }

        var gameDir = Config.ResolveGameDirectory(versionId)
                      ?? Path.Combine(_launcher.Paths.Root, "versions", versionId);

        ResourceBusy = true;
        try
        {
            if (hit.Kind == ResourceKind.Modpack)
            {
                var cache = Path.Combine(_launcher.Paths.Root, "modpack-cache");
                var (ok, path, message) = await ResourceSvc.DownloadToCacheAsync(file, cache).ConfigureAwait(false);
                if (!ok)
                {
                    ResourceStatus = message;
                    return;
                }

                var pack = ModpackManager.Inspect(path);
                if (pack is null)
                {
                    ResourceStatus = "下载的文件无法识别为整合包";
                    return;
                }

                var progress = new Progress<string>(line => LogService.Info("  " + line, "Resource"));
                var installed = await ModpackSvc.InstallAsync(pack, progress).ConfigureAwait(false);

                Ui.Post(() =>
                {
                    RefreshModpacks();
                    ResourceStatus = $"已安装整合包: {installed.Name}（模组 {installed.ModCount} 个）";
                    LogService.Ok($"资源下载中心: 整合包 {installed.Name} 安装完成", "Resource");
                });

                return;
            }

            var report = new Progress<double>(_ => { });
            var result = await ResourceSvc.InstallFileAsync(hit.Kind, file, gameDir, report).ConfigureAwait(false);

            Ui.Post(() =>
            {
                ResourceStatus = result.Message;
                if (result.Ok)
                {
                    LogService.Ok($"资源下载中心: {result.Message}（实例 {versionId}）", "Resource");
                    if (hit.Kind == ResourceKind.Mod) _ = RefreshModsAsync();
                }
                else
                {
                    LogService.Error("资源下载中心: " + result.Message, "Resource");
                }
            });
        }
        catch (Exception e)
        {
            ResourceStatus = "下载失败: " + e.Message;
        }
        finally
        {
            ResourceBusy = false;
        }
    }

    // ---------------------------------------- 模组在线下载：版本 / 装载器选择

    public ObservableCollection<string> ModGameVersions { get; } = ["（跟随实例）"];
    public ObservableCollection<string> ModLoaders { get; } =
        ["（跟随实例）", "Fabric", "Forge", "NeoForge", "Quilt", "Vanilla", "任意"];

    private string _modGameVersion = "（跟随实例）";
    private string _modLoaderChoice = "（跟随实例）";

    /// <summary>Target Minecraft version of the Modrinth search (default: the instance).</summary>
    public string ModGameVersion
    {
        get => _modGameVersion;
        set
        {
            if (!Set(ref _modGameVersion, value)) return;
            Raise(nameof(ModSearchScopeText));
        }
    }

    /// <summary>Target mod loader of the Modrinth search (default: the instance).</summary>
    public string ModLoaderChoice
    {
        get => _modLoaderChoice;
        set
        {
            if (!Set(ref _modLoaderChoice, value)) return;
            Raise(nameof(ModSearchScopeText));
        }
    }

    public string ModSearchScopeText
    {
        get
        {
            var game = _modGameVersion == "（跟随实例）"
                ? (SelectedVersion?.Id ?? "未选择实例")
                : _modGameVersion;
            var loader = _modLoaderChoice == "（跟随实例）"
                ? (SelectedVersion?.Loader ?? "原版")
                : _modLoaderChoice;
            return $"搜索范围：Minecraft {game} · {loader}";
        }
    }
    // ------------------------------------------- 自定义 JVM 参数（可一键恢复）

    private string _jvmArgsText = "";
    private string _jvmArgsSaved = "";
    private bool _jvmArgsLoading;
    private string _jvmArgsStatus = "";
    private string _jvmArgsPreview = "";
    private string _jvmArgsWarnings = "";
    private string _jvmPresetName = "";

    public ObservableCollection<string> JvmPresets { get; } =
    [
        "（选择预设…）",
        "默认（G1 + 适中内存）",
        "低配机器（省内存）",
        "G1 低延迟调优",
        "ZGC 低延迟（Java 15+）",
        "Shenandoah（Java 12+）",
        "调试与日志（-Dfile.encoding=UTF-8 等）",
        "清理（清空自定义参数）"
    ];

    public string JvmPresetName
    {
        get => _jvmPresetName;
        set
        {
            if (!Set(ref _jvmPresetName, value)) return;
            if (value.Length > 0 && value != "（选择预设…）") ApplyJvmPreset(value);
        }
    }

    /// <summary>Multi-line editor content (per instance, inherits the global value).</summary>
    public string JvmArgsText
    {
        get => _jvmArgsText;
        set
        {
            if (!Set(ref _jvmArgsText, value)) return;
            if (_jvmArgsLoading) return;

            Current.ExtraJvmArgs = value.Length == 0 ? null : value;
            InstanceChanged();
            UpdateJvmArgsInfo();
        }
    }

    public bool JvmArgsDirty => !string.Equals(_jvmArgsText, _jvmArgsSaved, StringComparison.Ordinal);

    public string JvmArgsStatus
    {
        get => _jvmArgsStatus;
        private set => Set(ref _jvmArgsStatus, value);
    }

    /// <summary>Final JVM argument list after the merge with the launcher's own args.</summary>
    public string JvmArgsPreview
    {
        get => _jvmArgsPreview;
        private set => Set(ref _jvmArgsPreview, value);
    }

    public string JvmArgsWarnings
    {
        get => _jvmArgsWarnings;
        private set => Set(ref _jvmArgsWarnings, value);
    }

    /// <summary>Loads the effective value into the editor (on instance change).</summary>
    private void ReloadJvmArgs()
    {
        _jvmArgsLoading = true;
        _jvmArgsText = Current.ExtraJvmArgs ?? Config.ExtraJvmArgs ?? "";
        _jvmArgsSaved = _jvmArgsText;
        Raise(nameof(JvmArgsText));
        Raise(nameof(JvmArgsDirty));
        _jvmArgsLoading = false;
        UpdateJvmArgsInfo();
    }

    /// <summary>Recomputes the preview, warnings and status line.</summary>
    private void UpdateJvmArgsInfo()
    {
        var arguments = JvmArguments.Parse(_jvmArgsText);
        var warnings = JvmArguments.Validate(_jvmArgsText);
        JvmArgsWarnings = warnings.Count == 0 ? "" : string.Join(Environment.NewLine, warnings.Select(w => "⚠ " + w));

        var effective = new TiaMc.Core.Launch.LaunchOptions
        {
            MinMemoryMb = 0,
            MaxMemoryMb = MaxMemoryMb,
            GcMode = GcMode,
            ExtraJvmArgs = _jvmArgsText
        };

        var managed = new List<string> { $"-Xmx{MaxMemoryMb}m" };
        switch (GcMode)
        {
            case "G1GC": managed.Add("-XX:+UseG1GC"); break;
            case "ZGC": managed.Add("-XX:+UseZGC"); break;
            case "ShenandoahGC": managed.Add("-XX:+UseShenandoahGC"); break;
            case "SerialGC": managed.Add("-XX:+UseSerialGC"); break;
            case "ParallelGC": managed.Add("-XX:+UseParallelGC"); break;
        }

        managed.Add("-Dfile.encoding=UTF-8");
        var (merged, replaced) = JvmArguments.Merge(managed, arguments);
        JvmArgsPreview = JvmArguments.Format(merged);

        JvmArgsStatus = (Current.ExtraJvmArgs is null
                            ? $"继承全局默认：{(Config.ExtraJvmArgs.Length == 0 ? "（无自定义参数）" : Config.ExtraJvmArgs)}"
                            : $"本实例自定义 {arguments.Count} 个参数") +
                        (replaced.Count > 0 ? $"    已覆盖启动器参数：{string.Join(" ", replaced)}" : "") +
                        (JvmArgsDirty ? "    ● 未保存到配置" : "");

        Raise(nameof(JvmArgsDirty));
    }

    /// <summary>保存当前编辑（写入本实例覆盖）。</summary>
    public void SaveJvmArgs()
    {
        Current.ExtraJvmArgs = _jvmArgsText.Length == 0 ? null : _jvmArgsText;
        _jvmArgsSaved = _jvmArgsText;
        Config.Save();
        InstanceChanged();
        UpdateJvmArgsInfo();
        LogService.Ok($"已保存本实例 JVM 参数（{JvmArguments.Parse(_jvmArgsText).Count} 个）", "JVM");
    }

    /// <summary>撤销本次修改，回到上次保存的值。</summary>
    public void UndoJvmArgs()
    {
        _jvmArgsLoading = true;
        JvmArgsText = _jvmArgsSaved;
        _jvmArgsLoading = false;
        Current.ExtraJvmArgs = _jvmArgsSaved.Length == 0 ? null : _jvmArgsSaved;
        InstanceChanged();
        UpdateJvmArgsInfo();
        LogService.Info("已撤销 JVM 参数修改，恢复到上次保存的值", "JVM");
    }

    /// <summary>一键恢复默认：清空自定义参数（本实例覆盖也一并清掉）。</summary>
    public void ResetJvmArgs()
    {
        _jvmArgsLoading = true;
        JvmArgsText = "";
        _jvmArgsLoading = false;
        Current.ExtraJvmArgs = null;
        _jvmArgsSaved = "";
        Config.Save();
        InstanceChanged();
        UpdateJvmArgsInfo();
        LogService.Ok("JVM 参数已恢复默认（清空自定义参数，回到启动器托管的 -Xmx / GC 等设置）", "JVM");
    }

    /// <summary>恢复继承：删掉本实例覆盖，改用全局默认参数。</summary>
    public void InheritJvmArgs()
    {
        Current.ExtraJvmArgs = null;
        Config.Save();
        InstanceChanged();
        ReloadJvmArgs();
        LogService.Info("本实例 JVM 参数已恢复继承全局默认", "JVM");
    }

    /// <summary>把当前编辑内容写入全局默认。</summary>
    public void ApplyJvmArgsAsGlobal()
    {
        Config.ExtraJvmArgs = _jvmArgsText;
        Current.ExtraJvmArgs = null;
        Config.Save();
        InstanceChanged();
        ReloadJvmArgs();
        LogService.Ok("已把当前 JVM 参数设为全局默认（本实例覆盖已清除）", "JVM");
    }

    /// <summary>写入一套常用参数。</summary>
    private void ApplyJvmPreset(string name)
    {
        var args = name switch
        {
            "默认（G1 + 适中内存）" => "-XX:+UseG1GC\n-XX:MaxGCPauseMillis=50",
            "低配机器（省内存）" =>
                "-XX:+UseSerialGC\n-XX:MaxMetaspaceSize=256m\n-XX:MaxDirectMemorySize=512m\n-Dfile.encoding=UTF-8",
            "G1 低延迟调优" =>
                "-XX:+UseG1GC\n-XX:MaxGCPauseMillis=37\n-XX:G1NewSizePercent=20\n-XX:G1HeapRegionSize=32M\n-XX:MaxMetaspaceSize=512m\n-XX:+ParallelRefProcEnabled",
            "ZGC 低延迟（Java 15+）" => "-XX:+UseZGC\n-XX:+UnlockExperimentalVMOptions\n-XX:ZCollectionInterval=120",
            "Shenandoah（Java 12+）" => "-XX:+UseShenandoahGC\n-XX:ShenandoahGCMode=iu\n-XX:+UnlockExperimentalVMOptions",
            "调试与日志（-Dfile.encoding=UTF-8 等）" =>
                "-Dfile.encoding=UTF-8\n-Dsun.jnu.encoding=UTF-8\n-XX:+HeapDumpOnOutOfMemoryError\n-XX:HeapDumpPath=crash-dumps",
            "清理（清空自定义参数）" => "",
            _ => _jvmArgsText
        };

        if (name == "清理（清空自定义参数）")
        {
            ResetJvmArgs();
            return;
        }

        _jvmArgsLoading = true;
        JvmArgsText = args;
        _jvmArgsLoading = false;
        Current.ExtraJvmArgs = args;
        InstanceChanged();
        UpdateJvmArgsInfo();
        LogService.Info($"已套用 JVM 参数预设：{name}（点「保存」或切换实例后生效，可随时撤销）", "JVM");
    }
    // ---------------------------------------- 皮肤实时预览（改颜色/模板立刻更新）

    private System.Windows.Media.ImageSource? _skin3dTexture;
    private System.Windows.Media.ImageSource? _skin3dRender;
    private string _skin3dAngle = "正面";

    /// <summary>在线 3D 材质渲染图（皮肤站那种全身/多角度视图）。</summary>
    public System.Windows.Media.ImageSource? Skin3DRender
    {
        get => _skin3dRender;
        private set { if (Set(ref _skin3dRender, value)) Raise(); }
    }

    public ObservableCollection<string> Skin3DAngles { get; } = ["正面", "左面", "右面", "后面"];

    public string Skin3DAngle
    {
        get => _skin3dAngle;
        set
        {
            if (!Set(ref _skin3dAngle, value)) return;
            Raise();
            _ = RefreshSkin3DRenderAsync();
        }
    }

    /// <summary>按当前账户名拉一张在线 3D 渲染图（缓存到本地，失败就静默保留 2D 视图）。</summary>
    private async Task RefreshSkin3DRenderAsync()
    {
        try
        {
            var name = SelectedAccount?.Name;
            if (string.IsNullOrWhiteSpace(name)) return;

            var angle = _skin3dAngle switch
            {
                "左面" => "left",
                "右面" => "right",
                "后面" => "back",
                _ => "front"
            };

            var result = await Services.SkinTextureService.FetchRenderAsync(name, angle).ConfigureAwait(false);
            if (result is null) return;
            Ui.Post(() => Skin3DRender = result);
        }
        catch (Exception)
        {
            // 在线渲染只是锦上添花，失败不影响 2D/3D 本地视图
        }
    }

    private string _skin3dInput = "";
    private string _skin3dStatus = "3D 预览：点「获取 3D 预览」用当前账户的皮肤，或填皮肤站名称 / 图片链接。";

    /// <summary>3D 预览用的整张皮肤贴图。</summary>
    public System.Windows.Media.ImageSource? Skin3DTexture
    {
        get => _skin3dTexture;
        private set { if (Set(ref _skin3dTexture, value)) Raise(); }
    }

    /// <summary>3D 预览来源输入：皮肤站名称 / UUID / 图片链接；留空表示用当前账户的皮肤。</summary>
    public string Skin3DInput
    {
        get => _skin3dInput;
        set
        {
            if (!Set(ref _skin3dInput, value)) return;
            Raise();

            // 自动获取：输入变化后 700ms 内没有继续输入就去取预览（不用点按钮）
            _skin3dDebounce ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(700)
            };
            _skin3dDebounce.Stop();
            _skin3dDebounce.Tick -= Skin3DDebounceTick;
            _skin3dDebounce.Tick += Skin3DDebounceTick;
            _skin3dDebounce.Start();
        }
    }

    private System.Windows.Threading.DispatcherTimer? _skin3dDebounce;

    private void Skin3DDebounceTick(object? sender, EventArgs e)
    {
        _skin3dDebounce?.Stop();
        _ = PreviewSkin3DAsync();
    }

    public string Skin3DStatus
    {
        get => _skin3dStatus;
        private set { if (Set(ref _skin3dStatus, value)) Raise(); }
    }

    public ICommand PreviewSkin3DCommand { get; }

    /// <summary>皮肤页被打开时调用：重新取一次在线渲染图和本地 3D 贴图。</summary>
    public void RefreshSkinPreviewOnShow()
    {
        _ = PreviewSkin3DAsync();
        _ = RefreshSkin3DRenderAsync();
    }

    /// <summary>刷新 3D 预览：留空用账户皮肤；填了就用皮肤站/链接。</summary>
    private async Task PreviewSkin3DAsync()
    {
        try
        {
            var input = (Skin3DInput ?? "").Trim();
            Services.SkinTextureService.TextureResult result;
            var accountForPreview = SelectedAccount;

            if (input.Length == 0)
            {
                result = await Services.SkinTextureService.FromAccountAsync(accountForPreview).ConfigureAwait(false);
            }
            else if (File.Exists(input))          // 本地皮肤文件（自己选的 / 模板生成的）
            {
                var local = Services.SkinTextureService.FromFile(input);
                result = local is null
                    ? new Services.SkinTextureService.TextureResult(false, "本地文件读不出来: " + input, null, input)
                    : new Services.SkinTextureService.TextureResult(true, "本地文件 " + Path.GetFileName(input), local, input);
            }
            else if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                result = await Services.SkinTextureService.FromUrlAsync(input).ConfigureAwait(false);
            }
            else
            {
                var site = TiaMc.Core.Utils.SkinSites.Presets.FirstOrDefault(s => s.Name == SkinSite)
                           ?? TiaMc.Core.Utils.SkinSites.Presets.FirstOrDefault();
                result = site is null
                    ? new Services.SkinTextureService.TextureResult(false, "没有可用的皮肤站", null, "")
                    : await Services.SkinTextureService.FromSiteAsync(site, input).ConfigureAwait(false);
            }

            Ui.Post(() =>
            {
                if (result.Ok && result.Texture is not null)
                {
                    Skin3DTexture = result.Texture;
                    Skin3DStatus = result.Message.Contains("回退") || result.Message.Contains("本地")
                        ? "预览已加载：" + result.Message
                        : "预览已加载：" + result.Message;
                    _ = RefreshSkin3DRenderAsync();
                    LogService.Ok("皮肤预览: " + result.Message, "Skin");

                    // 2D 各种视角（前/后/左/右/头部）也换成这张皮肤
                    try
                    {
                        var png = EncodePng(result.Texture);
                        if (png is not null)
                        {
                            SkinPreviewHead = Services.SkinPreview.Head(png);
                            SkinPreviewBody = Services.SkinPreview.Body(png, ViewOf(_skinView), _skinZoom);
                        }
                    }
                    catch (Exception e2)
                    {
                        LogService.Warn("2D 预览刷新失败: " + e2.Message, "Skin");
                    }
                }
                else
                {
                    Skin3DStatus = "3D 预览失败：" + result.Message;
                    LogService.Warn("3D 皮肤预览失败: " + result.Message, "Skin");
                }
            });
        }
        catch (Exception e)
        {
            Ui.Post(() => Skin3DStatus = "3D 预览出错：" + e.Message);
        }
    }

    private System.Windows.Media.ImageSource? _skinPreviewBody;
    private System.Windows.Media.ImageSource? _skinPreviewHead;
    private string _skinView = "前面";
    private int _skinZoom = 6;

    /// <summary>可选择查看的视角：前面 / 后面 / 左面 / 右面 / 头部。</summary>
    public ObservableCollection<string> SkinViews { get; } = ["前面", "后面", "左面", "右面", "头部"];

    public ObservableCollection<int> SkinZooms { get; } = [4, 6, 8, 12];

    public string SkinView
    {
        get => _skinView;
        set
        {
            if (!Set(ref _skinView, value)) return;
            RefreshDraftPreview();
        }
    }

    public int SkinZoom
    {
        get => _skinZoom;
        set
        {
            if (!Set(ref _skinZoom, value)) return;
            RefreshDraftPreview();
        }
    }

    private Services.SkinPreview.View ViewOf(string name) => name switch
    {
        "后面" => Services.SkinPreview.View.Back,
        "左面" => Services.SkinPreview.View.Left,
        "右面" => Services.SkinPreview.View.Right,
        "头部" => Services.SkinPreview.View.Head,
        _ => Services.SkinPreview.View.Front
    };

    /// <summary>Front view (head + body + limbs) of the draft or applied skin.</summary>
    public System.Windows.Media.ImageSource? SkinPreviewBody
    {
        get => _skinPreviewBody;
        private set => Set(ref _skinPreviewBody, value);
    }

    /// <summary>Face tile of the draft or applied skin.</summary>
    public System.Windows.Media.ImageSource? SkinPreviewHead
    {
        get => _skinPreviewHead;
        private set => Set(ref _skinPreviewHead, value);
    }

    /// <summary>
    /// Rebuilds the live preview. While the user edits the template and colours the
    /// preview shows the draft; otherwise it shows the skin stored on the account.
    /// </summary>
    public void RefreshSkinPreview()
    {
        try
        {
            byte[]? png = null;
            var account = SelectedAccount;

            // 1. the account's own skin (local file or downloaded)
            if (account?.HasLocalSkin == true)
            {
                try
                {
                    png = File.ReadAllBytes(account.SkinPath!);
                }
                catch (Exception)
                {
                    png = null;
                }
            }

            // 2. no skin yet (or the user is editing): render the draft template
            var draft = TiaMc.Core.Utils.SkinGenerator.Build(TemplateOf(_skinTemplate),
                _skinPrimary, _skinSecondary, account?.Name ?? "Player");

            if (png is null || account is null)
            {
                png = draft;
            }

            SkinPreviewHead = Services.SkinPreview.Head(png);
            SkinPreviewBody = Services.SkinPreview.Body(png, ViewOf(_skinView), _skinZoom);

            // The draft is what the "生成并应用" button writes; show it too so colour
            // changes are visible before applying.
            if (account?.HasLocalSkin != true)
            {
                SkinPreviewHead = Services.SkinPreview.Head(draft);
                SkinPreviewBody = Services.SkinPreview.Body(draft, ViewOf(_skinView), _skinZoom);
            }
        }
        catch (Exception e)
        {
            LogService.Warn("生成皮肤预览失败: " + e.Message, "Skin");
        }
    }

    /// <summary>Preview of the template + colours currently selected (draft).</summary>
    public void RefreshDraftPreview()
    {
        try
        {
            var png = TiaMc.Core.Utils.SkinGenerator.Build(TemplateOf(_skinTemplate),
                _skinPrimary, _skinSecondary, SelectedAccount?.Name ?? "Player");
            SkinPreviewHead = Services.SkinPreview.Head(png);
            SkinPreviewBody = Services.SkinPreview.Body(png, ViewOf(_skinView), _skinZoom);
        }
        catch (Exception)
        {
            // preview is cosmetic
        }
    }

    private static TiaMc.Core.Utils.SkinGenerator.Template TemplateOf(string name) => name switch
    {
        "条纹" => TiaMc.Core.Utils.SkinGenerator.Template.Stripes,
        "渐变" => TiaMc.Core.Utils.SkinGenerator.Template.Gradient,
        "棋盘格" => TiaMc.Core.Utils.SkinGenerator.Template.Checker,
        "按名字生成" => TiaMc.Core.Utils.SkinGenerator.Template.Named,
        _ => TiaMc.Core.Utils.SkinGenerator.Template.Solid
    };
    // ------------------------------------------- 外置登录（第三方皮肤站）

    private string _yggLoginServer = "https://littleskin.cn";
    private string _yggLoginEmail = "";
    private string _yggLoginPassword = "";
    private string _yggLoginStatus = "部分服务器要求使用皮肤站登录（外置登录），例如 LittleSkin / Blessing Skin。";
    private bool _yggLoginBusy;

    public ObservableCollection<string> AuthServers { get; } =
    [
        "https://littleskin.cn",
        "https://skin.mualliance.launcher",
        "（自定义地址…）"
    ];

    public string YggLoginServer
    {
        get => _yggLoginServer;
        set => Set(ref _yggLoginServer, value);
    }

    public string YggLoginEmail
    {
        get => _yggLoginEmail;
        set => Set(ref _yggLoginEmail, value);
    }

    public string YggLoginPassword
    {
        get => _yggLoginPassword;
        set => Set(ref _yggLoginPassword, value);
    }

    public string YggLoginStatus
    {
        get => _yggLoginStatus;
        private set => Set(ref _yggLoginStatus, value);
    }

    public bool YggLoginBusy
    {
        get => _yggLoginBusy;
        private set => Set(ref _yggLoginBusy, value);
    }

    /// <summary>Logs into a third party Yggdrasil server and stores the account.</summary>
    public async Task AddYggdrasilAccountAsync()
    {
        if (_yggLoginBusy) return;

        var server = _yggLoginServer.Trim();
        if (server.Length == 0 || server == "（自定义地址…）")
        {
            YggLoginStatus = "请填写皮肤站地址，例如 https://littleskin.cn";
            return;
        }

        if (_yggLoginEmail.Trim().Length == 0 || _yggLoginPassword.Length == 0)
        {
            YggLoginStatus = "请填写邮箱与密码（皮肤站的登录账号）";
            return;
        }

        YggLoginBusy = true;
        YggLoginStatus = $"正在登录 {server} …";
        LogService.User($"外置登录：{server} / {_yggLoginEmail}", "外置登录");

        try
        {
            var apiRoot = TiaMc.Core.Accounts.YggdrasilAuth.NormalizeServer(server);
            var (siteName, _) = await TiaMc.Core.Accounts.YggdrasilAuth.FetchMetadataAsync(apiRoot).ConfigureAwait(false);
            var result = await TiaMc.Core.Accounts.YggdrasilAuth
                .AuthenticateAsync(server, _yggLoginEmail.Trim(), _yggLoginPassword).ConfigureAwait(false);

            if (!result.Ok || result.Session is null)
            {
                Ui.Post(() =>
                {
                    YggLoginStatus = "登录失败：" + result.Message;
                    LogService.Error($"外置登录失败：{result.Message}", "外置登录");
                });
                return;
            }

            var session = result.Session;
            var (skin, cape) = await TiaMc.Core.Accounts.YggdrasilAuth
                .FetchTexturesAsync(server, session.Profile.Id).ConfigureAwait(false);

            var account = new MinecraftAccount
            {
                Kind = AccountKind.Yggdrasil,
                Name = session.Profile.Name,
                Uuid = session.Profile.Id,
                AccessToken = session.AccessToken,
                ClientToken = session.ClientToken,
                UserType = "mojang",
                AuthServer = session.ServerUrl,
                AuthServerName = string.IsNullOrWhiteSpace(siteName) ? server : siteName,
                LoginName = _yggLoginEmail.Trim(),
                SkinUrl = skin,
                CapeUrl = cape,
                LastLoginUtc = DateTime.UtcNow
            };

            _launcher.Accounts.AddOrUpdate(account);

            Ui.Post(() =>
            {
                RefreshAccounts();
                YggLoginStatus = $"登录成功：{account.Name}（{account.AuthServerName}）" +
                                 (skin is null ? " · 该角色还没有皮肤" : " · 已载入皮肤");
                _yggLoginPassword = "";
                Raise(nameof(YggLoginPassword));
                LogService.Ok($"外置登录成功：{account.Name} @ {account.AuthServerName}，" +
                              $"启动游戏时会自动挂载 authlib-injector", "外置登录");
            });
        }
        catch (Exception e)
        {
            Ui.Post(() =>
            {
                YggLoginStatus = "登录失败：" + e.Message;
                LogService.Error("外置登录失败: " + e.Message, "外置登录");
            });
        }
        finally
        {
            YggLoginBusy = false;
        }
    }

    /// <summary>Validates / refreshes the token of a 外置登录 account.</summary>
    public async Task RefreshYggdrasilAccountAsync()
    {
        var account = SelectedAccount;
        if (account is null || account.Kind != AccountKind.Yggdrasil || string.IsNullOrWhiteSpace(account.AuthServer))
        {
            LogService.Info("请选择一个外置登录账户", "外置登录");
            return;
        }

        var clientToken = account.ClientToken ?? Guid.NewGuid().ToString("N");
        var valid = await TiaMc.Core.Accounts.YggdrasilAuth
            .ValidateAsync(account.AuthServer!, account.AccessToken, clientToken).ConfigureAwait(false);

        if (valid)
        {
            LogService.Ok($"{account.Name} 的登录状态有效（{account.AuthServerName}）", "外置登录");
            return;
        }

        var result = await TiaMc.Core.Accounts.YggdrasilAuth
            .RefreshAsync(account.AuthServer!, account.AccessToken, clientToken).ConfigureAwait(false);

        if (!result.Ok || result.Session is null)
        {
            LogService.Error($"刷新失败：{result.Message}（可能需要重新输入密码登录）", "外置登录");
            return;
        }

        account.AccessToken = result.Session.AccessToken;
        account.ClientToken = result.Session.ClientToken;
        _launcher.Accounts.Save();
        LogService.Ok($"{account.Name} 的外置登录令牌已刷新", "外置登录");
    }

    /// <summary>Downloads authlib-injector when an 外置登录 account needs it.</summary>
    public async Task<string> EnsureAuthlibInjectorAsync()
    {
        var runtimeRoot = Path.Combine(_launcher.Paths.Root, "runtime");
        var existing = TiaMc.Core.Java.AuthlibInjector.Locate(_launcher.Paths.Root, runtimeRoot);
        if (existing.Length > 0) return existing;

        LogService.Info("首次使用外置登录，正在下载 authlib-injector…", "外置登录");
        var result = await TiaMc.Core.Java.AuthlibInjector
            .EnsureAsync(runtimeRoot, message => LogService.Info(message, "外置登录"))
            .ConfigureAwait(false);

        if (!result.Ok)
        {
            LogService.Error(result.Message, "外置登录");
            return "";
        }

        return result.Path;
    }
    // ------------------------------------------- 离线账户皮肤（本地 / 生成 / 皮肤站）

    private string _skinSite = "mc-heads.net";
    private string _skinQuery = "";
    private string _skinTemplate = "纯色";
    private string _skinPrimary = "#3B7FB5";
    private string _skinSecondary = "#1C1C22";
    private string _skinStatus = "选择账户后可更换皮肤";
    private int _skinRevision;

    public ObservableCollection<string> SkinSites { get; } = [];
    public ObservableCollection<string> SkinTemplates { get; } =
        ["纯色", "条纹", "渐变", "棋盘格", "按名字生成"];

    public ObservableCollection<string> SkinColors { get; } =
        ["#3B7FB5", "#2E7D32", "#C62828", "#6A1B9A", "#F9A825", "#00838F", "#4E342E", "#212121", "#EC407A", "#FFFFFF"];

    public string SkinSite
    {
        get => _skinSite;
        set => Set(ref _skinSite, value);
    }

    /// <summary>Player name, UUID or a direct image link.</summary>
    public string SkinQuery
    {
        get => _skinQuery;
        set => Set(ref _skinQuery, value);
    }

    public string SkinTemplate
    {
        get => _skinTemplate;
        set
        {
            if (!Set(ref _skinTemplate, value)) return;
            RefreshDraftPreview();
        }
    }

    public string SkinPrimary
    {
        get => _skinPrimary;
        set
        {
            if (!Set(ref _skinPrimary, value)) return;
            RefreshDraftPreview();
        }
    }

    public string SkinSecondary
    {
        get => _skinSecondary;
        set
        {
            if (!Set(ref _skinSecondary, value)) return;
            RefreshDraftPreview();
        }
    }

    public string SkinStatus
    {
        get => _skinStatus;
        private set => Set(ref _skinStatus, value);
    }

    /// <summary>Avatar source of the selected account (URL or local file).</summary>
    // 多源（正版 URL + 镜像 + 本地文件），由 SkinHeadConverter 逐个尝试
    public string? SkinAvatarSource => SelectedAccount?.SkinHeadUrls;

    /// <summary>切换账户后自动重新拉一次 3D 皮肤。</summary>
    /// <summary>本地皮肤变化（选文件 / 生成模板）后立刻刷新 3D 预览。</summary>
    /// <summary>把贴图重新编码成 PNG 字节，供 2D 预览合成各视角。</summary>
    private static byte[]? EncodePng(System.Windows.Media.ImageSource source)
    {
        if (source is not System.Windows.Media.Imaging.BitmapSource bitmap) return null;
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private void AutoRefreshSkin3DLocal()
    {
        Skin3DInput = "";
        _ = PreviewSkin3DAsync();
    }

    private void AutoRefreshSkin3D()
    {
        if (!string.IsNullOrWhiteSpace(Skin3DInput)) return;   // 用户自己指定来源时不覆盖
        _ = PreviewSkin3DAsync();
    }

    private static string SkinDirectory => Path.Combine(Services.AppConfig.ConfigDirectory, "skins");

    /// <summary>Applies local skin bytes to the selected account and refreshes the avatar.</summary>
    private void AttachSkin(byte[] png, string sourceText)
    {
        var account = SelectedAccount;
        if (account is null)
        {
            LogService.Warn("请先在列表中选择一个账户", "Skin");
            return;
        }

        var check = TiaMc.Core.Utils.SkinSites.ValidateSkin(png);
        if (!check.Ok)
        {
            SkinStatus = "皮肤无效：" + check.Message;
            LogService.Error($"皮肤文件无效：{check.Message}（{sourceText}）", "Skin");
            return;
        }

        var path = TiaMc.Core.Utils.SkinSites.SaveSkin(SkinDirectory, account.Key, png);
        account.SkinPath = path;
        account.SkinUrl = null;      // 离线账户优先用本地文件
        _launcher.Accounts.Save();
        _skinRevision++;
        RefreshAccounts();
        Raise(nameof(SkinAvatarSource));
        AutoRefreshSkin3DLocal();

        RefreshSkinPreview();
        SkinStatus = $"已应用皮肤：{sourceText}（{check.Message}）";
        LogService.Ok($"账户 {account.Name} 的皮肤已更新：{path}（来源 {sourceText}）", "Skin");
        PushSkinToServer(silent: true);
    }

    /// <summary>Picks a local PNG file.</summary>
    public void PickSkinFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择皮肤文件（64×64 或 64×32 PNG）",
            Filter = "Minecraft 皮肤 (*.png)|*.png|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;
        LogService.User($"选择皮肤文件 {dialog.FileName}", "Skin");

        try
        {
            AttachSkin(File.ReadAllBytes(dialog.FileName), Path.GetFileName(dialog.FileName));
        }
        catch (Exception e)
        {
            LogService.Error("读取皮肤文件失败: " + e.Message, "Skin");
        }
    }

    /// <summary>Generates a skin from the template + colours.</summary>
    public void GenerateSkin()
    {
        try
        {
            var name = SelectedAccount?.Name ?? "Player";
            var template = _skinTemplate switch
            {
                "条纹" => TiaMc.Core.Utils.SkinGenerator.Template.Stripes,
                "渐变" => TiaMc.Core.Utils.SkinGenerator.Template.Gradient,
                "棋盘格" => TiaMc.Core.Utils.SkinGenerator.Template.Checker,
                "按名字生成" => TiaMc.Core.Utils.SkinGenerator.Template.Named,
                _ => TiaMc.Core.Utils.SkinGenerator.Template.Solid
            };

            LogService.User($"生成皮肤（模板 {_skinTemplate}，主色 {_skinPrimary}）", "Skin");
            var png = template == TiaMc.Core.Utils.SkinGenerator.Template.Named
                ? TiaMc.Core.Utils.SkinGenerator.BuildFromName(name)
                : TiaMc.Core.Utils.SkinGenerator.Build(template, _skinPrimary, _skinSecondary, name);

            AttachSkin(png, $"模板 {_skinTemplate} {_skinPrimary}");
        }
        catch (Exception e)
        {
            LogService.Error("生成皮肤失败: " + e.Message, "Skin");
        }
    }

    /// <summary>Random skin (name based, deterministic colours).</summary>
    public void RandomSkin()
    {
        var random = new Random();
        _skinPrimary = SkinColors[random.Next(SkinColors.Count)];
        _skinSecondary = SkinColors[random.Next(SkinColors.Count)];
        _skinTemplate = SkinTemplates[random.Next(3)];
        Raise(nameof(SkinPrimary));
        Raise(nameof(SkinSecondary));
        Raise(nameof(SkinTemplate));
        GenerateSkin();
    }

    /// <summary>Downloads a skin from the chosen external skin site.</summary>
    public async Task FetchSkinFromSiteAsync()
    {
        var siteName = _skinSite;
        var site = TiaMc.Core.Utils.SkinSites.Presets.FirstOrDefault(s => s.Name == siteName)
                   ?? TiaMc.Core.Utils.SkinSites.Presets[0];

        var query = _skinQuery.Trim();
        if (query.Length == 0) query = SelectedAccount?.Name ?? "";
        if (query.Length == 0)
        {
            SkinStatus = "请填写玩家名 / UUID / 图片链接";
            return;
        }

        SkinStatus = $"正在从 {site.Name} 获取 {query} …";
        LogService.User($"从皮肤站获取皮肤：{site.Name} / {query}", "Skin");

        var result = await TiaMc.Core.Utils.SkinSites.FetchAsync(site, query).ConfigureAwait(false);

        Ui.Post(() =>
        {
            if (result.Ok && result.Png is not null)
            {
                AttachSkin(result.Png, $"{site.Name} · {query}");
            }
            else
            {
                SkinStatus = "获取失败：" + result.Message;
                LogService.Warn($"从 {site.Name} 获取皮肤失败：{result.Message}", "Skin");
            }
        });
    }

    /// <summary>Removes the local skin of the selected account.</summary>
    public void ClearSkin()
    {
        var account = SelectedAccount;
        if (account is null) return;

        try
        {
            if (account.HasLocalSkin) File.Delete(account.SkinPath!);
        }
        catch (Exception)
        {
            // ignore
        }

        account.SkinPath = null;
        account.SkinUrl = null;
        _launcher.Accounts.Save();
        _skinRevision++;
        RefreshAccounts();
        Raise(nameof(SkinAvatarSource));
        AutoRefreshSkin3DLocal();
        RefreshSkinPreview();
        SkinStatus = "已清除皮肤（回到默认外观）";
        LogService.User($"清除账户 {account.Name} 的皮肤", "Skin");
    }

    /// <summary>Uploads the skin to the built-in Yggdrasil server (in-game skin).</summary>
    public void PushSkinToServer(bool silent = false)
    {
        var account = SelectedAccount;
        if (account is null || !account.HasLocalSkin) return;
        if (!YggServerOn)
        {
            if (!silent) LogService.Info("认证服务端未开启，皮肤只在启动器里显示；开启服务端后会自动同步", "Skin");
            return;
        }

        var result = _yggServer.SetSkin(account.Name, account.SkinPath!, account.SkinModel);
        if (result.Ok)
        {
            SkinStatus = "已同步到本地认证服务端（游戏内可见）";
            LogService.Ok($"皮肤已同步到认证服务端：{account.Name} → {Path.GetFileName(account.SkinPath!)}", "Skin");
        }
        else if (!silent)
        {
            LogService.Warn("同步皮肤失败: " + result.Message, "Skin");
        }
    }
    // ------------------------------------------- Java 缺失自动补齐

    /// <summary>Config switch (default on): download the JRE a version needs.</summary>
    public bool AutoProvisionJava
    {
        get => Config.AutoProvisionJava;
        set
        {
            if (Config.AutoProvisionJava == value) return;
            Config.AutoProvisionJava = value;
            Config.Save();
            Raise();
            Raise(nameof(JavaProvisionStatus));
            LogService.Info(value ? "已开启「缺失 Java 自动补齐」" : "已关闭「缺失 Java 自动补齐」", "Java");
        }
    }

    private bool _javaProvisioning;

    public string JavaProvisionStatus
    {
        get
        {
            var required = ActiveChoice is null
                ? 0
                : TiaMc.Core.Java.JavaRuntimeInstaller.RequiredMajor(
                    _launcher.Installed.FirstOrDefault(i => i.Id == ActiveChoice.Id)?.Json.JavaVersion?.MajorVersion,
                    ActiveChoice.Id);
            if (required == 0) return "未选择实例";

            var satisfied = _launcher.JavaRuntimes.Any(j => j.MajorVersion >= required);
            var text = satisfied
                ? $"实例需要 Java {required}+：已满足"
                : $"实例需要 Java {required}+：缺失" + (Config.AutoProvisionJava ? "（启动时自动下载）" : "（自动补齐已关闭）");

            if (_javaProvisioning) text += "    正在下载…";
            return text;
        }
    }

    /// <summary>One click: download the required runtime now.</summary>
    public async Task ProvisionJavaAsync()
    {
        var instance = ActiveChoice is null
            ? null
            : _launcher.Installed.FirstOrDefault(i => i.Id == ActiveChoice.Id);
        if (instance is null)
        {
            LogService.Warn("请先在项目树中选择一个实例", "Java");
            return;
        }

        if (_javaProvisioning) return;
        _javaProvisioning = true;
        Raise(nameof(JavaProvisionStatus));

        try
        {
            var java = await _launcher.EnsureJavaAsync(instance).ConfigureAwait(false);
            Ui.Post(() =>
            {
                RefreshJavaChoices();
                Raise(nameof(JavaProvisionStatus));
                if (java is not null) LogService.Ok($"可用运行时: {java.ShortDisplay}", "Java");
            });
        }
        finally
        {
            _javaProvisioning = false;
            Ui.Post(() => Raise(nameof(JavaProvisionStatus)));
        }
    }
    // ------------------------------------------------------- 权限（管理员）

    /// <summary>管理员状态文本，显示在内存回收与日志区。</summary>
    public string ElevationText => Services.Elevation.StatusText +
        "    日志目录: " + LogService.LogDirectory +
        (Services.LogExporter.IsWritable(LogService.LogDirectory) ? "（可写）" : "（只读，导出会自动回退到可写目录）");

    public bool IsElevated => Services.Elevation.IsElevated;

    /// <summary>一键以管理员身份重启（会弹 UAC，当前实例会退出）。</summary>
    public void RestartElevated()
    {
        LogService.User("请求以管理员身份重启", "权限");
        if (Services.Elevation.RestartElevated())
        {
            LogService.Info("已启动提权实例，当前窗口即将关闭…", "权限");
            System.Windows.Application.Current?.Shutdown();
        }
        else
        {
            LogService.Info(IsElevated ? "当前已是管理员权限" : "未获得管理员权限（用户取消）", "权限");
        }
    }
    public string ModpackKindText => _selectedModpack is null
        ? "未选择整合包"
        : _selectedModpack.KindText;

    public string ModpackStartText => _serverRunning ? "服务端运行中" : "服务端未运行";

    public string ModpackSummary =>
        $"共 {ModpackList.Count} 个整合包  ·  客户端 {ModpackList.Count(p => !p.IsServer)}  ·  " +
        $"服务端 {ModpackList.Count(p => p.IsServer)}";

    public string ModpacksPath => $"客户端: {ModpackSvc.ClientRoot}    服务端: {ModpackSvc.ServerRoot}";

    public string ModpackDetail
    {
        get
        {
            if (_selectedModpack is null) return "选择左边的一个整合包查看详情；服务端整合包在列表里以橙色高亮。";
            var pack = _selectedModpack;
            var lines = new List<string>
            {
                $"名称: {pack.Name}",
                $"类型: {pack.KindText}{(pack.IsServer ? "  ★ 橙色高亮" : "")}",
                $"版本: {(string.IsNullOrEmpty(pack.Version) ? "-" : pack.Version)}" +
                $"    游戏: {(string.IsNullOrEmpty(pack.GameVersion) ? "-" : pack.GameVersion)}" +
                $"    装载器: {(string.IsNullOrEmpty(pack.Loader) ? "-" : $"{pack.Loader} {pack.LoaderVersion}".Trim())}",
                $"模组: {pack.ModCount} 个    大小: {pack.SizeText}" +
                (pack.MissingFiles > 0 ? $"    缺失文件: {pack.MissingFiles}" : ""),
                $"格式: {pack.Format}    安装时间: {pack.InstalledUtc.ToLocalTime():yyyy-MM-dd HH:mm}",
                pack.InstanceText +(pack.GameDirectory.Length > 0 ? $"    游戏目录: {pack.GameDirectory}" : ""),
                $"目录: {pack.Path}"
            };

            if (!string.IsNullOrEmpty(pack.SourceFile)) lines.Add($"来源: {pack.SourceFile}");
            if (!string.IsNullOrEmpty(pack.Summary)) lines.Add($"说明: {pack.Summary}");
            if (pack.IsServer)
            {
                var installer = ModpackManager.FindInstaller(pack.Path);
                if (installer is not null)
                {
                    lines.Add($"待执行安装器: {Path.GetFileName(installer)}（启动服务端时自动执行 --installServer）");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>java command line that would start the selected server pack.</summary>
    public string ModpackServerCommand
    {
        get
        {
            if (_selectedModpack is null) return "";
            if (!_selectedModpack.IsServer) return "客户端整合包没有服务端命令，请选择服务端整合包。";

            var java = _launcher.JavaRuntimes.FirstOrDefault()?.Path ?? "java";
            return ModpackManager.BuildServerCommand(_selectedModpack.Path, java, MaxMemoryMb);
        }
    }

    /// <summary>Rescans the pack folders.</summary>
    public void RefreshModpacks()
    {
        var selectedPath = _selectedModpack?.Path;

        ModpackList.Clear();
        foreach (var pack in ModpackSvc.Scan()) ModpackList.Add(pack);

        _selectedModpack = ModpackList.FirstOrDefault(p => p.Path == selectedPath) ?? ModpackList.FirstOrDefault();
        Raise(nameof(SelectedModpack));
        Raise(nameof(ModpackSummary));
        Raise(nameof(HasModpacks));
        Raise(nameof(ModpackDetail));
        Raise(nameof(ModpackServerCommand));
        Raise(nameof(ModpackServerStartEnabled));
        Raise(nameof(ModpackStartText));
        Raise(nameof(IsServerPackSelected));
        Raise(nameof(ModpackKindText));
        Raise(nameof(ModpacksPath));
        (ImportModpackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ImportServerModpackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartServerPackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopServerPackCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public bool ModpackServerStartEnabled => _selectedModpack?.IsServer == true;

    /// <summary>Asks for a pack file and installs it as a client or a server pack.</summary>
    private async Task ImportModpackAsync(bool server)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = server ? "选择服务端整合包（zip / 安装器 jar）" : "选择客户端整合包（.mrpack / zip）",
            Filter = server
                ? "服务端整合包 (*.zip;*.jar)|*.zip;*.jar|所有文件 (*.*)|*.*"
                : "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        var pack = ModpackReader.Read(dialog.FileName);
        if (pack is null)
        {
            LogService.Warn($"无法识别为整合包: {dialog.FileName}", "Packs");
            WorkspaceTab = 8;
            return;
        }

        if (server) pack.Kind = ModpackKind.Server;
        WorkspaceTab = 8;

        // Ask where the pack should go: standalone modpacks folder, or straight
        // into a Minecraft instance so it can be launched from the version list.
        var instances = CurrentInstances();
        var suggested = ModpackManager.MatchInstance(pack, instances);
        var window = new Views.PackImportWindow(dialog.FileName, pack, instances, suggested)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (window.ShowDialog() != true)
        {
            LogService.Info("已取消导入", "Packs");
            return;
        }

        var deployTo = window.DeployToInstance ? window.SelectedInstanceId : null;
        var progress = new Progress<string>(line => LogService.Info("  " + line, "Packs"));

        try
        {
            var installed = await ModpackSvc.InstallAsync(pack, progress).ConfigureAwait(false);

            if (deployTo is { Length: > 0 } instanceId)
            {
                var gameDir = Config.ResolveGameDirectory(instanceId)
                              ?? Path.Combine(_launcher.Paths.Root, "versions", instanceId);
                var (files, mods, message) = ModpackSvc.DeployToGameDir(installed.Path, gameDir, progress);

                ModpackSvc.SetInstance(installed, instanceId, gameDir);
                Ui.Post(() => LogService.Ok($"{message}（实例 {instanceId}，游戏目录 {gameDir}）", "Packs"));

                if (files == 0)
                {
                    Ui.Post(() => LogService.Warn("整合包里没有可复制的 mods / config，实例内容未改变", "Packs"));
                }
            }

            Ui.Post(() =>
            {
                RefreshModpacks();
                SelectedModpack = ModpackList.FirstOrDefault(p => p.Path == installed.Path) ?? SelectedModpack;
                LogService.Ok($"{(installed.IsServer ? "服务端整合包" : "客户端整合包")} {installed.Name} 安装完成" +
                              $"（模组 {installed.ModCount} 个，{installed.SizeText}）", "Packs");
                if (installed.IsServer)
                {
                    LogService.Info("启动命令: " + ModpackManager.BuildServerCommand(installed.Path), "Packs");
                }

                if (installed.HasInstance)
                {
                    // Refresh the instance list so the freshly packed instance shows up.
                    _launcher.ReloadInstallation();
                    RefreshInstalled();
                    LogService.Info($"实例 {installed.InstanceId} 已包含该整合包，可直接在「版本」页启动", "Packs");
                }
            });
        }
        catch (Exception e)
        {
            LogService.Error("整合包安装失败: " + e.Message, "Packs");
        }
    }

    /// <summary>Installed instances as (id, game version, loader) for pack matching.</summary>
    private List<(string Id, string GameVersion, string Loader)> CurrentInstances()
    {
        var result = new List<(string, string, string)>();
        foreach (var choice in VersionChoices)
        {
            var version = _launcher.Repository?.Load(choice.Id);
            var game = version?.Json.InheritsFrom
                       ?? (version?.Json.Id is { Length: > 0 } id ? id : choice.Id);

            // "1.20.1-forge" -> game 1.20.1, loader Forge
            if (game == choice.Id)
            {
                var parsed = ModpackReader.ParseVersionId(choice.Id);
                game = parsed.Game.Length > 0 ? parsed.Game : choice.Id;
            }

            result.Add((choice.Id, game, choice.Loader));
        }

        return result;
    }

    /// <summary>Copies the selected pack into an instance game directory.</summary>
    private async Task DeploySelectedModpackAsync()
    {
        LogService.Info("加入实例: 命令已进入", "Packs");
        var pack = _selectedModpack;
        if (pack is null)
        {
            LogService.Warn("请先选择一个整合包", "Packs");
            return;
        }

        if (pack.IsServer)
        {
            LogService.Warn("服务端整合包不加入客户端实例，请用「启动服务端」", "Packs");
            return;
        }

        var instances = CurrentInstances();
        if (instances.Count == 0)
        {
            LogService.Warn("本机还没有 Minecraft 实例，请先到「版本」页安装版本", "Packs");
            return;
        }

        var source = ModpackReader.Read(pack.SourceFile.Length > 0 && File.Exists(pack.SourceFile)
            ? pack.SourceFile
            : pack.Path);
        var model = source ?? new Modpack
        {
            Name = pack.Name,
            Kind = ModpackKind.Client,
            GameVersion = pack.GameVersion,
            Loader = pack.Loader,
            LoaderVersion = pack.LoaderVersion,
            SourceFile = pack.Path
        };

        var suggested = ModpackManager.MatchInstance(model, instances)
                        ?? (pack.HasInstance ? pack.InstanceId : null);
        var window = new Views.PackImportWindow(pack.SourceFile.Length > 0 ? pack.SourceFile : pack.Path,
            model, instances, suggested)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (window.ShowDialog() != true || !window.DeployToInstance) return;

        var instanceId = window.SelectedInstanceId;
        if (instanceId.Length == 0) return;

        var gameDir = Config.ResolveGameDirectory(instanceId)
                      ?? Path.Combine(_launcher.Paths.Root, "versions", instanceId);
        var progress = new Progress<string>(line => LogService.Info("  " + line, "Packs"));

        await Task.Run(() =>
        {
            var (_, _, message) = ModpackSvc.DeployToGameDir(pack.Path, gameDir, progress);
            ModpackSvc.SetInstance(pack, instanceId, gameDir);
            Ui.Post(() =>
            {
                LogService.Ok($"{message}（实例 {instanceId}）", "Packs");
                RefreshModpacks();
            });
        }).ConfigureAwait(false);
    }

    /// <summary>Starts the selected server pack (installs Forge/NeoForge first when needed).</summary>
    private async Task StartServerPackAsync()
    {
        var pack = _selectedModpack;
        if (pack is null || !pack.IsServer)
        {
            LogService.Warn("请先选择一个服务端整合包（列表里橙色高亮的那一行）", "Packs");
            return;
        }

        if (_serverRunning)
        {
            LogService.Warn("服务端已经在运行", "Packs");
            return;
        }

        var java = _launcher.JavaRuntimes.FirstOrDefault()?.Path ?? "java";
        var progress = new Progress<string>(line => LogService.Info("  " + line, "Packs"));

        ModpackManager.AcceptEula(pack.Path);
        ModpackManager.ConfigureServer(pack.Path, 25565, 20, $"TiaMC {pack.Name}", onlineMode: false);

        var installer = ModpackManager.FindInstaller(pack.Path);
        if (installer is not null)
        {
            LogService.Info("检测到服务端安装器，先执行 --installServer（可能需要几分钟）", "Packs");
            await ModpackSvc.RunInstallerAsync(pack.Path, java, progress).ConfigureAwait(false);
        }

        var command = ModpackManager.BuildServerCommand(pack.Path, java, MaxMemoryMb);
        LogService.Info("启动服务端: " + command, "Packs");

        try
        {
            var parts = SplitCommand(command);
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = parts[0],
                WorkingDirectory = pack.Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            foreach (var argument in parts.Skip(1)) info.ArgumentList.Add(argument);

            _serverProcess = System.Diagnostics.Process.Start(info);
            if (_serverProcess is null)
            {
                LogService.Error("无法启动服务端进程", "Packs");
                return;
            }

            _serverRunning = true;
            Ui.Post(() =>
            {
                Raise(nameof(ModpackStartText));
                (StopServerPackCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });

            _serverProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Game(e.Data);
            };
            _serverProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Warn(e.Data, "Minecraft");
            };
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            await _serverProcess.WaitForExitAsync().ConfigureAwait(false);
            LogService.Info($"服务端已退出（代码 {_serverProcess.ExitCode}）", "Packs");
        }
        catch (Exception e)
        {
            LogService.Error("服务端启动失败: " + e.Message, "Packs");
        }
        finally
        {
            _serverProcess?.Dispose();
            _serverProcess = null;
            _serverRunning = false;
            Ui.Post(() =>
            {
                Raise(nameof(ModpackStartText));
                (StopServerPackCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });
        }
    }

    /// <summary>Splits a command line that may contain quoted paths.</summary>
    private static List<string> SplitCommand(string command)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in command)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ' ' && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private void StopServerPack()
    {
        if (_serverProcess is null || _serverProcess.HasExited)
        {
            LogService.Info("没有正在运行的服务端", "Packs");
            return;
        }

        try
        {
            _serverProcess.StandardInput.WriteLine("stop");
            if (!_serverProcess.WaitForExit(15000)) _serverProcess.Kill(entireProcessTree: true);
            LogService.Ok("已停止服务端", "Packs");
        }
        catch (Exception e)
        {
            LogService.Error("停止服务端失败: " + e.Message, "Packs");
        }
    }

    public string YggServerStateText =>
        _yggServer?.IsRunning == true
            ? $"认证服务端 {(Ygg.Store.Settings.Bind == "any" ? Ygg.LanAddress : "127.0.0.1")}:{Ygg.Port} 运行中"
            : "认证服务端已关闭";

    public string YggServerSwitchTooltip =>
        (_yggServer?.IsRunning == true ? "关闭" : "启动") +
        " Yggdrasil 认证服务端（外置登录）。开启状态会记住，下次启动启动器时自动拉起。\n" +
        "端口与账号在「认证服务端」页面的终端里用命令配置（help 查看）。";

    /// <summary>Starts the server when the switch was left on last time.</summary>
    public void ApplyYggAutoStart()
    {
        if (!Config.AutoStartYggdrasil) return;
        Ygg.Start();
        Raise(nameof(YggServerOn));
        Raise(nameof(YggPrompt));
        Raise(nameof(YggServerStateText));
        Raise(nameof(YggServerSwitchTooltip));
    }

    /// <summary>Lazily creates the server; its log is streamed into the terminal.</summary>
    private YggdrasilServer Ygg => _yggServer ??= CreateYggServer();

    private YggdrasilServer CreateYggServer()
    {
        var root = Path.Combine(AppConfig.ConfigDirectory, "yggdrasil");
        var server = new YggdrasilServer(root);
        server.Log += line => YggConsole.Append("[server] " + line);
        YggConsole.Append($"数据目录: {root}");
        YggConsole.Append($"配置文件: {server.StorePath}");
        return server;
    }

    private void RaiseYggState()
    {
        Raise(nameof(YggPrompt));
        Raise(nameof(YggServerOn));
        Raise(nameof(YggServerStateText));
        Raise(nameof(YggServerSwitchTooltip));
    }
    /// <summary>Shows the banner the first time the page is used.</summary>
    public void EnsureYggBanner()
    {
        if (_yggBannerShown) return;
        _yggBannerShown = true;

        YggConsole.Append("");
        YggConsole.Append("TiaMC Yggdrasil 认证服务端（外置登录，authlib-injector 兼容）");
        YggConsole.Append("接口: /authserver/{authenticate,refresh,validate,invalidate,signout}");
        YggConsole.Append("      /sessionserver/session/minecraft/{join,hasJoined,profile/<uuid>}");
        YggConsole.Append("      /api/profiles/minecraft · /api/user/profile/<uuid>/<skin|cape>");
        YggConsole.Append("      /textures/<hash> · /skins/MinecraftSkins/<name>.png · GET / 元数据");
        YggConsole.Append("");
        YggConsole.Append("常用命令（help 看全部）:");
        YggConsole.Append("  start [端口]               启动（默认 25566，仅本机）");
        YggConsole.Append("  user add <角色名> <密码>    添加账号（角色名即登录名）");
        YggConsole.Append("  skin <角色名>              生成占位皮肤");
        YggConsole.Append("  api                       打印 API 与 -javaagent 参数");
        YggConsole.Append("");
    }

    /// <summary>Executes one terminal command line.</summary>
    public void ExecuteYggCommand()
    {
        var input = (YggInput ?? "").Trim();
        YggInput = "";
        Raise(nameof(YggPrompt));

        if (input.Length == 0) return;
        YggConsole.Append($"{YggPrompt} {input}");

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        try
        {
            switch (command)
            {
                case "help":
                case "?":
                    PrintYggHelp();
                    break;

                case "start":
                {
                    var port = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : (int?)null;
                    if (Ygg.Start(port))
                    {
                        Config.AutoStartYggdrasil = true;
                        Config.Save();
                    }

                    RaiseYggState();
                    break;
                }

                case "stop":
                    Ygg.Stop();
                    Config.AutoStartYggdrasil = false;
                    Config.Save();
                    RaiseYggState();
                    break;

                case "status":
                    YggConsole.Append($"运行状态: {(Ygg.IsRunning ? "运行中" : "已停止")}");
                    YggConsole.Append($"API 地址: {Ygg.ApiRoot}");
                    YggConsole.Append($"用户 {Ygg.Store.Users.Count} / 角色 {Ygg.Store.Profiles.Count} / " +
                                      $"有效令牌 {Ygg.Store.Tokens.Count(t => t.Valid)}");
                    YggConsole.Append($"签名公钥: {(string.IsNullOrEmpty(Ygg.Store.PublicKeyPem) ? "无" : "已生成")}");
                    break;

                case "api":
                    YggConsole.Append($"API 地址: {Ygg.ApiRoot}");
                    YggConsole.Append($"启动器参数: -javaagent:authlib-injector.jar={Ygg.ApiRoot}");
                    YggConsole.Append("authlib-injector 下载源: " +
                                      "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json");
                    break;

                case "open":
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = Ygg.ApiRoot,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception e)
                    {
                        YggConsole.Append("打开浏览器失败: " + e.Message);
                    }

                    break;

                case "port":
                    if (parts.Length > 1 && int.TryParse(parts[1], out var newPort) && newPort is > 0 and < 65536)
                    {
                        Ygg.Store.Settings.Port = newPort;
                        Ygg.SaveStore();
                        YggConsole.Append($"端口已设为 {newPort}（重启服务端后生效）");
                    }
                    else
                    {
                        YggConsole.Append("用法: port <1-65535>");
                    }

                    break;

                case "bind":
                    if (parts.Length > 1 && parts[1] is "local" or "any")
                    {
                        Ygg.Store.Settings.Bind = parts[1];
                        Ygg.SaveStore();
                        YggConsole.Append($"绑定方式: {parts[1]}（重启服务端后生效）" +
                                          (parts[1] == "any" ? $"，局域网地址 http://{Ygg.LanAddress}:{Ygg.Port}/" : ""));
                    }
                    else
                    {
                        YggConsole.Append("用法: bind local|any");
                    }

                    break;

                case "name":
                    if (parts.Length > 1)
                    {
                        Ygg.Store.Settings.ServerName = string.Join(' ', parts.Skip(1));
                        Ygg.SaveStore();
                        YggConsole.Append("服务器名称已更新: " + Ygg.Store.Settings.ServerName);
                    }

                    break;

                case "save":
                    Ygg.SaveStore();
                    YggConsole.Append("已保存 " + Ygg.StorePath);
                    break;

                case "clear":
                case "cls":
                    YggConsole.Clear();
                    break;

                case "user":
                    ExecuteYggUserCommand(parts);
                    break;

                case "skin":
                    ExecuteYggSkinCommand(parts);
                    break;

                case "token":
                    ExecuteYggTokenCommand(parts);
                    break;

                case "test":
                    YggConsole.Append("提示: 命令行 `tiamc-cli yggdrasil` 会跑完整的接口自测（13 项）");
                    break;

                default:
                    YggConsole.Append($"未知命令: {command}（输入 help 查看命令）");
                    break;
            }
        }
        catch (Exception e)
        {
            YggConsole.Append("命令执行失败: " + e.Message);
        }
    }

    private void ExecuteYggUserCommand(string[] parts)
    {
        var action = parts.Length > 1 ? parts[1].ToLowerInvariant() : "list";
        switch (action)
        {
            case "add":
                if (parts.Length < 4)
                {
                    YggConsole.Append("用法: user add <角色名> <密码> [邮箱]");
                    return;
                }

                var (ok, message) = Ygg.AddUser(parts[2], parts[3], parts.Length > 4 ? parts[4] : null);
                YggConsole.Append(message);
                if (ok) YggConsole.Append($"提示: 执行 skin {parts[2]} 可生成占位皮肤");
                break;

            case "del":
            case "delete":
                if (parts.Length < 3)
                {
                    YggConsole.Append("用法: user del <角色名>");
                    return;
                }

                YggConsole.Append(Ygg.RemoveUser(parts[2]).Message);
                break;

            case "pass":
            case "password":
                if (parts.Length < 4)
                {
                    YggConsole.Append("用法: user pass <角色名> <新密码>");
                    return;
                }

                YggConsole.Append(Ygg.SetPassword(parts[2], parts[3]).Message);
                break;

            case "list":
                if (Ygg.Store.Profiles.Count == 0)
                {
                    YggConsole.Append("还没有账号，用 user add <角色名> <密码> 添加");
                    return;
                }

                YggConsole.Append($"共 {Ygg.Store.Profiles.Count} 个角色:");
                foreach (var profile in Ygg.Store.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var owner = Ygg.Store.Users.FirstOrDefault(u => u.Id == profile.UserId);
                    var tokens = Ygg.Store.Tokens.Count(t => t.Valid && t.ProfileUuid == profile.Uuid);
                    YggConsole.Append($"  {profile.Name,-16} {profile.Uuid}  {profile.Model,-7} " +
                                      $"皮肤={(profile.SkinHash is null ? "无" : profile.SkinHash[..8])}  " +
                                      $"邮箱={owner?.Email ?? "-"}  有效令牌={tokens}");
                }

                break;

            default:
                YggConsole.Append("用法: user add|del|pass|list");
                break;
        }
    }

    private void ExecuteYggSkinCommand(string[] parts)
    {
        if (parts.Length < 2)
        {
            YggConsole.Append("用法: skin <角色名> [png 路径 | #RRGGBB] [default|slim] [cape]");
            return;
        }

        var name = parts[1];
        var isCape = parts.Any(p => p.Equals("cape", StringComparison.OrdinalIgnoreCase));
        var model = parts.Any(p => p.Equals("slim", StringComparison.OrdinalIgnoreCase)) ? "slim" : "default";
        var source = parts.Length > 2 ? parts[2] : "#3B7FB5";

        YggConsole.Append(source.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !source.StartsWith('#')
            ? Ygg.SetSkin(name, source, model, isCape).Message
            : Ygg.SetPlaceholderSkin(name, source).Message);
    }

    private void ExecuteYggTokenCommand(string[] parts)
    {
        var action = parts.Length > 1 ? parts[1].ToLowerInvariant() : "list";
        switch (action)
        {
            case "list":
            {
                var tokens = Ygg.Store.Tokens.Where(t => t.Valid).ToList();
                if (tokens.Count == 0)
                {
                    YggConsole.Append("没有有效令牌");
                    return;
                }

                YggConsole.Append($"有效令牌 {tokens.Count} 个:");
                foreach (var token in tokens.Take(20))
                {
                    var profile = Ygg.FindProfileByUuid(token.ProfileUuid);
                    YggConsole.Append($"  {token.AccessToken[..8]}…  角色={(profile?.Name ?? "-"),-14} " +
                                      $"到期={token.ExpiresUtc.ToLocalTime():yyyy-MM-dd HH:mm}  使用={token.Uses}");
                }

                break;
            }

            case "clear":
                foreach (var token in Ygg.Store.Tokens) token.Valid = false;
                Ygg.SaveStore();
                YggConsole.Append("已吊销全部令牌");
                break;

            default:
                YggConsole.Append("用法: token list|clear");
                break;
        }
    }

    private void PrintYggHelp()
    {
        YggConsole.Append("认证服务端命令:");
        YggConsole.Append("  start [端口]                     启动（默认 25566，仅 127.0.0.1）");
        YggConsole.Append("  stop                             停止");
        YggConsole.Append("  status                           运行状态与账号统计");
        YggConsole.Append("  port <n> / bind local|any        端口 / 监听范围（重启生效）");
        YggConsole.Append("  name <服务器名>                  元数据 serverName");
        YggConsole.Append("  user add <角色名> <密码> [邮箱]   添加账号");
        YggConsole.Append("  user del <角色名> / user list     删除 / 列出角色");
        YggConsole.Append("  user pass <角色名> <新密码>       改密码（吊销旧令牌）");
        YggConsole.Append("  skin <角色名> [#RRGGBB|png] [default|slim] [cape]");
        YggConsole.Append("  token list | token clear         查看 / 吊销令牌");
        YggConsole.Append("  api                              打印 API 与 -javaagent 参数");
        YggConsole.Append("  open / save / clear              浏览器打开 / 写盘 / 清屏");
    }

    public string ModrinthProject
    {
        get => _modrinthProject;
        set => Set(ref _modrinthProject, value);
    }

    public bool IsModBusy
    {
        get => _isModBusy;
        private set
        {
            if (!Set(ref _isModBusy, value)) return;
            (InstallModrinthCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (SearchModrinthCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private bool _isModBusy;

    /// <summary>界面上的版本徽标，例如 "v1.0.8"。</summary>
    public string VersionBadge => "v" + TiaMc.Core.Utils.AppInfo.Version;

    public string WindowTitle => TiaMc.Core.Utils.AppInfo.TitleWithVersion;   // 例：TIA-MC 工程启动器 v1.0.9

    public string RootPath => _launcher.Paths.Root;

    // ------------------------------------------------------------- accounts

    public ObservableCollection<MinecraftAccount> Accounts => _accountList;

    private readonly ObservableCollection<MinecraftAccount> _accountList = [];

    private MinecraftAccount? _selectedAccount;

    public MinecraftAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!Set(ref _selectedAccount, value)) return;
            if (value is not null)
            {
                _launcher.Accounts.Select(value);
                LogService.Info($"当前账户切换为 {value.Display}", "Account");
            }

            Raise(nameof(AccountSummary));
            Raise(nameof(AccountKindText));
            Raise(nameof(AccountUuid));
            Raise(nameof(AccountTokenText));
            BuildPlan();
        }
    }

    public string AccountSummary => SelectedAccount is null
        ? "(未选择账户)"
        : $"{SelectedAccount.Name}  ·  {SelectedAccount.KindText}";

    public string AccountKindText => SelectedAccount?.Kind == AccountKind.Microsoft
        ? "Microsoft 正版账户"
        : "离线账户";

    public string AccountUuid => SelectedAccount?.Uuid ?? "-";

    public string AccountTokenText => SelectedAccount switch
    {
        null => "-",
        { Kind: AccountKind.Offline } => "离线模式不需要令牌",
        { TokenExpiresUtc: { } expiry } => $"有效期至 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}",
        _ => "令牌状态未知"
    };

    public string AccountSkinText => SelectedAccount is { Kind: AccountKind.Microsoft } account
        ? $"{account.SkinVariant ?? "CLASSIC"} 皮肤"
        : "离线账户使用默认皮肤";

    /// <summary>The account that will be used for the next launch (for the ribbon).</summary>
    private void RefreshAccounts()
    {
        _accountList.Clear();
        foreach (var account in _launcher.Accounts.Accounts) _accountList.Add(account);

        var selected = _launcher.Accounts.Selected;
        _selectedAccount = _accountList.FirstOrDefault(a => a.Key == selected.Key) ?? _accountList.FirstOrDefault();
        Raise(nameof(Accounts));
        Raise(nameof(HasAccounts));
        Raise(nameof(SelectedAccount));
        Raise(nameof(AccountSummary));
        Raise(nameof(AccountKindText));
        Raise(nameof(AccountUuid));
        Raise(nameof(AccountTokenText));
        Raise(nameof(AccountSkinText));
    }

    /// <summary>账户列表/选择变化后，显式刷新账户相关按钮的可用状态。</summary>
    private void RaiseAccountCommands()
    {
        (AddMicrosoftAccountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshAccountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RemoveAccountCommand as RelayCommand)?.RaiseCanExecuteChanged();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private async Task AddOfflineAccountAsync()
    {
        var name = Views.TextInputWindow.Prompt(
            System.Windows.Application.Current.MainWindow,
            "新建离线账户",
            "输入玩家名称（离线模式下 UUID 由名称推导，仅本地生效）:",
            SelectedAccount?.Name ?? "Steve");

        if (string.IsNullOrWhiteSpace(name)) return;

        var account = MinecraftAccount.CreateOffline(name);
        _launcher.Accounts.AddOrUpdate(account);
        RefreshAccounts();
        SelectedAccount = _accountList.FirstOrDefault(a => a.Key == account.Key);
        LogService.Ok($"已创建离线账户 {account.Name} (uuid {account.Uuid})", "Account");
        await Task.CompletedTask;
    }

    private async Task AddMicrosoftAccountAsync()
    {
        var owner = System.Windows.Application.Current.MainWindow;
        var dialog = new Views.DeviceCodeWindow { Owner = owner };
        var vm = dialog.ViewModel;
        var tokenSource = new CancellationTokenSource();

        dialog.Closed += (_, _) => tokenSource.Cancel();
        dialog.Show();

        try
        {
            var auth = _launcher.MicrosoftAuth;
            var result = await auth.LoginAsync(
                info =>
                {
                    Ui.Post(() =>
                    {
                        vm.UserCode = info.UserCode;
                        vm.VerificationUri = info.VerificationUri;
                        vm.SecondsLeft = info.ExpiresInSeconds;
                        vm.Status = "等待你在浏览器中完成登录...";
                        vm.Detail = $"如果浏览器没有自动打开，请手动访问 {info.VerificationUri}";
                        vm.RaiseLinkChanged();
                    });
                },
                seconds => Ui.Post(() =>
                {
                    vm.SecondsLeft = seconds;
                    vm.RaiseLinkChanged();
                }),
                tokenSource.Token);

            _launcher.Accounts.AddOrUpdate(result.Account);
        RaiseAccountCommands();
            RefreshAccounts();
            SelectedAccount = _accountList.FirstOrDefault(a => a.Key == result.Account.Key);

            LogService.Ok($"Microsoft 登录成功: {result.Account.Name} " +
                          $"({result.Account.Uuid}, 游戏许可: {(result.Account.OwnsGame ? "有" : "无")})", "Account");

            Ui.Post(() =>
            {
                vm.IsWaiting = false;
                vm.IsFinished = true;
                vm.Status = $"登录成功: {result.Account.Name}";
                vm.Detail = result.Account.Subtitle +
                            (result.Account.OwnsGame ? "" : "\n警告: 该账户没有 Minecraft 游戏许可，可能无法进入游戏。");
            });

            await Task.Delay(1500);
        }
        catch (MicrosoftAuthException e)
        {
            LogService.Error($"Microsoft 登录失败: {e.Message}", "Account");
            Ui.Post(() =>
            {
                vm.IsWaiting = false;
                vm.IsFailed = true;
                vm.Status = "登录失败";
                vm.Detail = e.Message + (string.IsNullOrWhiteSpace(e.Detail) ? "" : "\n" + e.Detail);
            });

            await Task.Delay(400);
        }
        catch (OperationCanceledException)
        {
            LogService.Warn("Microsoft 登录已取消", "Account");
        }
        catch (Exception e)
        {
            LogService.Error($"Microsoft 登录异常: {e.Message}", "Account");
        }
        finally
        {
            Ui.Post(() =>
            {
                if (dialog.IsVisible) dialog.Close();
            });
        }
    }

    private async Task RefreshAccountAsync()
    {
        if (SelectedAccount is not { Kind: AccountKind.Microsoft } account)
        {
            LogService.Warn("只有 Microsoft 正版账户需要刷新登录状态", "Account");
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _launcher.MicrosoftAuth.RefreshAccountAsync(account);
            _launcher.Accounts.AddOrUpdate(result.Account);
        RaiseAccountCommands();
            RefreshAccounts();
            SelectedAccount = _accountList.FirstOrDefault(a => a.Key == result.Account.Key);
            LogService.Ok($"已刷新账户 {result.Account.Name}", "Account");
        }
        catch (MicrosoftAuthException e)
        {
            LogService.Error($"刷新失败: {e.Message}", "Account");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemoveAccount()
    {
        if (SelectedAccount is null) return;
        if (_launcher.Accounts.Accounts.Count <= 1)
        {
            LogService.Warn("至少需要保留一个账户", "Account");
            return;
        }

        var name = SelectedAccount.Name;
        _launcher.Accounts.Remove(SelectedAccount);
        RaiseAccountCommands();
        RefreshAccounts();
        SelectedAccount = _accountList.FirstOrDefault();
        LogService.Info($"已删除账户 {name}", "Account");
    }

    // ------------------------------------------------- per instance settings
    //
    // Every value below is "effective": the instance override when there is one,
    // otherwise the global default. Writing stores the value on the selected
    // instance and marks the overridden properties in the UI, so two versions can
    // run with different memory / Java / window settings (版本隔离 also covers the
    // launcher side).

    private InstanceSettings Current => Config.GetInstance(SelectedVersion?.Id);

    /// <summary>Persists the instance overrides after a change.</summary>
    private void InstanceChanged()
    {
        Config.Save();
        Raise(nameof(InstanceOverrideText));
        Raise(nameof(HasInstanceOverrides));
    }

    public int MinMemoryMb
    {
        get => Current.MinMemoryMb ?? Config.MinMemoryMb;
        set
        {
            // Never exceed the maximum, never below 256 MB.
            var clamped = Math.Clamp(value, 256, Math.Max(256, MaxMemoryMb));
            if (Current.MinMemoryMb == clamped) return;
            Current.MinMemoryMb = clamped;
            Raise();
            Raise(nameof(MemoryText));
            Raise(nameof(MemoryRangeText));
            InstanceChanged();
        }
    }

    // ------------------------------------------------------- machine memory

    /// <summary>Physical memory of this machine; every machine is different.</summary>
    public int TotalMemoryMb => TiaMc.Core.Utils.SystemInfo.TotalPhysicalMemoryMb;

    /// <summary>Upper bound of the -Xmx slider (the real physical memory).</summary>
    public int MemoryLimitMb => TiaMc.Core.Utils.SystemInfo.MemoryLimitMb;

    /// <summary>Upper bound of the -Xms slider (the current maximum).</summary>
    public int MinMemoryLimitMb => Math.Max(512, MaxMemoryMb);

    public string MemoryRangeText
    {
        get
        {
            var summary = TiaMc.Core.Utils.SystemInfo.MemorySummary();
            var recommended = TiaMc.Core.Utils.SystemInfo.RecommendedMaxMemoryMb;
            return $"{summary} · 本机建议 -Xmx {TiaMc.Core.Utils.SystemInfo.FormatMb(recommended)}";
        }
    }

    /// <summary>Sets min/max memory from the real hardware of this machine.</summary>
    public void ApplyRecommendedMemory()
    {
        var max = TiaMc.Core.Utils.SystemInfo.RecommendedMaxMemoryMb;
        var min = TiaMc.Core.Utils.SystemInfo.RecommendedMinMemoryMb;

        Current.MinMemoryMb = min;
        Current.MaxMemoryMb = max;
        Raise(nameof(MinMemoryMb));
        Raise(nameof(MaxMemoryMb));
        Raise(nameof(MemoryText));
        Raise(nameof(MemoryRangeText));
        Raise(nameof(MinMemoryLimitMb));
        InstanceChanged();

        LogService.Ok($"已按本机内存设置: -Xms{min}m -Xmx{max}m（{TiaMc.Core.Utils.SystemInfo.MemorySummary()}）", "Memory");
    }

    /// <summary>Fits the configured memory into the hardware of this machine.</summary>
    public void ClampMemoryToMachine()
    {
        var limit = TiaMc.Core.Utils.SystemInfo.MemoryLimitMb;
        var recommended = TiaMc.Core.Utils.SystemInfo.RecommendedMaxMemoryMb;

        // A default of 4096 MB is fine on 8 GB machines and silly on 4 GB ones.
        if (Config.MaxMemoryMb > recommended || Config.MaxMemoryMb <= 0) Config.MaxMemoryMb = recommended;
        if (Config.MinMemoryMb <= 0 || Config.MinMemoryMb > Config.MaxMemoryMb) Config.MinMemoryMb = Math.Min(512, Config.MaxMemoryMb);
        if (Config.MaxMemoryMb > limit) Config.MaxMemoryMb = limit;

        foreach (var (_, instance) in Config.Instances)
        {
            if (instance.MaxMemoryMb is { } instanceMax && instanceMax > limit) instance.MaxMemoryMb = limit;
            if (instance.MinMemoryMb is { } instanceMin && instanceMin > (instance.MaxMemoryMb ?? Config.MaxMemoryMb))
            {
                instance.MinMemoryMb = instance.MaxMemoryMb ?? Config.MaxMemoryMb;
            }
        }

        LogService.Info($"本机内存: {TiaMc.Core.Utils.SystemInfo.MemorySummary()}（-Xmx 上限 {TiaMc.Core.Utils.SystemInfo.FormatMb(limit)}）", "Memory");
    }

    // ---------------------------------------------------- modpack commands

    public AsyncRelayCommand ImportModpackCommand { get; }
    public AsyncRelayCommand ImportServerModpackCommand { get; }
    public RelayCommand ScanModpacksCommand { get; }
    public AsyncRelayCommand StartServerPackCommand { get; }
    public RelayCommand StopServerPackCommand { get; }
    public RelayCommand PickSkinFileCommand { get; }
    public RelayCommand GenerateSkinCommand { get; }
    public RelayCommand RandomSkinCommand { get; }
    public RelayCommand ClearSkinCommand { get; }
    public AsyncRelayCommand FetchSkinCommand { get; }
    public RelayCommand PushSkinCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand SaveJvmArgsCommand { get; }
    public RelayCommand UndoJvmArgsCommand { get; }
    public RelayCommand ResetJvmArgsCommand { get; }
    public RelayCommand InheritJvmArgsCommand { get; }
    public RelayCommand ApplyJvmArgsAsGlobalCommand { get; }
    public AsyncRelayCommand SearchResourcesCommand { get; }
    public AsyncRelayCommand DownloadResourceCommand { get; }
    public RelayCommand RecheckJavaCommand { get; }
    public RelayCommand ApplyRecommendedMemoryCommand { get; }
    public RelayCommand TrimMemoryCommand { get; }

    private bool _trimWorkingSets = true;
    private bool _trimStandby = TiaMc.Core.Utils.MemoryTrimmer.IsElevated;
    private bool _trimFileCache;
    private bool _autoTrim;
    private int _trimIntervalMinutes = 30;
    private string _memoryTrimText = "尚未执行内存回收";
    private System.Windows.Threading.DispatcherTimer? _trimTimer;

    /// <summary>清空所有进程的工作集（Mem Reduct 的主要手段，普通权限即可）。</summary>
    public bool TrimWorkingSets
    {
        get => _trimWorkingSets;
        set => Set(ref _trimWorkingSets, value);
    }

    /// <summary>清理待机页面列表（需要管理员权限）。</summary>
    public bool TrimStandbyList
    {
        get => _trimStandby;
        set => Set(ref _trimStandby, value);
    }

    /// <summary>清空系统文件缓存（需要管理员权限）。</summary>
    public bool TrimFileCache
    {
        get => _trimFileCache;
        set => Set(ref _trimFileCache, value);
    }

    /// <summary>按间隔自动回收。</summary>
    public bool AutoTrim
    {
        get => _autoTrim;
        set
        {
            if (!Set(ref _autoTrim, value)) return;
            if (value) StartTrimTimer();
            else _trimTimer?.Stop();
        }
    }

    public int TrimIntervalMinutes
    {
        get => _trimIntervalMinutes;
        set
        {
            var clamped = Math.Clamp(value, 1, 1440);
            if (!Set(ref _trimIntervalMinutes, clamped)) return;
            if (_autoTrim) StartTrimTimer();
        }
    }

    public string MemoryTrimText
    {
        get => _memoryTrimText;
        private set => Set(ref _memoryTrimText, value);
    }

    private void StartTrimTimer()
    {
        _trimTimer ??= new System.Windows.Threading.DispatcherTimer();
        _trimTimer.Stop();
        _trimTimer.Interval = TimeSpan.FromMinutes(TrimIntervalMinutes);
        _trimTimer.Tick -= OnTrimTimerTick;
        _trimTimer.Tick += OnTrimTimerTick;
        _trimTimer.Start();
        LogService.Info($"已开启自动内存回收：每 {TrimIntervalMinutes} 分钟一次", "Memory");
    }

    private void OnTrimTimerTick(object? sender, EventArgs e) => TrimMemoryCore(automatic: true);

    /// <summary>Runs the memory reclaim and reports before/after numbers.</summary>
    public void TrimMemory() => TrimMemoryCore(automatic: false);

    private void TrimMemoryCore(bool automatic)
    {
        try
        {
            var options = new TiaMc.Core.Utils.MemoryTrimmer.Options
            {
                EmptyWorkingSets = TrimWorkingSets,
                PurgeStandbyList = TrimStandbyList,
                FlushModifiedList = TrimStandbyList,
                ClearFileCache = TrimFileCache
            };

            var result = TiaMc.Core.Utils.MemoryTrimmer.Trim(options, line => LogService.Info("  " + line, "Memory"));
            MemoryTrimText = (automatic ? "自动回收 · " : "") + result.Summary +
                             $" · 工作集 {result.ProcessesTrimmed} 个进程";

            LogService.Ok($"内存回收完成：{result.Summary}", "Memory");
            foreach (var note in result.Notes) LogService.Warn(note, "Memory");
        }
        catch (Exception e)
        {
            MemoryTrimText = "内存回收失败: " + e.Message;
            LogService.Error("内存回收失败: " + e.Message, "Memory");
        }
    }
    public AsyncRelayCommand DeployModpackCommand { get; }
    public RelayCommand OpenModpackFolderCommand { get; }
    public RelayCommand DeleteModpackCommand { get; }

    /// <summary>删除选中的本地版本。</summary>
    public RelayCommand DeleteVersionCommand { get; }
    public RelayCommand CopyModpackCommandCommand { get; }

    private void OpenModpackFolder()
    {
        var path = _selectedModpack?.Path ?? ModpackSvc.ClientRoot;
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception e)
        {
            LogService.Error("打开目录失败: " + e.Message, "Packs");
        }
    }

    /// <summary>删除本地版本：确认后删除 versions/&lt;id&gt;（jar、json、natives）。</summary>
    private void DeleteSelectedVersion()
    {
        var version = SelectedVersion;
        if (version is null)
        {
            LogService.Warn("请先在项目树里选中一个本地版本", "版本");
            return;
        }

        if (_launcher.IsRunning)
        {
            LogService.Warn("游戏正在运行，先结束游戏再删除版本", "版本");
            return;
        }

        var directory = System.IO.Path.Combine(_launcher.Paths.VersionsDir, version.Id);
        long size = 0;
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                size = new System.IO.DirectoryInfo(directory)
                    .EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                    .Sum(f => { try { return f.Length; } catch (Exception) { return 0L; } });
            }
        }
        catch (Exception)
        {
            // 体积统计失败不影响删除
        }

        var answer = System.Windows.MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            $"确定要删除本地版本 {version.Id} 吗？\n\n" +
            $"会删除：{directory}\n（约 {TiaMc.Core.Utils.TextUtil.FormatBytes(size)}，含 jar / json / natives）\n\n" +
            "不会删除：存档、模组、资源包（这些在共享目录里）\n\n此操作不可撤销。",
            "删除本地版本", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var (ok, message, _) = _launcher.Repository.Delete(version.Id);
        if (!ok)
        {
            LogService.Error(message, "版本");
            return;
        }

        LogService.Ok(message, "版本");

        if (string.Equals(Config.ActiveInstance, version.Id, StringComparison.OrdinalIgnoreCase))
        {
            Config.ActiveInstance = "";
        }

        // 把实例的专属设置（JVM 参数、内存、模组目录覆盖等）也一起清掉，
        // 否则下次装同名版本会莫名其妙继承旧设置。
        if (Config.Instances.Remove(version.Id))
        {
            LogService.Info($"已清除实例 {version.Id} 的专属设置", "版本");
        }

        Config.Save();

        SelectedVersion = null;
        _launcher.ReloadInstallation();
        RefreshInstalled();

        // 项目树里的节点也要跟着消失：以前只刷新了"已安装实例"列表，
        // 「设备和网络」下那个节点会一直留着，点它还会去操作一个已经不存在的版本。
        SelectedNode = null;
        RebuildTree();

        BuildPlan();
        LogService.Info($"删除后剩余 {_launcher.Installed.Count} 个本地版本（项目树已同步刷新）", "版本");
    }

    private void DeleteSelectedModpack()
    {
        if (_selectedModpack is null) return;
        var (ok, message) = ModpackSvc.Delete(_selectedModpack);
        if (ok)
        {
            LogService.Ok(message, "Packs");
            SelectedModpack = null;
            RefreshModpacks();
        }
        else
        {
            LogService.Error(message, "Packs");
        }
    }
    public int MaxMemoryMb
    {
        get => Current.MaxMemoryMb ?? Config.MaxMemoryMb;
        set
        {
            // The slider is bounded by the physical memory of this machine.
            var clamped = Math.Clamp(value, 1024, Math.Max(1024, MemoryLimitMb));
            if (Current.MaxMemoryMb == clamped) return;
            Current.MaxMemoryMb = clamped;
            Raise();
            Raise(nameof(MemoryText));
            Raise(nameof(MemoryRangeText));
            Raise(nameof(MinMemoryLimitMb));
            if (MinMemoryMb > clamped) MinMemoryMb = clamped;
            InstanceChanged();
        }
    }

    public string MemoryText =>
        $"最大内存 {TiaMc.Core.Utils.SystemInfo.FormatMb(MaxMemoryMb)}（-Xmx{MaxMemoryMb}m，不设置 -Xms）";

    public string GcMode
    {
        get => Current.GcMode ?? Config.GcMode;
        set
        {
            if (Current.GcMode == value) return;
            Current.GcMode = value;
            Raise();
            InstanceChanged();
        }
    }

    public int WindowWidth
    {
        get => Current.WindowWidth ?? Config.WindowWidth;
        set
        {
            if (Current.WindowWidth == value) return;
            Current.WindowWidth = value;
            Raise();
            Raise(nameof(WindowText));
            InstanceChanged();
        }
    }

    public int WindowHeight
    {
        get => Current.WindowHeight ?? Config.WindowHeight;
        set
        {
            if (Current.WindowHeight == value) return;
            Current.WindowHeight = value;
            Raise();
            Raise(nameof(WindowText));
            InstanceChanged();
        }
    }

    public string WindowText => $"{WindowWidth} x {WindowHeight}";

    public bool Fullscreen
    {
        get => Current.Fullscreen ?? Config.Fullscreen;
        set
        {
            if (Current.Fullscreen == value) return;
            Current.Fullscreen = value;
            Raise();
            InstanceChanged();
        }
    }

    public string ExtraJvmArgs
    {
        get => Current.ExtraJvmArgs ?? Config.ExtraJvmArgs;
        set
        {
            if (Current.ExtraJvmArgs == value) return;
            Current.ExtraJvmArgs = value;
            Raise();
            InstanceChanged();
        }
    }

    public string ExtraGameArgs
    {
        get => Current.ExtraGameArgs ?? Config.ExtraGameArgs;
        set
        {
            if (Current.ExtraGameArgs == value) return;
            Current.ExtraGameArgs = value;
            Raise();
            InstanceChanged();
        }
    }

    public string ExtraClasspath
    {
        get => Current.ExtraClasspath ?? Config.ExtraClasspath;
        set
        {
            if (Current.ExtraClasspath == value) return;
            Current.ExtraClasspath = value;
            Raise();
            InstanceChanged();
        }
    }

    public string EnvironmentVariables
    {
        get => Current.EnvironmentVariables ?? Config.EnvironmentVariables;
        set
        {
            if (Current.EnvironmentVariables == value) return;
            Current.EnvironmentVariables = value;
            Raise();
            InstanceChanged();
        }
    }

    /// <summary>Per instance isolation override (null = follow the global switch).</summary>
    public bool IsolateThisInstance
    {
        get => Current.Isolate ?? Config.IsolateInstances;
        set
        {
            if ((Current.Isolate ?? Config.IsolateInstances) == value) return;
            Current.Isolate = value;
            Raise();
            RaiseIsolationState();
            InstanceChanged();
            _ = RefreshModsAsync();
        }
    }

    public bool HasInstanceOverrides => Current.OverrideCount > 0;

    public string InstanceOverrideText
    {
        get
        {
            var instance = Current;
            if (SelectedVersion is null) return "未选择实例，显示全局默认值";
            if (instance.IsEmpty) return "该实例未做任何覆盖，全部继承全局默认值";

            var parts = new List<string>();
            if (instance.MinMemoryMb is not null || instance.MaxMemoryMb is not null) parts.Add("内存");
            if (instance.GcMode is not null) parts.Add("GC");
            if (instance.JavaPath is not null) parts.Add("Java");
            if (instance.WindowWidth is not null || instance.WindowHeight is not null || instance.Fullscreen is not null)
            {
                parts.Add("窗口");
            }

            if (instance.ExtraJvmArgs is not null) parts.Add("JVM 参数");
            if (instance.ExtraGameArgs is not null) parts.Add("游戏参数");
            if (instance.ExtraClasspath is not null) parts.Add("classpath");
            if (instance.EnvironmentVariables is not null) parts.Add("环境变量");
            if (instance.GameDir is not null) parts.Add("游戏目录");
            if (instance.Isolate is not null) parts.Add("隔离开关");

            return $"本实例已覆盖 {instance.OverrideCount} 项：{string.Join("、", parts)}";
        }
    }

    public string GlobalDefaultsText =>
        $"全局默认: -Xmx{Config.MaxMemoryMb}m（无 -Xms） · {Config.GcMode} · " +
        $"{Config.WindowWidth}x{Config.WindowHeight} · 隔离 {(Config.IsolateInstances ? "开" : "关")}";

    /// <summary>Copies the effective instance values into the global defaults.</summary>
    private void ApplyInstanceAsGlobal()
    {
        Config.MinMemoryMb = MinMemoryMb;
        Config.MaxMemoryMb = MaxMemoryMb;
        Config.GcMode = GcMode;
        Config.WindowWidth = WindowWidth;
        Config.WindowHeight = WindowHeight;
        Config.Fullscreen = Fullscreen;
        Config.ExtraJvmArgs = ExtraJvmArgs;
        Config.ExtraGameArgs = ExtraGameArgs;
        Config.ExtraClasspath = ExtraClasspath;
        Config.EnvironmentVariables = EnvironmentVariables;
        Config.Save();

        Raise(nameof(GlobalDefaultsText));
        LogService.Ok($"已把当前实例设置写入全局默认（{MemoryText}, {GcMode}, {WindowText}）", "Isolation");
    }

    /// <summary>Removes every override of the selected instance.</summary>
    private void ResetInstanceOverrides()
    {
        if (SelectedVersion is null) return;
        Config.ResetInstance(SelectedVersion.Id);
        Config.Save();
        RaiseInstanceSettings();
        LogService.Info($"{SelectedVersion.Id} 已恢复为继承全局默认值", "Isolation");
    }

    /// <summary>Re-reads every instance bound property (after switching versions).</summary>
    private void RaiseInstanceSettings()
    {
        Raise(nameof(MinMemoryMb));
        Raise(nameof(MaxMemoryMb));
        Raise(nameof(MemoryText));
        Raise(nameof(GcMode));
        Raise(nameof(WindowWidth));
        Raise(nameof(WindowHeight));
        Raise(nameof(WindowText));
        Raise(nameof(Fullscreen));
        Raise(nameof(ExtraJvmArgs));
        Raise(nameof(ExtraGameArgs));
        Raise(nameof(ExtraClasspath));
        Raise(nameof(EnvironmentVariables));
        Raise(nameof(IsolateThisInstance));
        Raise(nameof(HasInstanceOverrides));
        Raise(nameof(InstanceOverrideText));
        Raise(nameof(GlobalDefaultsText));
        SelectedJava = JavaChoices.FirstOrDefault(j =>
            string.Equals(j.Path, Current.JavaPath ?? Config.JavaPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>单个日志文件上限（MB）——可直接在界面上改。</summary>
    public int LogMaxFileMb
    {
        get => Config.LogMaxFileMb;
        set
        {
            var clamped = Math.Clamp(value, 1, 1024);
            if (Config.LogMaxFileMb == clamped) return;
            Config.LogMaxFileMb = clamped;
            Config.Save();
            Services.LogService.ApplyLimits(Config.LogMaxFileMb, Config.LogKeepFiles, Config.LogMaxTotalMb);
            Raise();
        }
    }

    /// <summary>保留日志份数。</summary>
    public int LogKeepFiles
    {
        get => Config.LogKeepFiles;
        set
        {
            var clamped = Math.Clamp(value, 1, 200);
            if (Config.LogKeepFiles == clamped) return;
            Config.LogKeepFiles = clamped;
            Config.Save();
            Services.LogService.ApplyLimits(Config.LogMaxFileMb, Config.LogKeepFiles, Config.LogMaxTotalMb);
            Raise();
        }
    }

    /// <summary>日志目录总占用上限（MB）。</summary>
    public int LogMaxTotalMb
    {
        get => Config.LogMaxTotalMb;
        set
        {
            var clamped = Math.Clamp(value, 1, 4096);
            if (Config.LogMaxTotalMb == clamped) return;
            Config.LogMaxTotalMb = clamped;
            Config.Save();
            Services.LogService.ApplyLimits(Config.LogMaxFileMb, Config.LogKeepFiles, Config.LogMaxTotalMb);
            Raise();
        }
    }

    /// <summary>当前日志目录占用，显示在设置里。</summary>
    public string LogUsageText
    {
        get
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(Services.LogService.LogDirectory);
                if (!dir.Exists) return "（还没有日志）";
                var files = dir.GetFiles("tiamc-*.log");
                var total = files.Sum(f => f.Length);
                return $"{files.Length} 个文件 · {total / 1024.0 / 1024.0:0.0} MB · 目录 {dir.FullName}";
            }
            catch (Exception e)
            {
                return "读取失败: " + e.Message;
            }
        }
    }

    /// <summary>清空日志目录里除当前会话外的所有日志。</summary>
    public void ClearLogFiles()
    {
        try
        {
            var dir = new System.IO.DirectoryInfo(Services.LogService.LogDirectory);
            if (!dir.Exists) return;

            var current = Services.LogService.FilePath;
            var removed = 0;
            foreach (var file in dir.GetFiles("tiamc-*.log"))
            {
                if (current.Length > 0 && string.Equals(file.FullName, current, StringComparison.OrdinalIgnoreCase)) continue;
                try { file.Delete(); removed++; } catch (Exception) { }
            }

            Services.LogService.Info($"已清理 {removed} 个旧日志文件", "日志");
        }
        catch (Exception e)
        {
            Services.LogService.Warn("清理日志失败: " + e.Message, "日志");
        }

        Raise(nameof(LogUsageText));
    }

    public bool AutoCheckFiles
    {
        get => Config.AutoCheckFiles;
        set { if (Config.AutoCheckFiles == value) return; Config.AutoCheckFiles = value; Raise(); }
    }

    public bool AutoDownloadMissing
    {
        get => Config.AutoDownloadMissing;
        set { if (Config.AutoDownloadMissing == value) return; Config.AutoDownloadMissing = value; Raise(); }
    }

    /// <summary>下载源下拉里的三个选项（对应 PCL 的"自动/官方/镜像"）。</summary>
    public ObservableCollection<string> DownloadSourceModes { get; } = ["自动（推荐）", "官方（Mojang）", "BMCLAPI 镜像（国内快）", "自定义（填地址）"];

    /// <summary>当前下载源；改动后立即生效（下次下载就用新源）。</summary>
    public string SelectedDownloadSource
    {
        get => Config.DownloadSource switch
        {
            TiaMc.Core.Integrity.DownloadSource.Official => "官方（Mojang）",
            TiaMc.Core.Integrity.DownloadSource.BmclApi => "BMCLAPI 镜像（国内快）",
            TiaMc.Core.Integrity.DownloadSource.Custom => "自定义（填地址）",
            _ => "自动（推荐）"
        };
        set
        {
            Config.DownloadSource = value switch
            {
                "官方（Mojang）" => TiaMc.Core.Integrity.DownloadSource.Official,
                "BMCLAPI 镜像（国内快）" => TiaMc.Core.Integrity.DownloadSource.BmclApi,
                "自定义（填地址）" => TiaMc.Core.Integrity.DownloadSource.Custom,
                _ => TiaMc.Core.Integrity.DownloadSource.Auto
            };
            Config.Save();
            TiaMc.Core.Integrity.IntegrityChecker.CustomBaseUrl = Config.DownloadSourceCustom;   // 自定义源生效
            Raise();
            Raise(nameof(UseBmclApi));
            Raise(nameof(UseOfficialSource));
            Raise(nameof(DownloadSourceText));
            TiaMc.App.Services.LogService.Info("下载源已切换为 " + value, "下载");
        }
    }

    /// <summary>资源（模组/资源包）来源选项。</summary>
    public ObservableCollection<string> ResourceSourceModes { get; } = ["Modrinth 官方", "自定义镜像（填基址）"];

    public string SelectedResourceSource
    {
        get => Config.ResourceSource == "custom" ? "自定义镜像（填基址）" : "Modrinth 官方";
        set
        {
            Config.ResourceSource = value.StartsWith("自定义") ? "custom" : "modrinth";
            Config.Save();
            Raise();
            TiaMc.App.Services.LogService.Info("资源来源已切换为 " + value, "资源");
        }
    }

    /// <summary>自定义 Modrinth 镜像基址（形如 https://example.com/v2）。</summary>
    public string ResourceMirror
    {
        get => Config.ResourceMirror;
        set
        {
            if (Config.ResourceMirror == value) return;
            Config.ResourceMirror = value ?? "";
            Config.Save();
            Raise();
        }
    }

    /// <summary>自定义下载源基址（选「自定义」时生效）。</summary>
    public string DownloadSourceCustomBase
    {
        get => Config.DownloadSourceCustom;
        set
        {
            if (Config.DownloadSourceCustom == value) return;
            Config.DownloadSourceCustom = value ?? "";
            Config.Save();
            TiaMc.Core.Integrity.IntegrityChecker.CustomBaseUrl = Config.DownloadSourceCustom;
            Raise();
        }
    }

    public bool UseBmclApi
    {
        get => Config.DownloadSource == DownloadSource.BmclApi;
        set
        {
            var source = value ? DownloadSource.BmclApi : DownloadSource.Official;
            if (Config.DownloadSource == source) return;
            Config.DownloadSource = source;
            Raise();
            Raise(nameof(DownloadSourceText));
        }
    }

    public string DownloadSourceText => Config.DownloadSource == DownloadSource.BmclApi
        ? "BMCLAPI 镜像"
        : "Mojang 官方";

    /// <summary>Inverse switch used by the "下载源" submenu (radio style entries).</summary>
    public bool UseOfficialSource
    {
        get => Config.DownloadSource == DownloadSource.Official;
        set
        {
            if (!value) return;
            Config.DownloadSource = DownloadSource.Official;
            Raise();
            Raise(nameof(UseBmclApi));
            Raise(nameof(DownloadSourceText));
        }
    }

    public bool IsGameRunning => _launcher.IsRunning;

    private string _downloadSpeedText = "";

    /// <summary>状态栏上的下载速度，形如 "3.2 MB/s · 剩余 12 秒"。</summary>
    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        private set { if (Set(ref _downloadSpeedText, value)) Raise(); }
    }

    /// <summary>输出窗口代码高亮开关（切换后界面立刻重绘）。</summary>
    public bool LogHighlight
    {
        get => Config.LogHighlight;
        set
        {
            if (Config.LogHighlight == value) return;
            Config.LogHighlight = value;
            Config.Save();
            Raise();
            LogHighlightChanged?.Invoke();
        }
    }

    /// <summary>高亮开关变化时通知界面重绘输出窗口。</summary>
    public event Action? LogHighlightChanged;

    /// <summary>暗黑模式（代码高亮配色）。</summary>
    public bool DarkMode
    {
        get => Config.DarkMode;
        set
        {
            if (Config.DarkMode == value) return;
            Config.DarkMode = value;
            Config.Save();
            TiaMc.App.Services.ThemeService.Apply(Config.DarkMode, Config.Skin);
            Raise();
            LogService.Info(value ? "已切换到暗黑模式（代码高亮配色）" : "已切回浅色模式", "主题");
        }
    }

    public ObservableCollection<string> Skins { get; } =
        new(TiaMc.App.Services.ThemeService.Skins.Select(s => s.Name));

    /// <summary>皮肤（强调色）。</summary>
    public string SelectedSkin
    {
        get => Config.Skin;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || Config.Skin == value) return;
            Config.Skin = value;
            Config.Save();
            TiaMc.App.Services.ThemeService.Apply(Config.DarkMode, Config.Skin);
            Raise();
            LogService.Info("已切换皮肤: " + value, "主题");
        }
    }

    /// <summary>工具菜单里选皮肤（参数是皮肤名）。</summary>
    public RelayCommand SelectSkinCommand { get; private set; } = null!;

    /// <summary>把当前皮肤推到"进游戏也生效"：正版→上传 Mojang；离线/外置→内置认证服务端下发。</summary>
    public AsyncRelayCommand UploadSkinCommand { get; private set; } = null!;

    private async Task UploadSkinToGameAsync()
    {
        var account = SelectedAccount;
        if (account is null)
        {
            LogService.Warn("请先选择账户", "皮肤");
            return;
        }

        // 本地皮肤来源：账户自己的文件 → 本地缓存最新一张
        var path = account.HasLocalSkin ? account.SkinPath : null;
        byte[]? png = null;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) png = await File.ReadAllBytesAsync(path!);

        if (png is null && Skin3DTexture is System.Windows.Media.Imaging.BitmapSource bmp)
        {
            png = EncodePng(bmp);
        }

        if (png is null)
        {
            LogService.Warn("没有可用的本地皮肤文件：先在皮肤页选择皮肤文件或从皮肤站获取", "皮肤");
            return;
        }

        if (account.Kind == TiaMc.Core.Accounts.AccountKind.Microsoft)
        {
            var ready = await _launcher.EnsureAccountReadyAsync(SelectedVersion!).ConfigureAwait(false);
            var token = account.AccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                LogService.Error("正版令牌不可用，请重新登录后再上传皮肤", "皮肤");
                return;
            }

            var variant = string.Equals(account.SkinVariant, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";
            var result = await _launcher.MicrosoftAuth
                .UploadSkinAsync(token!, png!, variant)
                .ConfigureAwait(false);

            if (result.Ok) LogService.Ok(result.Message, "皮肤");
            else LogService.Error(result.Message, "皮肤");
            return;
        }

        // 离线 / 外置登录：皮肤由启动器的认证服务端 + authlib-injector 下发给游戏
        PushSkinToServer();
        LogService.Info("离线账户：皮肤已交给内置认证服务端；启动游戏时会自动挂 authlib-injector，进游戏即为此皮肤", "皮肤");
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(IsNotBusy));
            (CheckFilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            // 关键：所有 "() => !IsBusy" 的命令都必须重新查询可用状态。
            // 以前只通知了 CheckFilesCommand，于是启动过程中被判为不可用的按钮
            // （例如「Microsoft 正版登录」）会一直灰着、点不动。
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsNotBusy => !IsBusy;

    public LauncherState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StateText));
            Raise(nameof(StateKey));
            Raise(nameof(IsGameRunning));
            (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string StateText => State switch
    {
        LauncherState.Checking => "校验中",
        LauncherState.Downloading => "下载中",
        LauncherState.Launching => "启动中",
        LauncherState.Running => "运行中",
        LauncherState.Failed => "错误",
        _ => "就绪"
    };

    public string StateKey => State switch
    {
        LauncherState.Running => "Tia.Run",
        LauncherState.Failed => "Tia.Error",
        LauncherState.Downloading or LauncherState.Checking or LauncherState.Launching => "Tia.Warn",
        _ => "Tia.StatusText"
    };

    public string StateMessage
    {
        get => _stateMessage;
        private set => Set(ref _stateMessage, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => Set(ref _downloadStatus, value);
    }

    public double DownloadPercent
    {
        get => _downloadPercent;
        private set => Set(ref _downloadPercent, value);
    }

    public int WorkspaceTab
    {
        get => _workspaceTab;
        set
        {
            if (!Set(ref _workspaceTab, value)) return;
            // The authentication server page is a terminal: show the banner once.
            if (value == 7) EnsureYggBanner();
            if (value == 8) RefreshModpacks();
            if (value == 9) RefreshResourceVersions();
        }
    }

    public string SelectedLogLevel
    {
        get => _selectedLogLevel;
        set
        {
            if (!Set(ref _selectedLogLevel, value)) return;
            RefreshLogs();
        }
    }

    public bool AutoScrollConsole
    {
        get => _autoScrollConsole;
        set
        {
            if (!Set(ref _autoScrollConsole, value)) return;
            Console.AutoScroll = value;
        }
    }

    // ------------------------------------------------------- selection data

    public VersionChoice? ActiveChoice
    {
        get => _activeChoice;
        set
        {
            if (!Set(ref _activeChoice, value)) return;
            SelectedVersion = _launcher.Find(value?.Id);
        }
    }

    public InstalledVersion? SelectedVersion
    {
        get => _selectedVersion;
        private set
        {
            if (!Set(ref _selectedVersion, value)) return;
            _lastCheck = null;
            _lastPlan = null;
            PlanRows.Clear();
            Mods.Clear();
            ModrinthHits.Clear();
            SelectedMod = null;
            Raise(nameof(ModsSummary));
            Raise(nameof(HasMods));
            RaiseInstanceSettings();
            ReloadJvmArgs();
        RefreshDetailFields();
            BuildPlan();
            _ = RefreshModsAsync();
            (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CheckFilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DownloadMissingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CopyCommandLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string DetailVersion => SelectedVersion?.Id ?? "(未选择版本)";
    public string DetailLoader => SelectedVersion?.Loader ?? "-";
    public string DetailType => SelectedVersion?.Json.Type ?? "-";
    public string DetailJavaRequired
    {
        get
        {
            var required = SelectedVersion?.Json.JavaVersion?.MajorVersion ?? 0;
            return required > 0 ? $"Java {required}" : "-";
        }
    }

    public string DetailLibraryCount => SelectedVersion is null
        ? "-"
        : $"{VersionRepository.DeduplicateLibraries(SelectedVersion.Json.Libraries).Count}";

    public string DetailJarPath => SelectedVersion?.JarPath ?? "";
    public string DetailJsonPath => SelectedVersion?.JsonPath ?? "";
    public string DetailChain => SelectedVersion is null ? "-" : string.Join("  <-  ", SelectedVersion.Chain);

    public string DetailStatus => _lastCheck is null
        ? "未校验"
        : _lastCheck.IsComplete ? "文件完整" : $"缺失 {_lastCheck.Missing.Count} 项";

    public string DetailStatusKey => _lastCheck is null
        ? "Tia.TextMuted"
        : _lastCheck.IsComplete ? "Tia.Ok" : "Tia.Warn";

    public string DetailCheckSummary => _lastCheck?.Summary ?? "尚未执行完整性校验";
    public string DetailPlanSummary => _lastPlan?.Summary ?? "点击「生成启动方案」查看命令构成";
    public string MainClassText => _lastPlan?.MainClass ?? "-";
    public string NativesText => _lastPlan?.NativesDirectory ?? "-";

    public JavaInfo? SelectedJava
    {
        get => _selectedJava;
        set
        {
            if (!Set(ref _selectedJava, value)) return;
            if (value is not null)
            {
                // Stored on the instance so two versions can use different runtimes.
                Current.JavaPath = value.Path;
                InstanceChanged();
            }

            Raise(nameof(JavaText));
            BuildPlan();
        }
    }

    public string JavaText => SelectedJava?.Display ?? "未指定（自动匹配）";

    // ------------------------------------------- 在线版本：正式版 / 测试版分开

    private string _remoteTypeFilter = "全部";
    private string _remoteSearch = "";

    public ObservableCollection<string> RemoteTypeFilters { get; } =
        ["全部", "正式版", "快照（测试版）", "旧版（Beta/Alpha）", "已安装"];

    public ObservableCollection<RemoteVersionRow> FilteredRemoteVersions { get; } = [];

    /// <summary>正式版 / 测试版 / 旧版筛选。</summary>
    public string RemoteTypeFilter
    {
        get => _remoteTypeFilter;
        set
        {
            if (!Set(ref _remoteTypeFilter, value)) return;
            ApplyRemoteFilter();
        }
    }

    public string RemoteSearch
    {
        get => _remoteSearch;
        set
        {
            if (!Set(ref _remoteSearch, value)) return;
            ApplyRemoteFilter();
        }
    }

    /// <summary>Counts shown next to the filter so the split is obvious.</summary>
    public string RemoteTypeSummary
    {
        get
        {
            var releases = RemoteVersions.Count(v => v.IsRelease);
            var snapshots = RemoteVersions.Count(v => v.IsSnapshot);
            var legacy = RemoteVersions.Count(v => v.IsLegacy);
            return $"正式版 {releases} · 快照/测试版 {snapshots} · 旧版 {legacy} · 已安装 " +
                   $"{RemoteVersions.Count(v => v.Installed)}";
        }
    }

    /// <summary>Rebuilds the filtered online version list.</summary>
    private void ApplyRemoteFilter()
    {
        FilteredRemoteVersions.Clear();

        foreach (var row in RemoteVersions)
        {
            var keep = _remoteTypeFilter switch
            {
                "正式版" => row.IsRelease,
                "快照（测试版）" => row.IsSnapshot,
                "旧版（Beta/Alpha）" => row.IsLegacy,
                "已安装" => row.Installed,
                _ => true
            };

            if (!keep) continue;
            if (_remoteSearch.Length > 0 &&
                !row.Id.Contains(_remoteSearch, StringComparison.OrdinalIgnoreCase)) continue;

            FilteredRemoteVersions.Add(row);
        }

        Raise(nameof(RemoteTypeSummary));
        Raise(nameof(RemoteFilterText));
    }

    public string RemoteFilterText => _remoteTypeFilter == "全部" && _remoteSearch.Length == 0
        ? $"在线版本 {FilteredRemoteVersions.Count} 个"
        : $"筛选后 {FilteredRemoteVersions.Count} 个（{_remoteTypeFilter}" +
          (_remoteSearch.Length > 0 ? $" + \"{_remoteSearch}\"" : "") + "）";
    public string RemoteLatestText => _launcher.RemoteManifest?.Latest?.Release is { } release
        ? $"最新正式版 {release}"
        : "未获取清单";

    private RemoteVersionRow? _selectedRemoteVersion;

    /// <summary>The online version row currently selected in the version table.</summary>
    public RemoteVersionRow? SelectedRemoteVersion
    {
        get => _selectedRemoteVersion;
        set => Set(ref _selectedRemoteVersion, value);
    }

    public TreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value)) return;
            // Selecting an instance node in the tree selects it in the workspace too.
            if (value?.NodeKind == "instance" && value.Tag is { Length: > 0 } id)
            {
                var match = VersionChoices.FirstOrDefault(c =>
                    string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    ActiveChoice = match;
                    SelectedVersion = _launcher.Find(match.Id);
                }
            }
            else if (value?.NodeKind == "page" && value.Tag is { Length: > 0 } tag &&
                     int.TryParse(tag, out var tab))
            {
                WorkspaceTab = tab;
            }
        }
    }

    // ------------------------------------------------------------- commands

    public ICommand RefreshCommand { get; }
    public ICommand DetectJavaCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CheckFilesCommand { get; }
    public ICommand DownloadMissingCommand { get; }
    public ICommand BuildPlanCommand { get; }
    public ICommand SaveConfigCommand { get; }
    public ICommand BrowseRootCommand { get; }
    public ICommand AutoDetectRootCommand { get; }
    public ICommand ApplyInstanceAsGlobalCommand { get; }
    public ICommand ResetInstanceCommand { get; }
    public ICommand BrowseJavaCommand { get; }
    public ICommand CopyConsoleCommand { get; }
    public ICommand ClearConsoleCommand { get; }
    public ICommand CopyCommandLineCommand { get; }
    public ICommand RefreshManifestCommand { get; }
    public ICommand InstallVersionCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand AddOfflineAccountCommand { get; }
    public ICommand AddMicrosoftAccountCommand { get; }
    public ICommand RefreshAccountCommand { get; }
    public ICommand RemoveAccountCommand { get; }
    public ICommand RefreshModsCommand { get; }
    public ICommand ToggleModCommand { get; }
    public ICommand DeleteModCommand { get; }
    public ICommand ImportModsCommand { get; }
    public ICommand OpenModsFolderCommand { get; }
    public ICommand BrowseModsFolderCommand { get; }
    public ICommand OpenModPageCommand { get; }
    public ICommand SearchModrinthCommand { get; }
    public ICommand InstallModrinthCommand { get; }

    // -------------------------------------------------------------- startup

    private async Task InitializeAsync()
    {
        LogService.Info($"{AppInfo.LauncherTitle} 已启动");
        LogService.Info($"Minecraft 目录: {RootPath}");
        LogService.Info($"配置目录: {AppConfig.ConfigDirectory}");

        // Java detection scans the disk, so it runs after the shell is already
        // usable and does not block the initial file check.
        var javaTask = DetectJavaAsync();
        var checkTask = Config.AutoCheckFiles ? CheckFilesAsync() : Task.CompletedTask;

        await Task.WhenAll(javaTask, checkTask);

        // First run (or an empty folder): look for a usable installation.
        await AutoDetectOnStartupAsync();

        // 启动完成后自动拉一次皮肤预览（本地 3D + 在线多角度渲染）
        _ = PreviewSkin3DAsync();
        _ = RefreshSkin3DRenderAsync();

        Console.AppendHeader("就绪");

        // The startup diagnostics happen before the window is on screen, so they
        // are replayed into the output window once everything has settled.
        await Task.Delay(400);
        SyncLogToConsole();
        (ExportDiagnosticsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Replays the diagnostics log into the output window (single line per entry).</summary>
    private void SyncLogToConsole()
    {
        var lines = LogService.Entries
            .Select(e => $"{e.TimeText} [{e.LevelText,-4}] {e.Source,-6} {e.Message}")
            .ToList();

        foreach (var line in lines)
        {
            Console.Append(line);
        }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                _launcher.ReloadInstallation();
            });

            RefreshInstalled();
            RebuildTree();
            BuildPlan();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshInstalled()
    {
        VersionChoices.Clear();
        foreach (var version in _launcher.Installed)
        {
            VersionChoices.Add(new VersionChoice { Id = version.Id, Loader = version.Loader });
        }

        var preferred = Config.ActiveInstance;
        var choice = VersionChoices.FirstOrDefault(c => string.Equals(c.Id, preferred, StringComparison.OrdinalIgnoreCase))
                     ?? VersionChoices.FirstOrDefault();

        // Assign the *instance that lives in the collection*: a freshly built
        // VersionChoice is not reference equal to the one the ComboBox/DataGrid
        // holds, so binding it would silently leave the selection empty.
        ActiveChoice = choice ?? ActiveChoice;

        RebuildRemoteInstalledFlags();
    }

    private void RefreshJavaChoices()
    {
        JavaChoices.Clear();
        foreach (var java in _launcher.JavaRuntimes) JavaChoices.Add(java);

        // Prefer the runtime configured for the selected instance.
        var wanted = Current.JavaPath ?? Config.JavaPath;
        var configured = JavaChoices.FirstOrDefault(j =>
            string.Equals(j.Path, wanted, StringComparison.OrdinalIgnoreCase));
        _selectedJava = configured;
        Raise(nameof(SelectedJava));
        Raise(nameof(JavaText));
    }

    private async Task DetectJavaAsync()
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => _launcher.DetectJava());
            RefreshJavaChoices();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckFilesAsync()
    {
        if (SelectedVersion is null) return;
        IsBusy = true;
        try
        {
            var version = SelectedVersion;
            var result = await Task.Run(() => _launcher.CheckFiles(version));
            _lastCheck = result;
            Raise(nameof(DetailStatus));
            Raise(nameof(DetailStatusKey));
            Raise(nameof(DetailCheckSummary));
            (DownloadMissingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadMissingAsync()
    {
        if (_lastCheck is not { IsComplete: false }) return;

        IsBusy = true;
        try
        {
            var version = SelectedVersion;
            if (version is null) return;
            await _launcher.DownloadAllAsync(version, new Progress<DownloadProgress>(p =>
            {
                DownloadStatus = p.Summary;
                DownloadPercent = p.Percent;
            }), CancellationToken.None);

            DownloadStatus = "";
            DownloadPercent = 0;

            _lastCheck = await Task.Run(() => _launcher.CheckFiles(version));
            Raise(nameof(DetailStatus));
            Raise(nameof(DetailStatusKey));
            Raise(nameof(DetailCheckSummary));
            (DownloadMissingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshManifestAsync()
    {
        IsBusy = true;
        try
        {
            await _launcher.RefreshRemoteManifestAsync();
            RebuildRemoteVersions();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RebuildRemoteVersions()
    {
        // Keep the row the user is looking at: the table is rebuilt whenever the
        // manifest is refreshed or an install finishes.
        var previous = SelectedRemoteVersion?.Id;

        RemoteVersions.Clear();
        if (_launcher.RemoteManifest is null) return;

        var installed = _launcher.Installed.Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var version in _launcher.RemoteManifest.Versions)
        {
            RemoteVersions.Add(new RemoteVersionRow
            {
                Source = version,
                Installed = installed.Contains(version.Id)
            });
        }

        if (previous is { Length: > 0 })
        {
            SelectedRemoteVersion = RemoteVersions.FirstOrDefault(r =>
                string.Equals(r.Id, previous, StringComparison.OrdinalIgnoreCase));
        }

        ApplyRemoteFilter();
        Raise(nameof(RemoteLatestText));
    }

    private void RebuildRemoteInstalledFlags()
    {
        if (RemoteVersions.Count == 0) return;

        var previous = SelectedRemoteVersion?.Id;
        var installed = _launcher.Installed.Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rebuilt = RemoteVersions
            .Select(row => new RemoteVersionRow { Source = row.Source, Installed = installed.Contains(row.Source.Id) })
            .ToList();

        RemoteVersions.Clear();
        foreach (var row in rebuilt) RemoteVersions.Add(row);

        if (previous is { Length: > 0 })
        {
            SelectedRemoteVersion = RemoteVersions.FirstOrDefault(r =>
                string.Equals(r.Id, previous, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task InstallVersionAsync(object? parameter)
    {
        if (parameter is not RemoteVersionRow row) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                DownloadStatus = p.Summary;
                DownloadPercent = p.Percent;
            });

            await _launcher.InstallVanillaAsync(row.Source, progress);

            RefreshInstalled();
            RebuildTree();
            RebuildRemoteVersions();

            // A freshly installed version still lacks its libraries and assets, so
            // the installer runs the full "check + download" pass right away
            // instead of leaving the user with a broken instance.
            var installed = _launcher.Find(row.Id);
            if (installed is not null)
            {
                ActiveChoice = VersionChoices.FirstOrDefault(c => c.Id == installed.Id);
                SelectedVersion = installed;
                LogService.Info($"{row.Id} 开始补全库与资源文件...", "Install");
                await _launcher.DownloadAllAsync(installed, progress, CancellationToken.None);
                _lastCheck = await Task.Run(() => _launcher.CheckFiles(installed));

                // 新版本可能要新的 Java（例如 1.21 需要 21），顺手补齐。
                await _launcher.EnsureJavaAsync(installed);
                Ui.Post(() => { RefreshJavaChoices(); Raise(nameof(JavaProvisionStatus)); });
                Raise(nameof(DetailStatus));
                Raise(nameof(DetailStatusKey));
                Raise(nameof(DetailCheckSummary));
                BuildPlan();
            }

            DownloadStatus = "";
            DownloadPercent = 0;
        }
        catch (Exception e)
        {
            LogService.Error($"安装 {row.Id} 失败: {e.Message}", "Install");
            if (!string.IsNullOrWhiteSpace(e.InnerException?.Message))
            {
                LogService.Error(e.InnerException!.Message, "Install");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildPlan()
    {
        PlanRows.Clear();
        if (SelectedVersion is null) return;

        var plan = _launcher.BuildPlan(SelectedVersion, out var java, out var error);
        if (plan is null)
        {
            LogService.Warn(error ?? "无法生成启动方案", "Plan");
            Raise(nameof(DetailPlanSummary));
            return;
        }

        _lastPlan = plan;
        var exists = (string path) => File.Exists(path);

        PlanRows.Add(new PlanRow { Name = "Java 运行时", Category = "运行时", Value = java?.Display ?? "-" , Status = java is not null ? "就绪" : "缺失" });
        PlanRows.Add(new PlanRow { Name = "主类", Category = "运行时", Value = plan.MainClass });
        PlanRows.Add(new PlanRow { Name = "内存", Category = "运行参数", Value = MemoryText });
        PlanRows.Add(new PlanRow { Name = "垃圾回收器", Category = "运行参数", Value = GcMode });
        PlanRows.Add(new PlanRow { Name = "窗口", Category = "运行参数", Value = Fullscreen ? "全屏" : WindowText });
        PlanRows.Add(new PlanRow { Name = "natives 目录", Category = "运行参数", Value = plan.NativesDirectory, Status = Directory.Exists(plan.NativesDirectory) ? "就绪" : "未解压" });
        PlanRows.Add(new PlanRow { Name = "classpath 条目", Category = "类路径", Value = $"{plan.Classpath.Split(Path.PathSeparator).Length} 项" });
        PlanRows.Add(new PlanRow { Name = "客户端 Jar", Category = "类路径", Value = SelectedVersion.JarPath, Status = exists(SelectedVersion.JarPath) ? "就绪" : "缺失" });
        PlanRows.Add(new PlanRow { Name = "资源索引", Category = "资源", Value = SelectedVersion.Json.AssetIndex?.Id ?? SelectedVersion.Json.Assets ?? "-" });
        PlanRows.Add(new PlanRow { Name = "游戏目录", Category = "路径", Value = plan.GameDirectory });
        PlanRows.Add(new PlanRow { Name = "版本 JSON", Category = "路径", Value = SelectedVersion.JsonPath, Status = exists(SelectedVersion.JsonPath) ? "就绪" : "缺失" });
        PlanRows.Add(new PlanRow { Name = "JVM 参数", Category = "参数", Value = $"{plan.JvmArguments.Count} 项" });
        PlanRows.Add(new PlanRow { Name = "游戏参数", Category = "参数", Value = $"{plan.GameArguments.Count} 项" });

        foreach (var file in _lastCheck?.Missing.Take(20) ?? [])
        {
            PlanRows.Add(new PlanRow { Name = Path.GetFileName(file.Path), Category = "缺失文件", Value = file.Path, Status = "缺失" });
        }

        Raise(nameof(DetailPlanSummary));
        Raise(nameof(MainClassText));
        Raise(nameof(NativesText));
        (CopyCommandLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task LaunchAsync()
    {
        if (SelectedVersion is null) return;
        var version = SelectedVersion;

        try
        {
            IsBusy = true;

            // Refresh an expired Microsoft token before the file check, so a
            // failed login aborts the launch early with a clear message.
            await _launcher.EnsureAccountReadyAsync(version);

            if (Config.AutoCheckFiles)
            {
                await CheckFilesAsync();
                if (_lastCheck is { IsComplete: false })
                {
                    if (Config.AutoDownloadMissing)
                    {
                        await DownloadMissingAsync();

                        // 学 Axolotl / XMCL：补全之后必须**再校验一次**，仍然缺文件就中止启动。
                        // 以前补完直接启动，缺的资源要到游戏里才炸（NoSuchFileException: assets/objects/…），
                        // 用户只看到"游戏崩溃"，根本不知道是文件没下全。
                        if (_lastCheck is { IsComplete: false })
                        {
                            LogService.Warn($"补全后仍缺少 {_lastCheck.Missing.Count} 个文件（{_lastCheck.Summary}），已中止启动，"
                                            + "避免游戏内崩溃。可再点「补全文件」重试；确实要强行启动请在设置里关闭「启动前校验文件」。",
                                "Launch");
                            WorkspaceTab = 1;
                            return;
                        }
                    }
                    else
                    {
                        LogService.Warn("文件不完整，已取消启动。请先执行「补全文件」。", "Launch");
                        WorkspaceTab = 1;
                        return;
                    }
                }
            }

            // 外置登录账户：先把 authlib-injector 准备好，再生成启动方案。
            if (SelectedAccount?.NeedsAuthlibInjector == true)
            {
                var agent = await EnsureAuthlibInjectorAsync();
                if (agent.Length == 0)
                {
                    LogService.Warn("没有 authlib-injector，服务器可能拒绝这次登录（已继续启动）", "外置登录");
                }
            }

            // Java 缺失就自动补齐（可在属性面板关闭），补齐后再生成启动方案。
            var provided = await _launcher.EnsureJavaAsync(version);
            if (provided is not null)
            {
                LogService.Info($"使用 Java {provided.MajorVersion}：{provided.Path}", "Launch");
            }

            var plan = _launcher.BuildPlan(version, out var java, out var error);
            if (plan is null)
            {
                LogService.Error(error ?? "无法生成启动方案", "Launch");
                return;
            }

            _lastPlan = plan;
            Config.ActiveInstance = version.Id;
            Config.Save();

            Console.AppendHeader($"{version.Id}  ({version.Loader})");
            Console.Append($"java : {java?.Path}");
            Console.Append($"cmd  : {GameProcess.ToCommandLine(plan)}");
            WorkspaceTab = 1;

            _launcher.Start(plan);
            BuildPlan();
        }
        catch (MicrosoftAuthException e)
        {
            LogService.Error($"账户不可用，已取消启动: {e.Message}", "Launch");
            WorkspaceTab = 5;
        }
        catch (Exception e)
        {
            LogService.Error($"启动异常: {e.Message}", "Launch");
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(IsGameRunning));
            (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void SaveConfig()
    {
        Config.Save();
        LogService.Ok($"配置已保存到 {Config.FilePath}");
    }

    private void BrowseRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 .minecraft 目录",
            InitialDirectory = Directory.Exists(RootPath) ? RootPath : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };

        if (dialog.ShowDialog() != true) return;
        ApplyRoot(dialog.FolderName, "手动选择");
    }

    /// <summary>Opens the detection dialog and switches to the chosen folder.</summary>
    private void AutoDetectRoot()
    {
        var chosen = Views.McScanWindow.ShowDialog(System.Windows.Application.Current.MainWindow, RootPath);
        if (string.IsNullOrWhiteSpace(chosen)) return;
        ApplyRoot(chosen!, "自动检测");
    }

    /// <summary>Switches the launcher to another Minecraft folder and reloads everything.</summary>
    public void ApplyRoot(string path, string reason)
    {
        if (string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info($"当前已经使用 {path}", "Project");
            return;
        }

        Config.MinecraftRoot = path;
        Config.Save();
        _launcher.ReloadInstallation(path);
        RebuildTree();
        RefreshInstalled();
        BuildPlan();
        Raise(nameof(RootPath));
        Raise(nameof(BreadcrumbText));
        LogService.Ok($"{reason}: 已切换到 {path}（{_launcher.Installed.Count} 个版本）", "Project");
        _ = RefreshModsAsync();
    }

    /// <summary>
    /// First-run convenience: when the configured folder has no versions and
    /// portable mode is off, the detector looks for one that does and switches to
    /// it automatically. In portable mode the launcher stays where it is, so
    /// downloads always land next to the executable.
    /// </summary>
    private async Task AutoDetectOnStartupAsync()
    {
        if (_launcher.Installed.Count > 0) return;

        if (Config.PortableRoot)
        {
            LogService.Info($"便携模式已开启：版本与资源会下载到 {RootPath}", "Portable");
            LogService.Info("需要切换目录时可在「项目 → 自动检测 Minecraft 目录」里选择，或关闭便携模式", "Portable");
            return;
        }

        LogService.Info("当前目录没有已安装版本，开始自动检测其它 Minecraft 目录...", "Detect");
        var found = await Task.Run(() => MinecraftFinder.FindAll(Config.MinecraftRoot, null, scanDrives: true));
        foreach (var candidate in found)
        {
            LogService.Info($"[detect] {candidate.Path}  ({candidate.SourceText}, {candidate.Summary})", "Detect");
        }

        var best = found
            .Where(c => c.VersionCount > 0)
            .OrderByDescending(c => c.Source == McRootSource.Configured)
            .ThenByDescending(c => c.VersionCount)
            .FirstOrDefault();

        if (best is null)
        {
            if (found.Count > 0)
            {
                LogService.Warn($"检测到 {found.Count} 个目录但都没有已安装版本，" +
                                "可在「项目 → 自动检测 Minecraft 目录」里选择或手动指定", "Detect");
            }

            return;
        }

        ApplyRoot(best.Path, "自动检测");
    }

    private void BrowseJava()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 java.exe",
            Filter = "Java 可执行文件 (java.exe)|java.exe|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        var info = JavaDetector.Probe(dialog.FileName);
        if (info is not null && JavaChoices.All(j => !string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase)))
        {
            JavaChoices.Add(info);
        }

        SelectedJava = info;
        Config.JavaPath = dialog.FileName;
        Config.Save();
        BuildPlan();
    }

    private void CopyCommandLine()
    {
        if (_lastPlan is null) return;
        CopyToClipboard(GameProcess.ToCommandLine(_lastPlan));
    }

    private void OpenGameFolder() => OpenFolder(RootPath, "游戏目录");

    private void OpenLogFolder() => OpenFolder(AppConfig.ConfigDirectory, "配置目录");

    /// <summary>Opens a folder in Explorer, creating it when the game has not made it yet.</summary>
    private void OpenFolder(string path, string what)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception e)
        {
            LogService.Warn($"无法打开{what}: {e.Message}");
        }
    }

    // ------------------------------------------------- secondary menu actions

    /// <summary>Marks the instance picked in the version table as the active one.</summary>
    public void SetActiveInstance()
    {
        if (SelectedVersion is null)
        {
            LogService.Warn("请先在版本管理中选择一个实例", "Project");
            return;
        }

        Config.ActiveInstance = SelectedVersion.Id;
        Config.Save();
        ActiveChoice = VersionChoices.FirstOrDefault(c =>
            string.Equals(c.Id, SelectedVersion.Id, StringComparison.OrdinalIgnoreCase)) ?? ActiveChoice;
        LogService.Ok($"当前实例已设为 {SelectedVersion.Id}", "Project");
    }

    public void CopyGamePath() => CopyToClipboard(RootPath);

    /// <summary>
    /// Called when the version table (or its context menu) selects an instance:
    /// makes it the active instance and refreshes the workspace/property panel.
    /// </summary>
    public void SelectVersionFromTable()
    {
        if (ActiveChoice is null)
        {
            SelectedVersion = null;
            return;
        }

        var version = _launcher.Find(ActiveChoice.Id);
        if (version is null)
        {
            LogService.Warn($"{ActiveChoice.Id} 已不在 versions 目录中，请刷新目录树", "Project");
            return;
        }

        SelectedVersion = version;
        Raise(nameof(DetailVersion));
    }

    public void CopyInstancePath()
    {
        if (SelectedVersion is null) return;
        CopyToClipboard(_launcher.Paths.VersionDir(SelectedVersion.Id));
    }

    public void CopyInstanceJson()
    {
        if (SelectedVersion is null) return;
        CopyToClipboard(SelectedVersion.JsonPath);
    }

    public void OpenInstanceFolder()
    {
        if (SelectedVersion is null)
        {
            OpenFolder(RootPath, "游戏目录");
            return;
        }

        OpenFolder(_launcher.Paths.VersionDir(SelectedVersion.Id), "实例目录");
    }

    /// <summary>Opens the shared root mods folder (the instance one is used by the mods tab).</summary>
    public void OpenRootModsFolder() => OpenFolder(Path.Combine(RootPath, "mods"), "mods 目录");

    public void OpenSavesFolder() => OpenFolder(Path.Combine(RootPath, "saves"), "saves 目录");

    /// <summary>Opens the install confirmation dialog for the remote version row.</summary>
    public async Task PromptInstallAsync(RemoteVersionRow? row)
    {
        if (row is null) return;
        if (row.Installed)
        {
            LogService.Info($"{row.Id} 已安装，跳过下载", "Install");
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            $"即将下载并安装原版 {row.Id}（{row.Type}）到\n{RootPath}\n\n" +
            "只会写入 versions 目录、客户端 Jar 与资源索引；游戏库和资源文件会在随后的「校验文件」中按需补全。\n\n是否继续？",
            "安装 Minecraft 原版",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.OK) return;

        await InstallVersionAsync(row);
    }

    /// <summary>Writes the diagnostics log to a file next to the configuration.</summary>
    private void ExportDiagnostics()
    {
        try
        {
            var path = Path.Combine(AppConfig.ConfigDirectory,
                $"tiamc-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, LogService.Dump());
            LogService.Ok($"诊断日志已导出: {path}");
        }
        catch (Exception e)
        {
            LogService.Error($"导出诊断日志失败: {e.Message}");
        }
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            LogService.Ok("已复制到剪贴板");
        }
        catch (Exception e)
        {
            LogService.Warn($"复制失败: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ mods

    /// <summary>Rescans the mods folder of the selected instance.</summary>
    private async Task RefreshModsAsync()
    {
        if (SelectedVersion is null)
        {
            ModsPath = "(未选择实例)";
            return;
        }

        var version = SelectedVersion;
        var path = string.IsNullOrWhiteSpace(Config.ModsPath)
            ? ModsManager.ResolveDirectory(_launcher.Paths, version.Id, Config.IsolateInstances)
            : Config.ModsPath;

        ModsPath = path;

        var scanned = await Task.Run(() => ModsManager.Scan(path, message => LogService.Info(message, "Mods")));

        var previous = SelectedMod?.FileName;
        Mods.Clear();
        foreach (var mod in scanned) Mods.Add(mod);
        _modCountEnabled = scanned.Count(m => m.Enabled);
        _modCountDisabled = scanned.Count - _modCountEnabled;

        Raise(nameof(FilteredMods));
        Raise(nameof(ModsSummary));
        Raise(nameof(HasMods));
        SelectedMod = Mods.FirstOrDefault(m =>
            string.Equals(m.FileName, previous, StringComparison.OrdinalIgnoreCase)) ?? Mods.FirstOrDefault();

        LogService.Info($"{version.Id} 的 mods 目录: {path}（{scanned.Count} 个文件，" +
                        $"启用 {_modCountEnabled} / 停用 {_modCountDisabled}）", "Mods");
    }

    /// <summary>
    /// Installs a Modrinth search hit. The hit only carries display data, so the
    /// version list is fetched again and the entry matching the hit is picked.
    /// </summary>
    public async Task InstallModrinthByHitAsync(ModrinthSearchHit hit)
    {
        if (hit is null || SelectedVersion is null) return;

        try
        {
            IsModBusy = true;
            var versions = await _launcher.Modrinth.GetVersionsAsync(hit.Slug,
                SelectedVersion.Id, ModrinthClient.LoaderFacet(SelectedVersion.Loader));

            var version = versions.FirstOrDefault(v => v.Id == hit.ProjectId)
                          ?? versions.FirstOrDefault();

            if (version is null)
            {
                LogService.Warn($"{hit.Slug} 没有适配 {SelectedVersion.Id} 的文件", "Modrinth");
                return;
            }

            await InstallModrinthAsync(version);
        }
        catch (Exception e)
        {
            LogService.Error($"下载失败: {e.Message}", "Modrinth");
        }
        finally
        {
            IsModBusy = false;
        }
    }

    private void ToggleMod()
    {
        if (SelectedMod is null) return;

        try
        {
            var updated = ModsManager.SetEnabled(SelectedMod, !SelectedMod.Enabled,
                message => LogService.Info(message, "Mods"));
            var index = Mods.IndexOf(SelectedMod);
            if (index >= 0) Mods[index] = updated;
            SelectedMod = updated;

            _modCountEnabled = Mods.Count(m => m.Enabled);
            _modCountDisabled = Mods.Count - _modCountEnabled;
            Raise(nameof(FilteredMods));
            Raise(nameof(ModsSummary));
            Raise(nameof(HasMods));
            LogService.Ok($"{(updated.Enabled ? "已启用" : "已停用")} {updated.DisplayName}", "Mods");
        }
        catch (Exception e)
        {
            LogService.Error($"切换失败: {e.Message}", "Mods");
        }
    }

    private void DeleteMod()
    {
        if (SelectedMod is null) return;

        var answer = System.Windows.MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            $"确定要删除 {SelectedMod.FileName} 吗？\n\n此操作会直接从磁盘删除该文件。",
            "删除模组", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;

        try
        {
            ModsManager.Delete(SelectedMod, message => LogService.Info(message, "Mods"));
            Mods.Remove(SelectedMod);
            _modCountEnabled = Mods.Count(m => m.Enabled);
            _modCountDisabled = Mods.Count - _modCountEnabled;
            SelectedMod = Mods.FirstOrDefault();
            Raise(nameof(FilteredMods));
            Raise(nameof(ModsSummary));
            Raise(nameof(HasMods));
        }
        catch (Exception e)
        {
            LogService.Error($"删除失败: {e.Message}", "Mods");
        }
    }

    private async Task ImportModsAsync()
    {
        if (SelectedVersion is null)
        {
            LogService.Warn("请先选择一个实例", "Mods");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要安装的模组（可多选）",
            Filter = "模组文件 (*.jar)|*.jar|压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        var target = string.IsNullOrWhiteSpace(ModsPath)
            ? ModsManager.ResolveDirectory(_launcher.Paths, SelectedVersion.Id, Config.IsolateInstances)
            : ModsPath;

        try
        {
            var copied = await Task.Run(() => ModsManager.Import(dialog.FileNames, target,
                message => LogService.Info(message, "Mods")));
            LogService.Ok($"已导入 {copied} 个模组文件到 {target}", "Mods");
            await RefreshModsAsync();
        }
        catch (Exception e)
        {
            LogService.Error($"导入失败: {e.Message}", "Mods");
        }
    }

    private void OpenModsFolder()
    {
        if (string.IsNullOrWhiteSpace(ModsPath))
        {
            OpenFolder(Path.Combine(RootPath, "mods"), "mods 目录");
            return;
        }

        OpenFolder(ModsPath, "mods 目录");
    }

    /// <summary>Opens the mods folder from the 项目 menu (shared root folder).</summary>
    public void OpenRootModsFolderPublic() => OpenRootModsFolder();

    private void BrowseModsFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 mods 目录（留空则按实例自动解析）",
            InitialDirectory = Directory.Exists(ModsPath) ? ModsPath : RootPath
        };

        if (dialog.ShowDialog() != true) return;
        Config.ModsPath = dialog.FolderName;
        Config.Save();
        _ = RefreshModsAsync();
    }

    private void OpenModPage()
    {
        var url = SelectedMod switch
        {
            { Homepage: { Length: > 0 } page } => page,
            { Issues: { Length: > 0 } issues } => issues,
            _ => null
        };

        if (url is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception e)
        {
            LogService.Warn($"无法打开链接: {e.Message}", "Mods");
        }
    }

    /// <summary>Searches Modrinth for the term in the browser box, filtered by the instance.</summary>
    private async Task SearchModrinthAsync()
    {
        if (string.IsNullOrWhiteSpace(ModrinthProject)) return;

        IsModBusy = true;
        try
        {
            var gameVersion = SelectedVersion?.Id;
            var loader = ModrinthClient.LoaderFacet(SelectedVersion?.Loader ?? "vanilla");
            LogService.Info($"在 Modrinth 查询 “{ModrinthProject}”（{gameVersion} / {loader ?? "任意"}）...", "Modrinth");

            var versions = await _launcher.Modrinth.GetVersionsAsync(ModrinthProject, gameVersion, loader);
            ModrinthHits.Clear();
            foreach (var version in versions.Take(40)) ModrinthHits.Add(ToHit(version, ModrinthProject));

            if (ModrinthHits.Count == 0)
            {
                LogService.Warn("没有匹配的版本（可能不支持当前游戏版本或装载器）", "Modrinth");
            }
            else
            {
                LogService.Ok($"找到 {ModrinthHits.Count} 个可下载版本", "Modrinth");
            }
        }
        catch (Exception e)
        {
            LogService.Error($"Modrinth 查询失败: {e.Message}", "Modrinth");
            ModrinthHits.Clear();
        }
        finally
        {
            IsModBusy = false;
        }
    }

    /// <summary>Modrinth hits are shown through the same table as local mods.</summary>
    private static ModrinthSearchHit ToHit(ModrinthVersion version, string project) => new()
    {
        ProjectId = version.Id,
        Slug = project,
        Title = version.Name,
        Description = $"{version.VersionNumber}  ·  {version.LoadersText}  ·  {version.SizeText}",
        Downloads = version.Downloads
    };

    private async Task InstallModrinthAsync(object? parameter)
    {
        if (parameter is not ModrinthVersion version || SelectedVersion is null) return;
        var file = version.MainFile;
        if (file is null)
        {
            LogService.Warn("该版本没有可下载文件", "Modrinth");
            return;
        }

        var target = string.IsNullOrWhiteSpace(ModsPath)
            ? ModsManager.ResolveDirectory(_launcher.Paths, SelectedVersion.Id, Config.IsolateInstances)
            : ModsPath;

        IsModBusy = true;
        try
        {
            LogService.Info($"正在下载 {file.FileName} ...", "Modrinth");
            var progress = new Progress<long>(bytes =>
                DownloadStatus = $"Modrinth: {TiaMc.Core.Utils.TextUtil.FormatBytes(bytes)}");
            var path = await _launcher.Modrinth.DownloadAsync(file, target, progress);
            DownloadStatus = "";
            LogService.Ok($"已安装 {Path.GetFileName(path)}", "Modrinth");
            await RefreshModsAsync();
        }
        catch (Exception e)
        {
            DownloadStatus = "";
            LogService.Error($"下载失败: {e.Message}", "Modrinth");
        }
        finally
        {
            IsModBusy = false;
        }
    }

    // ------------------------------------------------------------------ tree

    /// <summary>
    /// Builds the "指令" palette: the same grouping idea as TIA Portal, but the
    /// entries are launcher operations (launch, verify, download, accounts...).
    /// </summary>
    private void BuildPalette()
    {
        PaletteSections.Clear();

        PaletteSections.Add(new PaletteSection
        {
            Title = "收藏夹",
            Items =
            [
                new PaletteItem { Name = "启动游戏", Description = "签出并启动当前实例", IconKey = "Icon.Play", CommandKey = "launch" },
                new PaletteItem { Name = "校验文件", Description = "比对库与资源文件", IconKey = "Icon.Compile", CommandKey = "check" },
                new PaletteItem { Name = "补全缺失文件", Description = "从下载源补齐", IconKey = "Icon.Download", CommandKey = "download" }
            ]
        });

        PaletteSections.Add(new PaletteSection
        {
            Title = "基本指令",
            Items =
            [
                new PaletteItem { Name = "实例概览", Description = "查看版本组态", IconKey = "Icon.Info", PageIndex = 0 },
                new PaletteItem { Name = "启动控制台", Description = "游戏标准输出", IconKey = "Icon.Console", PageIndex = 1 },
                new PaletteItem { Name = "启动方案", Description = "命令行与文件清单", IconKey = "Icon.Compile", PageIndex = 2 },
                new PaletteItem { Name = "诊断日志", Description = "启动器日志", IconKey = "Icon.Diagnostics", PageIndex = 3 }
            ]
        });

        PaletteSections.Add(new PaletteSection
        {
            Title = "模组",
            Items =
            [
                new PaletteItem { Name = "刷新模组列表", Description = "重新扫描 mods 目录", IconKey = "Icon.Refresh", CommandKey = "modrefresh" },
                new PaletteItem { Name = "导入模组", Description = "从本地 jar 安装", IconKey = "Icon.Download", CommandKey = "modimport" },
                new PaletteItem { Name = "启用 / 停用", Description = "切换选中模组", IconKey = "Icon.Check", CommandKey = "modtoggle" },
                new PaletteItem { Name = "打开 mods 目录", Description = "资源管理器中打开", IconKey = "Icon.Folder", CommandKey = "modfolder" }
            ]
        });

        PaletteSections.Add(new PaletteSection
        {
            Title = "在线与访问",
            Items =
            [
                new PaletteItem { Name = "版本清单", Description = "获取可下载版本", IconKey = "Icon.Online", CommandKey = "manifest" },
                new PaletteItem { Name = "下载并安装", Description = "安装选中的在线版本", IconKey = "Icon.Download", PageIndex = 4 },
                new PaletteItem { Name = "下载源切换", Description = "BMCLAPI / Mojang 官方", IconKey = "Icon.Server", PageIndex = 4 }
            ]
        });

        PaletteSections.Add(new PaletteSection
        {
            Title = "工艺对象",
            Items =
            [
                new PaletteItem { Name = "Java 运行时", Description = "检测与指定 java.exe", IconKey = "Icon.Java", CommandKey = "java" },
                new PaletteItem { Name = "账户", Description = "离线 / Microsoft 正版", IconKey = "Icon.Account", PageIndex = 5 },
                new PaletteItem { Name = "内存与 GC", Description = "运行参数组态", IconKey = "Icon.Settings", PageIndex = 0 },
                new PaletteItem { Name = "项目目录", Description = "打开 .minecraft", IconKey = "Icon.Folder", CommandKey = "openroot" }
            ]
        });

        PaletteSections.Add(new PaletteSection
        {
            Title = "诊断",
            Items =
            [
                new PaletteItem { Name = "导出诊断日志", Description = "写入日志文件", IconKey = "Icon.Save", CommandKey = "export" },
                new PaletteItem { Name = "重新检测 Java", Description = "扫描本机运行时", IconKey = "Icon.Refresh", CommandKey = "javadetect" },
                new PaletteItem { Name = "复制启动命令行", Description = "粘贴到终端复现", IconKey = "Icon.Copy", CommandKey = "copycmd" }
            ]
        });
    }

    /// <summary>Runs the action of an instruction-palette row.</summary>
    public void ActivatePaletteItem(PaletteItem? item)
    {
        if (item is null) return;

        if (item.PageIndex >= 0)
        {
            WorkspaceTab = item.PageIndex;
            return;
        }

        switch (item.CommandKey)
        {
            case "launch":
                if (LaunchCommand.CanExecute(null)) LaunchCommand.Execute(null);
                break;
            case "check":
                if (CheckFilesCommand.CanExecute(null)) CheckFilesCommand.Execute(null);
                break;
            case "download":
                if (DownloadMissingCommand.CanExecute(null)) DownloadMissingCommand.Execute(null);
                break;
            case "manifest":
                if (RefreshManifestCommand.CanExecute(null)) RefreshManifestCommand.Execute(null);
                break;
            case "java":
                if (DetectJavaCommand.CanExecute(null)) DetectJavaCommand.Execute(null);
                break;
            case "javadetect":
                if (DetectJavaCommand.CanExecute(null)) DetectJavaCommand.Execute(null);
                break;
            case "openroot":
                OpenGameFolder();
                break;
            case "export":
                ExportDiagnostics();
                break;
            case "copycmd":
                CopyCommandLine();
                break;
            case "modrefresh":
                if (RefreshModsCommand.CanExecute(null)) RefreshModsCommand.Execute(null);
                break;
            case "modimport":
                if (ImportModsCommand.CanExecute(null)) ImportModsCommand.Execute(null);
                break;
            case "modtoggle":
                if (ToggleModCommand.CanExecute(null)) ToggleModCommand.Execute(null);
                break;
            case "modfolder":
                OpenModsFolder();
                break;
        }
    }

    private void RebuildTree()
    {
        Navigation.Clear();

        var filter = TreeFilter?.Trim() ?? "";

        var root = new TreeNode
        {
            Title = Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar)),
            Subtitle = RootPath,
            IconKey = "Icon.Project",
            NodeKind = "root",
            IsExpanded = true
        };

        // TIA puts "添加新设备" and "设备和网络" right under the project node.
        root.Children.Add(new TreeNode
        {
            Title = "添加新设备",
            Subtitle = "下载并安装 Minecraft 版本",
            IconKey = "Icon.Plus",
            Tag = "4",
            NodeKind = "page"
        });
        root.Children.Add(new TreeNode
        {
            Title = "设备和网络",
            Subtitle = $"{_launcher.Installed.Count} 个实例",
            IconKey = "Icon.Devices",
            NodeKind = "group",
            IsExpanded = true,
            Children =
            {
                // Placeholder children are added below through the collection.
            }
        });

        var devices = root.Children[1];

        foreach (var version in _launcher.Installed)
        {
            if (filter.Length > 0 &&
                !version.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !version.Loader.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var required = version.Json.JavaVersion?.MajorVersion ?? 0;
            devices.Children.Add(new TreeNode
            {
                Title = version.Id,
                Subtitle = version.Loader,
                IconKey = "Icon.Instance",
                Tag = version.Id,
                NodeKind = "instance",
                IsExpanded = true,
                Children =
                {
                    new TreeNode
                    {
                        Title = "设备组态",
                        Subtitle = $"Java {required} · {VersionRepository.DeduplicateLibraries(version.Json.Libraries).Count} 库",
                        IconKey = "Icon.Settings",
                        Tag = "0",
                        NodeKind = "page"
                    },
                    new TreeNode
                    {
                        Title = "在线和诊断",
                        Subtitle = "文件校验 / 启动方案",
                        IconKey = "Icon.Diagnostics",
                        Tag = "2",
                        NodeKind = "page"
                    },
                    new TreeNode
                    {
                        Title = "程序块",
                        Subtitle = "启动流程",
                        IconKey = "Icon.Console",
                        IsExpanded = false,
                        Children =
                        {
                            new TreeNode { Title = "启动控制台", IconKey = "Icon.Console", Tag = "1", NodeKind = "page" },
                            new TreeNode { Title = "启动方案", IconKey = "Icon.Compile", Tag = "2", NodeKind = "page" },
                            new TreeNode { Title = "诊断日志", IconKey = "Icon.Diagnostics", Tag = "3", NodeKind = "page" }
                        }
                    }
                }
            });
        }

        if (devices.Children.Count == 0)
        {
            devices.Children.Add(new TreeNode
            {
                Title = filter.Length > 0 ? "没有匹配的实例" : "尚无已安装实例",
                Subtitle = "使用「添加新设备」下载版本",
                IconKey = "Icon.Warning",
                NodeKind = "info"
            });
        }

        var config = new TreeNode { Title = "设备与组态", IconKey = "Icon.Settings", NodeKind = "group", IsExpanded = true };
        config.Children.Add(new TreeNode
        {
            Title = "整合包管理",
            Subtitle = "客户端 / 服务端整合包",
            IconKey = "Icon.Mod",
            Tag = "8",
            NodeKind = "page"
        });
        config.Children.Add(new TreeNode
        {
            Title = "认证服务端",
            Subtitle = "外置登录 / Yggdrasil 终端",
            IconKey = "Icon.Server",
            Tag = "7",
            NodeKind = "page"
        });
        config.Children.Add(new TreeNode
        {
            Title = $"Java 运行时 ({_launcher.JavaRuntimes.Count})",
            IconKey = "Icon.Java",
            Tag = "-1",
            NodeKind = "page"
        });
        config.Children.Add(new TreeNode
        {
            Title = "账户管理",
            Subtitle = SelectedAccount?.Display ?? "离线 / 正版",
            IconKey = "Icon.Account",
            Tag = "5",
            NodeKind = "page"
        });
        config.Children.Add(new TreeNode
        {
            Title = $"下载源: {DownloadSourceText}",
            IconKey = "Icon.Online",
            NodeKind = "info"
        });
        root.Children.Add(config);

        Navigation.Add(root);
        root.IsSelected = true;
    }

    // ------------------------------------------------------------------- log

    private void OnLogEntry(LogEntry entry)
    {
        if (entry.Level < LogLevel.Warning && entry.Source is "Minecraft" or "java")
        {
            // Game output already goes to the console tab.
        }

        Ui.Post(() =>
        {
            if (MatchesFilter(entry)) Logs.Add(entry);
            while (Logs.Count > 3000) Logs.RemoveAt(0);
        });
    }

    private bool MatchesFilter(LogEntry entry) => SelectedLogLevel switch
    {
        "全部" => true,
        "INFO" => entry.Level == LogLevel.Info,
        "CMD" => entry.Level == LogLevel.Command,
        "OK" => entry.Level == LogLevel.Success,
        "WARN" => entry.Level == LogLevel.Warning,
        "ERR" => entry.Level == LogLevel.Error,
        "GAME" => entry.Level == LogLevel.Game,
        _ => true
    };

    private void RefreshLogs()
    {
        Logs.Clear();
        foreach (var entry in LogService.Entries.Where(MatchesFilter).TakeLast(1500))
        {
            Logs.Add(entry);
        }
    }

    private void OnStateChanged(LauncherState state, string message)
    {
        Ui.Post(() =>
        {
            State = state;
            StateMessage = message;
        });
    }

    private void OnGameExited(int exitCode, GameExitReason reason)
    {
        Ui.Post(() =>
        {
            Raise(nameof(IsGameRunning));
            (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            Console.AppendHeader($"{SelectedVersion?.Id} 已退出  code={exitCode} ({reason})");
        });
    }

    private void RefreshDetailFields()
    {
        Raise(nameof(DetailVersion));
        Raise(nameof(DetailLoader));
        Raise(nameof(DetailType));
        Raise(nameof(DetailJavaRequired));
        Raise(nameof(DetailLibraryCount));
        Raise(nameof(DetailJarPath));
        Raise(nameof(DetailJsonPath));
        Raise(nameof(DetailChain));
        Raise(nameof(DetailStatus));
        Raise(nameof(DetailStatusKey));
        Raise(nameof(DetailCheckSummary));
        Raise(nameof(DetailPlanSummary));
    }

    public void Dispose()
    {
        LogService.EntryAdded -= OnLogEntry;
        _launcher.StateChanged -= OnStateChanged;
        _launcher.GameExited -= OnGameExited;
        Console.Dispose();
    }
}
