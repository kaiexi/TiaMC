using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMc.Core.Net;
using TiaMc.Core.Utils;

namespace TiaMc.Core.Java;

/// <summary>
/// 官方（Mojang）Java 运行时安装器。
///
/// 参考 Axolotl 的做法：**优先装"官方给这个版本配的那套运行时"**，而不是随便找一个同主版本的 JRE。
/// 版本 JSON 里的 `javaVersion.component`（如 java-runtime-gamma / java-runtime-delta）就是 Mojang
/// 自己在用的运行时组件名，`launchermeta` 会给出该平台的全部文件、SHA-1 与可执行标记，
/// 我们逐个下载并校验，装到 &lt;MC&gt;\runtime\&lt;component&gt; 下。
///
/// 好处：
///   * 版本要求与运行时严格对应（不会再出现 Java 17 跑 Java 21 版本）；
///   * 只下该平台需要的文件（Windows x64 约 100–200 MB，且可断点式逐个校验）；
///   * 与官方启动器行为一致，模组/加载器兼容性最好。
/// </summary>
public static class MojangJavaRuntime
{
    /// <summary>平台运行时清单（按平台区分；这是 launchermeta 的固定入口）。</summary>
    private const string ManifestUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    public sealed record InstallResult(bool Ok, string Message, string JavaPath = "", long Bytes = 0);

    /// <summary>把主版本号映射到官方组件名（版本 JSON 没给 component 时用）。</summary>
    public static string ComponentFor(int major) => major switch
    {
        <= 8 => "jre-legacy",
        9 or 10 or 11 or 12 or 13 or 14 or 15 => "java-runtime-alpha",
        16 => "java-runtime-alpha",
        17 => "java-runtime-gamma",
        18 or 19 or 20 => "java-runtime-gamma",
        21 => "java-runtime-delta",
        _ => "java-runtime-delta"
    };

    /// <summary>当前平台的清单键。</summary>
    public static string PlatformKey()
    {
        var arch = Environment.Is64BitProcess ? "x64" : "x86";
        return $"windows-{arch}";
    }

    /// <summary>
    /// 安装官方运行时。component 为空时按 major 推断。
    /// </summary>
    public static async Task<InstallResult> InstallAsync(string? component, int major, string runtimeRoot,
        Action<string>? log = null, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var name = string.IsNullOrWhiteSpace(component) ? ComponentFor(major) : component!.Trim();
        var target = Path.Combine(runtimeRoot, name);
        var javaExe = Path.Combine(target, "bin", "java.exe");

        if (File.Exists(javaExe))
        {
            return new InstallResult(true, $"{name} 已存在", javaExe);
        }

        try
        {
            log?.Invoke($"[java] 获取官方运行时清单（{PlatformKey()}）…");
            var json = await Http.Client.GetStringAsync(ManifestUrl, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty(PlatformKey(), out var platform))
            {
                return new InstallResult(false, $"官方清单里没有 {PlatformKey()}");
            }

            if (!platform.TryGetProperty(name, out var entries) || entries.GetArrayLength() == 0)
            {
                return new InstallResult(false, $"官方清单里没有组件 {name}");
            }

            var entry = entries[0];
            var files = entry.GetProperty("files");
            var total = files.EnumerateObject().Count();
            log?.Invoke($"[java] 官方运行时 {name}：{total} 个文件");

            Directory.CreateDirectory(target);

            var done = 0;
            long bytes = 0;
            foreach (var file in files.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                var relative = file.Name.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.Combine(target, relative);
                var node = file.Value;

                if (node.TryGetProperty("type", out var type) && type.GetString() == "directory")
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                if (!node.TryGetProperty("downloads", out var downloads) ||
                    !downloads.TryGetProperty("raw", out var raw))
                {
                    continue;
                }

                var url = raw.GetProperty("url").GetString();
                var sha1 = raw.TryGetProperty("sha1", out var sha1Node) ? sha1Node.GetString() : null;
                var size = raw.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;
                if (string.IsNullOrEmpty(url)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var needDownload = true;
                if (File.Exists(destination) && new FileInfo(destination).Length == size && size > 0)
                {
                    needDownload = sha1 is null || string.Equals(Sha1OfFile(destination), sha1, StringComparison.OrdinalIgnoreCase) == false;
                }

                if (needDownload)
                {
                    var data = await Http.Client.GetByteArrayAsync(url, token).ConfigureAwait(false);
                    if (sha1 is not null && !string.Equals(Sha1OfBytes(data), sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        return new InstallResult(false, $"{relative} 校验失败（SHA-1 不匹配）");
                    }

                    await File.WriteAllBytesAsync(destination, data, token).ConfigureAwait(false);
                    bytes += data.Length;
                }

                done++;
                if (total > 0) progress?.Report((double)done / total);
            }

            if (!File.Exists(javaExe))
            {
                return new InstallResult(false, $"{name} 下载完成但没有找到 bin\\java.exe");
            }

            log?.Invoke($"[java] 官方运行时 {name} 已就绪：{javaExe}（本次下载 {TextUtil.FormatBytes(bytes)}）");
            return new InstallResult(true, $"{name} 已就绪", javaExe, bytes);
        }
        catch (Exception e)
        {
            return new InstallResult(false, "官方运行时安装失败: " + e.Message);
        }
    }

    private static string Sha1OfBytes(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private static string Sha1OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }
}
