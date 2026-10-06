using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMc.Web;

/// <summary>
/// A minimal HTTP/1.1 server (TcpListener based, like the Yggdrasil server in the
/// core: HttpListener would need an admin URL ACL even for 127.0.0.1).
/// Only what the web GUI needs: GET/POST, JSON bodies, files, no keep-alive tricks.
/// </summary>
internal sealed class WebServer(int preferredPort, string bindHost = "127.0.0.1")
{
    public sealed class Request
    {
        public string Method { get; init; } = "GET";
        public string Path { get; init; } = "/";
        public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; init; } = "";

        public string Field(string name)
        {
            if (Body.Length == 0) return "";
            try
            {
                using var document = JsonDocument.Parse(Body);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return "";
                return document.RootElement.TryGetProperty(name, out var value)
                    ? value.ToString()
                    : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        public bool Has(string name)
        {
            try
            {
                using var document = JsonDocument.Parse(Body);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                       document.RootElement.TryGetProperty(name, out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public byte[] BodyBase64()
        {
            var text = Field("data");
            if (text.Length == 0) return [];
            try
            {
                return Convert.FromBase64String(text);
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    public sealed record Response(byte[] Body, string ContentType, int Status = 200);

    private readonly Dictionary<string, Func<Request, Response>> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, Func<Request, Response> Handler)> _prefixes = [];
    private TcpListener? _listener;

    public int Port { get; private set; }

    public void Map(string method, string path, Func<Request, Response> handler) =>
        _exact[$"{method} {path}"] = handler;

    public void MapJson(string method, string path, Func<Request, JsonNode> handler) =>
        _exact[$"{method} {path}"] = request =>
            new Response(Encoding.UTF8.GetBytes(handler(request).ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            })), "application/json; charset=utf-8");

    public void MapPrefix(string method, string prefix, Func<Request, Response> handler) =>
        _prefixes.Add(($"{method} {prefix}", handler));

    public static Response File(string path, string contentType, int status = 200)
        => System.IO.File.Exists(path)
            ? new Response(System.IO.File.ReadAllBytes(path), contentType, status)
            : Text("not found", "text/plain; charset=utf-8", 404);

    public static Response Text(string text, string contentType, int status = 200)
        => new(Encoding.UTF8.GetBytes(text), contentType, status);

    public int Start()
    {
        foreach (var port in new[] { preferredPort, 0 })
        {
            try
            {
                var address = bindHost is "0.0.0.0" or "any" or "*"
                    ? IPAddress.Any
                    : IPAddress.TryParse(bindHost, out var parsed) ? parsed : IPAddress.Loopback;
                var listener = new TcpListener(address, port);
                listener.Start();
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoopAsync);
                return Port;
            }
            catch (SocketException)
            {
                // port in use: fall back to an ephemeral one
            }
        }

        throw new IOException("无法在 127.0.0.1 上监听端口");
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 服务器被正常关闭
                return;
            }
            catch (Exception e)
            {
                // 关键：接受连接失败绝不能结束整个循环，否则进程活着但页面"点不动"。
                App.Services.LogService.Error($"接受连接失败（继续监听）: {e.Message}", "Web");
                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var header = new StringBuilder();
                var buffer = new byte[8192];
                var headerEnd = -1;

                while (headerEnd < 0)
                {
                    var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read <= 0) return;
                    header.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    headerEnd = header.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (header.Length > 64 * 1024) return;
                }

                var raw = header.ToString();
                var lines = raw[..headerEnd].Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length < 2) return;

                var method = parts[0].ToUpperInvariant();
                var target = parts[1];
                var queryIndex = target.IndexOf('?');
                var path = queryIndex >= 0 ? target[..queryIndex] : target;
                var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (queryIndex >= 0)
                {
                    foreach (var pair in target[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = pair.IndexOf('=');
                        var key = eq > 0 ? pair[..eq] : pair;
                        var value = eq > 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                        query[key] = value;
                    }
                }

                var contentLength = 0;
                foreach (var line in lines.Skip(1))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line.Split(':')[1].Trim(), out var length))
                    {
                        contentLength = length;
                    }
                }

                var body = "";
                if (contentLength > 0)
                {
                    var already = raw.Length - headerEnd - 4;
                    var payload = new byte[contentLength];
                    var copied = Math.Min(already, contentLength);
                    if (copied > 0) Encoding.UTF8.GetBytes(raw[(headerEnd + 4)..]).CopyTo(payload, 0);

                    var offset = copied;
                    while (offset < contentLength)
                    {
                        var read = await stream.ReadAsync(payload.AsMemory(offset, contentLength - offset)).ConfigureAwait(false);
                        if (read <= 0) break;
                        offset += read;
                    }

                    body = Encoding.UTF8.GetString(payload, 0, offset);
                }

                var request = new Request { Method = method, Path = path, Query = query, Body = body };

                Response response;
                try
                {
                    response = Route(request);
                }
                catch (Exception routeError)
                {
                    // 处理函数抛异常时也要回一个明确响应，否则页面表现为"点不动"
                    App.Services.LogService.Error($"处理 {method} {path} 出错: {routeError.Message}", "Web");
                    var payload = Encoding.UTF8.GetBytes(
                        "{\"ok\":false,\"message\":\"" + routeError.Message.Replace("\"", "'").Replace("\\", "/") + "\"}");
                    response = new Response(payload, "application/json; charset=utf-8", 500);
                }

                var head = $"HTTP/1.1 {response.Status} {StatusText(response.Status)}\r\n" +
                           $"Content-Type: {response.ContentType}\r\n" +
                           $"Content-Length: {response.Body.Length}\r\n" +
                           "Cache-Control: no-store\r\n" +
                           "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.UTF8.GetBytes(head)).ConfigureAwait(false);
                await stream.WriteAsync(response.Body).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // a broken connection must not kill the server
            }
        }
    }

    private Response Route(Request request)
    {
        if (_exact.TryGetValue($"{request.Method} {request.Path}", out var handler)) return handler(request);

        foreach (var (key, prefixHandler) in _prefixes)
        {
            if (key == $"{request.Method} {request.Path}" ||
                (key.EndsWith(' ') && request.Path.StartsWith(key[..^1], StringComparison.OrdinalIgnoreCase)))
            {
                return prefixHandler(request);
            }
        }

        // 前缀路由：/flash/swf/xxx 之类
        if (request.Method == "GET" && request.Path.StartsWith("/flash/swf/", StringComparison.OrdinalIgnoreCase))
        {
            return _exact.TryGetValue("GET /flash/swf/", out var swf) ? swf(request) : Text("not found", "text/plain", 404);
        }

        if (request.Method == "GET" && request.Path.StartsWith("/flash/ruffle/", StringComparison.OrdinalIgnoreCase))
        {
            return _exact.TryGetValue("GET /flash/ruffle/", out var ruffle) ? ruffle(request) : Text("not found", "text/plain", 404);
        }

        return Text("404", "text/plain; charset=utf-8", 404);
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "OK"
    };
}
