using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinIsland.Core;

namespace WinIsland.Island;

/// <summary>
/// 灵动岛悬浮窗：无边框、透明、置顶、不抢焦点。
/// 状态机：空闲 → 紧凑 → 展开 → 临时消息。
/// 小岛时只展示优先级最高的一个活动；展开时所有活动
/// 按大岛样式从上到下排列，每个都是完整的 MorphView 大岛。
/// </summary>
public sealed partial class IslandWindow : Window
{
    private const double Pad = 24;
    private const double QueueSpacing = 8;
    private const int MaxExpandedItems = 4;
    private static readonly Size IdleSize = new(128, 34);
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(333);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(250);
    private const double MinCanvasWidth = 420;
    private static readonly Size MaxMessageSize = new(280, 40);

    private readonly SettingsService _settings;
    private readonly nint _hwnd;
    private readonly OverlappedPresenter _presenter;

    // 常驻活动列表（按优先级降序排列，优先级相同则按插入顺序）
    private readonly List<(string Owner, IslandLiveContent Content)> _live = new();
    // 临时内容
    private UIElement? _tempContent;
    private Size _tempSize;

    private readonly DispatcherQueueTimer _tempTimer;
    private readonly DispatcherQueueTimer _hoverGuard;

    private readonly UIElement _idleContent;

    private bool _shown;
    private bool _hover;
    private bool _expanded;
    private Size _currentIsland;
    private int _windowPhysW;
    private int _windowPhysH;
    private Storyboard? _sizeStoryboard;
    private int _animVersion;
    private readonly RectangleGeometry _clip = new();

    // 窗口画布（内容区，逻辑像素，含透明 padding）。只在内容集合变化时扩容，永不缩小 ——
    // 悬停展开/收起全程不改窗口几何，否则 DWM 会用上一帧（旧客户区坐标）合成新窗口矩形，
    // 出现偏移 Δw/2 的「副本残影」。
    private Size _canvas = new(MinCanvasWidth + Pad * 2, IdleSize.Height + Pad * 2);
    // 最近一次应用（或将应用）到窗口的点击穿透矩形（物理像素），用于逐帧去重。
    private List<(int x1, int y1, int x2, int y2)> _hitRects = new();
    // 最近一次应用窗口尺寸时使用的缩放比，用于识别 DPI 变化。
    private double _appliedScale;

    // 主活动（优先级最高）
    private IslandLiveContent? ActiveLive => _live.Count > 0 ? _live[0].Content : null;
    // 队列活动（除主活动外的其余活动）
    private IReadOnlyList<(string Owner, IslandLiveContent Content)> QueueItems
        => _live.Count > 1 ? _live.Skip(1).ToList() : Array.Empty<(string, IslandLiveContent)>();

    public IslandWindow(SettingsService settings)
    {
        _settings = settings;
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
        _presenter.IsAlwaysOnTop = false;
        _presenter.IsAlwaysOnTop = true;
        Win32.MakeIslandStyle(_hwnd);

        _idleContent = BuildIdleContent();

        IslandRoot.Clip = _clip;
        IslandRoot.SizeChanged += (_, e) =>
        {
            _clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            // 尺寸动画逐帧改变实际大小，点击穿透区域必须同步跟随
            UpdateHitRegion();
        };

        // DPI 变化时画布的逻辑尺寸不变，但物理像素尺寸需要重新应用（缩放、位置、命中区域）
        RootGrid.Loaded += (_, _) =>
        {
            if (RootGrid.XamlRoot is not { } root) return;
            root.Changed += (_, _) =>
            {
                if (Math.Abs(Scale - _appliedScale) < 0.001) return;
                ApplyCanvasBounds();
            };
        };

        _tempTimer = DispatcherQueue.CreateTimer();
        _tempTimer.IsRepeating = false;
        _tempTimer.Tick += (_, _) => DismissTemporary();

        _hoverGuard = DispatcherQueue.CreateTimer();
        _hoverGuard.Interval = TimeSpan.FromMilliseconds(300);
        _hoverGuard.IsRepeating = true;
        _hoverGuard.Tick += (_, _) => HoverGuardTick();

        _settings.Changed += OnSettingChanged;

        _currentIsland = IdleSize;
        IslandRoot.Width = IdleSize.Width;
        IslandRoot.Height = IdleSize.Height;
        ContentHost.Content = _idleContent;
        EnsureCanvas();
    }

    #region 公开操作（由 IslandService 在 UI 线程调用）

    public void SetLive(string owner, IslandLiveContent? content)
    {
        _live.RemoveAll(t => t.Owner == owner);
        if (content != null)
        {
            _live.Add((owner, content));
            _live.Sort((a, b) => b.Content.Priority.CompareTo(a.Content.Priority));
        }

        // 内容集合变化时才允许扩容画布，之后悬停展开/收起不再动窗口几何
        EnsureCanvas();

        if (_tempContent != null)
        {
            UpdateVisibility();
            return;
        }

        TransitionToLiveState();
        UpdateVisibility();
    }

    public void ShowTemporary(UIElement content, Size size, TimeSpan duration)
    {
        _tempContent = content;
        _tempSize = size;
        _tempTimer.Stop();
        _tempTimer.Interval = duration;
        _tempTimer.Start();

        // 收起时先安全拆卸队列，再收起主岛
        TeardownQueue();
        _expanded = false;

        var live = ActiveLive;
        if (live?.MorphView != null)
        {
            // 确保 View 在 ContentHost 里（可能刚从队列拆回来）
            ContentHost.Content = live.MorphView.View;
            live.MorphView.AnimateToCompact(CollapseDuration);
        }

        ContentHost.Content = content;
        EnsureCanvas();
        AnimateIslandSize(size, CollapseDuration, isTemporary: true);
        SetCornerRadius(size.Height / 2);
        UpdateVisibility();
    }

    public void DismissTemporary()
    {
        if (_tempContent == null) return;
        _tempTimer.Stop();
        _tempContent = null;
        EnsureCanvas();
        TransitionToLiveState();
        UpdateVisibility();
    }

    public void ShowMessage(IslandMessage msg)
    {
        var view = BuildMessageView(msg);
        const double width = 280;
        view.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Clamp(view.DesiredSize.Height, 36, 40);
        ShowTemporary(view, new Size(width, height), msg.Duration);
    }

    public void RefreshFromSettings()
    {
        UpdateVisibility();
        EnsureCanvas();
        ApplyCanvasBounds();
    }

    public void ShowIsland()
    {
        UpdateVisibility(force: true);
    }

    #endregion

    #region 状态机

    private Size ComputeTotalSize(bool expanded)
    {
        var live = ActiveLive;
        if (live == null) return IdleSize;

        double mainW, mainH;
        if (expanded)
        {
            mainW = live.ExpandedSize.Width;
            mainH = live.ExpandedSize.Height;
        }
        else
        {
            mainW = live.CompactSize.Width;
            mainH = live.CompactSize.Height;
        }

        var queue = QueueItems;
        if (expanded && queue.Count > 0)
        {
            int count = Math.Min(queue.Count, MaxExpandedItems - 1);
            double queueH = 0;
            double maxQueueW = mainW;
            for (int i = 0; i < count; i++)
            {
                var q = queue[i].Content;
                queueH += q.ExpandedSize.Height + QueueSpacing;
                maxQueueW = Math.Max(maxQueueW, q.ExpandedSize.Width);
            }
            return new Size(maxQueueW, mainH + queueH);
        }

        return new Size(mainW, mainH);
    }

    /// <summary>
    /// 当前内容集合在「空闲 / 临时消息 / 紧凑 / 展开 + 队列」各状态下所需的最大画布尺寸（含透明 padding）。
    /// </summary>
    private Size ComputeCanvasFootprint()
    {
        double w = Math.Max(MinCanvasWidth, Math.Max(IdleSize.Width, MaxMessageSize.Width));
        double h = Math.Max(IdleSize.Height, MaxMessageSize.Height);

        foreach (var (_, content) in _live)
        {
            w = Math.Max(w, Math.Max(content.CompactSize.Width, content.ExpandedSize.Width));
            h = Math.Max(h, Math.Max(content.CompactSize.Height, content.ExpandedSize.Height));
        }

        var expandedTotal = ComputeTotalSize(expanded: true);
        w = Math.Max(w, expandedTotal.Width);
        h = Math.Max(h, expandedTotal.Height);

        return new Size(w + Pad * 2, h + Pad * 2);
    }

    /// <summary>预留画布：只在内容集合需要更大空间时扩容，永不缩小。常规路径为 no-op。</summary>
    private void EnsureCanvas()
    {
        var need = ComputeCanvasFootprint();
        double w = Math.Max(_canvas.Width, need.Width);
        double h = Math.Max(_canvas.Height, need.Height);
        if (_windowPhysW > 0 && w == _canvas.Width && h == _canvas.Height) return;

        _canvas = new Size(w, h);
        ApplyCanvasBounds();
    }

    private void TransitionToLiveState()
    {
        var live = ActiveLive;
        if (live == null)
        {
            _expanded = false;
            TeardownQueue();
            ContentHost.Content = _idleContent;
            AnimateIslandSize(IdleSize, CollapseDuration);
            SetCornerRadius(IdleSize.Height / 2);
            return;
        }

        bool wantExpand = _hover;

        if (live.MorphView != null)
        {
            // 确保 View 在 ContentHost 里
            ContentHost.Content = live.MorphView.View;
            if (wantExpand)
            {
                _expanded = true;
                AnimateIslandSize(live.ExpandedSize, ExpandDuration);
                SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
                live.MorphView.AnimateToExpanded(ExpandDuration);
                BuildQueue();
            }
            else
            {
                _expanded = false;
                TeardownQueue();
                AnimateIslandSize(live.CompactSize, CollapseDuration);
                SetCornerRadius(live.CompactSize.Height / 2);
                live.MorphView.AnimateToCompact(CollapseDuration);
            }
        }
        else
        {
            if (wantExpand && live.ExpandedContent != null)
            {
                _expanded = true;
                ContentHost.Content = live.ExpandedContent;
                AnimateIslandSize(live.ExpandedSize, ExpandDuration);
                SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
                BuildQueue();
            }
            else
            {
                _expanded = false;
                TeardownQueue();
                ContentHost.Content = live.CompactContent ?? _idleContent;
                AnimateIslandSize(live.CompactSize, CollapseDuration);
                SetCornerRadius(live.CompactSize.Height / 2);
            }
        }
    }

    /// <summary>构建队列：每个队列活动用独立的 Border 包裹其 MorphView.View。</summary>
    private void BuildQueue()
    {
        var queue = QueueItems;
        QueuePanel.Children.Clear();
        if (queue.Count == 0)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            UpdateHitRegion(force: true);
            return;
        }

        int count = Math.Min(queue.Count, MaxExpandedItems - 1);
        for (int i = 0; i < count; i++)
        {
            var item = queue[i];
            var content = item.Content;

            UIElement inner;
            if (content.MorphView != null)
            {
                inner = content.MorphView.View;
                content.MorphView.AnimateToExpanded(ExpandDuration);
            }
            else
            {
                inner = content.ExpandedContent ?? content.CompactContent ?? BuildFallbackLabel(item.Owner, content);
            }

            var border = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0)),
                CornerRadius = new CornerRadius(17),
                Width = content.ExpandedSize.Width,
                Height = content.ExpandedSize.Height,
                Child = inner,
            };
            QueuePanel.Children.Add(border);
        }

        QueuePanel.Visibility = Visibility.Visible;
        UpdateHitRegion(force: true);
    }

    /// <summary>安全拆卸队列：先停止 Storyboard，把 MorphView.View 从 Border 中取出，
    /// 确保它回到 ContentHost 的掌控，最后清空 QueuePanel。</summary>
    private void TeardownQueue()
    {
        if (QueuePanel.Children.Count == 0)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var queue = QueueItems;
        int count = Math.Min(queue.Count, MaxExpandedItems - 1);

        // 先停止队列中所有 MorphView 的动画
        for (int i = 0; i < count; i++)
        {
            queue[i].Content.MorphView?.AnimateToCompact(CollapseDuration);
        }

        // 从 Border 中取出 MorphView.View（断开父子关系）
        // WinUI: 设置 Child=null 会断开引用
        foreach (var child in QueuePanel.Children)
        {
            if (child is Border b)
                b.Child = null;
        }

        QueuePanel.Children.Clear();
        QueuePanel.Visibility = Visibility.Collapsed;

        // 确保主活动的 View 在 ContentHost 里（可能被队列 Border 占用过）
        var live = ActiveLive;
        if (live?.MorphView != null && !ReferenceEquals(ContentHost.Content, live.MorphView.View))
        {
            ContentHost.Content = live.MorphView.View;
        }

        UpdateHitRegion(force: true);
    }

    private static UIElement BuildFallbackLabel(string owner, IslandLiveContent content)
    {
        var accent = content.OwnerAccent ?? Windows.UI.Color.FromArgb(255, 90, 90, 95);
        var label = content.OwnerLabel ?? owner;
        var glyph = content.OwnerGlyph ?? "\uE946";

        var grid = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconHost = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 14,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        Grid.SetColumn(iconHost, 0);

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        Grid.SetColumn(labelText, 1);

        grid.Children.Add(iconHost);
        grid.Children.Add(labelText);
        return grid;
    }

    private void OnHoverEnter()
    {
        if (_tempContent != null) return;

        var live = ActiveLive;
        if (live == null) return;
        if (live.MorphView == null && live.ExpandedContent == null) return;
        if (_expanded) return;

        _expanded = true;

        if (live.MorphView != null)
        {
            AnimateIslandSize(live.ExpandedSize, ExpandDuration);
            SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
            live.MorphView.AnimateToExpanded(ExpandDuration);
        }
        else
        {
            ContentHost.Content = live.ExpandedContent;
            AnimateIslandSize(live.ExpandedSize, ExpandDuration);
            SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
        }

        BuildQueue();
    }

    private void OnHoverExit()
    {
        if (_tempContent != null) return;
        if (!_expanded) return;

        // 先安全拆卸队列（停止动画 + 断开 Border.Child + 把主 View 放回 ContentHost）
        TeardownQueue();

        _expanded = false;

        var live = ActiveLive;
        if (live == null) return;

        if (live.MorphView != null)
        {
            AnimateIslandSize(live.CompactSize, CollapseDuration);
            SetCornerRadius(live.CompactSize.Height / 2);
            live.MorphView.AnimateToCompact(CollapseDuration);
        }
        else
        {
            ContentHost.Content = live.CompactContent ?? _idleContent;
            AnimateIslandSize(live.CompactSize, CollapseDuration);
            SetCornerRadius(live.CompactSize.Height / 2);
        }
    }

    #endregion

    #region 尺寸动画

    private void SetCornerRadius(double radius)
    {
        IslandRoot.CornerRadius = new CornerRadius(radius);
    }

    private void AnimateIslandSize(Size islandTarget, TimeSpan duration, bool isTemporary = false)
    {
        var totalTarget = isTemporary ? islandTarget : ComputeTotalSize(_expanded);
        bool unchanged = islandTarget == _currentIsland && _sizeStoryboard == null;

        // 这里只做兜底：正常路径下画布已由内容集合变化时预留好。
        // 尺寸动画期间绝不 resize 窗口 —— 否则客户区宽度变化会让 DWM 用上一帧
        // （旧客户区坐标）合成新窗口矩形，出现偏移 Δw/2 的副本残影。
        EnsureCanvas();

        var previousIsland = _currentIsland;
        _currentIsland = islandTarget;
        _currentTotalSize = totalTarget;

        if (unchanged)
        {
            IslandRoot.Width = islandTarget.Width;
            IslandRoot.Height = islandTarget.Height;
            UpdateHitRegion();
            return;
        }

        var fromW = double.IsNaN(IslandRoot.ActualWidth) || IslandRoot.ActualWidth <= 0
            ? previousIsland.Width : IslandRoot.ActualWidth;
        var fromH = double.IsNaN(IslandRoot.ActualHeight) || IslandRoot.ActualHeight <= 0
            ? previousIsland.Height : IslandRoot.ActualHeight;
        if (_sizeStoryboard != null)
        {
            _sizeStoryboard.Stop();
            _sizeStoryboard = null;
            IslandRoot.Width = fromW;
            IslandRoot.Height = fromH;
        }
        var version = ++_animVersion;

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        sb.Children.Add(MakeSizeAnim(IslandRoot, "Width", fromW, islandTarget.Width, duration, easing));
        sb.Children.Add(MakeSizeAnim(IslandRoot, "Height", fromH, islandTarget.Height, duration, easing));

        sb.Completed += (_, _) =>
        {
            if (version != _animVersion) return;
            _sizeStoryboard = null;
            IslandRoot.Width = islandTarget.Width;
            IslandRoot.Height = islandTarget.Height;
            UpdateHitRegion();
        };
        _sizeStoryboard = sb;
        sb.Begin();
    }

    private Size _currentTotalSize = IdleSize;

    private static DoubleAnimation MakeSizeAnim(
        DependencyObject target, string property,
        double from, double to, TimeSpan duration, EasingFunctionBase easing)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }

    #endregion

    #region 窗口位置 / 可见性

    private double Scale => Win32.GetDpiForWindow(_hwnd) / 96.0;

    /// <summary>
    /// 应用预留画布的尺寸与位置。只在内容集合变化（活动上岛/临时消息）或 DPI 变化时调用，
    /// 绝不在悬停展开/收起动画中调用 —— 见 <see cref="AnimateIslandSize"/>。
    /// </summary>
    private void ApplyCanvasBounds()
    {
        var s = Scale;
        int w = (int)Math.Round(_canvas.Width * s);
        int h = (int)Math.Round(_canvas.Height * s);

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double topOffset = _settings.Get("island.topOffset", 6.0);
        int x = area.X + (area.Width - w) / 2;
        int y = area.Y + (int)Math.Round((topOffset - Pad) * s);

        bool sizeChanged = w != _windowPhysW || h != _windowPhysH;
        _windowPhysW = w;
        _windowPhysH = h;
        _appliedScale = s;

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));

        if (sizeChanged)
            Win32.RemoveDwmBorder(_hwnd);

        UpdateHitRegion(force: true);
    }

    /// <summary>
    /// 用岛屿的「真实布局矩形」标记可点击范围，区域外在 WM_NCHITTEST 返回 HTTRANSPARENT。
    /// 必须跟随实际布局（尺寸动画中间帧也会变），且绝不能置空 ——
    /// 空/未设置的区域会让整块透明画布拦截点击。
    /// </summary>
    private void UpdateHitRegion(bool force = false)
    {
        var s = Scale;
        var rects = new List<(int x1, int y1, int x2, int y2)>(MaxExpandedItems);

        // 主岛（空闲 / 活动 / 临时消息都走这里）：以实际布局尺寸为准
        double iw = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        double ih = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;
        double ix = (_windowPhysW - iw * s) / 2.0;
        double iy = Pad * s;
        rects.Add((
            (int)Math.Round(ix), (int)Math.Round(iy),
            (int)Math.Round(ix + iw * s), (int)Math.Round(iy + ih * s)));

        // 队列小岛：各子元素的实际布局矩形（DIP → 物理）
        foreach (var child in QueuePanel.Children)
        {
            if (child is not FrameworkElement fe || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                continue;

            var box = fe.TransformToVisual(RootGrid)
                        .TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
            rects.Add((
                (int)Math.Round(box.X * s), (int)Math.Round(box.Y * s),
                (int)Math.Round((box.X + box.Width) * s), (int)Math.Round((box.Y + box.Height) * s)));
        }

        if (!force && SameRects(_hitRects, rects)) return;
        _hitRects = rects;
        SetHitRgn(rects);
    }

    private static bool SameRects(
        IReadOnlyList<(int x1, int y1, int x2, int y2)> a,
        IReadOnlyList<(int x1, int y1, int x2, int y2)> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private void SetHitRgn(IReadOnlyList<(int x1, int y1, int x2, int y2)> rects)
    {
        // 空集合绝不能清空命中区域：空区域等于整块透明画布拦截点击
        if (rects.Count == 0) return;

        var first = Win32.CreateRectRegion(rects[0].x1, rects[0].y1, rects[0].x2, rects[0].y2);
        if (rects.Count == 1) { Win32.SetHitRegion(first); return; }
        for (int i = 1; i < rects.Count; i++)
        {
            var next = Win32.CreateRectRegion(rects[i].x1, rects[i].y1, rects[i].x2, rects[i].y2);
            Win32.CombineRgnInPlace(first, next);
            Win32.DeleteRegion(next);
        }
        Win32.SetHitRegion(first);
    }

    private void UpdateVisibility(bool force = false)
    {
        bool hideIdle = _settings.Get("island.hideWhenIdle", false);
        bool visible = _settings.Get("island.visible", true)
                       && !(hideIdle && ActiveLive == null && _tempContent == null);
        if (!force && visible == _shown) return;
        _shown = visible;
        if (visible)
        {
            AppWindow.Show(false);
            _presenter.IsAlwaysOnTop = false;
            _presenter.IsAlwaysOnTop = true;
            Win32.MakeIslandStyle(_hwnd);
        }
        else
        {
            _hoverGuard.Stop();
            _hover = false;
            AppWindow.Hide();
        }
    }

    private void OnSettingChanged(string key)
    {
        if (!key.StartsWith("island.", StringComparison.Ordinal)) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshFromSettings);
            return;
        }
        RefreshFromSettings();
    }

    #endregion

    #region 指针交互 — 主程序统一处理

    private void IslandRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hover = true;
        _hoverGuard.Start();

        if (_tempContent != null)
        {
            _tempTimer.Stop();
            _tempTimer.Start();
            return;
        }
        OnHoverEnter();
    }

    private void IslandRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (QueuePanel.Visibility == Visibility.Visible) return;
        if (IsCursorOverIsland()) return;
        _hover = false;
        _hoverGuard.Stop();

        if (_tempContent != null) return;
        OnHoverExit();
    }

    private void IslandStack_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hover = true;
        _hoverGuard.Start();
    }

    private void IslandStack_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (IsCursorOverIsland()) return;
        _hover = false;
        _hoverGuard.Stop();

        if (_tempContent != null) return;
        OnHoverExit();
    }

    private void IslandRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_tempContent != null && e.OriginalSource is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
        {
            DismissTemporary();
            return;
        }

        if (e.OriginalSource is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
        {
            ActiveLive?.OnTap?.Invoke();
        }
    }

    private void HoverGuardTick()
    {
        if (!_hover) return;
        if (!IsCursorOverIsland())
        {
            _hover = false;
            _hoverGuard.Stop();
            if (_tempContent != null) return;
            OnHoverExit();
        }
    }

    private bool IsCursorOverIsland()
    {
        Win32.GetCursorPos(out var pt);
        var s = Scale;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        double iw = _currentTotalSize.Width * s;
        double ih = _currentTotalSize.Height * s;
        double ix = pos.X + (size.Width - iw) / 2;
        double iy = pos.Y + Pad * s;
        const double slack = 6;
        return pt.X >= ix - slack && pt.X <= ix + iw + slack
               && pt.Y >= iy - slack && pt.Y <= iy + ih + slack;
    }

    #endregion

    #region 内置视图

    private static UIElement BuildIdleContent()
    {
        var grid = new Grid();
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 46, 50)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        grid.Children.Add(dot);
        return grid;
    }

    private static FrameworkElement BuildMessageView(IslandMessage msg)
    {
        var accent = msg.AccentColor ?? Windows.UI.Color.FromArgb(255, 0, 122, 255);

        var grid = new Grid
        {
            ColumnSpacing = 10,
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconHost = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = msg.Glyph,
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        Grid.SetColumn(iconHost, 0);

        var title = new TextBlock
        {
            Text = msg.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        Grid.SetColumn(title, 1);

        grid.Children.Add(iconHost);
        grid.Children.Add(title);
        return grid;
    }

    #endregion
}
