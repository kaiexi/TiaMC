using System.Windows.Controls;

namespace TiaMc.App.Controls;

/// <summary>门户视图启动页（学 TIA Portal 的 Portal 视图）。放在独立的 UserControl 里，
/// 这样窗口那边只需要一行引用，增删都不会动到主窗口的根布局。</summary>
public partial class PortalView : UserControl
{
    public PortalView()
    {
        InitializeComponent();
    }
}