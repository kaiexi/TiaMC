using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace TiaMc.App.Mvvm;

/// <summary>Minimal INotifyPropertyChanged base (no external MVVM dependency).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Simple ICommand implementation with an optional CanExecute predicate.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;

        // 同上：让条件变化（例如账户数量从 1 变 2）能真正刷新按钮的可用状态
        System.Windows.Input.CommandManager.RequerySuggested += (_, _) => RaiseCanExecuteChanged();
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// One collapsible section of a TIA style instruction palette. The rows are
/// simple name/description pairs (the launcher has no drag and drop, opening the
/// related page is the equivalent gesture).
/// </summary>
public sealed class PaletteSection : ObservableObject
{
    private bool _isExpanded = true;
    private string _filter = "";

    public required string Title { get; init; }
    public List<PaletteItem> Items { get; init; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) Raise(nameof(VisibleItems));
        }
    }

    /// <summary>Rows that survive the current filter (used by the search box).</summary>
    public IEnumerable<PaletteItem> VisibleItems =>
        string.IsNullOrWhiteSpace(_filter)
            ? Items
            : Items.Where(i => i.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                               i.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A clickable row of the instruction palette.</summary>
public sealed class PaletteItem
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string IconKey { get; init; } = "Icon.Instance";
    /// <summary>Workspace page index opened when the row is activated.</summary>
    public int PageIndex { get; init; } = -1;
    public string? CommandKey { get; init; }
}

public static class ObservableObjectExtensions
{
    /// <summary>
    /// Raises PropertyChanged for a computed property from outside the class that
    /// declares it (ObservableObject.Raise itself is protected).
    /// </summary>
    public static void RaisePropertyChanged(this ObservableObject target, string propertyName)
    {
        var method = target.GetType().GetMethod("Raise",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method?.Invoke(target, [propertyName]);
    }
}

/// <summary>Async variant so long running commands stay off the UI thread.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;

        // 跟随 WPF 的全局重新查询。否则 "() => !IsBusy" / "SelectedAccount is not null"
        // 这类命令只在启动时被查询过一次（那一刻条件可能不成立），之后再也没有机会重判，
        // 按钮就一直灰着点不动——「Microsoft 正版登录 / 刷新登录状态 / 删除账户」都是这个病。
        System.Windows.Input.CommandManager.RequerySuggested += (_, _) => RaiseCanExecuteChanged();
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
