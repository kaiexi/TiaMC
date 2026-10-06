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

    /// <summary>
    /// 启动器版本号。以前一直写死 1.0.0，导致用户拿着旧版本问"为什么没有某个功能"时
    /// 无法确认自己在跑哪一版——现在和发布版本（Release tag）保持一致。
    /// </summary>
    public const string Version = "1.0.21";

    /// <summary>标题栏用：版本号紧跟在产品名后面，例如「TIA-MC 工程启动器 v1.0.9」。</summary>
    public static string TitleWithVersion => $"{LauncherTitle} v{Version}";
}
