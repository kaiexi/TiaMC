using System.Diagnostics;
using System.Text;

namespace TiaMc.Core.Servers;

/// <summary>
/// 通用的服务端进程运行器（学 MCSManager 的实例启停 + 控制台）：
///   * 启动一个服务端实例目录里的核心（``java -Xmx… -jar &lt;core&gt;.nogui``），或直接跑它的启动脚本；
///   * 把 stdout/stderr **逐行回调**出去（启动器把它写进「输出窗口」）；
///   * 停止时优先**向 stdin 发 ``stop``**（让服务端自己保存世界并退出），超时才杀进程。
/// </summary>
public sealed class ServerRunner : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _tail = new();

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>服务端输出的最后若干行（用于诊断/界面显示）。</summary>
    public string Tail
    {
        get { lock (_tail) return _tail.ToString(); }
    }

    public int LastExitCode { get; private set; } = -1;

    /// <summary>
    /// 启动实例目录里的服务端。
    /// </summary>
    public bool Start(string instanceDirectory, string coreJar, string javaPath, int maxMemoryMb,
        Action<string>? onOutput = null, Action<int>? onExit = null)
    {
        if (IsRunning) return false;
        if (!Directory.Exists(instanceDirectory)) return false;
        if (!File.Exists(Path.Combine(instanceDirectory, coreJar))) return false;

        var info = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(javaPath) ? "java" : javaPath,
            WorkingDirectory = instanceDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        info.ArgumentList.Add($"-Xmx{Math.Max(512, maxMemoryMb)}M");
        info.ArgumentList.Add("-Xms" + Math.Min(1024, Math.Max(512, maxMemoryMb)) + "M");
        info.ArgumentList.Add("-jar");
        info.ArgumentList.Add(coreJar);
        info.ArgumentList.Add("nogui");

        try
        {
            _process = new Process { StartInfo = info, EnableRaisingEvents = true };

            void Forward(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                lock (_tail)
                {
                    _tail.AppendLine(line);
                    if (_tail.Length > 20000) _tail.Remove(0, _tail.Length - 15000);   // 只留最近的日志
                }

                onOutput?.Invoke(line!);
            }

            _process.OutputDataReceived += (_, e) => Forward(e.Data);
            _process.ErrorDataReceived += (_, e) => Forward(e.Data);
            _process.Exited += (_, _) =>
            {
                LastExitCode = _process?.ExitCode ?? -1;
                onExit?.Invoke(LastExitCode);
            };

            if (!_process.Start()) return false;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            return true;
        }
        catch (Exception)
        {
            _process = null;
            return false;
        }
    }

    /// <summary>
    /// 优雅停止：先发 ``stop``（服务端会保存世界再退出），最多等 waitSeconds 秒，超时再杀。
    /// </summary>
    public bool Stop(int waitSeconds = 30, Action<string>? onOutput = null)
    {
        var process = _process;
        if (process is null || process.HasExited) return true;

        try
        {
            process.StandardInput.WriteLine("stop");
            process.StandardInput.Flush();

            if (process.WaitForExit(waitSeconds * 1000)) return true;

            onOutput?.Invoke("服务端未在超时内退出，强制结束进程");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10000);
            return true;
        }
        catch (Exception)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            return false;
        }
    }

    public void Dispose()
    {
        try { _process?.Dispose(); } catch (Exception) { }
        _process = null;
    }
}
