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

    private static long _maxSessionFileBytes = 8L * 1024 * 1024;

    /// <summary>可在设置里改：单个日志文件上限（字节）。默认 8 MB。</summary>
    public static long MaxSessionFileBytes
    {
        get { lock (FileGate) return _maxSessionFileBytes; }
        set { lock (FileGate) _maxSessionFileBytes = Math.Max(256 * 1024, value); }
    }

    private static long _maxTotalBytes = 64L * 1024 * 1024;

    /// <summary>可在设置里改：日志目录总量上限（字节）。默认 64 MB。</summary>
    public static long LogMaxTotalBytes
    {
        get { lock (FileGate) return _maxTotalBytes; }
        set { lock (FileGate) _maxTotalBytes = Math.Max(1 * 1024 * 1024, value); }
    }

    /// <summary>可在设置里改：保留的日志份数。默认 20。</summary>
    public static int KeepFiles { get; set; } = 20;

    /// <summary>把设置里的 MB / 份数应用到日志服务。</summary>
    public static void ApplyLimits(int maxFileMb, int keepFiles, int maxTotalMb)
    {
        MaxSessionFileBytes = Math.Max(1, maxFileMb) * 1024L * 1024L;
        LogMaxTotalBytes = Math.Max(1, maxTotalMb) * 1024L * 1024L;
        KeepFiles = Math.Clamp(keepFiles, 1, 200);
    }
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
                if (!_sessionFileCapped && _sessionFileBytes < MaxSessionFileBytes)
                {
                    var line = $"{entry.TimeText} [{entry.LevelText}] {entry.Source}: {entry.Message}";
                    _writer?.WriteLine(line);
                    _sessionFileBytes += line.Length + Environment.NewLine.Length;
                }
                else if (!_sessionFileCapped)
                {
                    // 单文件上限：模组服能在几分钟里刷出几十 MB 日志，以前会无限写大。
                    _sessionFileCapped = true;
                    _writer?.WriteLine(
                        $"{DateTime.Now:HH:mm:ss.fff} [WARN] TiaMC: 日志文件已达 {MaxSessionFileBytes / 1024 / 1024} MB 上限，" +
                        "后续内容不再写入文件（界面里仍然可见，可用「导出日志」保存完整内容）。");
                    _writer?.Flush();
                }
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
    private static long _sessionFileBytes;
    private static bool _sessionFileCapped;

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
        _sessionFileBytes = 0;
        _sessionFileCapped = false;
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
            var all = new DirectoryInfo(directory).GetFiles("tiamc-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            // 1) 份数上限
            var doomed = all.Skip(keep).ToList();

            // 2) 目录总量上限（在份数上限之外再兜一层，避免"每份都很大"时目录撑爆）
            long total = 0;
            var limit = LogMaxTotalBytes;
            foreach (var file in all)
            {
                total += file.Length;
                if (total > limit && !doomed.Contains(file)) doomed.Add(file);
            }

            foreach (var file in doomed)
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