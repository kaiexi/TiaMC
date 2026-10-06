using System.IO.Compression;

namespace TiaMc.Core.Skins;

/// <summary>
/// 皮肤模型识别（slim / classic）与旧贴图规范化。
///
/// 学 Axolotl 的 <c>determineModelType</c>：不看文件名、也不信用户选了什么，**直接读像素**——
/// slim（Alex）的手臂只有 3 像素宽，手臂区域最外侧那一列是**透明**的；classic（Steve）是 4 像素宽，整块不透明。
///
/// 这里不引任何图像库（TiaMc.Core 保持零 NuGet 依赖），自带一个最小 PNG 解码器：
/// 支持 8 位灰度/真彩/调色板/带 alpha 的常见皮肤 PNG，非隔行。
/// </summary>
public static class SkinModelDetector
{
    public const string Classic = "classic";
    public const string Slim = "slim";

    /// <summary>解码结果：宽、高、RGBA 像素。</summary>
    public sealed record SkinPixels(int Width, int Height, byte[] Rgba)
    {
        public byte Alpha(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
            return Rgba[(y * Width + x) * 4 + 3];
        }

        public bool HasTransparentOuterArm()
        {
            // 右臂顶部 4x4：x 44..47, y 16..19；slim 时最外一列（x=47）透明
            // 左臂顶部 4x4：x 36..39, y 48..51；slim 时最外一列（x=36）透明
            var hits = 0;
            var total = 0;

            for (var y = 16; y < 20; y++)
            {
                if (Alpha(44, y) > 0) { total++; if (Alpha(47, y) == 0) hits++; }
            }

            for (var y = 48; y < 52; y++)
            {
                if (Alpha(39, y) > 0) { total++; if (Alpha(36, y) == 0) hits++; }
            }

            return total > 0 && hits * 2 > total;
        }
    }

    /// <summary>从 PNG 字节识别模型类型（识别不出返回 classic）。</summary>
    public static string Detect(byte[] png)
    {
        var decoded = TryDecode(png);
        if (decoded is null || decoded.Width < 64 || decoded.Height < 32) return Classic;
        return decoded.HasTransparentOuterArm() ? Slim : Classic;
    }

    /// <summary>解码 PNG；失败返回 null。</summary>
    public static SkinPixels? TryDecode(byte[] png)
    {
        try
        {
            if (png.Length < 8 || png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') return null;

            var offset = 8;
            int width = 0, height = 0, bitDepth = 8, colorType = 6, interlace = 0;
            var idat = new MemoryStream();
            byte[]? palette = null;
            byte[]? transparency = null;

            while (offset + 8 <= png.Length)
            {
                var length = ReadInt32(png, offset);
                var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
                var dataStart = offset + 8;
                if (length < 0 || dataStart + length > png.Length) break;

                switch (type)
                {
                    case "IHDR":
                        width = ReadInt32(png, dataStart);
                        height = ReadInt32(png, dataStart + 4);
                        bitDepth = png[dataStart + 8];
                        colorType = png[dataStart + 9];
                        interlace = png[dataStart + 12];
                        break;

                    case "PLTE":
                        palette = new byte[length];
                        Buffer.BlockCopy(png, dataStart, palette, 0, length);
                        break;

                    case "tRNS":
                        transparency = new byte[length];
                        Buffer.BlockCopy(png, dataStart, transparency, 0, length);
                        break;

                    case "IDAT":
                        idat.Write(png, dataStart, length);
                        break;
                }

                if (type == "IEND") break;
                offset = dataStart + length + 4;   // 跳过 CRC
            }

            if (width <= 0 || height <= 0 || interlace != 0) return null;
            if (bitDepth != 8) return null;       // 皮肤都是 8 位

            idat.Position = 0;
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
            using var raw = new MemoryStream();
            inflate.CopyTo(raw);
            var rawBytes = raw.ToArray();

            var channels = colorType switch
            {
                0 => 1,   // 灰度
                2 => 3,   // 真彩
                3 => 1,   // 调色板
                4 => 2,   // 灰度 + alpha
                6 => 4,   // 真彩 + alpha
                _ => 0
            };
            if (channels == 0) return null;

            var stride = width * channels;
            var expected = (stride + 1) * height;
            if (rawBytes.Length < expected) return null;

            var image = Unfilter(rawBytes, width, height, channels, stride);
            var rgba = ToRgba(image, width, height, colorType, channels, palette, transparency);
            return new SkinPixels(width, height, rgba);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>旧贴图规范化：把 64×32 补成 64×64（右臂/右腿镜像到左下）。返回 RGBA 与尺寸。</summary>
    public static (int Width, int Height, byte[] Rgba) NormalizeToRgba(SkinPixels source)
    {
        if (source.Width != 64 || source.Height != 32) return (source.Width, source.Height, source.Rgba);

        var canvas = new byte[64 * 64 * 4];
        Buffer.BlockCopy(source.Rgba, 0, canvas, 0, source.Rgba.Length);

        CopyRegion(source.Rgba, canvas, 0, 16, 16, 16, 16, 48);    // 右腿 → 左腿
        CopyRegion(source.Rgba, canvas, 40, 16, 16, 16, 32, 48);   // 右臂 → 左臂

        return (64, 64, canvas);
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

    /// <summary>PNG 逐行反过滤（None/Sub/Up/Average/Paeth）。</summary>
    private static byte[] Unfilter(byte[] raw, int width, int height, int channels, int stride)
    {
        var output = new byte[stride * height];
        var previous = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var lineStart = y * (stride + 1) + 1;
            var outStart = y * stride;
            var current = new byte[stride];

            for (var x = 0; x < stride; x++)
            {
                var value = raw[lineStart + x];
                var left = x >= channels ? current[x - channels] : (byte)0;
                var up = previous[x];
                var upLeft = x >= channels ? previous[x - channels] : (byte)0;

                current[x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + (left + up) / 2),
                    4 => (byte)(value + Paeth(left, up, upLeft)),
                    _ => value
                };
            }

            Buffer.BlockCopy(current, 0, output, outStart, stride);
            previous = current;
        }

        return output;
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] ToRgba(byte[] image, int width, int height, int colorType, int channels,
        byte[]? palette, byte[]? transparency)
    {
        var rgba = new byte[width * height * 4];

        for (var i = 0; i < width * height; i++)
        {
            var source = i * channels;
            byte r, g, b, a = 255;

            switch (colorType)
            {
                case 0:
                    r = g = b = image[source];
                    break;
                case 2:
                    r = image[source];
                    g = image[source + 1];
                    b = image[source + 2];
                    break;
                case 3:
                    var index = image[source];
                    if (palette is null || index * 3 + 2 >= palette.Length) { r = g = b = 0; }
                    else { r = palette[index * 3]; g = palette[index * 3 + 1]; b = palette[index * 3 + 2]; }
                    if (transparency is not null && index < transparency.Length) a = transparency[index];
                    break;
                case 4:
                    r = g = b = image[source];
                    a = image[source + 1];
                    break;
                default:
                    r = image[source];
                    g = image[source + 1];
                    b = image[source + 2];
                    a = image[source + 3];
                    break;
            }

            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = a;
        }

        return rgba;
    }

    private static int ReadInt32(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
}
