using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMc.Core.Instances;

/// <summary>
/// 克隆 Minecraft 实例（学虚拟机软件的"克隆"）：把 versions/&lt;id&gt; 复制成新实例，
/// 并把新实例 JSON 里的 id / 名称改掉，否则启动器会认为两个目录是同一个版本。
///
/// 默认跳过 logs 与 crash-reports（虚拟机克隆也不会把日志带走）。
/// </summary>
public static class InstanceCloner
{
    public sealed record CloneResult(bool Ok, string NewId, string Directory, long Bytes, string Message);

    public static CloneResult Clone(string minecraftRoot, string sourceId, string newId,
        bool includeSaves = true, IProgress<string>? progress = null)
    {
        var versions = Path.Combine(minecraftRoot, "versions");
        var source = Path.Combine(versions, sourceId);
        if (!Directory.Exists(source))
        {
            return new CloneResult(false, newId, source, 0, $"找不到源实例：{source}");
        }

        var target = Path.Combine(versions, newId);
        if (Directory.Exists(target))
        {
            return new CloneResult(false, newId, target, 0, $"目标实例已存在：{newId}");
        }

        progress?.Report($"克隆 {sourceId} → {newId}");

        long bytes = 0;
        try
        {
            CopyTree(source, target, includeSaves, ref bytes, progress);

            // 改 JSON 里的 id（有的版本还会写 name），否则新目录会被当成同一个版本
            var jsonPath = Path.Combine(target, sourceId + ".json");
            if (File.Exists(jsonPath))
            {
                var renamed = Path.Combine(target, newId + ".json");
                RewriteId(jsonPath, renamed, sourceId, newId);
                if (!string.Equals(jsonPath, renamed, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(jsonPath);
                }
            }
            else
            {
                // 没有 JSON 就写一个最小的，指向源实例（继承）
                var minimal = new JsonObject
                {
                    ["id"] = newId,
                    ["inheritsFrom"] = sourceId,
                    ["type"] = "release"
                };
                File.WriteAllText(Path.Combine(target, newId + ".json"), minimal.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception e)
        {
            return new CloneResult(false, newId, target, bytes, "克隆失败：" + e.Message);
        }

        progress?.Report($"克隆完成：{target}（{bytes / 1024 / 1024} MB）");
        return new CloneResult(true, newId, target, bytes, $"已克隆为 {newId}（{bytes / 1024 / 1024} MB）");
    }

    private static void CopyTree(string source, string target, bool includeSaves, ref long bytes,
        IProgress<string>? progress)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            var destination = Path.Combine(target, name);
            File.Copy(file, destination, overwrite: true);
            bytes += new FileInfo(destination).Length;
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);

            // 克隆不带走日志；存档可选
            if (name.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crash-reports", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!includeSaves && name.Equals("saves", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyTree(directory, Path.Combine(target, name), includeSaves, ref bytes, progress);
        }
    }

    private static void RewriteId(string sourceJson, string targetJson, string oldId, string newId)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(sourceJson));
            if (node is JsonObject root)
            {
                root["id"] = newId;
                if (root["name"] is not null && root["name"]!.GetValue<string>() == oldId)
                {
                    root["name"] = newId;
                }
            }

            File.WriteAllText(targetJson, node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                                             ?? File.ReadAllText(sourceJson));
        }
        catch (Exception)
        {
            // JSON 读不动就直接原样拷
            File.Copy(sourceJson, targetJson, overwrite: true);
        }
    }

    /// <summary>给克隆生成一个不冲突的名字：`<源> - 副本`、`<源> - 副本 (2)`…</summary>
    public static string SuggestName(string minecraftRoot, string sourceId)
    {
        var versions = Path.Combine(minecraftRoot, "versions");
        var candidate = sourceId + " - 副本";
        var index = 2;
        while (Directory.Exists(Path.Combine(versions, candidate)))
        {
            candidate = $"{sourceId} - 副本 ({index})";
            index++;
        }

        return candidate;
    }
}
