using System.Collections.ObjectModel;
using TiaMc.App.Mvvm;

namespace TiaMc.App.ViewModels;

/// <summary>Node of the project tree in the left navigation pane.</summary>
public sealed class TreeNode : ObservableObject
{
    private bool _isExpanded = true;
    private bool _isSelected;

    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string IconKey { get; init; } = "Icon.Instance";
    public string? Tag { get; init; }
    public string? NodeKind { get; init; }
    public ObservableCollection<TreeNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool HasChildren => Children.Count > 0;

    public override string ToString() => Title;
}
