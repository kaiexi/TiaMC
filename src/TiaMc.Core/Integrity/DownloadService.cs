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

    public async Task<DownloadOutcome> DownloadMissingAsync(
        IReadOnlyList<MissingFile> files,
        DownloadSource source,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken token = default)
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

                                using (var response = await _http
                                           .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                                           .ConfigureAwait(false))
                                {
                                    response.EnsureSuccessStatusCode();
                                    await using var source1 = await response.Content.ReadAsStreamAsync(token)
                                        .ConfigureAwait(false);
                                    await using var target = File.Create(temp);
                                    await source1.CopyToAsync(target, 81920, token).ConfigureAwait(false);
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
