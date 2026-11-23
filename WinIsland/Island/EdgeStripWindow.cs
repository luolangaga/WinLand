using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace WinIsland.Island;

/// <summary>
/// 全屏应用占满屏幕时的「小色条」：一条贴在屏幕边缘的细色条，鼠标靠近由 IslandWindow 的看门狗判定并展开岛体。
/// 它只负责长相与落位（物理像素），不参与任何悬停/点击逻辑 —— 与岛体同款无边框置顶不抢焦点样式。
/// </summary>
internal sealed class EdgeStripWindow : Window
{
    /// <summary>色条的基准尺寸（DIP，缩放前）。</summary>
    public static readonly Windows.Foundation.Size BaseSize = new(72, 6);

    private readonly nint _hwnd;
    private readonly Border _bar;
    private readonly OverlappedPresenter _presenter;
    private bool _visible;
    private int _motionVersion;

    public EdgeStripWindow()
    {
        _bar = new Border
        {
            CornerRadius = new CornerRadius(2),
            // 深色条身 + 浅色细描边：在白底/黑底的全屏内容上都能看见
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        Content = new Grid { Background = new SolidColorBrush(Colors.Transparent), Children = { _bar } };

        AppWindow.IsShownInSwitchers = false;
        _presenter = OverlappedPresenter.Create();
        _presenter.SetBorderAndTitleBar(false, false);
        _presenter.IsAlwaysOnTop = true;
        _presenter.IsResizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsMaximizable = false;
        AppWindow.SetPresenter(_presenter);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Win32.InstallStyleGuard(_hwnd);
        SystemBackdrop = new TransparentBackdrop();
        Win32.MakeIslandStyle(_hwnd);
        AppWindow.Hide();
        Motion.Visual(_bar).Opacity = 0;
        Closed += (_, _) => ++_motionVersion;
    }

    /// <summary>落到物理像素矩形并显示。</summary>
    public void ShowAt(int x, int y, int w, int h)
    {
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, Math.Max(1, w), Math.Max(1, h)));
        Win32.RemoveDwmBorder(_hwnd);
        if (!_visible)
        {
            _visible = true;
            ++_motionVersion;
            AppWindow.Show(false);
            Win32.MakeIslandStyle(_hwnd);
            Motion.Scalar(Motion.Visual(_bar), "Opacity", 1, 180);
        }
    }

    public void HideStrip()
    {
        if (!_visible) return;
        _visible = false;
        int version = ++_motionVersion;
        var visual = Motion.Visual(_bar);
        var batch = visual.Compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        Motion.Scalar(visual, "Opacity", 0, 140);
        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            if (version == _motionVersion && !_visible) AppWindow.Hide();
        };
        batch.End();
    }

    public nint Handle => _hwnd;
}
