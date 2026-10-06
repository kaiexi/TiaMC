using System.IO;
using TiaMc.Core.Accounts;
using TiaMc.Core.Integrity;
using TiaMc.Core.Java;
using TiaMc.Core.Utils;
using TiaMc.Core.Launch;
using TiaMc.Core.Minecraft;
using TiaMc.Core.Net;

namespace TiaMc.App.Services;

public enum LauncherState
{
    Idle,
    Checking,
    Downloading,
    Launching,
    Running,
    Failed
}

public sealed class LaunchService
{
    private GameProcess? _process;

    public AppConfig Config { get; }

    /// <summary>Offline and Microsoft accounts; the selected one is used to launch.</summary>
    public AccountStore Accounts { get; }

    public MicrosoftAuth MicrosoftAuth { get; } = new();

    /// <summary>Modrinth client used by the mod browser.</summary>
    public TiaMc.Core.Mods.ModrinthClient Modrinth { get; } = new();

    public McPaths Paths { get; private set; }

    public VersionRepository Repository { get; private set; }

    public List<InstalledVersion> Installed { get; private set; } = [];

    public List<JavaInfo> JavaRuntimes { get; private set; } = [];

    public ManifestClient Manifest { get; } = new();

    public VersionManifest? RemoteManifest { get; private set; }

    public LauncherState State { get; private set; } = LauncherState.Idle;

    public event Action<LauncherState, string>? StateChanged;

    public event Action<string>? GameOutput;

    public event Action<int, GameExitReason>? GameExited;

    /// <summary>Raised after a failed launch with the log / crash report analysis.</summary>
    public event Action<TiaMc.Core.Diagnostics.CrashAnalysis>? DiagnosisReady;

    private readonly List<string> _gameLog = [];
    private LaunchPlan? _lastPlan;

    /// <summary>Full console log of the running (or last) game process.</summary>
    public string GameLogText
    {
        get
        {
            lock (_gameLog) return string.Join(Environment.NewLine, _gameLog);
        }
    }

    /// <summary>
    /// Analyses the last launch log plus the newest crash report of a game
    /// directory (HMCL style). Falls back to the active instance when no launch
    /// happened in this session.
    /// </summary>
    public TiaMc.Core.Diagnostics.CrashAnalysis Diagnose(int? exitCode = null, string? gameDirectory = null,
        bool notify = true)
    {
        gameDirectory ??= _lastPlan?.GameDirectory;
        if (string.IsNullOrEmpty(gameDirectory) && _lastPlan is not null)
        {
            gameDirectory = Path.Combine(Config.MinecraftRoot ?? "", "versions", _lastPlan.VersionId);
        }

        var mods = string.IsNullOrEmpty(gameDirectory)
            ? []
            : TiaMc.Core.Diagnostics.CrashAnalyzer.ListModFiles(gameDirectory);

        var analysis = TiaMc.Core.Diagnostics.CrashAnalyzer.Analyze(
            GameLogText,
            exitCode ?? -1,
            gameDirectory is not null && Directory.Exists(gameDirectory) ? gameDirectory : null,
            Config.MaxMemoryMb,
            mods.Count,
            mods);

        if (notify) DiagnosisReady?.Invoke(analysis);
        return analysis;
    }

    public LaunchService(AppConfig config)
    {
        Config = config;
        Accounts = AccountStore.Load(AppConfig.AccountsFilePath);
        Paths = new McPaths(McPaths.DetectRoot(config.MinecraftRoot));
        Repository = new VersionRepository(Paths);
        LogService.Info($"已载入 {Accounts.Accounts.Count} 个账户，当前账户: {Accounts.Selected.Display}", "Account");
    }

    public bool IsRunning => _process?.IsRunning == true;

    public int? GameProcessId => _process?.ProcessId;

    private void SetState(LauncherState state, string message)
    {
        State = state;
        LogService.Write(state switch
        {
            LauncherState.Failed => LogLevel.Error,
            LauncherState.Running => LogLevel.Success,
            LauncherState.Idle => LogLevel.Info,
            _ => LogLevel.Info
        }, message, "TiaMC");
        StateChanged?.Invoke(state, message);
    }

    /// <summary>Switches the active .minecraft folder and reloads everything from it.</summary>
    public void ReloadInstallation(string? root = null)
    {
        if (!string.IsNullOrWhiteSpace(root)) Config.MinecraftRoot = root;
        Paths = new McPaths(McPaths.DetectRoot(Config.MinecraftRoot));

        // Portable mode: make sure the folder structure exists so the game and the
        // installer have somewhere to write even on the very first run.
        EnsureStructure(Paths);

        Repository = new VersionRepository(Paths);
        Installed = Repository.LoadAll(out var errors);
        foreach (var error in errors) LogService.Warn($"版本读取失败 {error}", "Core");
        LogService.Info($"已载入 Minecraft 目录: {Paths.Root} (版本 {Installed.Count} 个)" +
                        (Config.PortableRoot ? "  [便携模式]" : ""), "Core");
    }

    /// <summary>Creates versions / libraries / assets / mods when they are missing.</summary>
    public static void EnsureStructure(McPaths paths)
    {
        foreach (var folder in new[] { "versions", "libraries", "assets", "assets/indexes", "assets/objects", "mods" })
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(paths.Root, folder.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogService.Warn($"无法创建目录 {folder}: {e.Message}", "Core");
            }
        }
    }

    public InstalledVersion? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Installed.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Detects Java runtimes (blocking; call from a worker thread).</summary>
    public List<JavaInfo> DetectJava()
    {
        SetState(LauncherState.Checking, "正在检测 Java 运行时...");
        var extra = new List<string>();
        var runtimeDir = Path.Combine(Paths.Root, "runtime");
        if (Directory.Exists(runtimeDir)) extra.Add(runtimeDir);

        // 真探测（含 -version）：只靠路径猜主版本会在"自动补齐的 Java"上出错，
            // 这与 Axolotl 的"发现即校验"一致；探测结果由 JavaDetector 内部按签名缓存。
            JavaRuntimes = JavaDetector.Detect(extra, message => LogService.Info(message, "Java"), probe: true);
        foreach (var java in JavaRuntimes)
        {
            LogService.Info($"检测到 {java.ShortDisplay}", "Java");
        }

        if (JavaRuntimes.Count == 0)
        {
            LogService.Warn("未检测到任何 Java 运行时，请在设置中手动指定 java.exe 路径", "Java");
        }

        SetState(LauncherState.Idle, $"Java 检测完成，共 {JavaRuntimes.Count} 个");
        return JavaRuntimes;
    }

    /// <summary>
    /// Makes sure an installed runtime satisfies the version requirement, downloading
    /// the JRE into &lt;root&gt;\runtime when nothing matches (auto provisioning).
    /// </summary>
    public async Task<JavaInfo?> EnsureJavaAsync(InstalledVersion version)
    {
        var required = JavaRuntimeInstaller.RequiredMajor(version.Json.JavaVersion?.MajorVersion,
            version.Id, version.Json.InheritsFrom);
        var matching = JavaDetector.Filter(JavaRuntimes, required).ToList();

        if (matching.Count > 0)
        {
            return matching.First();
        }

        LogService.Warn($"没有可运行 {version.Id} 的 Java {required}（需要 {required}+，当前 {JavaRuntimes.Count} 个运行时）",
            "Java");

        if (!Config.AutoProvisionJava)
        {
            LogService.Info("自动补齐 Java 已在设置中关闭（勾选后可自动下载）", "Java");
            return null;
        }

        var runtimeRoot = Path.Combine(Paths.Root, "runtime");
        LogService.Info($"开始自动补齐 Java {required}：下载到 {runtimeRoot}", "Java");

        var progress = new Progress<double>(p =>
        {
            var percent = (int)Math.Round(p * 100);
            if (percent % 10 == 0) LogService.Info($"Java {required} 下载进度 {percent}%", "Java");
        });

        var result = await JavaRuntimeInstaller.InstallAsync(required, runtimeRoot,
            message => LogService.Info(message, "Java"), progress, CancellationToken.None).ConfigureAwait(false);

        if (!result.Ok)
        {
            LogService.Error($"自动补齐 Java {required} 失败: {result.Message}", "Java");
            return null;
        }

        LogService.Ok($"Java {required} 已自动补齐: {result.JavaPath}", "Java");
        DetectJava();

        return JavaDetector.Filter(JavaRuntimes, required).FirstOrDefault() ?? JavaRuntimes.FirstOrDefault();
    }
    public JavaInfo? ResolveJava(InstalledVersion version)
    {
        var required = version.Json.JavaVersion?.MajorVersion ?? 8;

        // 1) 先按版本要求过滤（含 <MC>\runtime 下自动补齐的 Java）。
        //    不给"低于要求的 Java"任何被选中的机会——这正是 UnsupportedClassVersionError 的来源。
        var matching = JavaDetector.Filter(JavaRuntimes, required).ToList();
        if (matching.Count == 0)
        {
            matching = JavaDetector.Filter(ProbeManagedRuntimes(), required).ToList();
        }

        // 2) 设置里手动指定的 java.exe：只有满足该版本要求时才优先使用
        if (!string.IsNullOrWhiteSpace(Config.JavaPath) && File.Exists(Config.JavaPath))
        {
            var manual = JavaRuntimes.FirstOrDefault(j =>
                             string.Equals(j.Path, Config.JavaPath, StringComparison.OrdinalIgnoreCase))
                         ?? JavaDetector.Probe(Config.JavaPath!);

            if (manual is not null && manual.MajorVersion >= required)
            {
                return manual;
            }

            if (manual is not null)
            {
                LogService.Warn(
                    $"设置里指定的 Java {manual.MajorVersion} 低于 {version.Id} 需要的 Java {required}，" +
                    $"改用 {matching.FirstOrDefault()?.ShortDisplay ?? "（没有可用的，请点「自动补齐 Java」）"}", "Java");
            }
        }

        // 3) 精确匹配优先，其次取满足要求里最低的（省内存）
        return matching.FirstOrDefault(j => j.MajorVersion == required)
               ?? matching.OrderBy(j => j.MajorVersion).FirstOrDefault()
               ?? JavaRuntimes.OrderByDescending(j => j.MajorVersion).FirstOrDefault();
    }

    /// <summary>
    /// 探测 &lt;MC&gt;\runtime 下自动补齐的运行时（检测流程为速度会跳过 probe，导致主版本识别不出来）。
    /// 对应 Axolotl 的"发现即校验 + 缓存"思路。
    /// </summary>
    private List<JavaInfo> ProbeManagedRuntimes()
    {
        var found = new List<JavaInfo>();
        var runtimeDir = Path.Combine(Paths.Root, "runtime");
        if (!Directory.Exists(runtimeDir)) return found;

        foreach (var dir in Directory.GetDirectories(runtimeDir))
        {
            var exe = Path.Combine(dir, "bin", "java.exe");
            if (!File.Exists(exe)) continue;

            var known = JavaRuntimes.FirstOrDefault(j => string.Equals(j.Path, exe, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                found.Add(known);
                continue;
            }

            var probed = JavaDetector.Probe(exe);
            if (probed is null) continue;

            JavaRuntimes.Add(probed);
            LogService.Info($"探测到自动补齐的运行时: {probed.ShortDisplay}", "Java");
            found.Add(probed);
        }

        return found;
    }

    public CheckResult CheckFiles(InstalledVersion version)
    {
        SetState(LauncherState.Checking, $"正在校验 {version.Id} 的文件完整性...");
        var result = IntegrityChecker.Check(Paths, version, includeAssets: true,
            message => LogService.Info(message, "Check"));
        LogService.Info(result.Summary, "Check");

        if (result.IsComplete)
        {
            LogService.Ok($"{version.Id} 文件完整", "Check");
        }
        else
        {
            LogService.Warn($"{version.Id} 缺少 {result.Missing.Count} 个文件 " +
                            $"({TiaMc.Core.Utils.TextUtil.FormatBytes(result.MissingBytes)})", "Check");
        }

        SetState(LauncherState.Idle, result.IsComplete ? "校验通过" : $"缺少 {result.Missing.Count} 个文件");
        return result;
    }

    public async Task<DownloadOutcome?> DownloadMissingAsync(IReadOnlyList<MissingFile> files,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (files.Count == 0) return null;

        SetState(LauncherState.Downloading, $"正在补全 {files.Count} 个文件...");
        var service = new DownloadService();
        var outcome = await service.DownloadMissingAsync(files, Config.DownloadSource, progress,
            message => LogService.Info(message, "Download"), token, threads: Config.DownloadThreads);

        if (outcome.Ok)
        {
            LogService.Ok($"下载完成: 成功 {outcome.Succeeded} / 跳过 {outcome.Skipped}", "Download");
            SetState(LauncherState.Idle, "下载完成");
        }
        else
        {
            LogService.Error($"下载失败 {outcome.Failed} 个文件", "Download");
            SetState(LauncherState.Failed, $"下载失败 {outcome.Failed} 个文件");
        }

        return outcome;
    }

    /// <summary>
    /// Installer pass: repeats "check + download" until the version is complete.
    /// The asset index has to be downloaded before its objects can be enumerated,
    /// so a single pass is never enough for a fresh version.
    /// </summary>
    public async Task<bool> DownloadAllAsync(InstalledVersion version,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var downloader = new DownloadService();

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            token.ThrowIfCancellationRequested();

            var check = IntegrityChecker.Check(Paths, version, includeAssets: true,
                message => LogService.Info(message, "Check"));
            LogService.Info($"第 {attempt} 轮校验: {check.Summary}", "Install");

            if (check.IsComplete)
            {
                SetState(LauncherState.Idle, $"{version.Id} 已就绪");
                LogService.Ok($"{version.Id} 文件已完整 ({check.PresentCount} 个文件)", "Install");
                return true;
            }

            SetState(LauncherState.Downloading,
                $"第 {attempt} 轮: 需要补全 {check.Missing.Count} 个文件 " +
                $"({TiaMc.Core.Utils.TextUtil.FormatBytes(check.MissingBytes)})");

            var outcome = await downloader.DownloadMissingAsync(check.Missing, Config.DownloadSource, progress,
                message => LogService.Info(message, "Download"), token, threads: Config.DownloadThreads);

            if (!outcome.Ok)
            {
                SetState(LauncherState.Failed, $"下载失败 {outcome.Failed} 个文件");
                foreach (var error in outcome.Errors.Take(5)) LogService.Error(error, "Download");
                return false;
            }

            if (outcome.Succeeded == 0 && outcome.Skipped == 0) break;
        }

        var final = IntegrityChecker.Check(Paths, version, includeAssets: true);
        if (final.IsComplete)
        {
            SetState(LauncherState.Idle, $"{version.Id} 已就绪");
            return true;
        }

        SetState(LauncherState.Failed, $"仍有 {final.Missing.Count} 个文件缺失");
        return false;
    }

    public async Task<bool> RefreshRemoteManifestAsync()
    {
        SetState(LauncherState.Checking, "正在获取版本清单...");
        RemoteManifest = await Manifest.GetManifestAsync(Config.DownloadSource);
        if (RemoteManifest is null)
        {
            SetState(LauncherState.Failed, "无法获取版本清单（网络不可用）");
            return false;
        }

        LogService.Ok($"版本清单获取成功，共 {RemoteManifest.Versions.Count} 个版本", "Net");
        SetState(LauncherState.Idle, "就绪");
        return true;
    }

    public async Task<bool> InstallVanillaAsync(ManifestVersion version, IProgress<DownloadProgress>? progress)
    {
        SetState(LauncherState.Downloading, $"正在安装原版 {version.Id}...");
        var ok = await Manifest.InstallVanillaAsync(Paths, version, Config.DownloadSource, progress,
            message => LogService.Info(message, "Install"));
        ReloadInstallation();
        SetState(ok ? LauncherState.Idle : LauncherState.Failed, ok ? $"{version.Id} 安装完成" : $"{version.Id} 安装失败");
        return ok;
    }

    public LaunchPlan BuildPlan(InstalledVersion version, out JavaInfo? java, out string? error)
    {
        java = ResolveJava(version);
        error = null;

        if (java is null)
        {
            error = "未找到可用的 Java 运行时";
            return null!;
        }

        var account = Accounts.Selected;
        if (account.Kind == AccountKind.Microsoft && account.NeedsRefresh)
        {
            LogService.Warn($"账户 {account.Name} 的登录令牌需要刷新（启动前会自动刷新）", "Account");
        }

        var options = Config.ToLaunchOptions(version.Id);
        options.GameDirectory = Config.ResolveGameDirectory(version.Id);
        options.Isolated = Config.GetInstance(version.Id).Isolate ?? Config.IsolateInstances;
        // 外置登录账户需要 authlib-injector 才能被「要求皮肤站登录」的服务器接受。
        if (account.NeedsAuthlibInjector)
        {
            var runtimeRoot = Path.Combine(Paths.Root, "runtime");
            var agent = AuthlibInjector.Locate(Paths.Root, runtimeRoot);
            if (agent.Length > 0)
            {
                options.AuthlibInjectorPath = agent;
            }
            else
            {
                LogService.Warn($"账户 {account.Name} 使用外置登录（{account.AuthServerName}），" +
                                "但还没有 authlib-injector，启动时会自动下载", "外置登录");
            }
        }

        var plan = LaunchPlanner.Build(Paths, version, account, java.Path, java.MajorVersion,
            options, message => LogService.Info(message, "Plan"));

        LogService.Info(options.Isolated
            ? $"版本隔离: 游戏目录 = {plan.GameDirectory}（独立的 config/saves/mods/资源包）"
            : $"共享游戏目录: {plan.GameDirectory}", "Isolation");
        // 内存自检：32 位 Java 大堆 / 超过物理内存 / 堆太小 —— 这些都会让游戏起不来或卡死
        var advice = MemoryAdvisor.Check(options.MaxMemoryMb, options.MinMemoryMb, java, SystemInfo.TotalPhysicalMemoryMb);
        foreach (var warning in advice.Warnings) LogService.Warn(warning, "Memory");
        foreach (var note in advice.Notes) LogService.Info(note, "Memory");

        if (advice.MaxMemoryMb != options.MaxMemoryMb || advice.MinMemoryMb != options.MinMemoryMb)
        {
            options.MaxMemoryMb = advice.MaxMemoryMb;
            options.MinMemoryMb = advice.MinMemoryMb;
            Config.MaxMemoryMb = advice.MaxMemoryMb;
            Config.MinMemoryMb = advice.MinMemoryMb;
            Config.Save();
        }

        LogService.Info($"实例参数: -Xms{options.MinMemoryMb}m -Xmx{options.MaxMemoryMb}m, " +
                        $"GC={options.GcMode}, 窗口={options.WindowWidth}x{options.WindowHeight}" +
                        $"{(options.Fullscreen ? " 全屏" : "")}（可被实例覆盖）", "Isolation");
        LogService.Info($"使用账户: {account.Display} (userType={account.UserType})", "Account");
        return plan;
    }

    /// <summary>
    /// Makes sure the selected account can launch: a Microsoft account with an
    /// expired token is refreshed, and a launch is refused when the account does
    /// not own the game.
    /// </summary>
    public async Task<MinecraftAccount> EnsureAccountReadyAsync(InstalledVersion version,
        CancellationToken token = default)
    {
        var account = Accounts.Selected;

        if (account.Kind != AccountKind.Microsoft) return account;

        if (account.NeedsRefresh)
        {
            SetState(LauncherState.Checking, $"正在刷新 {account.Name} 的登录状态...");
            try
            {
                var result = await MicrosoftAuth.RefreshAccountAsync(account, token).ConfigureAwait(false);
                account = Accounts.AddOrUpdate(result.Account);
                LogService.Ok($"已刷新正版账户: {account.Name}", "Account");
            }
            catch (MicrosoftAuthException e)
            {
                SetState(LauncherState.Failed, $"正版账户刷新失败: {e.Message}");
                throw;
            }
        }

        if (!account.OwnsGame && !version.Json.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Warn($"账户 {account.Name} 未检测到 Minecraft 游戏许可，启动可能失败", "Account");
        }

        SetState(LauncherState.Idle, "就绪");
        return account;
    }

    /// <summary>Starts the game. Returns false when the process could not be started.</summary>
    public bool Start(LaunchPlan plan)
    {
        _process?.Dispose();
        _process = new GameProcess();

        // Keep the console output so a failure can be analysed afterwards (HMCL style).
        lock (_gameLog) _gameLog.Clear();
        _lastPlan = plan;

        _process.OutputLine += line =>
        {
            lock (_gameLog)
            {
                _gameLog.Add(line);
                if (_gameLog.Count > TiaMc.Core.Diagnostics.CrashAnalyzer.MaxLogLines) _gameLog.RemoveAt(0);
            }

            GameOutput?.Invoke(line);
        };

        _process.Exited += (_, e) =>
        {
            SetState(LauncherState.Idle, $"{plan.VersionId} 已退出 (code={e.ExitCode}, {e.Reason})");
            GameExited?.Invoke(e.ExitCode, e.Reason);

            // A non zero exit code means something went wrong: analyse the log.
            if (e.ExitCode != 0 || e.Reason != GameExitReason.Exited)
            {
                _ = Task.Run(() => Diagnose(e.ExitCode, plan.GameDirectory));
            }
        };

        SetState(LauncherState.Launching, $"正在启动 {plan.VersionId}...");
        try
        {
            _process.Start(plan, message => LogService.Info(message, "Launch"));
            SetState(LauncherState.Running, $"{plan.VersionId} 正在运行 (PID {_process.ProcessId})");
            Config.LastLaunchUtc = DateTime.UtcNow;
            Config.Save();
            return true;
        }
        catch (Exception e)
        {
            SetState(LauncherState.Failed, $"启动失败: {e.Message}");
            return false;
        }
    }

    public void Stop()
    {
        if (_process is null) return;
        SetState(LauncherState.Idle, "正在结束游戏进程...");
        _process.Kill();
    }
}
