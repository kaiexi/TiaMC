using System.IO;
using System.IO.Compression;

namespace TiaMc.Core.Utils;

/// <summary>
/// Builds 64×64 Minecraft skins for offline accounts: solid, striped, gradient and
/// pseudo random (deterministic per player name) variants. Everything is written by
/// hand — a small PNG encoder — so the core keeps zero image dependencies.
///
/// Layout follows the modern skin format: head, body, arms and legs with a face
/// drawn on the front of the head.
/// </summary>
public static class SkinGenerator
{
    public enum Template
    {
        Solid,
        Stripes,
        Gradient,
        Checker,
        Named
    }

    /// <summary>Generates a skin and returns the PNG bytes.</summary>
    public static byte[] Build(Template template, string primary, string secondary, string name = "Player")
    {
        var (pr, pg, pb) = ParseColor(primary, (59, 127, 181));
        var (sr, sg, sb) = ParseColor(secondary, (28, 28, 34));
        var (skinR, skinG, skinB) = SkinTone(template == Template.Named ? name : "");

        var pixels = new byte[64 * 64 * 4];

        void Set(int x, int y, byte r, byte g, byte b, byte a = 255)
        {
            if (x < 0 || y < 0 || x >= 64 || y >= 64) return;
            var index = (y * 64 + x) * 4;
            pixels[index] = r;
            pixels[index + 1] = g;
            pixels[index + 2] = b;
            pixels[index + 3] = a;
        }

        void Fill(int x0, int y0, int w, int h, Func<int, int, (byte, byte, byte)> colour)
        {
            for (var y = y0; y < y0 + h; y++)
            {
                for (var x = x0; x < x0 + w; x++)
                {
                    var (r, g, b) = colour(x, y);
                    Set(x, y, r, g, b);
                }
            }
        }

        // Cloth colour of the outfit, chosen by the template.
        (byte, byte, byte) Cloth(int x, int y)
        {
            return template switch
            {
                Template.Solid => (pr, pg, pb),
                Template.Stripes => ((x / 2 + y / 2) % 2 == 0 ? pr : sr, (x / 2 + y / 2) % 2 == 0 ? pg : sg,
                    (x / 2 + y / 2) % 2 == 0 ? pb : sb),
                Template.Gradient => ((byte)((pr * (63 - y) + sr * y) / 63), (byte)((pg * (63 - y) + sg * y) / 63),
                    (byte)((pb * (63 - y) + sb * y) / 63)),
                Template.Checker => ((x / 4 + y / 4) % 2 == 0 ? pr : sr, (x / 4 + y / 4) % 2 == 0 ? pg : sg,
                    (x / 4 + y / 4) % 2 == 0 ? pb : sb),
                _ => (pr, pg, pb)
            };
        }

        // ---- head (0..8, 0..8 face; 8..16, 0..8 top; ...) ----
        Fill(0, 0, 32, 16, Cloth);
        // Face area on the front of the head: skin tone with eyes and a mouth.
        Fill(8, 8, 8, 8, (_, _) => (skinR, skinG, skinB));
        Set(10, 11, 40, 40, 60);
        Set(13, 11, 40, 40, 60);
        Set(11, 14, 120, 70, 70);
        Set(12, 14, 120, 70, 70);

        // ---- body (16..40, 16..32) ----
        Fill(16, 16, 24, 16, Cloth);
        // ---- arms (40..56, 16..32) ----
        Fill(40, 16, 16, 16, Cloth);
        // ---- legs (0..16, 16..32) ----
        Fill(0, 16, 16, 16, Cloth);
        // ---- second layer (hat / jacket) kept transparent ----

        return Encode(pixels, 64, 64);
    }

    /// <summary>Deterministic skin for a player name (same name → same look).</summary>
    public static byte[] BuildFromName(string name)
    {
        var hash = 17;
        foreach (var c in name) hash = unchecked(hash * 31 + c);
        var r = (byte)(80 + Math.Abs(hash % 120));
        var g = (byte)(80 + Math.Abs(hash / 7 % 120));
        var b = (byte)(80 + Math.Abs(hash / 13 % 120));
        var second = (byte)(r / 3);
        return Build(Template.Checker, $"#{r:X2}{g:X2}{b:X2}", $"#{second:X2}{(byte)(g / 3):X2}{(byte)(b / 4):X2}", name);
    }

    /// <summary>Writes a 64×64 head only preview (used for the avatar tiles).</summary>
    public static (byte R, byte G, byte B) ParseColor(string hex, (byte, byte, byte) fallback)
    {
        try
        {
            var text = hex.Trim().TrimStart('#');
            if (text.Length == 3)
            {
                text = $"{text[0]}{text[0]}{text[1]}{text[1]}{text[2]}{text[2]}";
            }

            if (text.Length >= 6 &&
                byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return (r, g, b);
            }
        }
        catch (Exception)
        {
            // fall through
        }

        return fallback;
    }

    private static (byte, byte, byte) SkinTone(string name)
    {
        if (name.Length == 0) return (232, 190, 160);

        var hash = 7;
        foreach (var c in name) hash = unchecked(hash * 17 + c);
        var tones = new (byte, byte, byte)[]
        {
            (245, 214, 184), (232, 190, 160), (214, 168, 130), (188, 138, 100), (150, 106, 74), (110, 76, 52)
        };
        return tones[Math.Abs(hash) % tones.Length];
    }

    // ------------------------------------------------------------ PNG encoder

    private static byte[] Encode(byte[] pixels, int width, int height)
    {
        var raw = new byte[height * (1 + width * 4)];
        var index = 0;
        for (var y = 0; y < height; y++)
        {
            raw[index++] = 0; // filter: none
            Buffer.BlockCopy(pixels, y * width * 4, raw, index, width * 4);
            index += width * 4;
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // RGBA
        output.Write(Chunk("IHDR", header));

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            output.Write(Chunk("IDAT", compressed.ToArray()));
        }

        output.Write(Chunk("IEND", []));
        return output.ToArray();
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var result = new byte[data.Length + 12];
        WriteBigEndian(result, 0, data.Length);
        result[4] = (byte)type[0];
        result[5] = (byte)type[1];
        result[6] = (byte)type[2];
        result[7] = (byte)type[3];
        Buffer.BlockCopy(data, 0, result, 8, data.Length);

        var crc = Crc32(result, 4, data.Length + 4);
        WriteBigEndian(result, data.Length + 8, unchecked((int)crc));
        return result;
    }

    private static uint Crc32(byte[] data, int offset, int count)
    {
        var table = CrcTable.Value;
        uint crc = 0xFFFFFFFF;
        for (var i = 0; i < count; i++)
        {
            crc = table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static readonly Lazy<uint[]> CrcTable = new(() =>
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    });
}
