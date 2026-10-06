using System.Windows.Media;
using TiaMc.App.Mvvm;
using TiaMc.App.Services;
using TiaMc.Core.Resources;

namespace TiaMc.App.ViewModels;

/// <summary>
/// Row wrapper for a catalogue entry. It carries the decoded project icon so the
/// list shows what kind of content each row is (mod / modpack / pack / shader)
/// without blocking the UI thread.
/// </summary>
public sealed class ResourceHitVm : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconRequested;

    public ResourceHitVm(ResourceHit hit)
    {
        Hit = hit;

        // The icon is fetched the first time the row is realised by the virtualising
        // panel, which keeps a 40 hit search to a handful of downloads.
        IconCache.Load(hit.IconUrl, image =>
        {
            Icon = image;
            Raise(nameof(Icon));
            Raise(nameof(HasIcon));
        });
    }

    public ResourceHit Hit { get; }

    public string Title => Hit.TitleText;

    public string Subtitle => Hit.Subtitle;

    public string Slug => Hit.Slug;

    public string Updated => Hit.Updated.Length >= 10 ? Hit.Updated[..10] : Hit.Updated;

    public string VersionsText => string.Join(", ", Hit.GameVersions.Take(4));

    /// <summary>Fallback badge when a project has no icon.</summary>
    public string KindBadge => Hit.Kind switch
    {
        ResourceKind.Modpack => "整合包",
        ResourceKind.ResourcePack => "资源包",
        ResourceKind.Shader => "光影",
        ResourceKind.Datapack => "数据包",
        ResourceKind.World => "世界",
        _ => "模组"
    };

    public string KindShort => Hit.Kind switch
    {
        ResourceKind.Modpack => "包",
        ResourceKind.ResourcePack => "材",
        ResourceKind.Shader => "光",
        ResourceKind.Datapack => "数",
        ResourceKind.World => "界",
        _ => "模"
    };

    public ImageSource? Icon
    {
        get => _icon;
        private set => Set(ref _icon, value);
    }

    public bool HasIcon => _icon is not null;

    /// <summary>Loads the icon on demand (used when the row is created).</summary>
    public void EnsureIcon()
    {
        if (_iconRequested) return;
        _iconRequested = true;

        IconCache.Load(Hit.IconUrl, image =>
        {
            Icon = image;
            Raise(nameof(HasIcon));
        });
    }
}
