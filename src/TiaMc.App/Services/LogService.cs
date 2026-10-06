using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace TiaMc.App.Services;

public enum LogLevel
{
    Info,
    Command,
    Success,
    Warning,
    Error,
    Game,

    /// <summary>User action audit trail (clicks, switches, downloads).</summary>
    Action
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Source { get; init; } = "TiaMC";
    public string Message { get; init; } = "";

    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string LevelText => Level switch
    {
        LogLevel.Command => "CMD",
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERR",
        LogLevel.Game => "GAME",
        LogLevel.Action => "动作",
        _ => "INFO"
    };
}

/// <summary>
/// Thread safe diagnostics log. Entries are raised as events so the UI can
/// append them on the dispatcher without the core knowing about WPF.
/// </summary>
public static class LogService
{
    private const int MaxEntries = 4000;
    private static readonly object Gate = new();

    public static ObservableCollection<LogEntry> Entries { get; } = [];

    public static event Action<LogEntry>? EntryAdded;

    public static void Write(LogLevel level, string message, string source = "TiaMC")
    {
        var entry = new LogEntry
        {
            Level = level,
            Message = message,
            Source = source
        };

        lock (FileGate)
        {
            try
            {
                _writer?.WriteLine($"{entry.TimeText} [{entry.LevelText}] {entry.Source}: {entry.Message}");
            }
            catch (Exception)
            {
                // logging must never break the launcher
            }
        }

        lock (Gate)
        {
            Entries.Add(entry);

            // Trim in batches: removing one item per line makes the observable
            // collection notify the UI thousands of times during a game session.
            if (Entries.Count > MaxEntries)
            {
                var excess = Entries.Count - MaxEntries + 512;
                for (var i = 0; i < excess && Entries.Count > 0; i++) Entries.RemoveAt(0);
            }
        }

        EntryAdded?.Invoke(entry);
    }

    public static void Info(string message, string source = "TiaMC") => Write(LogLevel.Info, message, source);
    public static void Ok(string message, string source = "TiaMC") => Write(LogLevel.Success, message, source);
    public static void Warn(string message, string source = "TiaMC") => Write(LogLevel.Warning, message, source);
    public static void Error(string message, string source = "TiaMC") => Write(LogLevel.Error, message, source);
    public static void Game(string message) => Write(LogLevel.Game, message, "Minecraft");
    public static void Command(string message) => Write(LogLevel.Command, message, "java");

// ------------------------------------------------------------ 日志文件

    private static readonly object FileGate = new();
    private static StreamWriter? _writer;
    private static string _sessionFile = "";

    /// <summary>Current session log file (empty until InitializeLogFile runs).</summary>
    public static string FilePath
    {
        get { lock (FileGate) return _sessionFile; }
    }

    private static string _logDirectory = "";

    /// <summary>Directory that holds the log files (set by InitializeLogFile).</summary>
    public static string LogDirectory => _logDirectory.Length > 0
        ? _logDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMC", "logs");

    /// <summary>
    /// Starts writing every entry to disk. The file is flushed per line, so a
    /// crash (or a kill from the task manager) still leaves a complete log that
    /// the user can send to the developer.
    /// </summary>
    public static void InitializeLogFile(string? directory = null)
    {
        lock (FileGate)
        {
            if (_writer is not null) return;

            try
            {
                var root = directory ?? Path.Combine(AppContext.BaseDirectory, "logs");
                _logDirectory = root;
                Directory.CreateDirectory(root);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                _sessionFile = Path.Combine(root, $"tiamc-{stamp}.log");
                // BOM so Notepad and other editors detect UTF-8 (Chinese text).
                _writer = new StreamWriter(new FileStream(_sessionFile, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite), new UTF8Encoding(true))
                {
                    AutoFlush = true
                };

                _writer.WriteLine($"===== TiaMC 启动器日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                _writer.WriteLine($"版本: {TiaMc.Core.Utils.AppInfo.Version}    " +
                                  $"系统: {Environment.OSVersion}   " +
                                  $"物理内存: {TiaMc.Core.Utils.SystemInfo.TotalPhysicalMemoryMb} MB   " +
                                  $"CPU: {Environment.ProcessorCount} 核");
                _writer.WriteLine();

                CleanupOldLogs(root, keep: 20);
            }
            catch (Exception)
            {
                _writer = null;
                _sessionFile = "";
            }
        }
    }

    /// <summary>Keeps the newest N log files so the folder cannot grow forever.</summary>
    private static void CleanupOldLogs(string directory, int keep)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("tiamc-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception)
                {
                    // ignore
                }
            }
        }
        catch (Exception)
        {
            // ignore
        }
    }

    /// <summary>Writes the whole in-memory log to a user chosen file.</summary>
    public static (bool Ok, string Message) ExportTo(string path)
    {
        try
        {
            var text = Dump();
            var header = $"===== TiaMC 日志导出 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}" +
                         $"当前会话日志文件: {(_sessionFile.Length > 0 ? _sessionFile : "（未启用）")}{Environment.NewLine}" +
                         $"物理内存: {TiaMc.Core.Utils.SystemInfo.MemorySummary()}{Environment.NewLine}" +
                         $"{Environment.NewLine}";
            File.WriteAllText(path, header + text, new System.Text.UTF8Encoding(true));
            return (true, $"日志已导出到 {path}（{text.Length} 字符）");
        }
        catch (Exception e)
        {
            return (false, "导出日志失败: " + e.Message);
        }
    }

    /// <summary>Records a user action (audit trail).</summary>
    public static void User(string message, string source = "用户") => Write(LogLevel.Action, message, source);

    /// <summary>进程内日志条目的快照（Web 端轮询用）。</summary>
    public static List<LogEntry> Snapshot()
    {
        lock (Gate)
        {
            return Entries.ToList();
        }
    }

    public static string Dump()
    {
        var builder = new StringBuilder();
        lock (Gate)
        {
            foreach (var entry in Entries)
            {
                builder.Append(entry.TimeText).Append(" [").Append(entry.LevelText).Append("] ")
                       .Append(entry.Source).Append(": ").Append(entry.Message).AppendLine();
            }
        }

        return builder.ToString();
    }
}

/// <summary>Dispatcher helper so background threads can safely touch the UI.</summary>
public static class Ui
{
    public static Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}