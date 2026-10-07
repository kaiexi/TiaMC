using System.Globalization;
using System.Windows.Data;

namespace TiaMc.App.Converters;

/// <summary>把"是否正在运行"转成博途那种在线/离线文本。</summary>
public sealed class BoolToRunTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "● 在线（运行中）" : "○ 离线（已停止）";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}