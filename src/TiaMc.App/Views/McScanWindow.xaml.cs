using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TiaMc.App.ViewModels;
using TiaMc.Core.Minecraft;

namespace TiaMc.App.Views;

/// <summary>
/// "Automatically detect the Minecraft folder" dialog: scans the well known
/// locations, the registry, other launchers and (optionally) the drives, then
/// returns the folder the user picked.
/// </summary>
public partial class McScanWindow : Window
{
    public McScanWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.ScanAsync(ConfiguredRoot);
    }

    /// <summary>Folder to show as "current configuration" in the result list.</summary>
    public string? ConfiguredRoot { get; set; }

    public McScanViewModel ViewModel => (McScanViewModel)DataContext;

    /// <summary>Shows the dialog and returns the chosen folder, or null when cancelled.</summary>
    public static string? ShowDialog(Window? owner, string? configuredRoot)
    {
        var window = new McScanWindow
        {
            Owner = owner,
            ConfiguredRoot = configuredRoot
        };

        return window.ShowDialog() == true ? window.ViewModel.Selected?.Path : null;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ScanAsync(ConfiguredRoot, scanDrives: true);

    private async void ScanFast_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ScanAsync(ConfiguredRoot, scanDrives: false);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 .minecraft 目录",
            InitialDirectory = Directory.Exists(ConfiguredRoot) ? ConfiguredRoot! : Environment.CurrentDirectory
        };

        if (dialog.ShowDialog() != true) return;

        // Represent the manual choice as a candidate so the caller keeps one path.
        var candidate = new McRootCandidate
        {
            Path = dialog.FolderName,
            Source = McRootSource.Configured,
            Origin = "手动选择",
            VersionCount = MinecraftFinder.CountVersions(dialog.FolderName)
        };

        ViewModel.Candidates.Insert(0, candidate);
        ViewModel.Selected = candidate;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.Selected is null) return;
        DialogResult = true;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null)
        {
            MessageBox.Show(this, "请先选择一个目录。", "TIA-MC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
