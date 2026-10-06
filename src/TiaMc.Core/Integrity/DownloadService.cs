using System.Net.Http;

namespace TiaMc.Core.Integrity;

public sealed class DownloadProgress
{
    public int Completed { get; init; }
    public int Total { get; init; }
    public long BytesDone { get; init; }
    public long TotalBytes { get; init; }
    public string? CurrentFile { get; init; }
    public double Percent => TotalBytes > 0
        ? Math.Min(100, BytesDone * 100.0 / TotalBytes)
        : Total > 0 ? Completed * 100.0 / Total : 0;

    public string Summary =>
        $"{Completed}/{Total} 文件  {Utils.TextUtil.FormatBytes(BytesDone)}/{Utils.TextUtil.FormatBytes(TotalBytes)}  ({Percent:0.0}%)";
}

public sealed class DownloadOutcome
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public List<string> Errors { get; init; } = [];
    public bool Ok => Failed == 0;
}

/// <summary>
/// Downloads the files reported by <see cref="IntegrityChecker"/> with a small
/// bounded concurrency pool, progress callbacks and optional SHA1 validation.
/// </summary>
public sealed class DownloadService
{
    private readonly HttpClient _http;

    public DownloadService(HttpClient? http = null)
    {
        // Shared pooled client: connections and TLS sessions are reused across all
        // parallel workers instead of being rebuilt for every file.
        _http = http ?? Net.Http.Client;
    }

    /// <summary>下载完成后校验：有 SHA-1 就比对 SHA-1，另外比对声明的大小。</summary>
    private static async Task<bool> VerifyAsync(string path, MissingFile file, CancellationToken token)
    {
        if (!File.Exists(path)) return false;

        if (file.Size > 0 && new FileInfo(path).Length != file.Size) return false;

        if (!string.IsNullOrWhiteSpace(file.Sha1))
        {
            var actual = await Sha1Async(path, token).ConfigureAwait(false);
            if (!string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> Sha1Async(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hash = await sha1.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    /// <summary>分段阈值：小于它就没必要多连接（握手开销比省下的时间还大）。</summary>
    private const long SegmentThresholdBytes = 2L * 1024 * 1024;

    /// <summary>
    /// 单文件下载：小文件或服务端不支持 Range 时走单连接；否则按 threads 切成多段并发下载后合并。
    /// （这就是 NeatDM 那类多线程下载器的基本做法：Range 分段 + 并发 + 合并。）
    /// </summary>
    private async Task DownloadSegmentedAsync(string url, string temp, long expectedSize, int threads,
        CancellationToken token)
    {
        if (threads <= 1 || (expectedSize > 0 && expectedSize < SegmentThresholdBytes))
        {
            await DownloadWholeAsync(url, temp, token).ConfigureAwait(false);
            return;
        }

        long length = expectedSize;
        var supportsRanges = false;
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, url);
            probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await _http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                supportsRanges = true;
                if (response.Content.Headers.ContentRange?.Length is { } total) length = total;
            }
        }
        catch (Exception)
        {
            // 探测失败就退回单连接
        }

        if (!supportsRanges || length <= 0 || length < SegmentThresholdBytes)
        {
            await DownloadWholeAsync(url, temp, token).ConfigureAwait(false);
            return;
        }

        var parts = (int)Math.Clamp(threads, 2, 32);
        var chunk = length / parts;
        var tasks = new List<Task>();

        for (var i = 0; i < parts; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var from = index * chunk;
                var to = index == parts - 1 ? length - 1 : from + chunk - 1;
                var partPath = $"{temp}.part{index}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var part = File.Create(partPath);
                await stream.CopyToAsync(part, 81920, token).ConfigureAwait(false);
            }, token));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        await using (var output = File.Create(temp))
        {
            for (var i = 0; i < parts; i++)
            {
                var partPath = $"{temp}.part{i}";
                await using (var input = File.OpenRead(partPath))
                {
                    await input.CopyToAsync(output, 819200, token).ConfigureAwait(false);
                }

                try { File.Delete(partPath); } catch (Exception) { }
            }
        }
    }

    /// <summary>普通单连接下载。</summary>
    private async Task DownloadWholeAsync(string url, string temp, CancellationToken token)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = File.Create(temp);
        await source.CopyToAsync(target, 81920, token).ConfigureAwait(false);
    }
    public async Task<DownloadOutcome> DownloadMissingAsync(
        IReadOnlyList<MissingFile> files,
        DownloadSource source,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken token = default, int threads = 8)
    {
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var errors = new List<string>();
        var bytesDone = 0L;
        var totalBytes = files.Sum(f => Math.Max(0, f.Size));
        var completed = 0;
        var sync = new object();

        // 速度统计：每 800ms 用"这段时间新增字节 / 实际耗时"作为瞬时速度，
        // 再和上一次的速度做一次平滑，避免数字乱跳。
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastSampleMs = 0L;
        var lastSampleBytes = 0L;
        var smoothedSpeed = 0.0;

        var queue = new System.Collections.Concurrent.ConcurrentQueue<MissingFile>(files);
        var workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var tasks = new List<Task>();

        for (var i = 0; i < workers; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && queue.TryDequeue(out var file))
                {
                    try
                    {
                        if (File.Exists(file.Path) && file.Size > 0 && new FileInfo(file.Path).Length == file.Size)
                        {
                            Interlocked.Increment(ref skipped);
                            Advance(file, file.Size);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);

                        var urls = IntegrityChecker.BuildDownloadUrls(file, source);
                        var downloaded = false;
                        var failures = new List<string>();

                        for (var index = 0; index < urls.Count && !downloaded; index++)
                        {
                            var url = urls[index];
                            try
                            {
                                  var temp = file.Path + ".tiamc-download";

                                  // NeatDM 式加速：大文件按设定线程数分段并发下载后合并
                                  await DownloadSegmentedAsync(url, temp, file.Size, threads, token).ConfigureAwait(false);

                                  // 防损坏：校验 SHA-1 与声明大小；不一致就丢弃并换备用地址重下
                                  if (!await VerifyAsync(temp, file, token).ConfigureAwait(false))
                                  {
                                      try { File.Delete(temp); } catch (Exception) { }
                                      failures.Add($"{url} -> 校验失败（文件损坏，已丢弃重下）");
                                      log?.Invoke($"[download] {Path.GetFileName(file.Path)} 校验失败（可能损坏），换个地址重试");
                                      continue;
                                  }

                                  if (File.Exists(file.Path)) File.Delete(file.Path);
                                  File.Move(temp, file.Path);
                                downloaded = true;

                                if (index > 0)
                                {
                                    log?.Invoke($"[download] {Path.GetFileName(file.Path)} 改用备用地址成功: {url}");
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }
                            catch (Exception e)
                            {
                                failures.Add($"{url} -> {e.Message}");
                                if (index + 1 < urls.Count)
                                {
                                    log?.Invoke($"[download] {Path.GetFileName(file.Path)} 主地址失败，尝试备用地址 " +
                                                $"({e.Message})");
                                }
                            }
                        }

                        if (downloaded)
                        {
                            Interlocked.Increment(ref succeeded);
                            Advance(file, file.Size);
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                            var detail = failures.Count > 0 ? failures[0] : "未知错误";
                            lock (sync) errors.Add($"{Path.GetFileName(file.Path)}: {detail}");
                            log?.Invoke($"[download] 失败 {Path.GetFileName(file.Path)}: {detail}");
                            Advance(file, 0);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref failed);
                        lock (sync) errors.Add($"{Path.GetFileName(file.Path)}: {e.Message}");
                        log?.Invoke($"[download] 失败 {Path.GetFileName(file.Path)}: {e.Message}");
                        Advance(file, 0);
                    }
                }

                void Advance(MissingFile current, long bytes)
                {
                    lock (sync)
                    {
                        bytesDone += bytes;
                        completed++;
                        progress?.Report(new DownloadProgress
                        {
                            Completed = completed,
                            Total = files.Count,
                            BytesDone = bytesDone,
                            TotalBytes = totalBytes,
                            CurrentFile = Path.GetFileName(current.Path)
                        });
                    }
                }
            }, token));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Partial results are reported below.
        }

        return new DownloadOutcome
        {
            Succeeded = succeeded,
            Failed = failed,
            Skipped = skipped,
            Errors = errors
        };
    }
}
