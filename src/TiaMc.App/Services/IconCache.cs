using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TiaMc.App.Services;

/// <summary>
/// Tiny image cache for catalogue icons. Modrinth returns an icon URL for every
/// project, and rebuilding the bitmap for every row would make scrolling crawl,
/// so decoded (and frozen) images are kept in memory and downloads are limited
/// to a few at a time.
/// </summary>
public static class IconCache
{
    private const int MaxEntries = 240;
    private const int IconPixelWidth = 96;

    /// <summary>Icon cache files larger than this get re-encoded at 96 px.</summary>
    private const long ShrinkThresholdBytes = 24 * 1024;

    /// <summary>Total budget for the extracted mod icon folder.</summary>
    public const long DiskBudgetBytes = 24L * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(6, 6);
    private static readonly ConcurrentQueue<string> Order = new();

    /// <summary>Already decoded image, or null when it still has to be fetched.</summary>
    public static ImageSource? TryGet(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Cache.TryGetValue(url, out var image) ? image : null;

    /// <summary>Loads an image in the background, then reports it on the UI thread.</summary>
    public static void Load(string? url, Action<ImageSource> onLoaded)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (Cache.TryGetValue(url, out var cached))
        {
            onLoaded(cached);
            return;
        }

        if (!Pending.TryAdd(url, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                byte[]? bytes;
                try
                {
                    bytes = await FetchAsync(url).ConfigureAwait(false);
                }
                finally
                {
                    Gate.Release();
                }

                if (bytes is null || bytes.Length == 0) return;

                var image = Decode(bytes);
                if (image is null) return;

                Cache[url] = image;
                Order.Enqueue(url);
                while (Order.Count > MaxEntries && Order.TryDequeue(out var oldest)) Cache.TryRemove(oldest, out _);

                Ui.Post(() => onLoaded(image));
            }
            catch (Exception)
            {
                // a missing icon is not an error
            }
            finally
            {
                Pending.TryRemove(url, out _);
            }
        });
    }

    private static async Task<byte[]?> FetchAsync(string url)
    {
        try
        {
            using var response = await TiaMc.Core.Net.Http.Client
                .GetAsync(url, HttpCompletionOption.ResponseContentRead)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }


    // ------------------------------------------------------------- local files

    /// <summary>
    /// Decodes a local image (an icon extracted from a mod jar). Local files are
    /// small and read from disk, so this stays synchronous — the result is cached
    /// and the list can scroll without touching the disk again.
    /// </summary>
    public static ImageSource? GetFileImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        try
        {
            var bytes = File.ReadAllBytes(path);
            var image = Decode(bytes);
            if (image is null) return null;

            // Disk protection: a mod logo can be hundreds of KB. The decoded card is
            // only 96 px wide, so the cached file is re-encoded once and replaced by
            // a few KB PNG (or deleted when the re-encode fails).
            if (bytes.Length > ShrinkThresholdBytes) ShrinkFile(path, bytes);

            Cache[path] = image;
            Order.Enqueue(path);
            while (Order.Count > MaxEntries && Order.TryDequeue(out var oldest)) Cache.TryRemove(oldest, out _);
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // Icons are shown at 52px; decoding larger wastes memory on big lists.
            bitmap.DecodePixelWidth = IconPixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
    /// <summary>Re-encodes an oversized cached icon at 96 px, in the background.</summary>
    private static void ShrinkFile(string path, byte[] original)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(original);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = IconPixelWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                var temporary = path + ".shrink";
                using (var output = File.Create(temporary)) encoder.Save(output);

                var size = new FileInfo(temporary).Length;
                if (size > 0 && size < original.Length)
                {
                    // Overwrite in place: the metadata cache stores this exact path, and
                    // Windows/WPF detect the real format from the bytes, not the name.
                    File.Copy(temporary, path, overwrite: true);
                    File.Delete(temporary);
                }
                else
                {
                    File.Delete(temporary);
                }

                TiaMc.Core.Utils.AppPaths.EnforceIconBudget();
            }
            catch (Exception)
            {
                // shrinking is an optimisation, never an error
            }
        });
    }

    /// <summary>Frees the icon cache down to its budget (call at startup).</summary>
    public static (int Deleted, long FreedBytes, long TotalBytes) EnforceDiskBudget() =>
        TiaMc.Core.Utils.AppPaths.EnforceIconBudget();
}
