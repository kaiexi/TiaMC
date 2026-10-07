using System.Text;
using TiaMc.Core.Utils;

namespace TiaMc.Core.Servers;

/// <summary>
/// 把下载好的服务端核心变成一个**能直接跑的服务端实例**（学 MCSManager 的实例目录结构）。
///
/// MCSManager 的实例 = 一个目录 + 核心 jar + 配置 + 启动参数 + 控制台。这里做同样的第一步：
///   &lt;MC&gt;\server-cores\&lt;project&gt;-&lt;version&gt;\
///       ├─ &lt;核心&gt;.jar          下载好的核心
///       ├─ eula.txt              eula=true（否则服务端第一次启动会直接退出）
///       ├─ server.properties     端口 / MOTD / 存档名
///       ├─ start.bat             java -Xmx... -jar ... nogui
///       └─ 启动说明.txt           怎么开始、怎么给朋友连
/// </summary>
public static class ServerInstanceCreator
{
    public sealed record Created(string Directory, List<string> Files, string StartupCommand);

    /// <summary>在 root/server-cores 下创建实例目录并生成配置。</summary>
    public static Created Create(string root, string project, string gameVersion, string coreFileName,
        int port = 25565, int maxMemoryMb = 4096, string? javaPath = null)
    {
        var directory = Path.Combine(root, "server-cores", $"{project}-{gameVersion}");
        Directory.CreateDirectory(directory);

        var files = new List<string>();

        var eula = Path.Combine(directory, "eula.txt");
        File.WriteAllText(eula,
            "# 由 TiaMC 生成：同意 Minecraft 服务端 EULA 后服务端才能启动\r\n" +
            "# https://aka.ms/MinecraftEULA\r\n" +
            "eula=true\r\n", new UTF8Encoding(false));   // 不能带 BOM！否则服务端读成 ﻿eula=true 并拒绝启动
        files.Add(eula);

        var properties = Path.Combine(directory, "server.properties");
        File.WriteAllText(properties,
            "# 由 TiaMC 生成\r\n" +
            $"server-port={port}\r\n" +
            "motd=\\u00a7bTiaMC \\u00a7fServer\r\n" +
            "online-mode=true\r\n" +
            "level-name=world\r\n" +
            "max-players=20\r\n" +
            "view-distance=10\r\n" +
            "enable-command-block=false\r\n" +
            "allow-flight=false\r\n", new UTF8Encoding(false));
        files.Add(properties);

        var java = string.IsNullOrWhiteSpace(javaPath) ? "java" : javaPath!;
        var command = $"\"{java}\" -Xmx{maxMemoryMb}M -Xms{Math.Min(1024, maxMemoryMb)}M -jar \"{coreFileName}\" nogui";

        var bat = Path.Combine(directory, "start.bat");
        File.WriteAllText(bat,
            "@echo off\r\n" +
            "chcp 65001 >nul\r\n" +
            "title TiaMC Server\r\n" +
            command + "\r\n" +
            "echo.\r\n" +
            "echo 服务端已退出。按任意键关闭窗口。\r\n" +
            "pause >nul\r\n", new UTF8Encoding(false));
        files.Add(bat);

        var readme = Path.Combine(directory, "启动说明.txt");
        File.WriteAllText(readme,
            "TiaMC 生成的服务端实例\r\n" +
            "=================================\r\n" +
            $"核心：{coreFileName}（{project} {gameVersion}）\r\n" +
            $"内存：-Xmx{maxMemoryMb}M（在启动器里可改）\r\n" +
            $"端口：{port}（改 server.properties 的 server-port）\r\n" +
            "\r\n" +
            "怎么启动：双击 start.bat（本机需要先装好 Java，版本见核心要求）。\r\n" +
            "别人怎么进：本机内网用 \"localhost\"；同一局域网用你的内网 IP；\r\n" +
            "             公网需要在路由器上把端口转发到本机，或使用内网穿透。\r\n" +
            "第一次启动会生成 world 存档，稍等片刻直到控制台出现 \"Done\"。\r\n" +
            "\r\n" +
            "eula.txt 里的 eula=true 表示你已同意 Minecraft 最终用户许可协议。\r\n", new UTF8Encoding(true));
        files.Add(readme);

        return new Created(directory, files, command);
    }

    /// <summary>根据核心项目推荐 Java 主版本（服务端视角：1.17+ 用 17，1.20.5+ 用 21）。</summary>
    public static int RecommendedJava(string gameVersion)
    {
        var text = gameVersion.Trim();
        var parts = text.Split('.', '-');
        if (parts.Length < 2) return 21;
        if (!int.TryParse(parts[0], out var major)) return 21;
        if (!int.TryParse(parts[1], out var minor)) minor = 0;

        if (major >= 26) return 21;          // 新版快照/正式都用 21+
        if (major == 1 && minor >= 21) return 21;
        if (major == 1 && minor >= 18) return 17;
        if (major == 1 && minor >= 17) return 17;
        return 8;
    }
}
