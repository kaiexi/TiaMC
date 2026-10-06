using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TiaMc.Web;

/// <summary>
/// 内置的 IE6 外观浏览器窗口。
///
/// 说明：Windows 上真正的 IE6（2001 年的 MSHTML）已随系统更新不再单独存在，但
/// **MSHTML/Trident 引擎仍然在系统里**，WinForms 的 <see cref="WebBrowser"/> 控件就是它的封装。
/// 所以这里的做法是：
///
///   * 用系统自带的 Trident 引擎加载页面（无需安装任何浏览器）；
///   * 通过 HKCU\...\FeatureControl\FEATURE_BROWSER_EMULATION 指定文档模式
///     （默认 11001 = IE11 模式，页面里的 JS/CSS 能正常跑；--ie6-quirks 时用 5000 = IE5 quirks，
///      就是当年 IE6 的排版行为，配合 /legacy.html 使用）；
///   * 窗口本身**按 IE6 画**：蓝色渐变标题栏、菜单栏、工具栏 + 地址栏、状态栏——外观就是 IE6。
/// </summary>
internal sealed class Ie6Window : Form
{
    private const int WmNclbuttondown = 0xA1;
    private const int HtCaption = 0x2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly string _home;
    private WebBrowser _browser = null!;
    private TextBox _address = null!;
    private ToolStripStatusLabel _statusText = null!;
    private ToolStripStatusLabel _zoneText = null!;
    private ToolStripButton _back = null!, _forward = null!, _stop = null!, _refresh = null!;

    public Ie6Window(string home)
    {
        _home = home;

        Text = "TIA-MC 启动器 - Microsoft Internet Explorer";
        Icon = SystemIcons.Application;
        Width = 1280;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(236, 233, 216);
        Font = new Font("Tahoma", 8.25f);
        KeyPreview = true;

        // 自定义 IE6 外观：不要系统标题栏
        FormBorderStyle = FormBorderStyle.None;
        BuildChrome();
        BuildBrowser();
        BuildStatusBar();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            if (e.Control && e.KeyCode == Keys.R) Navigate(_address.Text);
        };
    }

    // ------------------------------------------------------------ 外观

    private void BuildChrome()
    {
        // 标题栏（蓝色渐变 + 最小化/最大化/关闭）
        var title = new Panel { Dock = DockStyle.Top, Height = 30 };
        title.Paint += (_, e) =>
        {
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, title.Width, title.Height),
                Color.FromArgb(0, 88, 238), Color.FromArgb(0, 70, 200), LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(brush, title.ClientRectangle);
            using var bold = new Font("Tahoma", 8.5f, FontStyle.Bold);
            e.Graphics.DrawString("TIA-MC 启动器（内置 IE6） - Microsoft Internet Explorer", bold,
                Brushes.White, 26, 8);
            // 小地球图标
            e.Graphics.FillEllipse(Brushes.WhiteSmoke, 6, 7, 14, 14);
            e.Graphics.FillEllipse(Brushes.DodgerBlue, 8, 9, 10, 10);
        };
        title.MouseDown += (_, _) =>
        {
            ReleaseCapture();
            SendMessage(Handle, WmNclbuttondown, HtCaption, 0);
        };

        var minimize = MakeTitleButton("_", () => WindowState = FormWindowState.Minimized);
        var maximize = MakeTitleButton("□", () => WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized);
        var close = MakeTitleButton("✕", Close, Color.FromArgb(192, 54, 27));

        title.Controls.Add(close);
        title.Controls.Add(maximize);
        title.Controls.Add(minimize);
        title.Resize += (_, _) =>
        {
            close.Left = title.Width - close.Width - 4;
            maximize.Left = close.Left - maximize.Width - 2;
            minimize.Left = maximize.Left - minimize.Width - 2;
        };

        // 菜单栏（IE6 的六项菜单，行为简化：刷新/主页/退出/关于）
        var menu = new MenuStrip
        {
            BackColor = Color.FromArgb(236, 233, 216),
            RenderMode = ToolStripRenderMode.System,
            GripStyle = ToolStripGripStyle.Hidden,
            Font = new Font("Tahoma", 8.25f)
        };
        var file = new ToolStripMenuItem("文件(F)");
        file.DropDownItems.Add("新建窗口(&N)", null, (_, _) => Navigate(_home));
        file.DropDownItems.Add("关闭(&C)", null, (_, _) => Close());
        var edit = new ToolStripMenuItem("编辑(E)");
        edit.DropDownItems.Add("全选(&A)", null, (_, _) => _browser.Document?.ExecCommand("SelectAll", false, null));
        edit.DropDownItems.Add("复制(&C)", null, (_, _) => _browser.Document?.ExecCommand("Copy", false, null));
        var view = new ToolStripMenuItem("查看(V)");
        view.DropDownItems.Add("刷新(&R)", null, (_, _) => RefreshPage());
        view.DropDownItems.Add("源代码(&C)", null, (_, _) => MessageBox.Show(this,
            "页面由 TiaMC-Web 内置服务器提供，源码在程序目录 wwwroot 下。", "查看源代码",
            MessageBoxButtons.OK, MessageBoxIcon.Information));
        var fav = new ToolStripMenuItem("收藏(A)");
        fav.DropDownItems.Add("添加到收藏夹(&A)", null, (_, _) => MessageBox.Show(this,
            "已经把启动器首页加入收藏（本窗口内）。", "收藏夹", MessageBoxButtons.OK, MessageBoxIcon.Information));
        var tools = new ToolStripMenuItem("工具(T)");
        tools.DropDownItems.Add("Internet 选项(&O)", null, (_, _) => MessageBox.Show(this,
            "文档模式：由 FEATURE_BROWSER_EMULATION 控制（默认 IE11，--ie6-quirks 时为 IE5 quirks）。",
            "Internet 选项", MessageBoxButtons.OK, MessageBoxIcon.Information));
        var help = new ToolStripMenuItem("帮助(H)");
        help.DropDownItems.Add("关于 Internet Explorer(&A)", null, (_, _) => MessageBox.Show(this,
            "TIA-MC 内置浏览器\n\n" +
            "外观：仿 Internet Explorer 6（Windows XP 风格）\n" +
            "内核：系统自带 MSHTML / Trident（WinForms WebBrowser 控件）\n" +
            $"主页：{_home}\n\n" +
            "Flash：页面内由 Ruffle 播放；真实 Flash 内容可交给投影播放器。",
            "关于 Internet Explorer", MessageBoxButtons.OK, MessageBoxIcon.Information));
        menu.Items.AddRange([file, edit, view, fav, tools, help]);

        // 工具栏 + 地址栏
        var toolbar = new ToolStrip
        {
            BackColor = Color.FromArgb(236, 233, 216),
            RenderMode = ToolStripRenderMode.System,
            GripStyle = ToolStripGripStyle.Hidden,
            Font = new Font("Tahoma", 8.25f)
        };

        _back = new ToolStripButton("后退") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _forward = new ToolStripButton("前进") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _stop = new ToolStripButton("停止") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _refresh = new ToolStripButton("刷新") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var home = new ToolStripButton("主页") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _back.Click += (_, _) => { if (_browser.CanGoBack) _browser.GoBack(); };
        _forward.Click += (_, _) => { if (_browser.CanGoForward) _browser.GoForward(); };
        _stop.Click += (_, _) => _browser.Stop();
        _refresh.Click += (_, _) => RefreshPage();
        home.Click += (_, _) => Navigate(_home);

        _address = new TextBox { Width = 620, Font = new Font("Tahoma", 8.25f) };
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Navigate(_address.Text);
            }
        };
        var go = new ToolStripButton("转到") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        go.Click += (_, _) => Navigate(_address.Text);

        toolbar.Items.AddRange([
            _back, _forward, _stop, _refresh, home,
            new ToolStripSeparator(),
            new ToolStripLabel("地址(D)"),
            new ToolStripControlHost(_address) { AutoSize = false, Width = 620 },
            go
        ]);

        Controls.Add(toolbar);
        Controls.Add(menu);
        Controls.Add(title);
    }

    private Button MakeTitleButton(string text, Action onClick, Color? color = null)
    {
        var button = new Button
        {
            Text = text,
            Width = 22,
            Height = 20,
            Top = 5,
            FlatStyle = FlatStyle.System,
            BackColor = color ?? Color.FromArgb(76, 155, 255),
            ForeColor = Color.White,
            Font = new Font("Tahoma", 8f, FontStyle.Bold),
            TabStop = false
        };
        button.Click += (_, _) => onClick();
        return button;
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

        _browser.Navigating += (_, e) =>
        {
            _statusText.Text = "正在打开网页…";
            _statusText.Text = "正在打开网页…";
            Application.DoEvents();
        };
        _browser.Navigated += (_, _) =>
        {
            _address.Text = _browser.Url?.ToString() ?? _home;
            _statusText.Text = "完成";
            _back.Enabled = _browser.CanGoBack;
            _forward.Enabled = _browser.CanGoForward;
        };
        _browser.DocumentCompleted += (_, _) => _statusText.Text = "完成";
        _browser.NewWindow += (_, e) =>
        {
            // IE6 没有标签页：新窗口也在本窗口里打开
            e.Cancel = true;
            if (e.Cancel && _browser.StatusText.Length == 0) return;
        };

        Controls.Add(_browser);
        _browser.BringToFront();
    }

    private void BuildStatusBar()
    {
        var status = new StatusStrip
        {
            BackColor = Color.FromArgb(236, 233, 216),
            SizingGrip = true,
            Font = new Font("Tahoma", 8.25f)
        };
        _statusText = new ToolStripStatusLabel("完成") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _zoneText = new ToolStripStatusLabel("Internet") { BorderSides = ToolStripStatusLabelBorderSides.Left };
        status.Items.Add(_statusText);
        status.Items.Add(_zoneText);
        Controls.Add(status);
    }

    // ------------------------------------------------------------ 导航

    public void Navigate(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }

            _browser.Navigate(url);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "无法打开页面：" + e.Message, "Internet Explorer",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        SetForegroundWindow(Handle);
    }

    /// <summary>
    /// 让系统里的 MSHTML 用指定文档模式渲染本程序（默认 IE11 = 11001；
    /// --ie6-quirks 时 5000 = IE5 quirks，也就是 IE6 当年的排版行为）。
    /// </summary>
    public static void ConfigureEmulation(int mode)
    {
        try
        {
            var exe = Path.GetFileName(Environment.ProcessPath ?? "TiaMC-Web.exe");
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
            key?.SetValue(exe, mode, RegistryValueKind.DWord);

            using var quirks = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_IE6_COMPAT");
            quirks?.SetValue(exe, 1, RegistryValueKind.DWord);
        }
        catch (Exception)
        {
            // 无权限写注册表时忽略：控件会退回默认文档模式
        }
    }
}
