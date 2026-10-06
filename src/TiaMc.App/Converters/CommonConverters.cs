using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace TiaMc.App.Converters;

/// <summary>true =&gt; Visible, false =&gt; Collapsed (or the inverse with the parameter "invert").</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Negates a boolean, used for IsEnabled bindings.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;
}

/// <summary>Shows only the file name of a path (optionally the last N folders with "tail:2").</summary>
public sealed class PathShortenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return "";

        if (parameter is string s && s.StartsWith("tail:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s[5..], out var tail) && tail > 0)
        {
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Join(Path.DirectorySeparatorChar, parts.TakeLast(tail));
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a log level to the brush key used by the log list.</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            "Error" => "Tia.Error",
            "Warning" => "Tia.Warn",
            "Success" => "Tia.Ok",
            "Command" => "Tia.BrandLight",
            "Game" => "Tia.Text",
            _ => "Tia.TextMuted"
        };

        return Application.Current?.TryFindResource(key) ?? System.Windows.Media.Brushes.Black;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>object =&gt; "Visible" when it is not null.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Looks up a geometry resource by the tree node's icon key.</summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string ?? "Icon.Instance";
        var resource = Application.Current?.TryFindResource(key);

        // Icon resources are geometry strings in some themes and Geometry objects in others.
        return resource switch
        {
            System.Windows.Media.Geometry geometry => geometry,
            string text => System.Windows.Media.Geometry.Parse(text),
            _ => System.Windows.Media.Geometry.Parse("M0,0 L10,10")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats the "installed" flag of a remote version row.</summary>
public sealed class InstalledTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "已安装" : "可下载";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Two way int equality, used by the vertical pane selector radio buttons
/// (IsChecked={Binding RightPane, Converter=..., ConverterParameter=0}).
/// </summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return false;
        var left = System.Convert.ToInt32(value, CultureInfo.InvariantCulture);
        var right = parameter is null ? 0 : System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture);
        return left == right;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool flag || !flag) return Binding.DoNothing;
        return parameter is null ? 0 : System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture);
    }
}

/// <summary>Colours a crash hint by its severity.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            "Fatal" => "Tia.Error",
            "Error" => "Tia.Error",
            "Warning" => "Tia.Warn",
            _ => "Tia.Text"
        };

        return Application.Current?.TryFindResource(key) ?? System.Windows.Media.Brushes.Black;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
/// <summary>Joins a string list for display in a grid cell.</summary>
public sealed class ListJoinConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is System.Collections.IEnumerable items and not string
            ? string.Join(", ", items.Cast<object>().Take(4))
            : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
/// <summary>Loads a mod icon from the extracted cache file (see IconCache).</summary>
public sealed class ModIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Services.IconCache.GetFileImage(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
/// <summary>Shows an element only when the bound text is not empty.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}