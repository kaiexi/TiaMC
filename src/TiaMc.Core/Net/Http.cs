using System.Net;
using System.Net.Http.Headers;

namespace TiaMc.Core.Net;

/// <summary>
/// One shared HTTP stack for the whole launcher.
///
/// Creating an HttpClient per download (as the first version did) means a new TCP
/// connection and TLS handshake for every file, no DNS caching and thousands of
/// sockets in TIME_WAIT. A single client with a pooled connection handler keeps
/// the connections warm, which is the single biggest download speedup.
/// </summary>
public static class Http
{
    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            // Plenty of parallel connections to one host (mirrors are per host).
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("TiaMC/1.0 (Minecraft launcher)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    });

    /// <summary>The shared client; do not dispose it.</summary>
    public static HttpClient Client => Shared.Value;

    /// <summary>A short-timeout client for API calls that must fail fast.</summary>
    public static HttpClient ApiClient { get; } = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    /// <summary>Cheap connectivity probe used to pick a download source.</summary>
    public static async Task<int> MeasureLatencyAsync(string url, CancellationToken token = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await ApiClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            watch.Stop();
            return response.IsSuccessStatusCode ? (int)watch.ElapsedMilliseconds : int.MaxValue;
        }
        catch (Exception)
        {
            return int.MaxValue;
        }
    }
}
