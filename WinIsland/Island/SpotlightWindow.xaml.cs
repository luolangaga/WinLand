using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics;
using WinIsland.Core;

namespace WinIsland.Island;

/// <summary>
/// 「超级展开」聚光卡的全屏覆盖窗：变暗遮罩 + 一张居中的大卡片。
/// 卡片内容与尺寸完全由插件决定，这里只负责「从主岛位置带倾角飞入居中 → 点卡片外 / Esc → 反向飞回」。
/// 与岛窗口完全解耦：岛窗口只在被遮挡期间把整块淡出，窗口几何一律不动。
/// </summary>
public sealed partial class SpotlightWindow : Window
{
    /// <summary>遮罩最终不透明度。元素自身保持不透明，动画只动合成层的 Opacity。</summary>
    private const float ScrimOpacity = 0.55f;
    private const int ScrimFadeInMs = 220;
    private const int ScrimFadeOutMs = 240;
    private const int CardFadeInMs = 170;
    private const int FlyInMs = 460;
    private const int FlyOutMs = 320;
    private const int HintDelayMs = 200;
    private const int HintFadeMs = 220;
    /// <summary>飞入倾角：绕 (0.18, 1, 0) 轴，带 Y 轴分量所以是真的 3D 倾斜而不是纯平面旋转。</summary>
    private const double EnterTilt = -9;
    /// <summary>收回倾角：与飞入反号，像卡片转回原来那一面。</summary>
    private const double ExitTilt = 8;
    private const double MinCardWidth = 240;
    private const double MinCardHeight = 180;
    /// <summary>卡片尺寸占工作区的上限（插件声明的尺寸会被夹到这个范围内）。</summary>
    private const double WorkAreaFill = 0.92;
    private const double HintGap = 14;

    private readonly IPluginLogger _log;
    private readonly nint _hwnd;
    private readonly OverlappedPresenter _presenter;

    private bool _shown;
    private bool _opening;
    private bool _closing;
    /// <summary>点遮罩可以收起：开场动画跑完之前一律为 false（此时卡片布局框与视觉位置还不一致）。</summary>
    private bool _armed;
    private bool _pendingClose;
    private bool _hotKeyInstalled;
    private PendingShow? _pendingShow;

    // 目标显示器与主岛起点（物理像素）
    private RectInt32 _outerBounds;
    private RectInt32 _workArea;
    private Win32.RECT _origin;
    private Size _originSizeDip;
    private Size _contentSize;
    private double _scale = 1;
    /// <summary>本次展示是"同一个会话内的替换"（遮罩已经在场，不重放淡入）。</summary>
    private bool _sessionActive;

    // 动画用的两组偏移/缩放：卡片布局位置（终点）与主岛位置（起点 = 收回终点）
    private Vector3 _layoutOffset;
    private Vector3 _originOffset;
    private float _startScaleX = 1f;
    private float _startScaleY = 1f;

    /// <summary>收起动画结束（窗口已隐藏、内容已卸下）后触发。</summary>
    internal event Action? Dismissed;

    private readonly record struct PendingShow(
        Win32.RECT Origin, Size OriginSize, IslandSpotlight Content, IslandStyleKind Style, bool Material);

    public SpotlightWindow(IPluginLogger log)
    {
        _log = log;
        InitializeComponent();

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
        Win32.MakeOverlayStyle(_hwnd);

        RootCanvas.Loaded += (_, _) =>
        {
            if (RootCanvas.XamlRoot is not { } root) return;
            root.Changed += (_, _) => OnDpiChanged();
        };

        Closed += (_, _) =>
        {
            if (_hotKeyInstalled)
            {
                Win32.UninstallEscapeHotKey(_hwnd);
                _hotKeyInstalled = false;
            }
        };
    }

    /// <summary>
    /// 展示聚光卡：窗口铺满主岛所在显示器，卡片按工作区居中，从主岛矩形飞入。
    /// 关闭动画进行中到来的请求会排队，等收完再开。
    /// </summary>
    internal void ShowOverlay(Win32.RECT origin, Size originSizeDip, IslandSpotlight content,
        IslandStyleKind style, bool materialApplied)
    {
        if (_closing)
        {
            _pendingShow = new PendingShow(origin, originSizeDip, content, style, materialApplied);
            return;
        }

        _origin = origin;
        _originSizeDip = originSizeDip;
        _contentSize = content.Size;
        _sessionActive = _shown;

        var display = ResolveDisplay(origin, out var outer);
        _outerBounds = outer;
        _workArea = display.WorkArea;

        Card.Background = IslandStyle.CreateSpotlightFill(style, materialApplied);
        Card.BorderBrush = IslandStyle.CreateSpotlightStroke(style);
        Card.CornerRadius = new CornerRadius(IslandStyle.ResolveSpotlightRadius(style));
        SpotlightHost.Content = content.Content;

        _presenter.IsAlwaysOnTop = false;
        _presenter.IsAlwaysOnTop = true;
        Win32.MakeOverlayStyle(_hwnd);
        AppWindow.MoveAndResize(_outerBounds);
        Win32.RemoveDwmBorder(_hwnd);
        Win32.ClearClientTransparent(_hwnd);
        AppWindow.Show(false);

        if (RootCanvas.XamlRoot == null)
        {
            // 首次显示时 XamlRoot 要等内容加载完成才对得上；此刻整窗都是 0 透明度，延后一拍无感
            DispatcherQueue.TryEnqueue(BeginShow);
            return;
        }

        BeginShow();
    }

    /// <summary>收起聚光卡：反向动画 → 隐藏窗口 → 卸下内容。开场动画未完成时排队。</summary>
    internal void CloseOverlay()
    {
        if (!_shown || _closing) return;
        if (_opening)
        {
            _pendingClose = true;
            return;
        }

        _closing = true;
        _armed = false;
        Scrim.IsHitTestVisible = false;

        var visual = ElementCompositionPreview.GetElementVisual(Card);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.55f, 1f));

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        AnimateVector(visual, "Offset", compositor, _originOffset, FlyOutMs, ease);
        AnimateVector(visual, "Scale", compositor, new Vector3(_startScaleX, _startScaleY, 1f), FlyOutMs, ease);
        AnimateScalar(visual, "RotationAngleInDegrees", compositor, (float)ExitTilt, FlyOutMs, ease);

        // 前 65% 保持不透明（让人看清"卡片在缩小飞回去"），最后一段淡出，落在岛体上时正好消失
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(FlyOutMs);
        fade.InsertKeyFrame(0.65f, 1f, ease);
        fade.InsertKeyFrame(1f, 0f, ease);
        visual.StartAnimation("Opacity", fade);

        AnimateScalar(ElementCompositionPreview.GetElementVisual(Scrim), "Opacity", compositor, 0f, ScrimFadeOutMs, ease);
        AnimateScalar(ElementCompositionPreview.GetElementVisual(Hint), "Opacity", compositor, 0f, HintFadeMs, ease);

        batch.End();
        batch.Completed += (_, _) => FinishClose();
    }

    /// <summary>不等动画立刻收场（应用退出/岛窗口关闭路径）。</summary>
    internal void ForceClose()
    {
        if (!_shown || _closing) return;
        FinishClose();
    }

    private void BeginShow()
    {
        ApplyLayout();
        ApplyStartTransform();
        _log.Debug(
            $"聚光卡入场：窗口 {_outerBounds.X},{_outerBounds.Y} {_outerBounds.Width}x{_outerBounds.Height}，" +
            $"工作区 {_workArea.X},{_workArea.Y} {_workArea.Width}x{_workArea.Height}，缩放 {_scale:F2}，" +
            $"卡片 {Card.Width:F0}x{Card.Height:F0} 布局 ({_layoutOffset.X:F0},{_layoutOffset.Y:F0})，" +
            $"起点 ({_originOffset.X:F0},{_originOffset.Y:F0}) 缩放 {_startScaleX:F2}x{_startScaleY:F2}。");
        StartEnterAnimation();
    }

    /// <summary>按目标显示器工作区摆放遮罩、卡片与提示（全部 DIP）。</summary>
    private void ApplyLayout()
    {
        // 缩放比必须问 Win32：窗口刚被搬到目标显示器时，XAML 的 XamlRoot.RasterizationScale
        // 还是旧值（DPI 变更是异步的），按它算会把卡片放大/摆偏。
        uint dpi = Win32.GetDpiForWindow(_hwnd);
        _scale = dpi > 0 ? dpi / 96.0 : RootCanvas.XamlRoot?.RasterizationScale ?? 1.0;
        double winW = _outerBounds.Width / _scale;
        double winH = _outerBounds.Height / _scale;

        Scrim.Width = winW;
        Scrim.Height = winH;
        Canvas.SetLeft(Scrim, 0);
        Canvas.SetTop(Scrim, 0);

        double workW = _workArea.Width / _scale;
        double workH = _workArea.Height / _scale;
        double maxW = Math.Max(MinCardWidth, workW * WorkAreaFill);
        double maxH = Math.Max(MinCardHeight, workH * WorkAreaFill);
        double cardW = Math.Clamp(_contentSize.Width, MinCardWidth, maxW);
        double cardH = Math.Clamp(_contentSize.Height, MinCardHeight, maxH);

        Card.Width = cardW;
        Card.Height = cardH;

        double cardLeft = (_workArea.X - _outerBounds.X) / _scale + (workW - cardW) / 2;
        double cardTop = (_workArea.Y - _outerBounds.Y) / _scale + (workH - cardH) / 2;
        Canvas.SetLeft(Card, cardLeft);
        Canvas.SetTop(Card, cardTop);

        // 卡片是 RootCanvas 的直接子元素，布局偏移就是 Canvas 坐标 —— 不能去读 visual.Offset：
        // 首次显示时布局还没跑，那里读到的 (0,0) 会把卡片飞到窗口左上角。
        _layoutOffset = new Vector3((float)cardLeft, (float)cardTop, 0);

        Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var hintSize = Hint.DesiredSize;
        double hintLeft = cardLeft + (cardW - hintSize.Width) / 2;
        double hintTop = cardTop + cardH + HintGap;
        Canvas.SetLeft(Hint, hintLeft);
        Canvas.SetTop(Hint, Math.Min(hintTop, (_workArea.Y - _outerBounds.Y) / _scale + workH - hintSize.Height - 4));

        RootCanvas.UpdateLayout();
    }

    /// <summary>把卡片摆成"主岛的样子"：同中心、同尺寸比例、带倾角、透明。</summary>
    private void ApplyStartTransform()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Card);
        double cardW = Card.ActualWidth > 0 ? Card.ActualWidth : Card.Width;
        double cardH = Card.ActualHeight > 0 ? Card.ActualHeight : Card.Height;
        double cardLeft = Canvas.GetLeft(Card);
        double cardTop = Canvas.GetTop(Card);

        visual.CenterPoint = new Vector3((float)(cardW / 2), (float)(cardH / 2), 0);

        double originW = _originSizeDip.Width > 0 ? _originSizeDip.Width : (_origin.Right - _origin.Left) / _scale;
        double originH = _originSizeDip.Height > 0 ? _originSizeDip.Height : (_origin.Bottom - _origin.Top) / _scale;
        _startScaleX = (float)Math.Clamp(originW / cardW, 0.18, 1.0);
        _startScaleY = (float)Math.Clamp(originH / cardH, 0.10, 1.0);

        double originCenterX = (_origin.Left - _outerBounds.X) / _scale + originW / 2;
        double originCenterY = (_origin.Top - _outerBounds.Y) / _scale + originH / 2;
        _originOffset = _layoutOffset + new Vector3(
            (float)(originCenterX - (cardLeft + cardW / 2)),
            (float)(originCenterY - (cardTop + cardH / 2)),
            0);

        visual.Offset = _originOffset;
        visual.Scale = new Vector3(_startScaleX, _startScaleY, 1f);
        visual.RotationAxis = Vector3.Normalize(new Vector3(0.18f, 1f, 0f));
        visual.RotationAngleInDegrees = (float)EnterTilt;
        visual.Opacity = 0f;

        // 同会话内的替换：遮罩与提示已经在场，保持原样，只让新卡片重新飞一次
        if (!_sessionActive)
        {
            ElementCompositionPreview.GetElementVisual(Scrim).Opacity = 0f;
            ElementCompositionPreview.GetElementVisual(Hint).Opacity = 0f;
        }
    }

    private void StartEnterAnimation()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Card);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.18f), new Vector2(0.36f, 1f));
        var easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));

        _shown = true;
        _opening = true;
        _armed = false;
        Scrim.IsHitTestVisible = false;

        if (!_hotKeyInstalled)
        {
            _hotKeyInstalled = Win32.InstallEscapeHotKey(_hwnd, CloseOverlay);
            if (!_hotKeyInstalled)
            {
                _log.Debug("聚光卡没能注册 Esc 热键（多半被别的程序占用了），点击卡片外区域仍可收起。");
            }
        }

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        AnimateVector(visual, "Offset", compositor, _layoutOffset, FlyInMs, ease);
        AnimateVector(visual, "Scale", compositor, Vector3.One, FlyInMs, ease);
        AnimateScalar(visual, "RotationAngleInDegrees", compositor, 0f, FlyInMs, ease);
        AnimateScalar(visual, "Opacity", compositor, 1f, CardFadeInMs, easeIn);

        if (_sessionActive)
        {
            ElementCompositionPreview.GetElementVisual(Scrim).Opacity = ScrimOpacity;
            ElementCompositionPreview.GetElementVisual(Hint).Opacity = 1f;
        }
        else
        {
            AnimateScalar(
                ElementCompositionPreview.GetElementVisual(Scrim), "Opacity", compositor, ScrimOpacity, ScrimFadeInMs, easeIn);

            var hint = compositor.CreateScalarKeyFrameAnimation();
            hint.DelayTime = TimeSpan.FromMilliseconds(HintDelayMs);
            hint.Duration = TimeSpan.FromMilliseconds(HintFadeMs);
            hint.InsertKeyFrame(1f, 1f, easeIn);
            ElementCompositionPreview.GetElementVisual(Hint).StartAnimation("Opacity", hint);
        }

        batch.End();
        batch.Completed += (_, _) =>
        {
            if (!_shown) return;
            _opening = false;
            _armed = true;
            Scrim.IsHitTestVisible = true;
            _log.Debug($"聚光卡入场完成：offset=({visual.Offset.X:F0},{visual.Offset.Y:F0}) scale={visual.Scale.X:F2} opacity={visual.Opacity:F2}。");
            if (_pendingClose)
            {
                _pendingClose = false;
                CloseOverlay();
            }
        };
    }

    private void FinishClose()
    {
        AppWindow.Hide();

        // 先藏窗口再复位视觉状态，下一次打开才不会闪一帧"已经居中"的卡片
        ResetVisuals();
        SpotlightHost.Content = null;      // 触发插件视图的 Unloaded：停表、收尾都在那边做
        if (_hotKeyInstalled)
        {
            Win32.UninstallEscapeHotKey(_hwnd);
            _hotKeyInstalled = false;
        }

        _shown = false;
        _closing = false;
        Dismissed?.Invoke();

        if (_pendingShow is { } pending)
        {
            _pendingShow = null;
            ShowOverlay(pending.Origin, pending.OriginSize, pending.Content, pending.Style, pending.Material);
        }
    }

    private void ResetVisuals()
    {
        var card = ElementCompositionPreview.GetElementVisual(Card);
        card.StopAnimation("Offset");
        card.StopAnimation("Scale");
        card.StopAnimation("RotationAngleInDegrees");
        card.StopAnimation("Opacity");
        card.Offset = _layoutOffset;
        card.Scale = Vector3.One;
        card.RotationAngleInDegrees = 0f;
        card.Opacity = 1f;

        var scrim = ElementCompositionPreview.GetElementVisual(Scrim);
        scrim.StopAnimation("Opacity");
        scrim.Opacity = 0f;

        var hint = ElementCompositionPreview.GetElementVisual(Hint);
        hint.StopAnimation("Opacity");
        hint.Opacity = 0f;
    }

    /// <summary>点遮罩 = 点卡片外区域。卡片在遮罩上层，点卡片根本不会走到这里。</summary>
    private void Scrim_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_armed) return;
        CloseOverlay();
    }

    private void OnDpiChanged()
    {
        if (!_shown || _opening || _closing) return;

        // XAML 里的缩放比是滞后生效的，只有它追上真实 DPI 才需要重排
        double scale = RootCanvas.XamlRoot?.RasterizationScale ?? _scale;
        if (Math.Abs(scale - _scale) < 0.001) return;

        ApplyLayout();
        ResetVisuals();
    }

    /// <summary>主岛所在的显示器；矩形无效（还没定位好）时退回主显示器。</summary>
    private static DisplayArea ResolveDisplay(Win32.RECT origin, out RectInt32 outerBounds)
    {
        int width = origin.Right - origin.Left;
        int height = origin.Bottom - origin.Top;
        var area = width > 0 && height > 0
            ? DisplayArea.GetFromRect(new RectInt32(origin.Left, origin.Top, width, height), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromRect(new RectInt32(0, 0, 1, 1), DisplayAreaFallback.Primary);
        outerBounds = area.OuterBounds;
        return area;
    }

    private static void AnimateVector(Visual visual, string property, Compositor compositor,
        Vector3 to, int milliseconds, CompositionEasingFunction ease)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.InsertKeyFrame(1f, to, ease);
        visual.StartAnimation(property, animation);
    }

    private static void AnimateScalar(Visual visual, string property, Compositor compositor,
        float to, int milliseconds, CompositionEasingFunction ease)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.InsertKeyFrame(1f, to, ease);
        visual.StartAnimation(property, animation);
    }
}
