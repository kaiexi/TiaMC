using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TiaMc.App.Converters;

/// <summary>
/// Turns a Minecraft skin texture URL into a head image: the face is the 8x8
/// tile at (8,8) of the 64x64 skin and the hat/overlay layer sits at (40,8).
/// </summary>
public sealed class SkinHeadConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string source || source.Length == 0) return null;

        // Local skin files (offline accounts) carry a timestamp so a regenerated skin
        // refreshes the avatar without restarting.
        var key = source;
        try
        {
            if (File.Exists(source)) key = $"{source}|{File.GetLastWriteTimeUtc(source).Ticks}";
        }
        catch (Exception)
        {
            // keep the plain source as key
        }

        if (Cache.TryGetValue(key, out var cached)) return cached;

        BitmapSource? head = null;
        try
        {
            head = File.Exists(source)
                ? LoadLocal(source)
                : Task.Run(() => LoadAsync(source)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            head = null;
        }

        Cache[key] = head;
        return head;
    }

    /// <summary>Crops the face of a local skin file (no file lock, cached upstream).</summary>
    private static BitmapSource? LoadLocal(string path)
    {
        var skin = new BitmapImage();
        skin.BeginInit();
        skin.CacheOption = BitmapCacheOption.OnLoad;
        skin.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        skin.UriSource = new Uri(path, UriKind.Absolute);
        skin.EndInit();
        skin.Freeze();
        return Crop(skin);
    }

    private static BitmapSource? Crop(BitmapSource skin)
    {
        if (skin.PixelWidth < 64 || skin.PixelHeight < 16) return null;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var face = new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8));
            var destination = new Rect(0, 0, 64, 64);
            dc.DrawImage(face, destination);

            // The hat layer exists on the 64x64 format.
            if (skin.PixelHeight >= 64)
            {
                var hat = new CroppedBitmap(skin, new Int32Rect(40, 8, 8, 8));
                dc.DrawImage(hat, destination);
            }
        }

        var target = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static async Task<BitmapSource?> LoadAsync(string url)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);

            var skin = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                skin.BeginInit();
                skin.CacheOption = BitmapCacheOption.OnLoad;
                skin.StreamSource = stream;
                skin.EndInit();
                skin.Freeze();
            }

            return Crop(skin);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
