namespace TiaMc.Core.Launch;

/// <summary>Everything the OS needs to start the game, produced before the process starts.</summary>
public sealed class LaunchPlan
{
    public required string VersionId { get; init; }
    public required string JavaPath { get; init; }
    public required string MainClass { get; init; }
    public required List<string> JvmArguments { get; init; }
    public required List<string> GameArguments { get; init; }
    public required string Classpath { get; init; }
    public required string NativesDirectory { get; init; }
    public required string GameDirectory { get; init; }
    public required Dictionary<string, string> Environment { get; init; }
    public required int RequiredJavaMajor { get; init; }

    /// <summary>Short summary used by the UI (loader + version + java).</summary>
    public string Summary =>
        $"{VersionId} | java {RequiredJavaMajor}+ | {Path.GetFileName(JavaPath)} | " +
        $"{(JvmArguments.Count + GameArguments.Count + 1)} args";
}

public sealed class LaunchOptions
{
    public int MinMemoryMb { get; set; } = 512;
    public int MaxMemoryMb { get; set; } = 4096;
    public string GcMode { get; set; } = "G1GC";

    /// <summary>Detected Java major version; open source collectors need a minimum one.</summary>
    public int JavaMajor { get; set; }
    public string? ExtraJvmArgs { get; set; }
    public string? ExtraGameArgs { get; set; }
    public string? JavaAgentPath { get; set; }

    /// <summary>authlib-injector.jar used for 外置登录 accounts (第三方皮肤站登录).</summary>
    public string? AuthlibInjectorPath { get; set; }
    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;
    public bool Fullscreen { get; set; }
    public bool DemoMode { get; set; }
    /// <summary>Extra classpath entries (";" separated), supports the %GAME_DIR% placeholder.</summary>
    public string? ExtraClasspath { get; set; }
    /// <summary>Extra environment variables, one KEY=VALUE per line.</summary>
    public string? EnvironmentVariables { get; set; }
    /// <summary>Replaces the main class (advanced override).</summary>
    public string? MainClassOverride { get; set; }
    /// <summary>Server to join directly, host:port.</summary>
    public string? QuickPlayServer { get; set; }

    /// <summary>
    /// Working directory / --gameDir of the launch. Empty means the shared
    /// .minecraft root; an instance folder here gives the version its own
    /// config, saves and mods (版本隔离).
    /// </summary>
    public string? GameDirectory { get; set; }

    /// <summary>True when <see cref="GameDirectory"/> is a per-instance folder that must exist.</summary>
    public bool Isolated { get; set; }

    public static LaunchOptions Default => new();
}
