using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using TiaMc.Core.Utils;

namespace TiaMc.App.Services;

/// <summary>
/// Exports everything needed to explain a problem:
///
///   * the whole launcher log (in-memory entries, one line per event)
///   * the user action audit trail (already part of the entries)
///   * Minecraft's own logs: logs/latest.log, logs/debug.log, crash-reports/*.txt,
///     hs_err_pid*.log of the active instance
///   * system information (OS, RAM, CPU, Java runtimes, installed versions)
///   * the launcher configuration with every secret masked
///   * the mod list of the instance
///
/// Two formats are offered: one plain .log file (everything concatenated, easy to
/// paste into a chat) and a .zip bundle (one file per source, best for a bug
/// report).
/// </summary>
public static class LogExporter
{
    private const int MaxGameLogBytes = 4 * 1024 * 1024;
    private const int MaxCrashReports = 8;

    public sealed record Source(string Name, string Path, long Bytes);

    /// <summary>Collects the Minecraft side files of an instance game directory.</summary>
    public static List<Source> CollectGameSources(string? gameDirectory)
    {
        var sources = new List<Source>();
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) return sources;

        void Add(string path, string name)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) sources.Add(new Source(name, info.FullName, info.Length));
            }
            catch (Exception)
            {
                // ignore
            }
        }

        Add(Path.Combine(gameDirectory, "logs", "latest.log"), "minecraft/latest.log");
        Add(Path.Combine(gameDirectory, "logs", "debug.log"), "minecraft/debug.log");
        Add(Path.Combine(gameDirectory, "logs", "telemetry.log"), "minecraft/telemetry.log");

        // Newest crash reports and JVM crash dumps.
        try
        {
            var crashReports = Path.Combine(gameDirectory, "crash-reports");
            if (Directory.Exists(crashReports))
            {
                foreach (var file in new DirectoryInfo(crashReports).GetFiles("crash-*.txt")
                             .OrderByDescending(f => f.LastWriteTimeUtc)
                             .Take(MaxCrashReports))
                {
                    sources.Add(new Source("crash-reports/" + file.Name, file.FullName, file.Length));
                }
            }

            foreach (var file in new DirectoryInfo(gameDirectory).GetFiles("hs_err_pid*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Take(3))
            {
                sources.Add(new Source("jvm/" + file.Name, file.FullName, file.Length));
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return sources;
    }

    /// <summary>Builds the complete text export (launcher log + every Minecraft log).</summary>
    public static (bool Ok, string Message, long Bytes, int Sources) ExportText(string targetPath, string? gameDirectory,
        string? versionId, string summaryHeader)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("================ TiaMC 诊断日志 ================");
            builder.AppendLine(summaryHeader);
            builder.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"日志文件: {(LogService.FilePath.Length > 0 ? LogService.FilePath : "（未启用落盘）")}");
            builder.AppendLine();

            builder.AppendLine("================ 系统信息 ================");
            builder.AppendLine(BuildSystemInfo(versionId));
            builder.AppendLine();

            builder.AppendLine("================ 模组列表 ================");
            builder.AppendLine(BuildModList(gameDirectory));
            builder.AppendLine();

            builder.AppendLine("================ 配置（已脱敏）================");
            builder.AppendLine(BuildSettings());
            builder.AppendLine();

            builder.AppendLine("================ 启动器日志（完整）================");
            builder.AppendLine(LogService.Dump());
            builder.AppendLine();

            var sources = CollectGameSources(gameDirectory);
            foreach (var source in sources)
            {
                builder.AppendLine($"================ {source.Name}（{TextUtil.FormatBytes(source.Bytes)}）================");
                builder.AppendLine(ReadTail(source.Path, MaxGameLogBytes));
                builder.AppendLine();
            }

            if (sources.Count == 0)
            {
                builder.AppendLine("================ Minecraft 日志 ================");
                builder.AppendLine($"没有找到游戏日志（游戏目录: {gameDirectory ?? "未确定"}）。" +
                                   "启动过一次游戏后这里会出现 logs/latest.log、crash-reports 等文件。");
            }

            var text = builder.ToString();
            File.WriteAllText(targetPath, text, new UTF8Encoding(true));
            LogService.Ok($"日志已完整导出: {targetPath}（{TextUtil.FormatBytes(text.Length)}，含 {sources.Count} 个 MC 日志文件）",
                "日志");
            return (true, targetPath, text.Length, sources.Count);
        }
        catch (Exception e)
        {
            LogService.Error("导出日志失败: " + e.Message, "日志");
            return (false, e.Message, 0, 0);
        }
    }

    /// <summary>Builds a zip bundle with one file per source plus the summary.</summary>
    public static (bool Ok, string Message, int Files) ExportBundle(string targetPath, string? gameDirectory,
        string? versionId, string summaryHeader)
    {
        try
        {
            var sources = CollectGameSources(gameDirectory);
            var count = 0;

            using (var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create))
            {
                Write(archive, "summary.txt",
                    "================ TiaMC 诊断包 ================\r\n" + summaryHeader + "\r\n" +
                    $"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                    $"游戏目录: {gameDirectory ?? "未确定"}\r\n" +
                    $"当前实例: {versionId ?? "未选择"}\r\n");
                count++;

                Write(archive, "launcher.log", LogService.Dump());
                count++;

                if (LogService.FilePath.Length > 0 && File.Exists(LogService.FilePath))
                {
                    AddFile(archive, LogService.FilePath, "launcher-session.log");
                    count++;
                }

                Write(archive, "system-info.txt", BuildSystemInfo(versionId));
                count++;

                Write(archive, "settings-sanitized.json", BuildSettings());
                count++;

                Write(archive, "mods.txt", BuildModList(gameDirectory));
                count++;

                foreach (var source in sources)
                {
                    AddFile(archive, source.Path, source.Name);
                    count++;
                }
            }

            var size = new FileInfo(targetPath).Length;
            LogService.Ok($"诊断包已导出: {targetPath}（{TextUtil.FormatBytes(size)}，{count} 个文件，" +
                          $"其中 MC 日志 {sources.Count} 个）".Replace("FormatBytes", "FormatBytes"), "日志");
            return (true, targetPath, count);
        }
        catch (Exception e)
        {
            LogService.Error("导出诊断包失败: " + e.Message, "日志");
            return (false, e.Message, 0);
        }
    }

    // ------------------------------------------------------------- helpers

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddFile(ZipArchive archive, string path, string name)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            // Huge debug logs are truncated to keep the bundle sendable.
            if (info.Length <= MaxGameLogBytes)
            {
                archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
                return;
            }

            Write(archive, name, ReadTail(path, MaxGameLogBytes));
        }
        catch (Exception)
        {
            // a locked file must not break the export
        }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= maxBytes)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                return reader.ReadToEnd();
            }

            stream.Seek(-maxBytes, SeekOrigin.End);
            using var tailReader = new StreamReader(stream, Encoding.UTF8, true);
            return $"（文件较大，仅保留最后 {TextUtil.FormatBytes(maxBytes)}）{Environment.NewLine}" +
                   tailReader.ReadToEnd();
        }
        catch (Exception e)
        {
            return "（无法读取: " + e.Message + "）";
        }
    }

    private static string BuildSystemInfo(string? versionId)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"启动器版本 : {AppInfo.Version}");
        builder.AppendLine($"操作系统   : {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        builder.AppendLine($"物理内存   : {SystemInfo.MemorySummary()}");
        builder.AppendLine($"处理器     : {Environment.ProcessorCount} 逻辑核心");
        builder.AppendLine($"机器名     : {Environment.MachineName}    用户: {Environment.UserName}");
        builder.AppendLine($"运行目录   : {AppContext.BaseDirectory}");
        builder.AppendLine($"配置目录   : {AppConfig.ConfigDirectory}");
        builder.AppendLine($"缓存目录   : {AppPaths.CacheRoot}");
        builder.AppendLine($"当前实例   : {versionId ?? "未选择"}");

        try
        {
            builder.AppendLine("Java 运行时:");
            foreach (var java in TiaMc.Core.Java.JavaDetector.Detect(log: null))
            {
                builder.AppendLine($"  - {java.Display}");
            }
        }
        catch (Exception)
        {
            builder.AppendLine("Java 运行时: 检测失败");
        }

        return builder.ToString();
    }

    private static string BuildModList(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return "（未确定游戏目录）";
        var mods = TiaMc.Core.Mods.ModsManager.Scan(Path.Combine(gameDirectory, "mods"));
        if (mods.Count == 0) return $"（{Path.Combine(gameDirectory, "mods")} 下没有模组）";

        var builder = new StringBuilder();
        builder.AppendLine($"目录: {Path.Combine(gameDirectory, "mods")}    共 {mods.Count} 个");
        foreach (var mod in mods)
        {
            builder.AppendLine($"  [{(mod.Enabled ? "启用" : "停用")}] {mod.DisplayName} {mod.DisplayVersion} " +
                               $"({mod.LoaderText}) {mod.FileName}");
        }

        return builder.ToString();
    }

    /// <summary>Configuration dump with tokens, passwords and UUIDs masked.</summary>
    private static string BuildSettings()
    {
        try
        {
            var path = Path.Combine(AppConfig.ConfigDirectory, "config.json");
            if (!File.Exists(path)) return "（没有 config.json）";

            var json = File.ReadAllText(path);
            json = Mask(json, "\"accessToken\"\\s*:\\s*\"[^\"]*\"", "\"accessToken\":\"******\"");
            json = Mask(json, "\"refreshToken\"\\s*:\\s*\"[^\"]*\"", "\"refreshToken\":\"******\"");
            json = Mask(json, "\"clientToken\"\\s*:\\s*\"[^\"]*\"", "\"clientToken\":\"******\"");
            json = Mask(json, "\"password\"\\s*:\\s*\"[^\"]*\"", "\"password\":\"******\"");
            json = Mask(json, "\"uuid\"\\s*:\\s*\"[^\"]*\"", "\"uuid\":\"******\"");
            return json;
        }
        catch (Exception e)
        {
            return "（读取配置失败: " + e.Message + "）";
        }
    }

    private static string Mask(string text, string pattern, string replacement) =>
        Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase);
    // ------------------------------------------------- 无权限时的落盘回退

    /// <summary>True when the launcher can create files in the directory.</summary>
    public static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".tiamc-write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Fallback targets used when the chosen path cannot be written (the launcher may
    /// run as a normal user from a protected folder). Order: log folder, local
    /// application data, desktop, temp.
    /// </summary>
    public static List<string> FallbackDirectories()
    {
        var candidates = new List<string> { LogService.LogDirectory };

        try
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMC", "logs"));
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop)) candidates.Add(desktop);
            candidates.Add(Path.GetTempPath());
        }
        catch (Exception)
        {
            // ignore
        }

        return candidates.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToList();
    }

    /// <summary>Writes the export, retrying in a writable folder when the target fails.</summary>
    public static (bool Ok, string Path, string Message, bool FellBack) WriteWithFallback(string targetPath,
        Func<string, (bool Ok, string Message)> writer)
    {
        var result = writer(targetPath);
        if (result.Ok) return (true, targetPath, result.Message, false);

        var fileName = Path.GetFileName(targetPath);
        foreach (var directory in FallbackDirectories())
        {
            if (!IsWritable(directory)) continue;

            var alternative = Path.Combine(directory, fileName);
            var retry = writer(alternative);
            if (retry.Ok)
            {
                var message = $"原路径没有写入权限（{targetPath}），已改存到可写目录: {alternative}";
                LogService.Warn(message, "日志");
                return (true, alternative, message, true);
            }
        }

        return (false, targetPath, result.Message, false);
    }
}
