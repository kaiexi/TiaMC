using System.Runtime.InteropServices;
using TiaMc.Core.Minecraft;

namespace TiaMc.Core.Rules;

/// <summary>
/// Evaluates the "rules" arrays used by the vanilla launcher for libraries and
/// for individual game/jvm arguments. This mirrors the logic described in
/// https://minecraft.wiki/w/Client.json: default deny, last matching rule wins.
/// </summary>
public static class RuleEvaluator
{
    /// <summary>Feature flags that a rule can test. Anything absent is false.</summary>
    public sealed class FeatureSet
    {
        private readonly Dictionary<string, bool> _map = new(StringComparer.OrdinalIgnoreCase);

        public bool this[string key]
        {
            get => _map.TryGetValue(key, out var v) && v;
            set => _map[key] = value;
        }

        public static FeatureSet Default()
        {
            var set = new FeatureSet
            {
                ["is_demo_user"] = false,
                ["has_custom_resolution"] = false,
                ["has_quick_plays_support"] = false,
                ["is_quick_play_singleplayer"] = false,
                ["is_quick_play_multiplayer"] = false,
                ["is_quick_play_realms"] = false
            };
            return set;
        }
    }

    public static string OsName =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" :
        OperatingSystem.IsLinux() ? "linux" : "unknown";

    /// <summary>Mojang's os.arch value: "x86" for 32 bit processes, otherwise "x64".</summary>
    public static string OsArch => Environment.Is64BitProcess ? "x64" : "x86";

    public static bool IsAllowed(IEnumerable<RuleJson>? rules, FeatureSet? features = null)
    {
        if (rules is null) return true;

        var list = rules.ToList();
        if (list.Count == 0) return true;

        features ??= FeatureSet.Default();
        var allowed = false;

        foreach (var rule in list)
        {
            if (!Matches(rule, features)) continue;
            allowed = !string.Equals(rule.Action, "disallow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(RuleJson rule, FeatureSet features)
    {
        if (rule.Os is { } os)
        {
            if (!string.IsNullOrEmpty(os.Name) &&
                !string.Equals(os.Name, OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(os.Arch) &&
                !string.Equals(NormalizeArch(os.Arch), NormalizeArch(OsArch), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(os.Version))
            {
                try
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(
                            Environment.OSVersion.Version.ToString(), os.Version))
                    {
                        return false;
                    }
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
        }

        if (rule.Features is { Count: > 0 })
        {
            foreach (var (key, expected) in rule.Features)
            {
                if (features[key] != expected) return false;
            }
        }

        return true;
    }

    private static string NormalizeArch(string arch) => arch.ToLowerInvariant() switch
    {
        "x86_64" or "amd64" or "x64" => "x64",
        "x86" or "i386" or "i686" => "x86",
        "arm64" or "aarch64" => "arm64",
        var other => other
    };
}
