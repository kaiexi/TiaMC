using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TiaMc.Web;

/// <summary>
/// 内置浏览器窗口（真实 IE 引擎，不做外观模仿）。
///
/// 设计取舍：**不再手绘 IE6 皮肤**。窗口用系统原生控件（系统标题栏、原生菜单/工具栏/状态栏），
/// 里面承载的是系统自带的 **MSHTML/Trident** 引擎（WinForms <see cref="WebBrowser"/> 即其封装）——
/// 也就是说渲染内核是真的 IE，不是仿制品；需要 IE6 级排版时把文档模式设为
/// **5000（IE5 quirks）**，那就是当年 IE6 的真实排版行为。
///
/// 文档模式（HKCU\...\FeatureControl\FEATURE_BROWSER_EMULATION）：
///   5000  = IE5 quirks（IE6 级排版，配 /legacy 兼容页）
///   11001 = IE11（现代页面，可用 --ie11-mode 切换）
/// </summary>
internal sealed class EmbeddedBrowserWindow : Form
{
    private readonly string _home;
    private WebBrowser _browser = null!;
    private TextBox _address = null!;
    private ToolStripStatusLabel _statusText = null!;
    private ToolStripButton _back = null!, _forward = null!;

    public EmbeddedBrowserWindow(string home, string title)
    {
        _home = home;

        // 系统原生窗口（不自定义边框、不画皮肤）
        Text = title;
        Icon = SystemIcons.Application;
        Width = 1280;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        KeyPreview = true;

        BuildMenu();
        BuildToolbar();
        BuildBrowser();
        BuildStatusBar();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            if (e.Control && e.KeyCode == Keys.R) Navigate(_address.Text);
        };
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip { RenderMode = ToolStripRenderMode.System };

        var file = new ToolStripMenuItem("文件(&F)");
        file.DropDownItems.Add("新建窗口(&N)", null, (_, _) => Navigate(_home));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("关闭(&C)", null, (_, _) => Close());

        var edit = new ToolStripMenuItem("编辑(&E)");
        edit.DropDownItems.Add("全选(&A)", null, (_, _) => _browser.Document?.ExecCommand("SelectAll", false, null));
        edit.DropDownItems.Add("复制(&C)", null, (_, _) => _browser.Document?.ExecCommand("Copy", false, null));

        var view = new ToolStripMenuItem("查看(&V)");
        view.DropDownItems.Add("刷新(&R)", null, (_, _) => RefreshPage());
        view.DropDownItems.Add("页面源码(&C)", null, (_, _) => ShowSource());

        var tools = new ToolStripMenuItem("工具(&T)");
        tools.DropDownItems.Add("引擎信息(&I)", null, (_, _) => ShowEngineInfo());

        var help = new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add("关于(&A)", null, (_, _) => ShowAbout());

        menu.Items.AddRange([file, edit, view, tools, help]);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void BuildToolbar()
    {
        var toolbar = new ToolStrip { RenderMode = ToolStripRenderMode.System, GripStyle = ToolStripGripStyle.Hidden };

        _back = new ToolStripButton("◀ 后退") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _forward = new ToolStripButton("前进 ▶") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var stop = new ToolStripButton("停止") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var refresh = new ToolStripButton("刷新") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var home = new ToolStripButton("主页") { DisplayStyle = ToolStripItemDisplayStyle.Text };

        _back.Click += (_, _) => { if (_browser.CanGoBack) _browser.GoBack(); };
        _forward.Click += (_, _) => { if (_browser.CanGoForward) _browser.GoForward(); };
        stop.Click += (_, _) => _browser.Stop();
        refresh.Click += (_, _) => RefreshPage();
        home.Click += (_, _) => Navigate(_home);

        _address = new TextBox { Width = 560 };
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Navigate(_address.Text);
        };

        var go = new ToolStripButton("转到") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        go.Click += (_, _) => Navigate(_address.Text);

        toolbar.Items.AddRange([
            _back, _forward, stop, refresh, home,
            new ToolStripSeparator(),
            new ToolStripLabel("地址"),
            new ToolStripControlHost(_address) { AutoSize = false, Width = 560 },
            go
        ]);

        Controls.Add(toolbar);
    }

    private void BuildBrowser()
    {
        _browser = new WebBrowser
        {
            Dock = DockStyle.Fill,
            IsWebBrowserContextMenuEnabled = true,
            WebBrowserShortcutsEnabled = true,
            AllowWebBrowserDrop = true,
            ScriptErrorsSuppressed = false
        };

        _browser.Navigating += (_, _) => _statusText.Text = "正在打开网页…";
        _browser.Navigated += (_, _) =>
        {
            _address.Text = _browser.Url?.ToString() ?? _home;
            _statusText.Text = "完成";
            _back.Enabled = _browser.CanGoBack;
            _forward.Enabled = _browser.CanGoForward;
        };
        _browser.DocumentCompleted += (_, _) => _statusText.Text = "完成";
        _browser.NewWindow += (_, e) => e.Cancel = true;   // 单窗口

        Controls.Add(_browser);
        _browser.BringToFront();
    }

    private void BuildStatusBar()
    {
        var status = new StatusStrip { SizingGrip = true };
        _statusText = new ToolStripStatusLabel("完成") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        status.Items.Add(_statusText);
        status.Items.Add(new ToolStripStatusLabel("本机回环 · 127.0.0.1") { BorderSides = ToolStripStatusLabelBorderSides.Left });
        Controls.Add(status);
    }

    private void ShowSource()
    {
        try
        {
            var html = _browser.DocumentText ?? "";
            var path = Path.Combine(Path.GetTempPath(), "tiamc-page-source.htm");
            File.WriteAllText(path, html);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            _statusText.Text = "已用记事本/默认程序打开页面源码：" + path;
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "页面源码", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowEngineInfo()
    {
        var version = "未知";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Internet Explorer");
            version = key?.GetValue("svcVersion")?.ToString()
                      ?? key?.GetValue("Version")?.ToString()
                      ?? "未知";
        }
        catch (Exception)
        {
            // ignore
        }

        MessageBox.Show(this,
            "渲染引擎：系统自带 MSHTML / Trident（WinForms WebBrowser 控件，无第三方依赖）\n" +
            $"已安装 IE 版本：{version}\n" +
            $"当前文档模式：{DocumentModeText()}\n\n" +
            "文档模式说明：5000 = IE5 quirks（IE6 级排版，配 /legacy 兼容页）；11001 = IE11（现代页面）。\n" +
            "切换方式：启动参数 --ie6-quirks（默认）或 --ie11-mode。",
            "引擎信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string DocumentModeText()
    {
        try
        {
            var exe = Path.GetFileName(Environment.ProcessPath ?? "TiaMC-Web.exe");
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
            var value = key?.GetValue(exe);
            return value switch
            {
                5000 => "5000（IE5 quirks = IE6 级）",
                7000 => "7000（IE7）",
                8000 => "8000（IE8）",
                11001 => "11001（IE11）",
                null => "系统默认",
                var other => other.ToString() ?? "系统默认"
            };
        }
        catch (Exception)
        {
            return "未知";
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "TIA-MC 启动器 · 内置浏览器窗口\n\n" +
            "内核：系统自带 MSHTML / Trident（真实 IE 引擎，非仿制）\n" +
            "窗口：系统原生控件（标题栏、菜单、工具栏、状态栏均为系统绘制）\n" +
            "主页：" + _home + "\n\n" +
            "Flash：网页内由 Ruffle（开源 Flash 模拟器）播放；真实 Flash 内容可交给 Flash 投影播放器。\n" +
            "所有功能与桌面版共用同一套启动内核与配置。",
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void Navigate(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
            _browser.Navigate(url);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "无法打开页面：" + e.Message, "浏览器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshPage()
    {
        try
        {
            _browser.Refresh(WebBrowserRefreshOption.Completely);
        }
        catch (Exception)
        {
            Navigate(_address.Text);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Navigate(_home);
    }

    /// <summary>设置本程序使用的文档模式（真实 IE 引擎的行为）。</summary>
    public static void ConfigureEmulation(int mode)
    {
        try
        {
            var exe = Path.GetFileName(Environment.ProcessPath ?? "TiaMC-Web.exe");
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
            key?.SetValue(exe, mode, RegistryValueKind.DWord);
        }
        catch (Exception)
        {
            // 写不了注册表时用系统默认文档模式
        }
    }
}
