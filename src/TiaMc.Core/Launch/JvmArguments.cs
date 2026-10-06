using System.Text;

namespace TiaMc.Core.Launch;

/// <summary>
/// Parsing, validation and merging of JVM arguments.
///
/// The launcher manages a few arguments itself (-Xmx, the GC flag, the natives
/// path, ...). Everything the user writes must win over those, otherwise a custom
/// "-Xmx8G" would be silently followed by the launcher's own "-Xmx4096m" and the
/// JVM would take the last one. <see cref="Merge"/> removes the managed argument
/// when the user provides one with the same identity.
/// </summary>
public static class JvmArguments
{
    /// <summary>The argument families the launcher builds itself.</summary>
    private static readonly string[] ManagedPrefixes =
    [
        "-Xmx", "-Xms", "-Xss", "-XX:MaxMetaspaceSize", "-XX:MetaspaceSize",
        "-XX:+UseG1GC", "-XX:+UseSerialGC", "-XX:+UseParallelGC", "-XX:+UseZGC",
        "-XX:+UseShenandoahGC", "-XX:+UseEpsilonGC", "-Xgcpolicy:",
        "-javaagent:", "-agentlib:", "-Djava.library.path", "-Dfile.encoding",
        "-XX:HeapDumpPath", "-Djna.tmpdir"
    ];

    /// <summary>Splits a free-form argument string into single arguments (quotes respected).</summary>
    public static List<string> Parse(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var current = new StringBuilder();
        var quoted = false;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // One argument per line is also accepted (the editor is multi line).
            if (!line.Contains(' ') || (line.StartsWith('-') && !line.Contains(" -")))
            {
                result.Add(Unquote(line));
                continue;
            }

            foreach (var c in line)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }

                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
        }

        return result.Where(a => a.Length > 0).ToList();
    }

    /// <summary>
    /// Identity of an argument, used to decide whether two arguments configure the
    /// same thing ("-Xmx4G" and "-Xmx8G" share the identity "-Xmx", and
    /// "-XX:+UseG1GC" / "-XX:-UseG1GC" / "-XX:UseG1GC=..." share "-XX:UseG1GC").
    /// </summary>
    public static string Identity(string argument)
    {
        var arg = argument.Trim();
        if (arg.Length == 0) return "";

        // -XX:+Flag / -XX:-Flag / -XX:Flag=Value -> the option name only.
        if (arg.StartsWith("-XX:", StringComparison.Ordinal))
        {
            var rest = arg[4..];
            if (rest.StartsWith('+') || rest.StartsWith('-')) rest = rest[1..];
            var equalsIndex = rest.IndexOf('=');
            if (equalsIndex > 0) rest = rest[..equalsIndex];

            // Only one collector can be active, so all GC switches share a single
            // identity: a user supplied -XX:+UseZGC replaces the launcher's
            // -XX:+UseG1GC instead of being appended next to it (which would make
            // the JVM refuse to start).
            if (rest.StartsWith("Use", StringComparison.OrdinalIgnoreCase) &&
                rest.EndsWith("GC", StringComparison.OrdinalIgnoreCase))
            {
                return "-XX:GC";
            }

            return "-XX:" + rest;
        }

        // OpenJ9's collector switch is a collector choice as well.
        if (arg.StartsWith("-Xgcpolicy:", StringComparison.OrdinalIgnoreCase)) return "-XX:GC";

        // -Xmx4G / -Xms512m / -Xss1m: the switch itself is the identity.
        foreach (var prefix in new[] { "-Xmx", "-Xms", "-Xss", "-Xmn", "-Xloggc", "-Xprof" })
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return prefix;
        }

        // -javaagent:jar, -agentlib:jdwp=..., -Xgcpolicy:gencon
        var colon = arg.IndexOf(':');
        if (colon > 0) return arg[..colon];

        // -Dname=value -> -Dname
        if (arg.StartsWith("-D", StringComparison.Ordinal))
        {
            var equals = arg.IndexOf('=');
            return equals > 0 ? arg[..equals] : arg;
        }

        var lastEquals = arg.IndexOf('=');
        return lastEquals > 0 ? arg[..lastEquals] : arg;
    }

    /// <summary>True when the launcher also generates this argument itself.</summary>
    public static bool IsManaged(string argument) =>
        ManagedPrefixes.Any(p => argument.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Merges managed and user arguments: the user wins for every identity it
    /// provides, and the removed managed arguments are reported.
    /// </summary>
    public static (List<string> Arguments, List<string> Replaced) Merge(IEnumerable<string> managed,
        IEnumerable<string> user)
    {
        var userList = user.ToList();
        var userIdentities = userList
            .Select(Identity)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();
        var replaced = new List<string>();

        foreach (var argument in managed)
        {
            var identity = Identity(argument);
            if (identity.Length > 0 && userIdentities.Contains(identity))
            {
                replaced.Add(argument);
                continue;
            }

            result.Add(argument);
        }

        result.AddRange(userList);
        return (result, replaced);
    }

    /// <summary>Human readable warnings about the user's argument list.</summary>
    public static List<string> Validate(string? text)
    {
        var warnings = new List<string>();
        var arguments = Parse(text);
        if (arguments.Count == 0) return warnings;

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            if (!argument.StartsWith('-'))
            {
                warnings.Add($"「{argument}」不是以 - 开头的参数，会被原样传给 JVM");
                continue;
            }

            var identity = Identity(argument);
            if (seen.TryGetValue(identity, out var previous) && !previous.Equals(argument, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"参数重复：{previous} 与 {argument}（后者生效）");
            }
            else
            {
                seen[identity] = argument;
            }

            if (argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("-Xms 已被弃用（本启动器只设置最大内存），写了也不影响启动，但建议删掉");
            }

            if (argument.Contains("UseConcMarkSweepGC", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("-XX:+UseConcMarkSweepGC 在 Java 14+ 已被移除，会导致 JVM 无法启动");
            }
        }

        return warnings.Distinct().ToList();
    }

    /// <summary>Formats arguments for display, one per line.</summary>
    public static string Format(IEnumerable<string> arguments) => string.Join(Environment.NewLine, arguments);

    /// <summary>Formats arguments as a single copyable command line fragment.</summary>
    public static string FormatInline(IEnumerable<string> arguments) => string.Join(' ', arguments);

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
