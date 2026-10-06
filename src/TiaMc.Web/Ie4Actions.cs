using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using TiaMc.App.Services;
using TiaMc.Core.Utils;

namespace TiaMc.Web;

/// <summary>
/// Trident 4.0（IE4）档的服务端逻辑。
///
/// IE4 时代没有可靠的脚本环境（没有 getElementById / attachEvent / XMLHttpRequest），
/// 所以这一档**完全不用 JavaScript**：所有按钮都是普通链接或表单，
/// 提交后由服务端执行动作并整页刷新（302 回 /ie4）。
///
/// 动作本身不重复实现，而是通过内部 HTTP 调用启动器已有的同一套 /api/*，
/// 这样 IE4 档与 Web 界面做的事完全一致。
/// </summary>
internal static class Ie4Actions
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>把 ie4.html 的占位符替换成当前状态。</summary>
    public static string Render(string templatePath, LaunchService launcher, int port, string message)
    {
        var html = File.Exists(templatePath)
            ? File.ReadAllText(templatePath, Encoding.UTF8)
            : "<html><body>ie4.html 缺失</body></html>";

        var config = launcher.Config;
        var installed = launcher.Installed.ToList();

        var instanceRows = new StringBuilder();
        var options = new StringBuilder();
        foreach (var instance in installed)
        {
            var javaMajor = TiaMc.Core.Java.JavaRuntimeInstaller.RequiredMajor(instance.Json.JavaVersion?.MajorVersion, instance.Id);
            var active = instance.Id == config.ActiveInstance;
            instanceRows.Append(active ? "<b>" : "")
                .Append(Escape(instance.Id))
                .Append(active ? "（当前）</b>" : "")
                .Append(" &nbsp; Java ").Append(javaMajor)
                .Append("<br>");

            options.Append("<option value=\"").Append(Escape(instance.Id)).Append('"')
                .Append(active ? " selected" : "").Append('>')
                .Append(Escape(instance.Id)).Append("</option>");
        }

        if (instanceRows.Length == 0) instanceRows.Append("（没有实例，请先在 Web 界面里安装）");
        if (options.Length == 0) options.Append("<option value=\"\">（无）</option>");

        var logs = new StringBuilder();
        var recent = LogService.Snapshot();
        foreach (var entry in recent.Skip(Math.Max(0, recent.Count - 12)))
        {
            logs.Append(Escape($"{entry.TimeText} [{entry.LevelText}] {entry.Source}: {entry.Message}")).Append('\n');
        }

        var java = TiaMc.Core.Java.JavaDetector.Detect().Take(3).ToList();
        var javaText = java.Count == 0
            ? "未检测到（可在下面点「检测 / 安装 Java」）"
            : string.Join("；", java.Select(j => $"Java {j.MajorVersion} · {j.Path}"));

        return html
            .Replace("{{instances}}", instanceRows.ToString())
            .Replace("{{instanceoptions}}", options.ToString())
            .Replace("{{accounts}}", Escape(config.PlayerName ?? "(未设置)"))
            .Replace("{{java}}", Escape(javaText))
            .Replace("{{kernel}}", $"文档模式 IE=5（Quirks）· 系统 Trident {Ie6EngineService.SystemEngineVersion()} · 本页无脚本")
            .Replace("{{address}}", $"http://127.0.0.1:{port}/ie4")
            .Replace("{{message}}", message.Length > 0 ? message : "就绪。所有操作都会整页刷新（IE4 时代的做法）。")
            .Replace("{{logs}}", logs.ToString())
            .Replace("{{query}}", "");
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>执行 IE4 档的动作，返回要显示的结果文字。</summary>
    public static string Run(string action, WebServer.Request request, LaunchService launcher, int port)
    {
        try
        {
            switch (action)
            {
                case "refresh":
                    launcher.ReloadInstallation();
                    return $"状态已刷新：{launcher.Installed.Count()} 个实例。";

                case "select":
                {
                    var id = request.Query.TryGetValue("instance", out var value) ? value : "";
                    if (id.Length > 0)
                    {
                        launcher.Config.ActiveInstance = id;
                        launcher.Config.Save();
                        launcher.ReloadInstallation();
                        return "已切换到实例：" + id;
                    }

                    return "没有选择实例。";
                }

                case "manifest":
                    return Post(port, "/api/manifest/refresh", null);

                case "launch":
                    return Post(port, "/api/launch", null);

                case "stop":
                    return Post(port, "/api/stop", null);

                case "diagnose":
                    return Post(port, "/api/diagnose", null);

                case "java":
                    return Post(port, "/api/java/provision", null);

                case "search":
                {
                    var q = request.Query.TryGetValue("q", out var value) ? value : "";
                    return Get(port, "/api/mods?q=" + Uri.EscapeDataString(q));
                }

                default:
                    return "未知操作：" + action;
            }
        }
        catch (Exception e)
        {
            return "操作失败：" + e.Message;
        }
    }

    private static string Post(int port, string path, JsonObject? body)
    {
        var content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json");
        using var response = Client.PostAsync($"http://127.0.0.1:{port}{path}", content).GetAwaiter().GetResult();
        return Summarize(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), (int)response.StatusCode);
    }

    private static string Get(int port, string path)
    {
        using var response = Client.GetAsync($"http://127.0.0.1:{port}{path}").GetAwaiter().GetResult();
        return Summarize(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), (int)response.StatusCode);
    }

    private static string Summarize(string json, int status)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var message = node?["message"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(message)) return message;

            var ok = node?["ok"]?.GetValue<bool>();
            return ok == true ? "完成。" : $"完成（HTTP {status}）。";
        }
        catch (Exception)
        {
            return $"完成（HTTP {status}）。";
        }
    }
}
