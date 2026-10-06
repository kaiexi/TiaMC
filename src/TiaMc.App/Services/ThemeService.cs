using System.Windows;
using System.Windows.Media;

namespace TiaMc.App.Services;

/// <summary>
/// 轻量主题服务：在「工具」菜单里切换 暗黑模式（代码高亮配色）与 皮肤（强调色）。
///
/// 做法：启动时先把浅色主题的画笔值拍个快照，之后在「快照」与「暗黑调色板」之间切换，
/// 因此不需要维护两份完整的资源字典；界面用的是 DynamicResource，切换即时生效。
/// 暗黑配色取自常见的代码编辑器（VS Code Dark+）：背景 #1E1E1E、正文 #D4D4D4、
/// 关键字蓝 #569CD6、字符串橙 #CE9178、类型青 #4EC9B0、注释灰 #6A9955。
/// </summary>
public static class ThemeService
{
    /// <summary>要接管的画笔键（与 TiaTheme.xaml 中的 DynamicResource 键一致）。</summary>
    private static readonly string[] Keys =
    [
        "Tia.WindowBg", "Tia.PanelBg", "Tia.PanelBgAlt", "Tia.ContentBg", "Tia.RowAlt",
        "Tia.Border", "Tia.BorderStrong", "Tia.Text", "Tia.MutedText", "Tia.TextStrong",
        "Tia.Brand", "Tia.Accent", "Tia.Run", "Tia.Warn", "Tia.Error", "Tia.RibbonBg"
    ];

    private static readonly Dictionary<string, Brush> LightSnapshot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>代码高亮风格的暗色调色板。</summary>
    private static readonly Dictionary<string, Color> DarkPalette = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tia.WindowBg"] = Color.FromRgb(0x1E, 0x1E, 0x1E),
        ["Tia.PanelBg"] = Color.FromRgb(0x25, 0x25, 0x26),
        ["Tia.PanelBgAlt"] = Color.FromRgb(0x2D, 0x2D, 0x30),
        ["Tia.ContentBg"] = Color.FromRgb(0x1E, 0x1E, 0x1E),
        ["Tia.RowAlt"] = Color.FromRgb(0x26, 0x26, 0x28),
        ["Tia.Border"] = Color.FromRgb(0x3C, 0x3C, 0x3C),
        ["Tia.BorderStrong"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["Tia.Text"] = Color.FromRgb(0xD4, 0xD4, 0xD4),
        ["Tia.MutedText"] = Color.FromRgb(0x85, 0x85, 0x85),
        ["Tia.TextStrong"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
        ["Tia.Brand"] = Color.FromRgb(0x56, 0x9C, 0xD6),   // 关键字蓝
        ["Tia.Accent"] = Color.FromRgb(0xCE, 0x91, 0x78),  // 字符串橙
        ["Tia.Run"] = Color.FromRgb(0x4E, 0xC9, 0xB0),     // 类型青
        ["Tia.Warn"] = Color.FromRgb(0xDC, 0xDC, 0xAA),    // 变量黄
        ["Tia.Error"] = Color.FromRgb(0xF4, 0x47, 0x47),
        ["Tia.RibbonBg"] = Color.FromRgb(0x2D, 0x2D, 0x30)
    };

    /// <summary>皮肤（强调色）预设：名称 → 主色 / 强调色。</summary>
    public static readonly (string Name, string Brand, string Accent)[] Skins =
    [
        ("工程蓝（默认）", "#2F6FB3", "#E9762B"),
        ("代码蓝", "#569CD6", "#CE9178"),
        ("翡翠绿", "#2E9E6B", "#D6B44A"),
        ("品红紫", "#A855F7", "#22D3EE"),
        ("石墨灰", "#5A6472", "#9CA3AF")
    ];

    public static bool DarkMode { get; private set; }
    public static string CurrentSkin { get; private set; } = Skins[0].Name;

    /// <summary>启动时调用：拍下浅色快照并应用当前设置。</summary>
    public static void Initialize(bool dark, string? skin)
    {
        Snapshot();
        CurrentSkin = string.IsNullOrWhiteSpace(skin) ? Skins[0].Name : skin!;
        Apply(dark, CurrentSkin);
    }

    public static void Apply(bool dark, string skin)
    {
        if (Application.Current is null) return;

        DarkMode = dark;
        CurrentSkin = skin;

        if (dark)
        {
            foreach (var (key, color) in DarkPalette)
            {
                SetBrush(key, color);
            }
        }
        else
        {
            // 先还原浅色，再叠加所选皮肤的主色/强调色
            foreach (var (key, brush) in LightSnapshot)
            {
                Application.Current.Resources[key] = brush;
            }
        }

        var preset = Skins.FirstOrDefault(s => s.Name == skin);
        if (preset.Name is null) preset = Skins[0];

        if (!dark)
        {
            SetBrush("Tia.Brand", Parse(preset.Brand));
            SetBrush("Tia.Accent", Parse(preset.Accent));
        }
        else
        {
            // 暗色下用代码高亮配色，但皮肤仍影响强调色
            SetBrush("Tia.Accent", Parse(preset.Accent));
        }
    }

    private static void Snapshot()
    {
        if (Application.Current is null || LightSnapshot.Count > 0) return;
        foreach (var key in Keys)
        {
            if (Application.Current.Resources[key] is Brush brush) LightSnapshot[key] = brush;
        }
    }

    private static void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current!.Resources[key] = brush;
    }

    private static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (Exception) { return Colors.SteelBlue; }
    }
}
