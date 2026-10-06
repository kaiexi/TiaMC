using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace TiaMc.App.Controls;

/// <summary>
/// Minecraft 角色 3D 材质预览（皮肤站那种可以转、可以缩放的模型）。
///
/// 只用 WPF 自带的 Viewport3D / MeshGeometry3D，不引任何第三方依赖：
///   * 按官方皮肤布局给头(8³)、帽层、身体(8×12×4)、双臂(4×12×4)、双腿(4×12×4)贴图；
///   * 鼠标左键拖动旋转、滚轮缩放、双击复位；可开启自动旋转；
///   * 皮肤来源由外部通过 <see cref="Skin"/> 传进来（正版 URL / 皮肤站 / 本地文件都行）。
/// </summary>
public sealed class SkinPreview3D : UserControl
{
    // 官方 64x64 皮肤布局（像素矩形：x, y, w, h）
    private static readonly Int32Rect HeadFront = new(8, 8, 8, 8);
    private static readonly Int32Rect HeadBack = new(24, 8, 8, 8);
    private static readonly Int32Rect HeadRight = new(0, 8, 8, 8);
    private static readonly Int32Rect HeadLeft = new(16, 8, 8, 8);
    private static readonly Int32Rect HeadTop = new(8, 0, 8, 8);
    private static readonly Int32Rect HeadBottom = new(16, 0, 8, 8);

    private static readonly Int32Rect BodyFront = new(20, 20, 8, 12);
    private static readonly Int32Rect BodyBack = new(32, 20, 8, 12);
    private static readonly Int32Rect BodyRight = new(16, 20, 4, 12);
    private static readonly Int32Rect BodyLeft = new(28, 20, 4, 12);
    private static readonly Int32Rect BodyTop = new(20, 16, 8, 4);
    private static readonly Int32Rect BodyBottom = new(28, 16, 8, 4);

    private static readonly Int32Rect ArmFront = new(44, 20, 4, 12);
    private static readonly Int32Rect ArmBack = new(52, 20, 4, 12);
    private static readonly Int32Rect ArmRight = new(40, 20, 4, 12);
    private static readonly Int32Rect ArmLeft = new(48, 20, 4, 12);
    private static readonly Int32Rect ArmTop = new(44, 16, 4, 4);
    private static readonly Int32Rect ArmBottom = new(48, 16, 4, 4);

    private static readonly Int32Rect LegFront = new(4, 20, 4, 12);
    private static readonly Int32Rect LegBack = new(12, 20, 4, 12);
    private static readonly Int32Rect LegRight = new(0, 20, 4, 12);
    private static readonly Int32Rect LegLeft = new(8, 20, 4, 12);
    private static readonly Int32Rect LegTop = new(4, 16, 4, 4);
    private static readonly Int32Rect LegBottom = new(8, 16, 4, 4);

    private readonly Viewport3D _viewport = new();
    private readonly ModelVisual3D _model = new();
    private readonly PerspectiveCamera _camera = new()
    {
        FieldOfView = 45,
        UpDirection = new Vector3D(0, 1, 0)
    };

    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), 25);
    private readonly AxisAngleRotation3D _pitch = new(new Vector3D(1, 0, 0), 8);
    private readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(40) };

    private Point _dragStart;
    private double _distance = 62;
    private double _centerY;          // 模型（含位移后）中心的 y
    private double _modelHeight = 32; // 模型高度，用来定距离
    private bool _dragging;
    private bool _autoSpin = true;

    public SkinPreview3D()
    {
        var group = new Transform3DGroup();
        group.Children.Add(new RotateTransform3D(_yaw));
        group.Children.Add(new RotateTransform3D(_pitch));
        group.Children.Add(new TranslateTransform3D(0, -16, 0));   // 让模型绕自身中心旋转
        _model.Transform = group;

        _viewport.Camera = _camera;
        _viewport.Children.Add(new ModelVisual3D { Content = Lights() });
        _viewport.Children.Add(_model);
        _viewport.ClipToBounds = true;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x25, 0x33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x60)),
            BorderThickness = new Thickness(1),
            Child = _viewport,
            Cursor = Cursors.Hand
        };

        _spin.Tick += (_, _) =>
        {
            if (_autoSpin && !_dragging) _yaw.Angle += 0.6;
        };
        Loaded += (_, _) =>
        {
            Reset();        // 复位视角：以前初始俯角是负的，相机会从下往上看，模型跑到画面外
            _spin.Start();
        };
        Unloaded += (_, _) => _spin.Stop();

        MouseLeftButtonDown += (_, e) => { _dragging = true; _dragStart = e.GetPosition(this); CaptureMouse(); };
        MouseLeftButtonUp += (_, _) => { _dragging = false; ReleaseMouseCapture(); };
        MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            var now = e.GetPosition(this);
            _yaw.Angle += (now.X - _dragStart.X) * 0.6;
            _pitch.Angle = Math.Clamp(_pitch.Angle - (now.Y - _dragStart.Y) * 0.4, -80, 80);
            _dragStart = now;
        };
        MouseWheel += (_, e) =>
        {
            _distance = Math.Clamp(_distance - e.Delta * 0.02, 22, 120);
            UpdateCamera();
        };
        MouseDoubleClick += (_, _) => Reset();
        SizeChanged += (_, _) => UpdateCamera();
    }

    /// <summary>整张皮肤贴图（64×64 或 64×32 的 BitmapSource）。</summary>
    public static readonly DependencyProperty SkinProperty = DependencyProperty.Register(
        nameof(Skin), typeof(ImageSource), typeof(SkinPreview3D),
        new PropertyMetadata(null, (d, _) => ((SkinPreview3D)d).Rebuild()));

    public ImageSource? Skin
    {
        get => (ImageSource?)GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    /// <summary>是否自动旋转。</summary>
    public static readonly DependencyProperty AutoSpinProperty = DependencyProperty.Register(
        nameof(AutoSpin), typeof(bool), typeof(SkinPreview3D),
        new PropertyMetadata(true, (d, e) => ((SkinPreview3D)d)._autoSpin = (bool)e.NewValue));

    public bool AutoSpin
    {
        get => (bool)GetValue(AutoSpinProperty);
        set => SetValue(AutoSpinProperty, value);
    }

    /// <summary>诊断信息（用于排查"框里空白"）。</summary>
    public string Diagnostics
    {
        get
        {
            var content = _model.Content as Model3DGroup;
            var bounds = content?.Bounds ?? Rect3D.Empty;
            var count = content?.Children.Count ?? 0;
            return $"贴图={(Skin is BitmapSource b ? $"{b.PixelWidth}x{b.PixelHeight}" : "null")} " +
                   $"模型子项={count} 包围盒=({bounds.X:0.#},{bounds.Y:0.#},{bounds.Z:0.#} {bounds.SizeX:0.#}x{bounds.SizeY:0.#}x{bounds.SizeZ:0.#}) " +
                   $"中心Y={_centerY:0.#} 距离={_distance:0.#} 相机=({_camera.Position.X:0.#},{_camera.Position.Y:0.#},{_camera.Position.Z:0.#}) " +
                   $"视线=({_camera.LookDirection.X:0.#},{_camera.LookDirection.Y:0.#},{_camera.LookDirection.Z:0.#}) 视角={(int)_viewport.ActualWidth}x{(int)_viewport.ActualHeight}";
        }
    }

    /// <summary>
    /// 视角预设（和皮肤站的 3D 预览一致）：正面 / 右面 / 背面 / 左面 / 头部特写 / 全身。
    /// 同时把俯仰角与距离一起设好，所以点一下就到位。
    /// </summary>
    public void SetView(string preset)
    {
        var (yaw, pitch, distance) = preset switch
        {
            "右面" => (90.0, 0.0, 1.85),
            "背面" => (180.0, 0.0, 1.85),
            "左面" => (270.0, 0.0, 1.85),
            "头部" => (0.0, 6.0, 0.62),      // 拉近看头
            "全身" => (25.0, 6.0, 2.30),     // 拉远看全身
            _ => (0.0, 6.0, 1.85)            // 正面
        };

        _yaw.Angle = yaw;
        _pitch.Angle = pitch;
        _distance = _modelHeight * distance;
        UpdateCamera();
    }

    public void Reset()
    {
        _yaw.Angle = 25;
        _pitch.Angle = 6;
        _distance = _modelHeight * 1.85;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        // 模型被 Translate(0,-16,0) 移到原点附近，包围盒也随之移动；
        // 这里直接按包围盒算中心与距离，避免"相机看向别处 → 框里空白"。
        var radians = _pitch.Angle * Math.PI / 180;
        var depth = _distance * Math.Cos(radians);
        var height = _distance * Math.Sin(radians);

        _camera.Position = new Point3D(0, _centerY + height, depth);
        _camera.LookDirection = new Vector3D(0, -height, -depth);
        _camera.NearPlaneDistance = 0.5;
        _camera.FarPlaneDistance = 500;
    }

    private static Model3D Lights()
    {
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x9A, 0x9A, 0x9A)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0xFF, 0xFF, 0xFF), new Vector3D(-1, -1.6, -1.2)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0x60, 0x60, 0x70), new Vector3D(1, -0.6, 1)));
        return group;
    }

    private void Rebuild()
    {
        try
        {
            RebuildCore();
        }
        catch (Exception)
        {
            // 任何贴图格式问题都不应该让控件崩掉或留下空白（以前就是这样：越界异常被吞，框里没东西）
            try { _model.Content = null; } catch (Exception) { }
        }
    }

    private void RebuildCore()
    {
        if (Skin is not BitmapSource skin || skin.PixelWidth < 64 || skin.PixelHeight < 32)
        {
            _model.Content = null;
            return;
        }

        // 64×32 旧皮肤先转成 64×64，否则下面的贴图坐标会越界
        skin = Services.SkinTextureService.Normalize(skin);

        var group = new Model3DGroup();
        var material = new DiffuseMaterial(new ImageBrush(skin) { Stretch = Stretch.Fill });

        // 头（8³）放在 y=24..32，身体在 y=12..24，腿 y=0..12
        group.Children.Add(Cuboid(material, 8, 8, 8, 0, 28, 0,
            HeadFront, HeadBack, HeadRight, HeadLeft, HeadTop, HeadBottom));

        // 帽子层（略大一点，保证看得见）
        var hat = new DiffuseMaterial(new ImageBrush(skin) { Stretch = Stretch.Fill, Opacity = skin.PixelHeight >= 64 ? 1.0 : 0.0 });
        group.Children.Add(Cuboid(hat, 8.6, 8.6, 8.6, 0, 28, 0,
            new Int32Rect(40, 8, 8, 8), new Int32Rect(56, 8, 8, 8), new Int32Rect(32, 8, 8, 8),
            new Int32Rect(48, 8, 8, 8), new Int32Rect(40, 0, 8, 8), new Int32Rect(48, 0, 8, 8)));

        // 身体（8 宽 × 12 高 × 4 厚）
        group.Children.Add(Cuboid(material, 8, 12, 4, 0, 18, 0,
            BodyFront, BodyBack, BodyRight, BodyLeft, BodyTop, BodyBottom));

        // 右臂 / 左臂（4×12×4）
        group.Children.Add(Cuboid(material, 4, 12, 4, -6, 18, 0,
            ArmFront, ArmBack, ArmRight, ArmLeft, ArmTop, ArmBottom));
        group.Children.Add(Cuboid(material, 4, 12, 4, 6, 18, 0,
            new Int32Rect(36, 52, 4, 12), new Int32Rect(44, 52, 4, 12), new Int32Rect(32, 52, 4, 12),
            new Int32Rect(40, 52, 4, 12), new Int32Rect(36, 48, 4, 4), new Int32Rect(40, 48, 4, 4)));

        // 右腿 / 左腿（4×12×4，注意是 1.8 皮肤的左右腿布局）
        group.Children.Add(Cuboid(material, 4, 12, 4, -2, 6, 0,
            LegFront, LegBack, LegRight, LegLeft, LegTop, LegBottom));
        group.Children.Add(Cuboid(material, 4, 12, 4, 2, 6, 0,
            new Int32Rect(20, 52, 4, 12), new Int32Rect(28, 52, 4, 12), new Int32Rect(16, 52, 4, 12),
            new Int32Rect(24, 52, 4, 12), new Int32Rect(20, 48, 4, 4), new Int32Rect(24, 48, 4, 4)));

        _model.Content = group;

        // 按包围盒对准相机（包围盒是局部坐标，位移由 _model.Transform 施加）
        var bounds = group.Bounds;
        _modelHeight = Math.Max(8, bounds.SizeY);
        _centerY = bounds.Y + bounds.SizeY / 2 - 16;   // 减去 Translate(0,-16,0)
        _distance = _modelHeight * 1.85;
        UpdateCamera();
    }

    /// <summary>用六个面的贴图矩形拼一个长方体（中心在 x/y/z，尺寸为 w/h/d）。</summary>
    private static GeometryModel3D Cuboid(DiffuseMaterial material, double w, double h, double d,
        double cx, double cy, double cz,
        Int32Rect front, Int32Rect back, Int32Rect right, Int32Rect left, Int32Rect top, Int32Rect bottom,
        int textureWidth = 64, int textureHeight = 64)
    {
        var halfW = w / 2;
        var halfH = h / 2;
        var halfD = d / 2;

        var mesh = new MeshGeometry3D();
        var texture = new PointCollection();

        void Face(Point3D a, Point3D b, Point3D c, Point3D e, Int32Rect rect, bool flip = false)
        {
            var index = mesh.Positions.Count;
            mesh.Positions.Add(a);
            mesh.Positions.Add(b);
            mesh.Positions.Add(c);
            mesh.Positions.Add(e);

            var (u0, v0, u1, v1) = Uv(rect, textureWidth, textureHeight);
            if (flip)
            {
                texture.Add(new Point(u1, v0));
                texture.Add(new Point(u0, v0));
                texture.Add(new Point(u0, v1));
                texture.Add(new Point(u1, v1));
            }
            else
            {
                texture.Add(new Point(u0, v0));
                texture.Add(new Point(u1, v0));
                texture.Add(new Point(u1, v1));
                texture.Add(new Point(u0, v1));
            }

            mesh.TriangleIndices.Add(index);
            mesh.TriangleIndices.Add(index + 1);
            mesh.TriangleIndices.Add(index + 2);
            mesh.TriangleIndices.Add(index);
            mesh.TriangleIndices.Add(index + 2);
            mesh.TriangleIndices.Add(index + 3);
        }

        // WPF 坐标：+X 右、+Y 上、+Z 朝向观察者
        Face(new Point3D(cx - halfW, cy - halfH, cz + halfD), new Point3D(cx + halfW, cy - halfH, cz + halfD),
             new Point3D(cx + halfW, cy + halfH, cz + halfD), new Point3D(cx - halfW, cy + halfH, cz + halfD), front);
        Face(new Point3D(cx + halfW, cy - halfH, cz - halfD), new Point3D(cx - halfW, cy - halfH, cz - halfD),
             new Point3D(cx - halfW, cy + halfH, cz - halfD), new Point3D(cx + halfW, cy + halfH, cz - halfD), back);
        Face(new Point3D(cx + halfW, cy - halfH, cz + halfD), new Point3D(cx + halfW, cy - halfH, cz - halfD),
             new Point3D(cx + halfW, cy + halfH, cz - halfD), new Point3D(cx + halfW, cy + halfH, cz + halfD), right);
        Face(new Point3D(cx - halfW, cy - halfH, cz - halfD), new Point3D(cx - halfW, cy - halfH, cz + halfD),
             new Point3D(cx - halfW, cy + halfH, cz + halfD), new Point3D(cx - halfW, cy + halfH, cz - halfD), left);
        Face(new Point3D(cx - halfW, cy + halfH, cz + halfD), new Point3D(cx + halfW, cy + halfH, cz + halfD),
             new Point3D(cx + halfW, cy + halfH, cz - halfD), new Point3D(cx - halfW, cy + halfH, cz - halfD), top);
        Face(new Point3D(cx - halfW, cy - halfH, cz - halfD), new Point3D(cx + halfW, cy - halfH, cz - halfD),
             new Point3D(cx + halfW, cy - halfH, cz + halfD), new Point3D(cx - halfW, cy - halfH, cz + halfD), bottom);

        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    /// <summary>把像素矩形换算成 0..1 的贴图坐标（支持 64×64 与 64×32 皮肤）。</summary>
    private static (double U0, double V0, double U1, double V1) Uv(Int32Rect rect, int width, int height)
    {
        var u0 = (double)rect.X / width;
        var v0 = (double)rect.Y / height;
        var u1 = (double)(rect.X + rect.Width) / width;
        var v1 = (double)(rect.Y + rect.Height) / height;
        return (u0, v0, u1, v1);
    }

    /// <summary>从文件或字节加载一张皮肤贴图（冻结，可跨线程）。</summary>
    public static BitmapSource? LoadSkin(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
