using System.Collections.Concurrent;
using System.Text;
using System.Windows.Threading;
using TiaMc.App.Mvvm;

namespace TiaMc.App.ViewModels;

/// <summary>
/// Batching text sink for the launch console. The game can emit thousands of
/// lines per second, so incoming lines are queued and flushed to the bound
/// string on a timer instead of touching the UI for every line.
/// </summary>
public sealed class ConsoleViewModel : ObservableObject, IDisposable
{
    private const int MaxCharacters = 400_000;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _timer;
    private readonly StringBuilder _builder = new();
    private string _text = "";
    private bool _autoScroll = true;
    private long _lineCount;

    public ConsoleViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    public string Text
    {
        get => _text;
        private set => Set(ref _text, value);
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => Set(ref _autoScroll, value);
    }

    public long LineCount
    {
        get => _lineCount;
        private set => Set(ref _lineCount, value);
    }

    /// <summary>Raised after a flush so the view can scroll to the end.</summary>
    public event Action? Flushed;

    public void Append(string? line)
    {
        if (line is null) return;
        _pending.Enqueue(line);
    }

    public void AppendHeader(string text)
    {
        _pending.Enqueue("");
        _pending.Enqueue("===== " + text + " =====");
    }

    public void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        _builder.Clear();
        Text = "";
        LineCount = 0;
    }

    private void Flush()
    {
        if (_pending.IsEmpty) return;

        var added = 0;
        while (_pending.TryDequeue(out var line))
        {
            _builder.AppendLine(line);
            _lineCount++;
            added++;
        }

        if (added == 0) return;

        // Trim in chunks: rebuilding the whole buffer on every flush was the most
        // expensive part of a chatty game log (hundreds of KB per second).
        if (_builder.Length > MaxCharacters)
        {
            var keepFrom = _builder.Length - MaxCharacters;
            var newline = _builder.ToString(keepFrom, Math.Min(200, _builder.Length - keepFrom)).IndexOf('\n');
            _builder.Remove(0, newline >= 0 ? keepFrom + newline + 1 : keepFrom);
        }

        Text = _builder.ToString();
        Raise(nameof(LineCount));
        Flushed?.Invoke();
    }

    public string Dump() => _builder.ToString();

    public void Dispose() => _timer.Stop();
}
