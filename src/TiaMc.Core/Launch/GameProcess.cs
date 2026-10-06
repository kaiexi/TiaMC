using System.Diagnostics;
using System.Text;

namespace TiaMc.Core.Launch;

public enum GameExitReason
{
    Exited,
    Failed,
    Killed
}

public sealed class GameExitedEventArgs : EventArgs
{
    public required int ExitCode { get; init; }
    public required GameExitReason Reason { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Owns the running java process: builds the command line, forwards stdout and
/// stderr lines as events and raises a single exit notification.
/// </summary>
public sealed class GameProcess : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private bool _exitRaised;

    /// <summary>Raised for every stdout line the game writes.</summary>
    public event Action<string>? OutputLine;

    public event EventHandler<GameExitedEventArgs>? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                try { return _process?.Id; }
                catch (InvalidOperationException) { return null; }
            }
        }
    }

    /// <summary>Human readable command line, useful for the log window and for bug reports.</summary>
    public static string ToCommandLine(LaunchPlan plan)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(plan.JavaPath).Append('"');
        foreach (var arg in plan.JvmArguments)
        {
            builder.Append(' ').Append(Quote(arg));
        }

        builder.Append(' ').Append(plan.MainClass);
        foreach (var arg in plan.GameArguments)
        {
            builder.Append(' ').Append(Quote(arg));
        }

        return builder.ToString();

        static string Quote(string value) =>
            value.Contains(' ') ? $"\"{value}\"" : value;
    }

    public void Start(LaunchPlan plan, Action<string>? log = null)
    {
        var info = new ProcessStartInfo(plan.JavaPath)
        {
            WorkingDirectory = Directory.Exists(plan.GameDirectory) ? plan.GameDirectory : Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in plan.JvmArguments) info.ArgumentList.Add(arg);
        info.ArgumentList.Add(plan.MainClass);
        foreach (var arg in plan.GameArguments) info.ArgumentList.Add(arg);

        foreach (var (key, value) in plan.Environment)
        {
            info.Environment[key] = value;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) OutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) OutputLine?.Invoke(e.Data);
        };
        process.Exited += (_, _) =>
        {
            int code;
            try { code = process.ExitCode; }
            catch (InvalidOperationException) { code = -1; }

            RaiseExited(new GameExitedEventArgs { ExitCode = code, Reason = GameExitReason.Exited });
            try { process.Dispose(); } catch { /* ignore */ }
        };

        lock (_gate)
        {
            _process = process;
        }

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            log?.Invoke($"[launch] 已启动 java 进程 (PID {process.Id})");
        }
        catch (Exception e)
        {
            RaiseExited(new GameExitedEventArgs
            {
                ExitCode = -1,
                Reason = GameExitReason.Failed,
                Error = e.Message
            });
            throw;
        }
    }

    private void RaiseExited(GameExitedEventArgs args)
    {
        lock (_gate)
        {
            if (_exitRaised) return;
            _exitRaised = true;
        }

        Exited?.Invoke(this, args);
    }

    /// <summary>Forcefully stops the game.</summary>
    public void Kill()
    {
        Process? process;
        lock (_gate) process = _process;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                RaiseExited(new GameExitedEventArgs { ExitCode = -1, Reason = GameExitReason.Killed });
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process already gone.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _process?.Dispose();
            }
            catch (InvalidOperationException) { /* ignore */ }

            _process = null;
        }
    }
}
