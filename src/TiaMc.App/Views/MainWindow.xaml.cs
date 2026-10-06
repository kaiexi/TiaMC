using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TiaMc.App.Mvvm;
using TiaMc.App.ViewModels;
using TiaMc.Core.Mods;

namespace TiaMc.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Audit trail: capture every click in the window (buttons, menus, toggles).
        AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler(OnAnyClick), handledEventsToo: true);

        StateChanged += OnWindowStateChanged;

        if (DataContext is MainViewModel vm)
        {
            vm.Console.Flushed += ScrollConsolesToEnd;
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // ------------------------------------------------------- window chrome

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can throw when the button is released early.
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeIcon is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = (Geometry)(FindResource(maximized ? "Chrome.Restore" : "Chrome.Maximize"));
        MaximizeButton.ToolTip = maximized ? "鍚戜笅杩樺師" : "鏈€澶у寲";
    }

    // ------------------------------------------------------------- menu / tree

    private void SwitchTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        if (ViewModel is { } vm) vm.WorkspaceTab = index;
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        Services.LogService.Entries.Clear();
        ViewModel?.Logs.Clear();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "TIA-MC 工程启动器 1.0.0\n\n" +
            "一个以西门子 TIA Portal 界面语言设计的 Minecraft 启动器。\n" +
            "内核设计参考 ColorMC (https://github.com/Coloryr/ColorMC) 的启动流程：\n" +
            "  版本 JSON 合并 -> 规则筛选 -> natives 解压 -> classpath 组装 ->\n" +
            "  JVM/游戏参数替换 -> java 进程托管。\n\n" +
            "界面按 TIA Portal 的博途布局组织：标题栏、菜单、工具栏、项目树、编辑器、指令面板、状态栏。",
            "关于 TIA-MC", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>3D 皮肤预览：复位视角。</summary>
    private void ResetSkin3D_Click(object sender, RoutedEventArgs e) => Skin3D?.Reset();

    /// <summary>3D 皮肤预览：自动旋转开关。</summary>
    private void Skin3DSpin_Changed(object sender, RoutedEventArgs e)
    {
        if (Skin3D is null) return;
        Skin3D.AutoSpin = sender is System.Windows.Controls.CheckBox { IsChecked: true };
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetExpansion(true);

    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetExpansion(false);

    private void SetExpansion(bool expanded)
    {
        if (ViewModel is null) return;
        foreach (var node in ViewModel.Navigation)
        {
            SetExpansion(node, expanded);
        }

        static void SetExpansion(TreeNode node, bool expanded)
        {
            node.IsExpanded = expanded;
            foreach (var child in node.Children) SetExpansion(child, expanded);
        }
    }

    private void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel is { } vm) vm.SelectedNode = e.NewValue as TreeNode;
    }

    /// <summary>
    /// Expands the project tree once it is realised. The containers do not exist
    /// before the first layout pass, so this runs from the Loaded event and is
    /// scheduled at Loaded priority to let the root container generate first.
    /// </summary>
    private void ProjectTree_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(ExpandAllNodes), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ExpandAllNodes()
    {
        if (ProjectTree is null || ViewModel is null) return;

        foreach (var node in ViewModel.Navigation)
        {
            ExpandNode(ProjectTree, node);
        }
    }

    private static void ExpandNode(ItemsControl parent, TreeNode node)
    {
        parent.UpdateLayout();
        if (parent.ItemContainerGenerator.ContainerFromItem(node) is not TreeViewItem container) return;

        container.IsExpanded = node.IsExpanded;
        foreach (var child in node.Children)
        {
            ExpandNode(container, child);
        }
    }

    private void RemoteVersions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not RemoteVersionRow row) return;
        _ = ViewModel?.PromptInstallAsync(row);
    }

    private void InstallRemote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RemoteVersionRow row })
        {
            _ = ViewModel?.PromptInstallAsync(row);
        }
    }

    private void CopyRemoteId_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RemoteVersionRow row })
        {
            try
            {
                Clipboard.SetText(row.Id);
                Services.LogService.Ok($"宸插鍒剁増鏈?ID {row.Id}");
            }
            catch (Exception)
            {
                // Clipboard can be busy; ignore.
            }
        }
    }

    // ------------------------------------------- second level menu handlers

    private void SetActiveInstance_Click(object sender, RoutedEventArgs e) => ViewModel?.SetActiveInstance();

    /// <summary>
    /// The version table works on VersionChoice rows while the workspace works on
    /// InstalledVersion; this keeps the two in sync when the user picks a row.
    /// </summary>
    private void VersionTable_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        ViewModel?.SelectVersionFromTable();
    }

    private void CopyGamePath_Click(object sender, RoutedEventArgs e) => ViewModel?.CopyGamePath();

    private void CopyInstancePath_Click(object sender, RoutedEventArgs e) => ViewModel?.CopyInstancePath();

    private void CopyInstanceJson_Click(object sender, RoutedEventArgs e) => ViewModel?.CopyInstanceJson();

    private void OpenInstanceFolder_Click(object sender, RoutedEventArgs e) => ViewModel?.OpenInstanceFolder();

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e) => ViewModel?.OpenRootModsFolder();

    private void OpenSavesFolder_Click(object sender, RoutedEventArgs e) => ViewModel?.OpenSavesFolder();

    /// <summary>Double clicking an instruction palette row runs its action.</summary>
    private void Palette_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list) return;
        if (list.SelectedItem is PaletteItem item) ViewModel?.ActivatePaletteItem(item);
    }

    /// <summary>Double clicking a mod row toggles it, like renaming the file by hand.</summary>
    private void Mods_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.ToggleModCommand.CanExecute(null) == true) ViewModel.ToggleModCommand.Execute(null);
    }

    /// <summary>Searches the resource catalogue (mods / modpacks / resource packs / shaders).</summary>
    private void SearchResources_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.SearchResourcesCommand.Execute(null);

    /// <summary>Downloads the selected resource into the active instance.</summary>
    private void DownloadResource_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.DownloadResourceCommand.Execute(null);

    /// <summary>
    /// One hook for every button / menu item / toggle in the window: it records the
    /// user action in the log (audit trail) without touching each handler.
    /// </summary>
    private void OnAnyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // For AddHandler on the window, sender is the window, so the clicked
            // control has to be found from the event source up the tree.
            var label = Describe(e.Source as DependencyObject) ?? Describe(e.OriginalSource as DependencyObject);
            if (string.IsNullOrWhiteSpace(label)) return;

            Services.LogService.User($"点击「{label}」", "用户");
        }
        catch (Exception)
        {
            // auditing must never break the UI
        }
    }

    /// <summary>Finds the nearest button / menu item and returns a readable label.</summary>
    private static string? Describe(DependencyObject? source)
    {
        var current = source;
        for (var depth = 0; current is not null && depth < 12; depth++)
        {
            switch (current)
            {
                case System.Windows.Controls.MenuItem menu:
                    return menu.Header?.ToString();
                case System.Windows.Window:
                    return null;   // clicking the window body is not an action
                case System.Windows.Controls.ContentControl content when content.Content is string text &&
                                                                        text.Length > 0:
                    return text;
                case System.Windows.Controls.ContentControl content when content.Content is not null:
                    return content.Name.Length > 0 ? content.Name : content.GetType().Name;
            }

            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : System.Windows.LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
    // --------------------------------------------------- 外置登录（第三方皮肤站）

    private async void YggLogin_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.AddYggdrasilAccountAsync();
    }

    private async void YggRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshYggdrasilAccountAsync();
    }
    // ----------------------------------------------------------- 皮肤（离线账户）

    private void PickSkin_Click(object sender, RoutedEventArgs e) => ViewModel?.PickSkinFile();

    private void GenerateSkin_Click(object sender, RoutedEventArgs e) => ViewModel?.GenerateSkin();

    private void RandomSkin_Click(object sender, RoutedEventArgs e) => ViewModel?.RandomSkin();

    private void ClearSkin_Click(object sender, RoutedEventArgs e) => ViewModel?.ClearSkin();

    private void PushSkin_Click(object sender, RoutedEventArgs e) => ViewModel?.PushSkinToServer();

    private async void FetchSkin_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.FetchSkinFromSiteAsync();
    }
    /// <summary>Downloads the Java runtime the selected instance needs.</summary>
    private async void ProvisionJava_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ProvisionJavaAsync();
    }

    /// <summary>Restarts the launcher elevated (UAC) — needed for the kernel memory passes.</summary>
    private void RestartElevated_Click(object sender, RoutedEventArgs e) => ViewModel?.RestartElevated();

    // ------------------------------------------------------------- log export

    /// <summary>Saves the whole log (including the user action trail) to a file.</summary>
    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        Services.LogService.User("导出完整日志（含 MC 日志）", "日志");

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出日志",
            FileName = $"TiaMC-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".log",
            AddExtension = true
        };

        var directory = Services.LogService.LogDirectory;
        try
        {
            if (System.IO.Directory.Exists(directory)) dialog.InitialDirectory = directory;
        }
        catch (Exception)
        {
            // ignore
        }

        if (dialog.ShowDialog(this) != true) return;

        // Complete export: launcher log + audit trail + every Minecraft log of the
        // active instance (latest.log, debug.log, crash-reports, hs_err).
        var result = Services.LogExporter.ExportText(dialog.FileName, ActiveGameDirectory(),
            ViewModel?.ActiveChoice?.Id, SummaryHeader());
        if (!result.Ok)
        {
            Services.LogService.Error($"导出失败: {result.Message}", "日志");
        }
    }

    /// <summary>Exports the zip bundle (one file per source).</summary>
    private void ExportBundle_Click(object sender, RoutedEventArgs e)
    {
        Services.LogService.User("导出诊断包（zip）", "日志");

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出诊断包",
            FileName = $"TiaMC-diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "诊断包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            DefaultExt = ".zip",
            AddExtension = true
        };

        var directory = Services.LogService.LogDirectory;
        try
        {
            if (System.IO.Directory.Exists(directory)) dialog.InitialDirectory = directory;
        }
        catch (Exception)
        {
            // ignore
        }

        if (dialog.ShowDialog(this) != true) return;

        Services.LogExporter.ExportBundle(dialog.FileName, ActiveGameDirectory(),
            ViewModel?.ActiveChoice?.Id, SummaryHeader());
    }

    /// <summary>
    /// One click export that needs no file dialog: it writes into the log folder and
    /// copies the resulting path to the clipboard, so it works in every Windows
    /// location the user can write to.
    /// </summary>
    private void QuickExport_Click(object sender, RoutedEventArgs e)
    {
        Services.LogService.User($"快速导出日志（实例 {ViewModel?.ActiveChoice?.Id ?? "未选择"}）", "日志");

        var directory = Services.LogService.LogDirectory;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // the exporter falls back to %LOCALAPPDATA% / desktop / temp
        }

        var target = System.IO.Path.Combine(directory, $"TiaMC-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var result = Services.LogExporter.ExportText(target, ActiveGameDirectory(),
            ViewModel?.ActiveChoice?.Id, SummaryHeader());

        if (result.Ok)
        {
            try
            {
                System.Windows.Clipboard.SetText(target);
                Services.LogService.Info("导出路径已复制到剪贴板: " + target, "日志");
            }
            catch (Exception)
            {
                // clipboard can be busy
            }

            Services.LogService.Info("完整日志: " + target, "日志");
        }
    }

    /// <summary>Game directory of the instance that is currently selected.</summary>
    private string? ActiveGameDirectory()
    {
        try
        {
            var versionId = ViewModel?.ActiveChoice?.Id;
            if (string.IsNullOrEmpty(versionId)) return null;

            var config = ViewModel?.Config;
            if (config is null) return null;

            return config.ResolveGameDirectory(versionId)
                   ?? System.IO.Path.Combine(config.MinecraftRoot ?? "", "versions", versionId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string SummaryHeader()
    {
        var vm = ViewModel;
        return $"启动器: TiaMC {TiaMc.Core.Utils.AppInfo.Version}    " +
               $"实例: {vm?.ActiveChoice?.Id ?? "未选择"}    " +
               $"账户: {vm?.SelectedAccount?.Name ?? "无"}    " +
               $"内存: {TiaMc.Core.Utils.SystemInfo.MemorySummary()}";
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Services.LogService.LogDirectory;
        try
        {
            System.IO.Directory.CreateDirectory(directory);

            // explorer.exe with an argument works even where ShellExecute is blocked.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Services.LogService.Error("打开日志文件夹失败: " + ex.Message, "日志");
            Services.LogService.Info("日志目录: " + directory, "日志");
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(Services.LogService.Dump());
            Services.LogService.Ok("已复制全部日志到剪贴板", "日志");
        }
        catch (Exception ex)
        {
            Services.LogService.Error("复制日志失败: " + ex.Message, "日志");
        }
    }
    // ---------------------------------------------------------- JVM arguments

    private void SaveJvmArgs_Click(object sender, RoutedEventArgs e) => ViewModel?.SaveJvmArgs();

    private void UndoJvmArgs_Click(object sender, RoutedEventArgs e) => ViewModel?.UndoJvmArgs();

    private void ResetJvmArgs_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetJvmArgs();

    private void InheritJvmArgs_Click(object sender, RoutedEventArgs e) => ViewModel?.InheritJvmArgs();

    private void ApplyJvmArgsGlobal_Click(object sender, RoutedEventArgs e) => ViewModel?.ApplyJvmArgsAsGlobal();

    /// <summary>Runs the log / crash report analysis of the last launch.</summary>
    private void Diagnose_Click(object sender, RoutedEventArgs e) => ViewModel?.DiagnoseLastLaunch();

    /// <summary>
    /// Modpack actions are wired through code-behind: commands bound inside the
    /// scrollable toolbar did not fire reliably, and this keeps every entry point
    /// (toolbar, context menu, buttons) on one implementation.
    /// </summary>
    private void ImportClientPack_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.ImportModpackCommand.Execute(null);

    private void ImportServerPack_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.ImportServerModpackCommand.Execute(null);

    private void DeployPack_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.DeployModpackCommand.Execute(null);

    private void TrimMemory_Click(object sender, RoutedEventArgs e) => ViewModel?.TrimMemory();

    private void StartServerPack_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.StartServerPackCommand.Execute(null);

    private void StopServerPack_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.StopServerPackCommand.Execute(null);

    private void ScanPacks_Click(object sender, RoutedEventArgs e) => ViewModel?.RefreshModpacks();

    private void DeletePack_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.DeleteModpackCommand.Execute(null);

    private void OpenPackFolder_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.OpenModpackFolderCommand.Execute(null);

    private void CopyPackCommand_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.CopyModpackCommandCommand.Execute(null);

    private void ApplyMemory_Click(object sender, RoutedEventArgs e) => ViewModel?.ApplyRecommendedMemory();

    /// <summary>The toolbar switch flips the authentication server on or off.</summary>
    private void YggSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        ViewModel.ToggleYggServer();
        YggConsoleBox?.ScrollToEnd();
    }

    /// <summary>Enter in the auth-server terminal runs the typed command line.</summary>
    private void YggInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is null) return;

        ViewModel.ExecuteYggCommand();
        YggConsoleBox?.ScrollToEnd();
        e.Handled = true;
    }

    /// <summary>Double clicking a Modrinth result installs that version.</summary>
    private void Modrinth_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list) return;
        if (list.SelectedItem is ModrinthSearchHit hit)
        {
            _ = ViewModel?.InstallModrinthByHitAsync(hit);
        }
    }

    // ------------------------------------------------------------- console

    private void ScrollConsolesToEnd()
    {
        if (ViewModel is { AutoScrollConsole: false }) return;

        ConsoleBox?.ScrollToEnd();
        ConsoleStrip?.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        StateChanged -= OnWindowStateChanged;
        if (DataContext is MainViewModel vm)
        {
            vm.Console.Flushed -= ScrollConsolesToEnd;
        }

        base.OnClosed(e);
    }
}

