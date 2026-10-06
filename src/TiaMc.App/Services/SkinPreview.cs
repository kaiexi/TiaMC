using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TiaMc.App.Services;

/// <summary>
/// Builds live preview images from skin bytes: the head tile and a body view
/// assembled from the head / body / arms / legs patches, so the user sees the result
/// while still choosing colours (no file needed) and can look at any side.
/// </summary>
public static class SkinPreview
{
    /// <summary>Which side of the skin to show.</summary>
    public enum View
    {
        Front,
        Back,
        Left,
        Right,
        Head
    }

    /// <summary>Face + hat of a skin (64×64 output).</summary>
    public static ImageSource? Head(byte[] png)
    {
        var skin = Decode(png);
        return skin is null ? null : Head(skin);
    }

    public static ImageSource? Head(BitmapSource skin)
    {
        if (skin.PixelWidth < 64 || skin.PixelHeight < 16) return null;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var destination = new Rect(0, 0, 64, 64);
            dc.DrawImage(new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8)), destination);
            if (skin.PixelHeight >= 64)
            {
                dc.DrawImage(new CroppedBitmap(skin, new Int32Rect(40, 8, 8, 8)), destination);
            }
        }

        return Render(visual, 64, 64);
    }

    /// <summary>
    /// Body view assembled from the texture patches (front / back / left / right),
    /// scaled so it is readable. "Head" renders the face tile instead.
    /// </summary>
    public static ImageSource? Body(byte[] png, View view = View.Front, int scale = 6)
    {
        var skin = Decode(png);
        if (skin is null) return null;
        if (view == View.Head) return Head(skin);

        var width = 16 * scale;
        var height = 32 * scale;
        var modern = skin.PixelHeight >= 64;
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            void Patch(int x, int y, int w, int h, int dx, int dy, int dw, int dh)
            {
                try
                {
                    var source = new CroppedBitmap(skin, new Int32Rect(x, y, w, h));
                    dc.DrawImage(source, new Rect(dx * scale, dy * scale, dw * scale, dh * scale));
                }
                catch (Exception)
                {
                    // 64x32 skins have no second layer / left limbs
                }
            }

            switch (view)
            {
                case View.Back:
                    Patch(24, 8, 8, 8, 4, 0, 8, 8);
                    if (modern) Patch(56, 8, 8, 8, 4, 0, 8, 8);
                    Patch(32, 20, 8, 12, 4, 8, 8, 12);
                    if (modern) Patch(32, 36, 8, 12, 4, 8, 8, 12);
                    Patch(52, 20, 4, 12, 0, 8, 4, 12);
                    if (modern) Patch(52, 36, 4, 12, 0, 8, 4, 12);
                    Patch(12, 20, 4, 12, 4, 20, 4, 12);
                    if (modern) Patch(12, 36, 4, 12, 4, 20, 4, 12);
                    break;

                case View.Left:
                    Patch(16, 8, 8, 8, 4, 0, 8, 8);
                    if (modern) Patch(48, 8, 8, 8, 4, 0, 8, 8);
                    Patch(28, 20, 4, 12, 4, 8, 8, 12);
                    if (modern) Patch(28, 36, 4, 12, 4, 8, 8, 12);
                    Patch(20, 52, 4, 12, 0, 8, 4, 12);
                    if (modern) Patch(4, 52, 4, 12, 0, 8, 4, 12);
                    Patch(20, 52, 4, 12, 4, 20, 4, 12);
                    break;

                case View.Right:
                    Patch(0, 8, 8, 8, 4, 0, 8, 8);
                    if (modern) Patch(32, 8, 8, 8, 4, 0, 8, 8);
                    Patch(16, 20, 4, 12, 4, 8, 8, 12);
                    if (modern) Patch(16, 36, 4, 12, 4, 8, 8, 12);
                    Patch(44, 20, 4, 12, 0, 8, 4, 12);
                    if (modern) Patch(44, 36, 4, 12, 0, 8, 4, 12);
                    Patch(4, 20, 4, 12, 4, 20, 4, 12);
                    if (modern) Patch(4, 36, 4, 12, 4, 20, 4, 12);
                    break;

                default: // Front
                    Patch(8, 8, 8, 8, 4, 0, 8, 8);
                    if (modern) Patch(40, 8, 8, 8, 4, 0, 8, 8);
                    Patch(20, 20, 8, 12, 4, 8, 8, 12);
                    if (modern) Patch(20, 36, 8, 12, 4, 8, 8, 12);
                    Patch(44, 20, 4, 12, 0, 8, 4, 12);
                    if (modern) Patch(44, 36, 4, 12, 0, 8, 4, 12);
                    Patch(4, 20, 4, 12, 4, 20, 4, 12);
                    if (modern) Patch(4, 36, 4, 12, 4, 20, 4, 12);
                    break;
            }

            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)), null,
                new Rect(0, height - 1, width, 1));
        }

        return Render(visual, width, height);
    }

    private static BitmapSource? Decode(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            using (var stream = new MemoryStream(png))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.StreamSource = stream;
                image.EndInit();
            }

            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource Render(DrawingVisual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
