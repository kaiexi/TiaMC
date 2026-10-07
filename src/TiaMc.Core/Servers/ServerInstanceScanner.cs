namespace TiaMc.Core.Servers;

/// <summary>
/// 扫描本机已有的服务端实例（学 MCSManager 的实例列表）。
///
/// 两类目录都算：
///   &lt;MC&gt;\server-cores\&lt;name&gt;\     由「服务端核心」页生成（含 end 生成的 start.bat / eula.txt）
///   &lt;MC&gt;\serverpacks\&lt;name&gt;\     由「整合包 → 导入服务端整合包」装的（含安装器 / 运行脚本）
/// </summary>
public static class ServerInstanceScanner
{
    public sealed record ServerInstance(
        string Name,
        string Path,
        string Kind,          // "core" | "pack"
        string CoreJar,       // 找到的核心 jar 名（可能为空）
        bool HasEula,
        bool HasStartScript,
        int Port,
        long SizeBytes)
    {
        public string Display => $"{Name}（{Kind}）" + (CoreJar.Length > 0 ? $" · {CoreJar}" : "") +
                                 (HasEula ? " · eula✓" : " · 缺 eula") + (HasStartScript ? " · 脚本✓" : "");
    }

    public static List<ServerInstance> Scan(string minecraftRoot)
    {
        var result = new List<ServerInstance>();

        foreach (var (root, kind) in new[]
                 {
                     (Path.Combine(minecraftRoot, "server-cores"), "core"),
                     (Path.Combine(minecraftRoot, "serverpacks"), "pack")
                 })
        {
            if (!Directory.Exists(root)) continue;

            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    var name = Path.GetFileName(dir);
                    var jars = Directory.GetFiles(dir, "*.jar", SearchOption.TopDirectoryOnly)
                        .Where(f => !Path.GetFileName(f).Contains("installer", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var coreJar = jars.Count > 0 ? Path.GetFileName(jars[0]) : "";
                    var hasEula = File.Exists(Path.Combine(dir, "eula.txt")) &&
                                  File.ReadAllText(Path.Combine(dir, "eula.txt")).Contains("eula=true");
                    var hasScript = File.Exists(Path.Combine(dir, "start.bat")) ||
                                    File.Exists(Path.Combine(dir, "run.bat")) ||
                                    File.Exists(Path.Combine(dir, "start.sh")) ||
                                    File.Exists(Path.Combine(dir, "run.sh"));

                    var port = 25565;
                    var properties = Path.Combine(dir, "server.properties");
                    if (File.Exists(properties))
                    {
                        foreach (var line in File.ReadLines(properties))
                        {
                            if (line.StartsWith("server-port=", StringComparison.OrdinalIgnoreCase) &&
                                int.TryParse(line["server-port=".Length..].Trim(), out var parsed))
                            {
                                port = parsed;
                                break;
                            }
                        }
                    }

                    long size = 0;
                    try
                    {
                        size = new DirectoryInfo(dir)
                            .EnumerateFiles("*", SearchOption.AllDirectories)
                            .Sum(f => { try { return f.Length; } catch (Exception) { return 0L; } });
                    }
                    catch (Exception)
                    {
                        // 体积统计失败不影响列出
                    }

                    result.Add(new ServerInstance(name, dir, kind, coreJar, hasEula, hasScript, port, size));
                }
                catch (Exception)
                {
                    // 单个目录出错就跳过
                }
            }
        }

        return result
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
