using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TiaMc.App.Mvvm;
using TiaMc.Core.Minecraft;

namespace TiaMc.App.ViewModels;

/// <summary>Drives the "automatic detection of Minecraft folders" dialog.</summary>
public sealed class McScanViewModel : ObservableObject
{
    private bool _isScanning;
    private McRootCandidate? _selected;
    private string _status = "点击「开始检测」扫描本机的 Minecraft 目录";

    public ObservableCollection<McRootCandidate> Candidates { get; } = [];

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            Raise(nameof(IsNotScanning));
        }
    }

    public bool IsNotScanning => !IsScanning;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public McRootCandidate? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Scans in the background so the dialog stays responsive.</summary>
    public async Task ScanAsync(string? configuredRoot, bool scanDrives = true)
    {
        if (IsScanning) return;

        IsScanning = true;
        Status = scanDrives ? "正在检测（含磁盘扫描）..." : "正在检测...";

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var found = await Task.Run(() => MinecraftFinder.FindAll(configuredRoot, null, scanDrives));
            watch.Stop();

            var previous = Selected?.Path;
            Candidates.Clear();
            foreach (var candidate in found) Candidates.Add(candidate);

            Selected = Candidates.FirstOrDefault(c =>
                           string.Equals(c.Path, previous, StringComparison.OrdinalIgnoreCase))
                       ?? Candidates.FirstOrDefault(c => c.VersionCount > 0)
                       ?? Candidates.FirstOrDefault();

            Status = Candidates.Count == 0
                ? "没有找到 Minecraft 目录，请手动浏览选择"
                : $"找到 {Candidates.Count} 个目录（{watch.ElapsedMilliseconds} ms），" +
                  $"其中 {Candidates.Count(c => c.VersionCount > 0)} 个包含已安装版本";
        }
        catch (Exception e)
        {
            Status = "检测失败: " + e.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }
}
