namespace TiaMc.Core.Utils;

public static class TextUtil
{
    /// <summary>Applies every key/value replacement once, left to right.</summary>
    public static string ReplaceMap(string input, IReadOnlyDictionary<string, string> map)
    {
        if (string.IsNullOrEmpty(input)) return input;

        foreach (var (key, value) in map)
        {
            if (input.Contains(key, StringComparison.Ordinal))
            {
                input = input.Replace(key, value ?? "", StringComparison.Ordinal);
            }
        }

        return input;
    }

    /// <summary>Collapses whitespace and truncates for log/UI display.</summary>
    public static string Shorten(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "...";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}

public static class AppInfo
{
    public const string LauncherName = "TiaMC";
    public const string LauncherTitle = "TIA-MC 工程启动器";
    public const string Version = "1.0.0";
}
