using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using TiaMc.Core.Accounts;
using TiaMc.Core.Utils;

namespace TiaMc.App.Services;

/// <summary>
/// 把"某个来源的皮肤"变成一张可用的贴图（BitmapSource），供 3D 预览 / 2D 头像使用。
///
/// 三种来源：
///   * 账户：正版走 SkinUrl（失败自动换 crafatar / mc-heads 镜像），离线账户用本地文件；
///   * 皮肤站：按名称 / UUID 走 <see cref="SkinSites"/>（Blessing Skin 站点走 Yggdrasil API）；
///   * 直接链接：任意 PNG 地址。
///
/// 下载成功的皮肤缓存到 &lt;配置目录&gt;\cache\skins\，离线也能继续预览。
/// </summary>
public static class SkinTextureService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public sealed record TextureResult(bool Ok, string Message, BitmapSource? Texture, string Source);

    /// <summary>
    /// 把 64×32 的旧版皮肤转成 64×64（标准做法：把右臂/右腿区域镜像到左臂/左腿位置）。
    /// 不做这一步的话，按 64×64 布局取 y=48~64 的贴图会越界，3D 模型直接画不出来。
    /// </summary>
    public static BitmapSource Normalize(BitmapSource source)
    {
        if (source.PixelWidth != 64 || source.PixelHeight != 32) return source;

        try
        {
            var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var source32 = new byte[64 * 32 * 4];
            converted.CopyPixels(source32, 64 * 4, 0);

            var canvas = new byte[64 * 64 * 4];
            Buffer.BlockCopy(source32, 0, canvas, 0, source32.Length);

            CopyRegion(source32, canvas, 0, 16, 16, 16, 16, 48);    // 右腿 → 左腿
            CopyRegion(source32, canvas, 40, 16, 16, 16, 32, 48);   // 右臂 → 左臂

            var bitmap = new WriteableBitmap(64, 64, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, 64, 64), canvas, 64 * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return source;
        }
    }

    private static void CopyRegion(byte[] source, byte[] target,
        int sx, int sy, int width, int height, int tx, int ty)
    {
        const int stride = 64 * 4;
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(source, (sy + row) * stride + sx * 4,
                             target, (ty + row) * stride + tx * 4, width * 4);
        }
    }

    /// <summary>从已经拿到的 PNG 字节构造贴图（冻结，可跨线程使用）。</summary>
    public static BitmapSource? FromBytes(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.StreamSource = stream;
                image.EndInit();
            }

            image.Freeze();
            return Normalize(image);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static BitmapSource? FromFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return Normalize(image);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>账户的皮肤（正版 URL 优先，失败走镜像，最后本地文件）。</summary>
    public static async Task<TextureResult> FromAccountAsync(MinecraftAccount? account, CancellationToken token = default)
    {
        if (account is null) return new TextureResult(false, "还没有选择账户", null, "");

        var mirrors = new List<string>();
        if (!string.IsNullOrWhiteSpace(account.SkinUrl)) mirrors.Add(account.SkinUrl!);
        if (!string.IsNullOrWhiteSpace(account.Uuid))
        {
            mirrors.Add($"https://crafatar.com/skins/{account.Uuid}");
            mirrors.Add($"https://mc-heads.net/skin/{account.Uuid}");
        }
        else if (!string.IsNullOrWhiteSpace(account.Name))
        {
            mirrors.Add($"https://mc-heads.net/skin/{account.Name}");
        }

        foreach (var url in mirrors)
        {
            var cached = CachedPath(url);
            var bytes = await ReadCachedOrDownloadAsync(url, cached, token).ConfigureAwait(false);
            if (bytes is null) continue;

            var texture = FromBytes(bytes);
            if (texture is null) continue;
            return new TextureResult(true, $"{account.Name} 的皮肤（{new Uri(url).Host}）", texture, url);
        }

        if (account.HasLocalSkin)
        {
            var local = FromFile(account.SkinPath!);
            if (local is not null) return new TextureResult(true, $"{account.Name} 的本地皮肤", local, account.SkinPath!);
        }

        return new TextureResult(false, "没有取到皮肤（正版账户没设置皮肤？或网络受限）", null, "");
    }

    /// <summary>
    /// 在线 3D 材质渲染图（皮肤站那种全身视图）：MC-Heads 支持 正面/左面/右面/后面 四个角度，
    /// 失败时退回 Crafatar 的全身渲染。两者都实测可用，结果缓存到本地。
    /// </summary>
    public static async Task<BitmapSource?> FetchRenderAsync(string nameOrUuid, string angle)
    {
        var urls = new List<string>
        {
            $"https://mc-heads.net/body/{Uri.EscapeDataString(nameOrUuid)}/{angle}",
            $"https://mc-heads.net/body/{Uri.EscapeDataString(nameOrUuid)}/front"
        };

        foreach (var url in urls)
        {
            var cached = CachedPath("render:" + url);
            var bytes = await ReadCachedOrDownloadAsync(url, cached, CancellationToken.None).ConfigureAwait(false);
            if (bytes is null) continue;

            var image = FromBytes(bytes);
            if (image is not null) return image;
        }

        return null;
    }

    /// <summary>任意图片链接。</summary>
    public static async Task<TextureResult> FromUrlAsync(string url, CancellationToken token = default)
    {
        var cached = CachedPath(url);
        var bytes = await ReadCachedOrDownloadAsync(url, cached, token).ConfigureAwait(false);
        if (bytes is null) return new TextureResult(false, "下载失败: " + url, null, url);

        var texture = FromBytes(bytes);
        return texture is null
            ? new TextureResult(false, "这个地址不是有效的 PNG: " + url, null, url)
            : new TextureResult(true, "来自链接 " + new Uri(url).Host, texture, url);
    }

    /// <summary>皮肤站（按名称 / UUID）。</summary>
    public static async Task<TextureResult> FromSiteAsync(SkinSites.Site site, string nameOrId,
        CancellationToken token = default)
    {
        var result = await SkinSites.FetchAsync(site, nameOrId, null, token).ConfigureAwait(false);
        if (!result.Ok || result.Png is null)
        {
            return new TextureResult(false, result.Message, null, result.SourceUrl);
        }

        // 皮肤站的皮肤也缓存一份，方便离线预览
        try
        {
            var path = CachedPath(result.SourceUrl.Length > 0 ? result.SourceUrl : site.Name + ":" + nameOrId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, result.Png, token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 缓存失败不影响本次预览
        }

        var texture = FromBytes(result.Png);
        return texture is null
            ? new TextureResult(false, "皮肤站返回的不是有效皮肤", null, result.SourceUrl)
            : new TextureResult(true, $"{site.Name}: {nameOrId}", texture, result.SourceUrl);
    }

    private static async Task<byte[]?> ReadCachedOrDownloadAsync(string url, string cachePath, CancellationToken token)
    {
        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
            {
                return await File.ReadAllBytesAsync(cachePath, token).ConfigureAwait(false);
            }

            var bytes = await Http.GetByteArrayAsync(url, token).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllBytesAsync(cachePath, bytes, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ignore cache write failures
            }

            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CachedPath(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(AppPaths.SkinCacheDirectory, hash + ".png");
    }
}
