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
    private const double EnterTilt = -2.5;
    /// <summary>收回倾角：与飞入反号，像卡片转回原来那一面。</summary>
    private const double ExitTilt = 0;
    private const double MinCardWidth = 240;
    private const double MinCardHeight = 180;
    /// <summary>卡片尺寸占工作区的上限（插件声明的尺寸会被夹到这个范围内）。</summary>
    private const double WorkAreaFill = 0.92;
    private const double HintGap = 14;
    /// <summary>宿主外观键：与岛体共用（圆角比例、不透明度）。</summary>
    private const string RadiusKey = "island.radius";
    private const string OpacityKey = "island.opacity";

    private readonly ISettingsStore _settings;
    private readonly IPluginLogger _log;
    private readonly nint _hwnd;
    private readonly OverlappedPresenter _presenter;

    private bool _shown;
    private bool _opening;
    private bool _closing;
    /// <summary>点遮罩可以收起：开场动画跑完之前一律为 false（此时卡片布局框与视觉位置还不一致）。</summary>
    private bool _armed;
    private bool _hotKeyInstalled;
    /// <summary>输入会话（卡片内容里的文本框获得焦点时临时解除 NOACTIVATE）：见 <see cref="InputSession"/>。</summary>
    private readonly InputSession _input;
    private int _motionVersion;
    private bool _disposed;
    private bool _pendingShow;

    // 外观（与岛体同一套规则）：圆角、底衬、描边、明暗主题全都来自 IslandStyle，
    // 主题变化时就地重刷（见 OnSystemThemeChanged），所以卡片在屏幕上开着也会跟着换色
    private IslandStyleKind _style = IslandStyleKind.Apple;
    private bool _materialApplied;
    private bool _light = SystemTheme.IsLight;

    // 目标显示器与主岛起点（物理像素）
    private RectInt32 _outerBounds;
    private RectInt32 _workArea;
    private Win32.RECT _origin;
    private Size _originSizeDip;
    private double _originRadiusPixels;
    private readonly CompositionRoundedRectangleGeometry _cardGeometry;
    private readonly CompositionPropertySet _shape;
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

    internal event Action? Returning;
    internal event Action? Landing;

    internal Func<(Win32.RECT Rect, Size Size, double RadiusPixels)>? ReturnAnchor { get; set; }

    public SpotlightWindow(ISettingsStore settings, IPluginLogger log)
    {
        _settings = settings;
        _log = log;
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(Card, true);
        var pose = Motion.Visual(Card);
        pose.Properties.InsertVector3("Translation", Vector3.Zero);
        _shape = pose.Compositor.CreatePropertySet();
        _shape.InsertScalar("Width", 1);
        _shape.InsertScalar("Height", 1);
        _shape.InsertScalar("Radius", 28);
        _shape.InsertScalar("OriginRadius", 20);
        _shape.InsertScalar("StartScaleY", 0.1f);
        _cardGeometry = pose.Compositor.CreateRoundedRectangleGeometry();
        pose.Clip = pose.Compositor.CreateGeometricClip(_cardGeometry);
        using var sizeExpression = pose.Compositor.CreateExpressionAnimation("Vector2(shape.Width, shape.Height)");
        sizeExpression.SetReferenceParameter("shape", _shape);
        _cardGeometry.StartAnimation("Size", sizeExpression);
        // The screen-space radius must survive non-uniform Scale. The same live pose drives both axes.
        const string radius = "Min(Lerp(shape.Radius, shape.OriginRadius, Clamp((1 - pose.Scale.Y) / Max(0.001, 1 - shape.StartScaleY), 0, 1)), Min(shape.Width * pose.Scale.X, shape.Height * pose.Scale.Y) / 2)";
        using var cornerExpression = pose.Compositor.CreateExpressionAnimation(
            $"Vector2({radius} / Max(0.001, pose.Scale.X), {radius} / Max(0.001, pose.Scale.Y))");
        cornerExpression.SetReferenceParameter("shape", _shape);
        cornerExpression.SetReferenceParameter("pose", pose);
        _cardGeometry.StartAnimation("CornerRadius", cornerExpression);
        ElementCompositionPreview.SetIsTranslationEnabled(SpotlightHost, true);
        Motion.Visual(SpotlightHost).Properties.InsertVector3("Translation", Vector3.Zero);
        // Hide before first Show: the first loaded frame must not flash at the destination.
        Motion.Visual(Card).Opacity = 0;
        Motion.Visual(Scrim).Opacity = 0;
        Motion.Visual(Hint).Opacity = 0;

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

        // 输入会话：默认不激活，只有卡片内容里的文本框获得焦点/被点击时才临时放开（见 InputSession）。
        // 会话期间 Esc 全局热键必须撤掉 —— 它会吞掉本应送往输入法的 Esc（取消组词），
        // 那时改由 XAML 的 RootCanvas_KeyDown 接管。
        _input = new InputSession(this, _log);
        _input.Attach(RootCanvas);
        _input.Entered += OnInputEntered;
        _input.Exited += OnInputExited;

        RootCanvas.Loaded += (_, _) =>
        {
            if (RootCanvas.XamlRoot is not { } root) return;
            root.Changed += (_, _) => OnDpiChanged();
        };

        SystemTheme.Changed += OnSystemThemeChanged;
        _settings.Changed += OnSettingChanged;
        Closed += (_, _) =>
        {
            _disposed = true;
            ++_motionVersion;
            SystemTheme.Changed -= OnSystemThemeChanged;
            _settings.Changed -= OnSettingChanged;
            _input.Dispose();
            if (_hotKeyInstalled)
            {
                Win32.UninstallEscapeHotKey(_hwnd);
                _hotKeyInstalled = false;
            }
        };
    }

    /// <summary>
    /// 进入输入会话：撤掉 Esc 全局热键。热键在系统层就把 Esc 拦走了，输入法/输入框根本收不到；
    /// 输入期间改由 <see cref="RootCanvas_KeyDown"/> 接管（组词中的第一次 Esc 会被 TSF 先吃掉）。
    /// </summary>
    private void OnInputEntered()
    {
        if (!_hotKeyInstalled) return;
        Win32.UninstallEscapeHotKey(_hwnd);
        _hotKeyInstalled = false;
    }

    /// <summary>输入会话结束（窗口失活但聚光卡还开着）：把 Esc 全局热键装回去。</summary>
    private void OnInputExited()
    {
        if (_closing || !_shown || _hotKeyInstalled) return;
        _hotKeyInstalled = Win32.InstallEscapeHotKey(_hwnd, CloseOverlay);
    }

    /// <summary>
    /// 聚光卡的外观：底衬、描边、圆角、明暗主题 —— 和岛体同一套 <see cref="IslandStyle"/> 规则。
    /// <c>RequestedTheme</c> 也在这里落到根节点上，插件的大卡片内容（<c>{ThemeResource ...}</c>）
    /// 因此跟着岛体一起明暗切换。
    /// </summary>
    private void ApplyChrome()
    {
        // 用户外观：圆角比例（island.radius）与不透明度（island.opacity），与岛体同一套设置。
        // 不透明度乘在底衬/描边画刷的 alpha 与内容宿主（SpotlightHost）上 —— Card 本身的 Opacity
        // 由飞入/飞回的 Composition 动画占用，绝不能在这里写。
        double radiusScale = Math.Clamp(_settings.Get(RadiusKey, 100.0) / 100.0, 0.0, 2.0);
        double opacity = Math.Clamp(_settings.Get(OpacityKey, 100.0) / 100.0, 0.05, 1.0);

        RootCanvas.RequestedTheme = IslandStyle.IsLightChrome(_style, _light) ? ElementTheme.Light : ElementTheme.Dark;
        Card.Background = IslandStyle.CreateSpotlightFill(_style, _materialApplied, _light, opacity);
        Card.BorderBrush = IslandStyle.CreateSpotlightStroke(_style, _light, opacity);
        double radius = IslandStyle.ResolveSpotlightRadius(_style, radiusScale);
        Card.CornerRadius = new CornerRadius(radius);
        _shape.InsertScalar("Radius", (float)radius);
        SpotlightHost.Opacity = opacity;
    }

    /// <summary>系统换主题：卡片开着就当场换色（没开着的话下次展示自然会取到新主题）。</summary>
    private void OnSystemThemeChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnSystemThemeChanged);
            return;
        }

        if (SystemTheme.IsLight == _light) return;

        _light = SystemTheme.IsLight;
        if (!_shown) return;
        ApplyChrome();
    }

    /// <summary>外观设置变化：卡片开着就当场重刷圆角/不透明度（没开着的话下次展示自然取到新值）。</summary>
    private void OnSettingChanged(string key)
    {
        if (key != RadiusKey && key != OpacityKey) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnSettingChanged(key));
            return;
        }

        if (!_shown) return;
        ApplyChrome();
    }

    /// <summary>
    /// 展示聚光卡：窗口铺满主岛所在显示器，卡片按工作区居中，从主岛矩形飞入。
    /// 替换和反向请求从合成器当前姿态续接，不排队。
    /// </summary>
    internal void ShowOverlay(Win32.RECT origin, Size originSizeDip, double originRadiusPixels, IslandSpotlight content,
        IslandStyleKind style, bool materialApplied)
    {
        if (_disposed) return;
        bool replacing = _shown;
        _closing = false;
        _input.End();
        // FLIP: record the previous layout, then preserve its rendered pose with StartingValue.
        double oldW = Card.Width;
        double oldH = Card.Height;
        var oldCenter = _layoutOffset + new Vector3((float)(oldW / 2), (float)(oldH / 2), 0);

        bool changingContent = !ReferenceEquals(SpotlightHost.Content, content.Content);
        SpotlightLeaving.Content = null;
        if (replacing && changingContent)
        {
            var outgoing = SpotlightHost.Content;
            SpotlightHost.Content = null;
            SpotlightLeaving.Width = oldW;
            SpotlightLeaving.Height = oldH;
            SpotlightLeaving.Content = outgoing;
            if (outgoing is UIElement oldView) Motion.Scalar(Motion.Visual(oldView), "Opacity", 0, 120);
        }

        _origin = origin;
        _originSizeDip = originSizeDip;
        _originRadiusPixels = originRadiusPixels;
        _contentSize = content.Size;
        _sessionActive = _shown;

        var display = ResolveDisplay(origin, out var outer);
        _outerBounds = outer;
        _workArea = new RectInt32(display.WorkX, display.WorkY, display.WorkWidth, display.WorkHeight);

        _style = style;
        _materialApplied = materialApplied;
        _light = SystemTheme.IsLight;
        ApplyChrome();
        SpotlightHost.Content = content.Content;
        if (replacing && changingContent) Motion.Reveal(content.Content, 220, 8);
        else if (!replacing)
        {
            var contentVisual = Motion.Visual(content.Content);
            contentVisual.StopAnimation("Opacity");
            contentVisual.Opacity = 1;
            ElementCompositionPreview.SetIsTranslationEnabled(content.Content, true);
            contentVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        }

        _presenter.IsAlwaysOnTop = false;
        _presenter.IsAlwaysOnTop = true;
        Win32.MakeOverlayStyle(_hwnd);
        AppWindow.MoveAndResize(_outerBounds);
        Win32.RemoveDwmBorder(_hwnd);
        Win32.ClearClientTransparent(_hwnd);
        AppWindow.Show(false);

        if (RootCanvas.XamlRoot == null)
        {
            ApplyLayout();
            UpdateOriginTransform();
            ApplyStartTransform();
            _shown = true;
            _pendingShow = true;
            int pendingVersion = ++_motionVersion;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (pendingVersion != _motionVersion || !_shown || _closing || _disposed) return;
                _pendingShow = false;
                BeginShow();
            });
            return;
        }

        if (replacing)
        {
            ApplyLayout();
            UpdateOriginTransform();
            var visual = Motion.Visual(Card);
            visual.CenterPoint = new Vector3((float)Card.Width / 2, (float)Card.Height / 2, 0);
            var newCenter = _layoutOffset + visual.CenterPoint;
            Motion.Vector(visual, "Translation", Vector3.Zero, 280,
                "this.StartingValue + adjustment", oldCenter - newCenter);
            Motion.Vector(visual, "Scale", Vector3.One, 280,
                "this.StartingValue * adjustment", new Vector3((float)(oldW / Card.Width), (float)(oldH / Card.Height), 1));
            StartEnterAnimation(replacing: true);
        }
        else BeginShow();
    }

    /// <summary>收起聚光卡：从当前姿态反向动画 → 隐藏窗口 → 卸下内容。</summary>
    internal void CloseOverlay()
    {
        if (!_shown || _closing) return;
        if (_pendingShow)
        {
            _pendingShow = false;
            ++_motionVersion;
            _closing = true;
            Returning?.Invoke();
            FinishClose();
            return;
        }
        if (ReturnAnchor?.Invoke() is { } anchor)
        {
            _origin = anchor.Rect;
            _originSizeDip = anchor.Size;
            _originRadiusPixels = anchor.RadiusPixels;
            UpdateOriginTransform();
        }
        Returning?.Invoke();
        int version = ++_motionVersion;
        _opening = false;
        _closing = true;
        _armed = false;
        Scrim.IsHitTestVisible = false;

        var visual = ElementCompositionPreview.GetElementVisual(Card);
        var compositor = visual.Compositor;
        var ease = Motion.Ease(compositor);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        Motion.Vector(visual, "Translation", _originOffset, FlyOutMs);
        AnimateVector(visual, "Scale", compositor, new Vector3(_startScaleX, _startScaleY, 1f), FlyOutMs, ease);
        AnimateScalar(visual, "RotationAngleInDegrees", compositor, (float)ExitTilt, FlyOutMs, ease);

        // Keep the body painted all the way to the landing. Fade the enlarged content only.
        // The main HWND replaces this exact rounded silhouette before this overlay is hidden.
        AnimateScalar(visual, "Opacity", compositor, 1f, FlyOutMs, ease);
        using var contentFade = compositor.CreateScalarKeyFrameAnimation();
        contentFade.Duration = Motion.Duration(FlyOutMs);
        contentFade.InsertExpressionKeyFrame(0, "this.StartingValue");
        contentFade.InsertExpressionKeyFrame(0.5f, "this.StartingValue");
        contentFade.InsertKeyFrame(1, 0, ease);
        Motion.Visual(SpotlightHost).StartAnimation("Opacity", contentFade);

        AnimateScalar(ElementCompositionPreview.GetElementVisual(Scrim), "Opacity", compositor, 0f, ScrimFadeOutMs, ease);
        AnimateScalar(ElementCompositionPreview.GetElementVisual(Hint), "Opacity", compositor, 0f, HintFadeMs, ease);

        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            if (version != _motionVersion || _disposed) return;
            FinishClose();
        };
        batch.End();
    }

    /// <summary>不等动画立刻收场（应用退出/岛窗口关闭路径）。</summary>
    internal void ForceClose()
    {
        ++_motionVersion;
        _closing = true;
        if (_shown) FinishClose();
    }

    private void BeginShow()
    {
        ApplyLayout();
        UpdateOriginTransform();
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
        _shape.InsertScalar("Width", (float)cardW);
        _shape.InsertScalar("Height", (float)cardH);

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

        // Canvas coordinates are sufficient; do not force a synchronous layout of plugin content.
    }

    /// <summary>把卡片摆成"主岛的样子"：同中心、同尺寸比例、带倾角、透明。</summary>
    private void UpdateOriginTransform()
    {
        // Physical origin rect already includes the user's island scale. Use target monitor DPI.
        double originW = Math.Max(1, (_origin.Right - _origin.Left) / _scale);
        double originH = Math.Max(1, (_origin.Bottom - _origin.Top) / _scale);
        _startScaleX = (float)Math.Clamp(originW / Card.Width, 0.01, 1);
        _startScaleY = (float)Math.Clamp(originH / Card.Height, 0.01, 1);
        _shape.InsertScalar("StartScaleY", _startScaleY);
        _shape.InsertScalar("OriginRadius", (float)(_originRadiusPixels / _scale));
        _originOffset = new Vector3(
            (float)((_origin.Left - _outerBounds.X) / _scale + originW / 2 - (_layoutOffset.X + Card.Width / 2)),
            (float)((_origin.Top - _outerBounds.Y) / _scale + originH / 2 - (_layoutOffset.Y + Card.Height / 2)), 0);
    }

    private void ApplyStartTransform()
    {
        var visual = Motion.Visual(Card);
        visual.CenterPoint = new Vector3((float)Card.Width / 2, (float)Card.Height / 2, 0);
        visual.Properties.InsertVector3("Translation", _originOffset);
        visual.Scale = new Vector3(_startScaleX, _startScaleY, 1);
        visual.RotationAxis = Vector3.Normalize(new Vector3(0.18f, 1, 0));
        visual.RotationAngleInDegrees = _style == IslandStyleKind.Fluent ? 0 : (float)EnterTilt;
        visual.Opacity = 0;
        Motion.Visual(Scrim).Opacity = 0;
        Motion.Visual(Hint).Opacity = 0;
    }

    private void StartEnterAnimation(bool replacing = false)
    {
        var visual = ElementCompositionPreview.GetElementVisual(Card);
        var compositor = visual.Compositor;
        var ease = Motion.Ease(compositor);
        int version = ++_motionVersion;
        var easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));

        Motion.Scalar(Motion.Visual(SpotlightHost), "Opacity", 1, 180);
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

        if (!replacing)
        {
            Motion.Vector(visual, "Translation", Vector3.Zero, FlyInMs);
            AnimateVector(visual, "Scale", compositor, Vector3.One, FlyInMs, ease);
        }
        AnimateScalar(visual, "RotationAngleInDegrees", compositor, 0f, FlyInMs, ease);
        AnimateScalar(visual, "Opacity", compositor, 1f, CardFadeInMs, easeIn);

        if (_sessionActive)
        {
            AnimateScalar(Motion.Visual(Scrim), "Opacity", compositor, ScrimOpacity, ScrimFadeInMs, ease);
            AnimateScalar(Motion.Visual(Hint), "Opacity", compositor, 1, HintFadeMs, ease);
        }
        else
        {
            AnimateScalar(
                ElementCompositionPreview.GetElementVisual(Scrim), "Opacity", compositor, ScrimOpacity, ScrimFadeInMs, easeIn);

            var hint = compositor.CreateScalarKeyFrameAnimation();
            hint.DelayTime = Motion.Enabled ? TimeSpan.FromMilliseconds(HintDelayMs) : TimeSpan.Zero;
            hint.Duration = Motion.Duration(HintFadeMs);
            hint.InsertKeyFrame(1f, 1f, easeIn);
            ElementCompositionPreview.GetElementVisual(Hint).StartAnimation("Opacity", hint);
        }

        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            if (version != _motionVersion || !_shown || _disposed) return;
            SpotlightLeaving.Content = null;
            _opening = false;
            _armed = true;
            Scrim.IsHitTestVisible = true;
            _log.Debug($"聚光卡入场完成：offset=({visual.Offset.X:F0},{visual.Offset.Y:F0}) scale={visual.Scale.X:F2} opacity={visual.Opacity:F2}。");
        };
        batch.End();
    }

    private void FinishClose()
    {
        // 先结束输入会话（_closing 为 true，Exited 不会把刚撤下的 Esc 热键又装回去），
        // 再藏窗口 —— 随后的失活就是老样式下的正常路径了
        _input.End();
        Landing?.Invoke();
        AppWindow.Hide();

        // 先藏窗口再复位视觉状态，下一次打开才不会闪一帧"已经居中"的卡片
        ResetVisuals();
        SpotlightLeaving.Content = null;
        SpotlightHost.Content = null;      // 触发插件视图的 Unloaded：停表、收尾都在那边做
        if (_hotKeyInstalled)
        {
            Win32.UninstallEscapeHotKey(_hwnd);
            _hotKeyInstalled = false;
        }

        _shown = false;
        _pendingShow = false;
        _closing = false;
        _opening = false;
        Dismissed?.Invoke();

    }

    private void ResetVisuals()
    {
        var card = ElementCompositionPreview.GetElementVisual(Card);
        card.StopAnimation("Translation");
        card.StopAnimation("Scale");
        card.StopAnimation("RotationAngleInDegrees");
        card.StopAnimation("Opacity");
        card.Properties.InsertVector3("Translation", Vector3.Zero);
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

    /// <summary>
    /// 输入会话期间的 Esc 通道：会话里 Esc 全局热键已被撤掉（否则它会吞掉送往输入法的 Esc，
    /// 用户没法取消组词）。此时窗口是激活的，按键正常走 XAML；组词中的第一次 Esc 会被 TSF
    /// 先吃掉（取消组词），再按才到这里关卡。常态下走全局热键，这里根本收不到 Esc。
    /// </summary>
    private void RootCanvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || !_input.IsActive) return;
        e.Handled = true;
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

    /// <summary>
    /// 聚光卡铺满的显示器：**主岛所在的那块**（飞入起点就是主岛矩形，所以卡片必须开在同一块屏上，
    /// 否则会从另一块屏飞过来）。
    ///
    /// 按主岛矩形与各显示器外框的重叠面积挑（<see cref="DisplayResolver.MonitorOf(Win32.RECT)"/>）——
    /// 岛是真的在那块屏上，这是"岛所在屏"最可靠的判据；矩形无效（还没定位好）时退回主显示器。
    /// 不能用 <c>DisplayAreaFallback.Nearest</c> 反推：显示器排列有重叠、或岛刚好压在两块屏交界处时会挑错屏。
    /// </summary>
    private static MonitorInfo ResolveDisplay(Win32.RECT origin, out RectInt32 outerBounds)
    {
        var monitors = DisplayResolver.List();
        int width = origin.Right - origin.Left;
        int height = origin.Bottom - origin.Top;
        var monitor = (width > 0 && height > 0
                ? MonitorLayout.BestFit(monitors, origin.Left, origin.Top, width, height)
                : null)
            ?? MonitorLayout.Primary(monitors)
            ?? new MonitorInfo(string.Empty, true, 0, 0, 1, 1, 0, 0, 1, 1);

        outerBounds = new RectInt32(monitor.X, monitor.Y, monitor.Width, monitor.Height);
        return monitor;
    }

    private static void AnimateVector(Visual visual, string property, Compositor compositor,
        Vector3 to, int milliseconds, CompositionEasingFunction ease)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = Motion.Duration(milliseconds);
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1f, to, ease);
        visual.StartAnimation(property, animation);
    }

    private static void AnimateScalar(Visual visual, string property, Compositor compositor,
        float to, int milliseconds, CompositionEasingFunction ease)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = Motion.Duration(milliseconds);
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1f, to, ease);
        visual.StartAnimation(property, animation);
    }
}
