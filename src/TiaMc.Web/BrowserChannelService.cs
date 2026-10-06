using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using TiaMc.App.Services;
using TiaMc.Core.Utils;

namespace TiaMc.Web;

/// <summary>
/// 浏览器通道：把页面交给本机已安装的浏览器 / IE 外壳打开。
///
/// 关于「世界之窗（TheWorld）里面的内核」：TheWorld 3.x 是**纯 IE 外壳**——安装目录里只有
/// TheWorld.exe，没有任何 mshtml.dll / ieframe.dll，所以它的"IE 内核"就是**系统 IE**（本机为
/// IE 11.00.26100）。360se、傲游、腾讯 TT、GreenBrowser 等早期国产浏览器同理。因此这里做的是
/// **如实列出每个通道实际使用的引擎版本**，并支持一键用它们打开页面；真正的 IE6 引擎请用
/// Ie6EngineService 的可插拔引擎槽（或 WSL + Wine 里的 IE6）。
/// </summary>
internal sealed class BrowserChannelService
{
    public sealed record Channel(string Id, string Name, string Executable, string EngineText, bool IeShell);

    private static readonly (string Id, string Name, string[] Patterns)[] Known =
    [
        ("theworld", "世界之窗 TheWorld", ["TheWorld 3\\TheWorld.exe", "TheWorld\\TheWorld.exe", "TheWorld\\TheWorld6.exe", "TheWorld.exe"]),
        ("maxthon", "傲游 Maxthon", ["Maxthon\\Maxthon.exe", "Maxthon3\\Bin\\Maxthon.exe", "Maxthon5\\Bin\\Maxthon.exe"]),
        ("360se", "360 安全浏览器", ["360\\360se6\\Application\\360se.exe", "360se6\\Application\\360se.exe", "360\\360Safe\\360se.exe"]),
        ("tt", "腾讯 TT", ["Tencent\\Traveler\\TTraveler.exe", "Tencent\\TT\\TTraveler.exe"]),
        ("greenbrowser", "GreenBrowser", ["GreenBrowser\\GreenBrowser.exe"]),
        ("ie", "系统 Internet Explorer", ["Internet Explorer\\iexplore.exe"]),
        ("edge", "Microsoft Edge", ["Microsoft\\Edge\\Application\\msedge.exe"]),
        ("chrome", "Google Chrome", ["Google\\Chrome\\Application\\chrome.exe"])
    ];

    /// <summary>全部通道（含引擎版本说明）。</summary>
    public List<Channel> Detect()
    {
        var result = new List<Channel>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (var (id, name, patterns) in Known)
        {
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                foreach (var pattern in patterns)
                {
                    var path = Path.Combine(root, pattern);
                    if (!File.Exists(path)) continue;

                    var ieShell = id is "theworld" or "maxthon" or "360se" or "tt" or "greenbrowser" or "ie";
                    var engine = ieShell
                        ? $"系统 IE / Trident {Ie6EngineService.SystemEngineVersion()}"
                        : "自带内核（Chromium 等）";

                    result.Add(new Channel(id, name, path, engine, ieShell));
                    goto next;
                }
            }

            next: ;
        }

        // 32 位 IE 单独列一条（Web 版 x86 进程里更接近"32 位引擎"）
        var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Internet Explorer", "iexplore.exe");
        if (File.Exists(wow) && result.All(c => c.Id != "ie-x86"))
        {
            var mshtml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "mshtml.dll");
            var version = File.Exists(mshtml)
                ? FileVersionInfo.GetVersionInfo(mshtml).FileVersion ?? ""
                : "";
            result.Add(new Channel("ie-x86", "系统 IE（32 位 / SysWOW64 引擎）", wow,
                $"Trident {version}", true));
        }

        return result;
    }

    public sealed record ActionResult(bool Ok, string Message, string Channel = "");

    public ActionResult Launch(string channelId, string url)
    {
        var channel = Detect().FirstOrDefault(c => c.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase));
        if (channel is null) return new ActionResult(false, $"没有找到浏览器通道：{channelId}");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = channel.Executable,
                Arguments = url.Length > 0 ? $"\"{url}\"" : "",
                UseShellExecute = true
            });

            LogService.User($"用 {channel.Name} 打开页面: {url}（引擎：{channel.EngineText}）", "浏览器");
            return new ActionResult(true, $"已用 {channel.Name} 打开（引擎：{channel.EngineText}）", channel.Name);
        }
        catch (Exception e)
        {
            return new ActionResult(false, $"启动 {channel.Name} 失败: {e.Message}");
        }
    }

    public JsonObject Status()
    {
        var channels = new JsonArray();
        foreach (var channel in Detect())
        {
            channels.Add(new JsonObject
            {
                ["id"] = channel.Id,
                ["name"] = channel.Name,
                ["executable"] = channel.Executable,
                ["engine"] = channel.EngineText,
                ["ieShell"] = channel.IeShell
            });
        }

        return new JsonObject
        {
            ["channels"] = channels,
            ["note"] =
                "世界之窗 / 傲游 / 360 / 腾讯 TT 等早期国产浏览器都是 IE 外壳：安装目录里没有 mshtml.dll，" +
                "它们的 IE 模式用的就是系统 IE（本机 " + Ie6EngineService.SystemEngineVersion() + "）。" +
                "需要真正的 IE6 引擎请使用「IE6 引擎槽」（ie6\\ 目录）或 WSL + Wine 里的 IE6。"
        };
    }
}
