using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TiaMc.App.Services;
using TiaMc.Core.Utils;

namespace TiaMc.App.Converters;

/// <summary>
/// 皮肤 → 头像（8×8 脸部，含帽子层）。
///
/// 输入可以是：
///   * 一个来源字符串，或**多个来源用 '|' 分隔**（按顺序尝试，第一个成功即用）；
///   * 本地皮肤文件路径；
///   * http(s) 皮肤地址（正版皮肤来自 textures.minecraft.net）。
///
/// 特点（为了"正版登录后头像一定显示得出来"）：
///   * 下载成功的皮肤会**落到磁盘缓存**（&lt;配置目录&gt;\cache\skins\），下次离线也能显示；
///   * 下载失败只缓存 60 秒就重试，避免一次网络抖动导致头像永久空白；
///   * 主域名失败会自动尝试镜像（由账户模型给出的 crafatar / mc-heads 地址）。
/// </summary>
public sealed class SkinHeadConverter : IValueConverter
{
    private static readonly Dictionary<string, (BitmapSource? Image, DateTime At)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string source || source.Length == 0) return null;

        var key = source;
        try
        {
            if (File.Exists(source)) key = $"{source}|{File.GetLastWriteTimeUtc(source).Ticks}";
        }
        catch (Exception)
        {
            // keep the plain source as key
        }

        if (Cache.TryGetValue(key, out var cached))
        {
            // 成功的结果一直用；失败的结果 60 秒后允许重试
            if (cached.Image is not null || DateTime.UtcNow - cached.At < FailureRetry) return cached.Image;
        }

        BitmapSource? head = null;
        foreach (var candidate in source.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(candidate))
                {
                    head = LoadLocal(candidate);
                }
                else
                {
                    // 不能在这里同步等网络：以前最长会卡住 UI 15 秒。
                    // 改成"先返回空、后台下载并写缓存"，下次刷新（切账户/刷新皮肤库）就会显示出来。
                    var url = candidate;
                    _ = Task.Run(() => LoadRemote(url));
                    head = null;
                }

                if (head is not null) break;
            }
            catch (Exception)
            {
                // 试下一个来源
            }
        }

        Cache[key] = (head, DateTime.UtcNow);
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

    /// <summary>Downloads a remote skin (with a disk cache) and crops the face.</summary>
    private static async Task<BitmapSource?> LoadRemote(string url)
    {
        try
        {
            var cacheFile = CacheFileFor(url);
            byte[] bytes;

            if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 0)
            {
                bytes = await File.ReadAllBytesAsync(cacheFile).ConfigureAwait(false);
            }
            else
            {
                bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                    await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 缓存写不进去也不影响本次显示
                }
            }

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

    /// <summary>&lt;配置目录&gt;\cache\skins\&lt;url 的 sha1&gt;.png</summary>
    private static string CacheFileFor(string url)
    {
        var hash = System.Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(AppPaths.SkinCacheDirectory, hash + ".png");
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

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
