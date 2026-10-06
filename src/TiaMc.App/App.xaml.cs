using System.IO;
using System.Windows;

namespace TiaMc.App;

public partial class App : Application
{
    /// <summary>
    /// Last resort diagnostics sink. It only uses the temp folder so it works
    /// even when the configuration directory is not writable.
    /// </summary>
    internal static void Report(string source, Exception exception)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {exception}";
        Services.LogService.Error($"未处理异常({source}): {exception.Message}");

        foreach (var folder in new[] { SafeConfigDirectory(), Path.GetTempPath() })
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "startup-error.log"),
                    text + Environment.NewLine + new string('-', 80) + Environment.NewLine);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Try the next folder.
            }
        }
    }

    private static string? SafeConfigDirectory()
    {
        try
        {
            return Services.AppConfig.ConfigDirectory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The WPF generated entry point calls InitializeComponent() (which parses
    /// App.xaml and every merged dictionary) *before* OnStartup, so the whole
    /// start-up sequence runs inside one try/catch here.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            Report("App.xaml", exception);
            MessageBox.Show(exception.ToString(), "TIA-MC 主题加载失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Report("Dispatcher", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) Report("AppDomain", exception);
        };

        // A failure while parsing XAML must never become a silent
        // "the app closed immediately" experience, so the window is created
        // inside a try/catch.
        try
        {
            // "--selftest" builds the shell, prints the bound state and exits.
            // Used to verify the view model wiring without a human looking at
            // the window.
            if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                RunSelfTest();
                return;
            }

            // Caches (mod icons, metadata, Java list) live under <config>\cache.
            Services.AppConfig.ApplyCacheRoot();

            // Web 界面优先：`--web` 参数或配置 webUiFirst=true 时，
            // 直接启动 Web 版（启动器 = 浏览器窗口 + 本地 HTTP 服务）并退出本进程。
            // 默认启动即进 Web 界面；`--desktop` 强制回到桌面界面，`--web` 强制进 Web。
            var forceDesktop = e.Args.Any(a => a.Equals("--desktop", StringComparison.OrdinalIgnoreCase));
            var forceWeb = e.Args.Any(a => a.Equals("--web", StringComparison.OrdinalIgnoreCase));
            var webFirst = false;
            if (!forceDesktop)
            {
                try
                {
                    webFirst = Services.AppConfig.Load().WebUiFirst;   // 默认 true：启动即进 Web 界面
                }
                catch (Exception)
                {
                    // 配置读不到就按默认（桌面界面）启动
                }
            }

            if (forceWeb) webFirst = true;
            if (forceDesktop) webFirst = false;

            if (webFirst)
            {
                var (ok, message) = Services.WebShellLauncher.Start();
                if (!ok) MessageBox.Show(message, "TIA-MC Web 界面", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(ok ? 0 : 1);
                return;
            }

            // Every line also goes to <config>\logs\tiamc-<时间>.log so problems can
            // be reported afterwards (the file is flushed per line).
            Services.LogService.InitializeLogFile(
                System.IO.Path.Combine(Services.AppConfig.ConfigDirectory, "logs"));

            // Image cache keeps a hard disk budget (see AppPaths.EnforceIconBudget).
            var trimmed = Services.IconCache.EnforceDiskBudget();
            if (trimmed.Deleted > 0)
            {
                Services.LogService.Info($"图标缓存已清理 {trimmed.Deleted} 个文件，" +
                                         $"释放 {TiaMc.Core.Utils.TextUtil.FormatBytes(trimmed.FreedBytes)}", "Cache");
            }

            var window = new Views.MainWindow();
            MainWindow = window;
            window.Show();

            // "--tab:<n>" opens a workspace page right away; used by the UI smoke
            // tests and handy when debugging one page.
            foreach (var argument in e.Args)
            {
                if (!argument.StartsWith("--tab:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(argument[6..], out var tab)) continue;
                if (window.DataContext is ViewModels.MainViewModel viewModel) viewModel.WorkspaceTab = tab;
            }

            // "--detect" opens the Minecraft folder detection dialog immediately.
            if (e.Args.Any(a => a.Equals("--detect", StringComparison.OrdinalIgnoreCase)))
            {
                var root = window.DataContext is ViewModels.MainViewModel vm ? vm.RootPath : null;
                Views.McScanWindow.ShowDialog(window, root);
            }

            // "--ygg:<command>" runs a command in the authentication server terminal,
            // so the server can be started head-less together with the launcher.
            // Quote commands containing spaces:
            //   TiaMC.exe "--ygg:start" "--ygg:user add Steve 123456"
            var yggCommands = e.Args
                .Where(a => a.StartsWith("--ygg:", StringComparison.OrdinalIgnoreCase))
                .Select(a => a[6..])
                .Where(c => c.Length > 0)
                .ToList();

            if (yggCommands.Count > 0 && window.DataContext is ViewModels.MainViewModel yggVm)
            {
                yggVm.WorkspaceTab = 7;
                foreach (var command in yggCommands)
                {
                    yggVm.YggInput = command;
                    yggVm.ExecuteYggCommand();
                }

                return;
            }

            // The authentication server switch is remembered: bring it up again.
            if (window.DataContext is ViewModels.MainViewModel autoVm) autoVm.ApplyYggAutoStart();
        }
        catch (Exception exception)
        {
            Report("Startup", exception);
            MessageBox.Show(exception.ToString(), "TIA-MC 启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void RunSelfTest()
    {
        var window = new Views.MainWindow();
        MainWindow = window;
        window.Show();
        window.Hide();

        var vm = (ViewModels.MainViewModel)window.DataContext;
        var report = new System.Text.StringBuilder();
        report.AppendLine("selftest.version = " + Core.Utils.AppInfo.Version);
        report.AppendLine("selftest.minecraftRoot = " + vm.RootPath);
        report.AppendLine("selftest.instances = " + vm.VersionChoices.Count);
        report.AppendLine("selftest.javaRuntimes = " + vm.JavaChoices.Count);
        report.AppendLine("selftest.selectedJava = " + (vm.SelectedJava?.ShortDisplay ?? "(null)"));
        report.AppendLine("selftest.activeInstance = " + (vm.ActiveChoice?.Id ?? "(null)"));
        report.AppendLine("selftest.detailStatus = " + vm.DetailStatus);
        report.AppendLine("selftest.detailJavaRequired = " + vm.DetailJavaRequired);
        report.AppendLine("selftest.planRows = " + vm.PlanRows.Count);
        report.AppendLine("selftest.mainClass = " + vm.MainClassText);
        report.AppendLine("selftest.treeNodes = " + vm.Navigation.Count);
        report.AppendLine("selftest.state = " + vm.StateText + " / " + vm.StateMessage);

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "TiaMC-selftest.log");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine("selftest written to " + path);
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is { DataContext: ViewModels.MainViewModel vm })
        {
            vm.Dispose();
        }

        base.OnExit(e);
    }
}
