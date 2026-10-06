using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TiaMc.Core.Yggdrasil;

/// <summary>
/// Minimal HTTP/1.1 transport used by the Yggdrasil server. It is written on top
/// of TcpListener on purpose: HttpListener needs a URL ACL (an administrator
/// netsh reservation) even for 127.0.0.1, which a launcher must not require.
/// </summary>
internal sealed class MiniHttpRequest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string RemoteAddress { get; init; } = "";
    public byte[] Body { get; init; } = [];

    public string BodyText => Encoding.UTF8.GetString(Body);
}

internal sealed class MiniHttpResponse(NetworkStream stream)
{
    public int Status { get; private set; } = 200;

    public async Task JsonAsync(int status, string json)
    {
        Status = status;
        var bytes = Encoding.UTF8.GetBytes(json);
        await WriteAsync(status, "application/json; charset=utf-8", bytes).ConfigureAwait(false);
    }

    public async Task BytesAsync(int status, string contentType, byte[] bytes)
    {
        Status = status;
        await WriteAsync(status, contentType, bytes).ConfigureAwait(false);
    }

    public async Task EmptyAsync(int status)
    {
        Status = status;
        await WriteAsync(status, null, []).ConfigureAwait(false);
    }

    private async Task WriteAsync(int status, string? contentType, byte[] body)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
        if (contentType is not null)
        {
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        }

        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Connection: close\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("\r\n");

        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        _ => "OK"
    };
}

internal static class MiniHttp
{
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>Reads one request from the socket; returns null when the peer closed it.</summary>
    public static async Task<MiniHttpRequest?> ReadAsync(NetworkStream stream, string remoteAddress,
        CancellationToken token)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read <= 0) return null;
            buffer.Write(chunk, 0, read);
            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
            if (buffer.Length > 64 * 1024) return null; // header too large
        }

        var raw = buffer.GetBuffer();
        var headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var target = parts[1];
        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (queryIndex >= 0)
        {
            foreach (var pair in target[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("Content-Length", out var value) && int.TryParse(value, out var length)
            ? Math.Clamp(length, 0, MaxBodyBytes)
            : 0;

        var bodyStart = headerEnd + 4;
        var alreadyRead = (int)buffer.Length - bodyStart;
        var body = new byte[contentLength];

        if (alreadyRead > 0)
        {
            Array.Copy(raw, bodyStart, body, 0, Math.Min(alreadyRead, contentLength));
        }

        var remaining = contentLength - Math.Min(alreadyRead, contentLength);
        var offset = Math.Min(alreadyRead, contentLength);
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, remaining), token).ConfigureAwait(false);
            if (read <= 0) break;
            offset += read;
            remaining -= read;
        }

        return new MiniHttpRequest
        {
            Method = parts[0].ToUpperInvariant(),
            Path = path.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/",
            Query = query,
            Headers = headers,
            RemoteAddress = remoteAddress,
            Body = body
        };
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == 13 && buffer[i - 2] == 10 && buffer[i - 1] == 13 && buffer[i] == 10)
            {
                return i - 3;
            }
        }

        return -1;
    }

    /// <summary>Writes a 64x64 PNG without any imaging dependency (single colour + band).</summary>
    public static byte[] BuildSolidSkin(byte r, byte g, byte b, int width = 64, int height = 64)
    {
        // Raw scanlines: one filter byte (0) + width * RGBA per row.
        var raw = new byte[height * (1 + width * 4)];
        var index = 0;
        for (var y = 0; y < height; y++)
        {
            raw[index++] = 0;
            for (var x = 0; x < width; x++)
            {
                var band = y is >= 20 and < 28;
                raw[index++] = band ? (byte)(r / 2) : r;
                raw[index++] = band ? (byte)(g / 2) : g;
                raw[index++] = band ? (byte)(b / 2) : b;
                raw[index++] = 255;
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // colour type: RGBA
        output.Write(Chunk("IHDR", ihdr));

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(compressed,
                       System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            output.Write(Chunk("IDAT", compressed.ToArray()));
        }

        output.Write(Chunk("IEND", []));
        return output.ToArray();
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var result = new byte[12 + data.Length];
        WriteBigEndian(result, 0, data.Length);
        Encoding.ASCII.GetBytes(type, result.AsSpan(4));
        data.CopyTo(result.AsSpan(8));
        WriteBigEndian(result, 8 + data.Length, (int)Crc32(result.AsSpan(4, 4 + data.Length)));
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
