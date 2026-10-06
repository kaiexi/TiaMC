using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TiaMc.Web;

/// <summary>
/// 用开源内核 miniblink（Apache-2.0）渲染的启动器内嵌窗口。
///
/// 直接加载 `mb132_x64.dll` / `mb132_x32.dll` 并通过 `mb*` 导出创建 WebView：
///   mbInit → mbCreateWebWindow(MB_WINDOW_TYPE_CONTROL, panel.Handle, …) → mbLoadURL
/// 窗口本身用系统原生控件（不做外观模仿），内容区由 Blink 渲染。
/// </summary>
internal sealed class MiniblinkWindow : Form
{
    private const int MbWindowTypeControl = 2;   // MB_WINDOW_TYPE_CONTROL
    private const int MbWindowTypePopup = 0;     // MB_WINDOW_TYPE_POPUP（内核自己开窗口，最可靠）

    // ---- wke/miniblink C API ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbInitDelegate(IntPtr settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr MbCreateWebWindowDelegate(int type, IntPtr parent, int x, int y, int w, int h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbLoadUrlDelegate(IntPtr view, [MarshalAs(UnmanagedType.LPWStr)] string url);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbMoveWindowDelegate(IntPtr view, int x, int y, int w, int h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbShowWindowDelegate(IntPtr view, bool show);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbSetTitleDelegate(IntPtr view, [MarshalAs(UnmanagedType.LPWStr)] string title);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbSetUserAgentDelegate(IntPtr view, [MarshalAs(UnmanagedType.LPWStr)] string ua);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbSetZoomDelegate(IntPtr view, float factor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbRunDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbSimpleDelegate(IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbViewCallback(IntPtr view, IntPtr param);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MbOnReadyDelegate(IntPtr view, IntPtr callback, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    private readonly string _url;
    private IntPtr _module;
    private IntPtr _view;
    private Panel _host = null!;
    private TextBox _address = null!;
    private ToolStripStatusLabel _status = null!;
    private string _dll;

    public MiniblinkWindow(string dllPath, string url)
    {
        _dll = dllPath;
        _url = url;

        Text = "TIA-MC · 内置 miniblink（开源 Blink 内核）";
        Icon = SystemIcons.Application;
        Width = 1280;
        Height = 880;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);

        BuildChrome();
        Shown += (_, _) => InitializeEngine();
        FormClosed += (_, _) => Shutdown();
    }

    private void BuildChrome()
    {
        var toolbar = new ToolStrip { RenderMode = ToolStripRenderMode.System, GripStyle = ToolStripGripStyle.Hidden };
        var back = new ToolStripButton("后退") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        var forward = new ToolStripButton("前进") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        var reload = new ToolStripButton("刷新") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var home = new ToolStripButton("主页") { DisplayStyle = ToolStripItemDisplayStyle.Text };

        _address = new TextBox { Width = 560 };
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (_view != IntPtr.Zero) LoadUrl(_address.Text);
        };
        var go = new ToolStripButton("转到") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        go.Click += (_, _) => { if (_view != IntPtr.Zero) LoadUrl(_address.Text); };
        reload.Click += (_, _) => { if (_view != IntPtr.Zero) LoadUrl(_url); };
        home.Click += (_, _) => { if (_view != IntPtr.Zero) LoadUrl(_url); };

        toolbar.Items.AddRange([
            back, forward, reload, home,
            new ToolStripSeparator(),
            new ToolStripLabel("地址"),
            new ToolStripControlHost(_address) { AutoSize = false, Width = 560 },
            go
        ]);

        var status = new StatusStrip { SizingGrip = true };
        _status = new ToolStripStatusLabel("正在加载 miniblink 内核…") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        status.Items.Add(_status);
        status.Items.Add(new ToolStripStatusLabel("miniblink · Apache-2.0") { BorderSides = ToolStripStatusLabelBorderSides.Left });

        _host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

        Controls.Add(_host);
        Controls.Add(toolbar);
        Controls.Add(status);
        _host.BringToFront();
        _host.Resize += (_, _) =>
        {
            if (_view != IntPtr.Zero) MoveView();
        };
    }

    private T Resolve<T>(string name) where T : Delegate
    {
        var address = GetProcAddress(_module, name);
        if (address == IntPtr.Zero)
        {
            throw new MissingMethodException($"miniblink 缺少导出：{name}");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private void InitializeEngine()
    {
        try
        {
            _module = LoadLibrary(_dll);
            if (_module == IntPtr.Zero)
            {
                _status.Text = $"加载内核失败：{_dll}（Win32 错误 {Marshal.GetLastWin32Error()}）";
                return;
            }

            var init = Resolve<MbInitDelegate>("mbInit");
            var create = Resolve<MbCreateWebWindowDelegate>("mbCreateWebWindow");
            var load = Resolve<MbLoadUrlDelegate>("mbLoadURL");
            var move = Resolve<MbMoveWindowDelegate>("mbMoveWindow");
            var show = Resolve<MbShowWindowDelegate>("mbShowWindow");
            var title = Resolve<MbSetTitleDelegate>("mbSetWindowTitle");
            var agent = Resolve<MbSetUserAgentDelegate>("mbSetUserAgent");
            var zoom = Resolve<MbSetZoomDelegate>("mbSetZoomFactor");

            init(IntPtr.Zero);

            _host.CreateControl();
            var width = Math.Max(900, _host.ClientSize.Width);
            var height = Math.Max(600, _host.ClientSize.Height);

            // 先试 CONTROL 嵌入；若内核不渲染（这类版本常见），退回 POPUP 自建窗口
            _view = create(MbWindowTypeControl, _host.Handle, 0, 0, width, height);
            if (_view == IntPtr.Zero) _view = create(MbWindowTypePopup, IntPtr.Zero, 80, 80, width, height);
            TiaMc.App.Services.LogService.Info($"miniblink 视图句柄: 0x{_view.ToInt64():X}", "浏览器内核");
            if (_view == IntPtr.Zero)
            {
                _status.Text = "mbCreateWebWindow 返回空句柄";
                return;
            }

            _viewMove = move;
            move(_view, 80, 80, width, height);
            show(_view, true);
            title(_view, "TIA-MC 启动器");
            agent(_view, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TiaMC-Web/miniblink");
            zoom(_view, 1.0f);
            var onReady = Resolve<MbOnReadyDelegate>("mbOnDocumentReady");
            _readyCallback = (_, _) => BeginInvoke(new Action(() =>
            {
                _status.Text = "文档已就绪：" + _address.Text;
                TiaMc.App.Services.LogService.Info($"miniblink 文档就绪: {_address.Text}", "浏览器内核");
            }));
            onReady(_view, Marshal.GetFunctionPointerForDelegate(_readyCallback), IntPtr.Zero);

            load(_view, _url);

            _address.Text = _url;
            _status.Text = "已用 miniblink（开源 Blink 内核）加载页面";
        }
        catch (Exception e)
        {
            _status.Text = "内核初始化失败：" + e.Message;
        }
    }

    private MbMoveWindowDelegate? _viewMove;
    private MbViewCallback? _readyCallback;

    private void MoveView()
    {
        if (_view == IntPtr.Zero || _viewMove is null) return;
        _viewMove(_view, 0, 0, Math.Max(200, _host.ClientSize.Width), Math.Max(200, _host.ClientSize.Height));
    }

    private void LoadUrl(string url)
    {
        try
        {
            var load = Resolve<MbLoadUrlDelegate>("mbLoadURL");
            load(_view, url);
            _address.Text = url;
        }
        catch (Exception e)
        {
            _status.Text = "加载失败：" + e.Message;
        }
    }

    private void Shutdown()
    {
        try
        {
            if (_module != IntPtr.Zero)
            {
                var uninit = GetProcAddress(_module, "mbUninit");
                if (uninit != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<MbRunDelegate>(uninit)();
            }
        }
        catch (Exception)
        {
            // 退出阶段的异常无需处理
        }
    }
}
