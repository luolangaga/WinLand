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
using WinIsland.Core.Plugins;

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

    // 位置（island.position / island.horizontal / 两条偏移）
    private const string PositionKey = "island.position";
    private const string HorizontalKey = "island.horizontal";
    private const string PositionTop = "top";
    private const string PositionBottom = "bottom";
    private const string HorizontalCenter = "center";
    private const string HorizontalLeft = "left";
    private const string HorizontalRight = "right";
    private const string TopOffsetKey = "island.topOffset";
    private const string BottomOffsetKey = "island.bottomOffset";
    private const string HorizontalOffsetKey = "island.horizontalOffset";
    private const double DefaultOffset = 6.0;
    /// <summary>靠左/靠右时距条带（或工作区）边缘的间距。Win11 图标居中排列，任务栏左端通常是空的。</summary>
    private const double EdgeInset = 12;
    /// <summary>底部模式靠右、且量不到托盘区（TrayNotifyWnd）时的兜底预留量。</summary>
    private const double TrayReserve = 220;
    private const int PositionSlideMs = 300;
    /// <summary>
    /// 任务栏跟随轮询周期。同时兼作置顶自愈的兜底检查 —— shell 会在你点任务栏时把 Shell_TrayWnd
    /// 抬到所有置顶窗口之上，岛整块被盖住，所以这个周期就是「被盖住到顶回来」的最坏延迟，取短一些。
    /// </summary>
    private const int TaskbarPollMs = 120;
    /// <summary>任务栏空闲段查询（UIA）的最小间隔：调用较慢，只在内容变化等时机补充刷新。</summary>
    private const int FreeBandsThrottleMs = 2000;
    /// <summary>
    /// 紧凑态宽度上限（逻辑像素）。任务栏上被开始/搜索/图标/托盘占满时，可用的空闲段往往只有
    /// 一两百逻辑像素宽，而 explorer 自己的内容永远不会为外部窗口让位 ——
    /// 想让岛不压住它们，只能让岛自己变窄。这个上限对所有位置模式一视同仁：
    /// 曾经只按底部模式实测的空闲段收窄，结果「嵌进任务栏变窄、出去又变宽」，观感很怪，别再来一次。
    /// 170 逻辑像素 = 340 物理像素：配合 EdgeInset(12) 与默认偏移(6) 刚好能塞进 187 逻辑宽的空闲段。
    /// </summary>
    private const double CompactWidthMax = 170;

    private readonly SettingsService _settings;
    private readonly IPluginLogger _log;
    private readonly nint _hwnd;
    private readonly OverlappedPresenter _presenter;

    // 常驻活动列表（按优先级降序排列，优先级相同则按插入顺序）
    private readonly List<(string Owner, IslandLiveContent Content)> _live = new();
    // 临时内容
    private UIElement? _tempContent;
    private Size _tempSize;
    private string? _tempOwner;

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

    // 外观风格（island.style / island.material）：材质、底衬、描边、圆角与空闲点颜色都由它决定
    private readonly IslandSurface _mainSurface;
    private readonly List<IslandSurface> _queueSurfaces = new();
    private readonly Ellipse _idleDot;
    private IslandStyleKind _style = IslandStyleKind.Apple;
    private IslandMaterialKind _material = IslandMaterialKind.Acrylic;
    private bool _styleApplied;
    // 窗口级系统材质（仅 Fluent）：不支持时退回纯色底衬
    private IslandBackdrop? _backdrop;
    private bool _backdropOwned;
    private bool _materialApplied;
    // 最近一次圆角对应的岛体高度与展开态，风格切换时据此按当前状态重算圆角
    private double _radiusHeight = IdleSize.Height;
    private bool _radiusExpanded;

    // 岛体锚定方向：false = 贴工作区顶部（队列向下），true = 贴任务栏条带（队列向上）
    private bool _bottomAnchored;
    private string _horizontal = HorizontalCenter;
    private bool _positionApplied;
    /// <summary>底部模式：任务栏已收起（自动隐藏/全屏）→ 岛一起收起。</summary>
    private bool _taskbarHidden;
    /// <summary>上次贴合的任务栏底边，用于识别条带移动/变高/换分辨率（只在真的变了才动窗口）。</summary>
    private int _lastStripBottom;
    private DispatcherQueueTimer? _taskbarWatch;

    // 任务栏空闲段（UIA 实测，物理像素，按左边界升序）：底部模式的水平落点用它避开 explorer 自己的内容。
    // 为空表示「没查到 / 不可用」→ 回退到按条带边缘对齐。
    private Windows.Graphics.RectInt32[] _freeBands = Array.Empty<Windows.Graphics.RectInt32>();
    private bool _freeBandsInFlight;
    private bool _freeBandsDirty;
    private bool _freeBandsFallbackLogged;
    private long _freeBandsAt;
    /// <summary>最近一次置顶自愈的元凶描述与时间（日志去重）＋累计次数。</summary>
    private string? _topmostHealLast;
    private long _topmostHealLoggedAt;
    private int _topmostHealCount;
    /// <summary>最近一次定位用的条带水平中心（物理像素），挑「离屏幕中心最近的空闲段」时用。</summary>
    private double _stripCenterX;

    // 位置切换滑动：只移动窗口、绝不改窗口尺寸
    private DispatcherQueueTimer? _positionSlide;
    private int _slideFromX, _slideFromY, _slideToX, _slideToY;
    private long _slideStartTick;
    /// <summary>方向刚切换瞬间岛体的屏幕 Y：滑动起点，保证岛体不跳变。</summary>
    private double? _slideOriginIslandTop;
    /// <summary>方向刚切换瞬间岛体的屏幕左边缘 X：滑动起点（水平对齐方式变化时用）。</summary>
    private double? _slideOriginIslandLeft;

    // 窗口画布（内容区，逻辑像素，含透明 padding）。只在内容集合变化时扩容，永不缩小 ——
    // 悬停展开/收起全程不改窗口几何，否则 DWM 会用上一帧（旧客户区坐标）合成新窗口矩形，
    // 出现偏移 Δw/2 的「副本残影」。
    private Size _canvas = new(MinCanvasWidth + Pad * 2, IdleSize.Height + Pad * 2);
    // 最近一次应用（或将应用）到窗口的点击穿透形状（物理像素），用于逐帧去重。
    private List<ShapeRect> _hitRects = new();
    // 最近一次应用窗口尺寸时使用的缩放比，用于识别 DPI 变化。
    private double _appliedScale;

    // 主活动（优先级最高）
    private IslandLiveContent? ActiveLive => _live.Count > 0 ? _live[0].Content : null;
    // 队列活动（除主活动外的其余活动）
    private IReadOnlyList<(string Owner, IslandLiveContent Content)> QueueItems
        => _live.Count > 1 ? _live.Skip(1).ToList() : Array.Empty<(string, IslandLiveContent)>();

    public IslandWindow(SettingsService settings, PluginLogService logs)
    {
        _settings = settings;
        _log = logs.Host;
        InitializeComponent();

        _mainSurface = new IslandSurface(IslandRoot, ContentHost);

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

        _idleDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(IslandStyle.IdleDotColor(_style)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        _idleContent = BuildIdleContent(_idleDot);

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
        Closed += (_, _) =>
        {
            StopPositionSlide();
            _positionSlide = null;
            _taskbarWatch?.Stop();
            _taskbarWatch = null;
            _backdrop?.Dispose();
        };

        _currentIsland = IdleSize;
        IslandRoot.Width = IdleSize.Width;
        IslandRoot.Height = IdleSize.Height;
        ContentHost.Content = _idleContent;
        ApplyPositionMode();
        ApplyStyle();
        EnsureCanvas();
    }

    #region 公开操作（由 IslandService 在 UI 线程调用）

    public void SetLive(string owner, IslandLiveContent? content)
    {
        _live.RemoveAll(t => t.Owner == owner);
        if (content != null)
        {
            _live.Add((owner, content));
        }

        SortLive();

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

    public void ShowTemporary(UIElement content, Size size, TimeSpan duration, string? owner = null)
    {
        _tempContent = content;
        _tempSize = size;
        _tempOwner = owner;
        _tempTimer.Stop();
        _tempTimer.Interval = duration;
        _tempTimer.Start();

        // 收起时先安全拆卸队列，再收起主岛
        TeardownQueue();
        _expanded = false;

        ContentHost.Content = content;
        EnsureCanvas();
        AnimateIslandSize(size, CollapseDuration, isTemporary: true);
        ApplyCornerRadius(size.Height, expanded: false);
        UpdateVisibility();
    }

    public void DismissTemporary(string? owner = null)
    {
        if (_tempContent == null) return;
        if (owner != null && _tempOwner != owner) return;
        _tempTimer.Stop();
        _tempContent = null;
        _tempOwner = null;
        EnsureCanvas();
        TransitionToLiveState();
        UpdateVisibility();
    }

    public void ShowMessage(IslandMessage msg, string? owner = null)
    {
        var view = BuildMessageView(msg);
        const double width = 280;
        view.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Clamp(view.DesiredSize.Height, 36, 40);
        ShowTemporary(view, new Size(width, height), msg.Duration, owner);
    }

    public void RefreshFromSettings()
    {
        ApplyStyle();
        ApplyPositionMode();
        UpdateVisibility();
        EnsureCanvas();
        ApplyCanvasBounds();
    }

    /// <summary>
    /// 应用外观风格：窗口材质、底衬、描边、圆角与空闲点颜色。风格与材质都没变时为 no-op
    /// （其余 island.* 设置改动也会触发刷新，避免每次都重建材质与画刷）。
    /// </summary>
    private void ApplyStyle()
    {
        var style = IslandStyle.ParseStyle(_settings.Get(IslandStyle.StyleKey, IslandStyle.AppleValue));
        var material = IslandStyle.ParseMaterial(_settings.Get(IslandStyle.MaterialKey, IslandStyle.AcrylicValue));
        if (_styleApplied && style == _style && material == _material) return;

        _style = style;
        _material = material;
        _styleApplied = true;

        ApplyBackdrop();
        _mainSurface.ApplyStyle(_style, _materialApplied);
        ApplyCornerRadius(_radiusHeight, _radiusExpanded);
        RefreshQueueStyle();
        _idleDot.Fill = new SolidColorBrush(IslandStyle.IdleDotColor(_style));
    }

    /// <summary>
    /// 窗口级材质。Fluent 用控制器直接挂 Desktop Acrylic / Mica —— 岛体与队列卡片都在窗口形状内，
    /// 材质因此只出现在岛屿轮廓里。Apple 保持原来的透明画布背景（材质让位）。
    /// </summary>
    private void ApplyBackdrop()
    {
        if (_style != IslandStyleKind.Fluent)
        {
            _materialApplied = false;
            if (_backdropOwned)
            {
                _backdrop?.Clear();
                SystemBackdrop = new TransparentBackdrop();
                _backdropOwned = false;
            }
            return;
        }

        _backdrop ??= new IslandBackdrop(this);
        if (!_backdropOwned)
        {
            // 控制器要占用窗口背景，先释放 XAML 侧的透明背景
            SystemBackdrop = null;
            _backdropOwned = true;
        }

        if (_backdrop.TryApply(_material))
        {
            _materialApplied = true;
            return;
        }

        _log.Warn($"当前系统不支持 {_material.ToValue()} 材质，已回退为纯色底衬。");
        _backdrop.Clear();
        _materialApplied = false;
        SystemBackdrop = new TransparentBackdrop();
        _backdropOwned = false;
    }

    /// <summary>就地刷新队列卡片的外观（不重放形态动画）。</summary>
    private void RefreshQueueStyle()
    {
        foreach (var surface in _queueSurfaces)
        {
            surface.ApplyStyle(_style, _materialApplied);
            surface.SetRadius(IslandStyle.ResolveQueueRadius(_style, surface.Border.Height));
        }
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

        Size main = expanded ? ExpandedMainSize() : CompactSizeOf(live);

        var queue = QueueItems;
        if (expanded && queue.Count > 0)
        {
            int count = Math.Min(queue.Count, MaxExpandedItems - 1);
            return new Size(main.Width, main.Height + count * (QueueCardHeight() + QueueSpacing));
        }

        return main;
    }

    /// <summary>展开态主岛尺寸：高度由活动自己决定，宽度统一到栈宽（与卡片边缘对齐）。</summary>
    private Size ExpandedMainSize()
        => new(StackWidth(), ActiveLive?.ExpandedSize.Height ?? IdleSize.Height);

    /// <summary>展开态的统一宽度：主岛与所有队列卡片取最大展开宽度，整列边缘对齐。</summary>
    private double StackWidth()
    {
        double width = ActiveLive?.ExpandedSize.Width ?? IdleSize.Width;
        foreach (var (_, content) in _live)
        {
            width = Math.Max(width, content.ExpandedSize.Width);
        }
        return width;
    }

    /// <summary>队列卡片的统一高度：取展示出来的卡片里最高的一张，保证每张卡片一样大。</summary>
    private double QueueCardHeight()
    {
        var queue = QueueItems;
        int count = Math.Min(queue.Count, MaxExpandedItems - 1);
        double height = 0;
        for (int i = 0; i < count; i++)
        {
            height = Math.Max(height, queue[i].Content.ExpandedSize.Height);
        }
        return height > 0 ? height : IdleSize.Height;
    }

    /// <summary>按生效优先级稳定排序（数值大的在前；相同则保持注册顺序）。</summary>
    private void SortLive()
    {
        var sorted = _live.OrderByDescending(t => EffectivePriority(t.Owner, t.Content)).ToList();
        _live.Clear();
        _live.AddRange(sorted);
    }

    /// <summary>生效优先级：宿主设置 <c>plugin.&lt;id&gt;.priority</c> 覆盖插件自己声明的优先级。</summary>
    private double EffectivePriority(string owner, IslandLiveContent content)
        => _settings.Get<double?>($"plugin.{owner}.priority", null) ?? content.Priority;

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
        RefreshFreeBands();                     // 内容集合变了，顺便补一次任务栏空闲段（带节流）
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
            ApplyCornerRadius(IdleSize.Height, expanded: false);
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
                AnimateIslandSize(ExpandedMainSize(), ExpandDuration);
                ApplyCornerRadius(live.ExpandedSize.Height, expanded: true);
                AnimateMorphView(ActiveOwner, live.MorphView, expand: true);
                BuildQueue();
            }
            else
            {
                _expanded = false;
                TeardownQueue();
                Size compact = CompactSizeOf(live);
                AnimateIslandSize(compact, CollapseDuration);
                ApplyCornerRadius(compact.Height, expanded: false);
                AnimateMorphView(ActiveOwner, live.MorphView, expand: false);
            }
        }
        else
        {
            if (wantExpand && live.ExpandedContent != null)
            {
                _expanded = true;
                ContentHost.Content = live.ExpandedContent;
                AnimateIslandSize(ExpandedMainSize(), ExpandDuration);
                ApplyCornerRadius(live.ExpandedSize.Height, expanded: true);
                BuildQueue();
            }
            else
            {
                _expanded = false;
                TeardownQueue();
                ContentHost.Content = live.CompactContent ?? _idleContent;
                Size compact = CompactSizeOf(live);
                AnimateIslandSize(compact, CollapseDuration);
                ApplyCornerRadius(compact.Height, expanded: false);
            }
        }
    }

    /// <summary>构建队列：每个队列活动一张卡片，所有卡片统一尺寸（同宽同高）。</summary>
    private void BuildQueue()
    {
        // 活动顺序可能刚变过（优先级调整）：先按各自记录的视图安全拆掉旧卡片，再重建
        TeardownQueue();

        var queue = QueueItems;
        if (queue.Count == 0)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            UpdateHitRegion(force: true);
            return;
        }

        double cardWidth = StackWidth();
        double cardHeight = QueueCardHeight();

        // 底部模式下队列朝岛体上方排列：反向添加，使最先入队的活动仍最靠近岛体
        int count = Math.Min(queue.Count, MaxExpandedItems - 1);
        for (int n = 0; n < count; n++)
        {
            int i = _bottomAnchored ? count - 1 - n : n;
            var item = queue[i];
            var content = item.Content;

            UIElement inner;
            if (content.MorphView != null)
            {
                inner = content.MorphView.View;
            }
            else
            {
                inner = content.ExpandedContent ?? content.CompactContent ?? BuildFallbackLabel(item.Owner, content);
            }

            var surface = CreateCardSurface();
            surface.Owner = item.Owner;
            surface.MorphView = content.MorphView;
            surface.Border.Width = cardWidth;
            surface.Border.Height = cardHeight;
            surface.ApplyStyle(_style, _materialApplied);
            surface.SetRadius(IslandStyle.ResolveQueueRadius(_style, cardHeight));
            surface.SetContent(inner);
            QueuePanel.Children.Add(surface.Border);
            _queueSurfaces.Add(surface);

            // 动画必须在视图进入可视树之后再启动（否则插件视图可能在未加载状态下执行属性路径动画而失败）
            AnimateMorphView(item.Owner, content.MorphView, expand: true);
        }

        QueuePanel.Visibility = Visibility.Visible;
        UpdateHitRegion(force: true);
    }

    /// <summary>安全拆卸队列：先停止各卡片的形态动画，把 MorphView.View 从卡片中取出，
    /// 确保它回到 ContentHost 的掌控，最后清空 QueuePanel。</summary>
    private void TeardownQueue()
    {
        if (_queueSurfaces.Count == 0)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        // 先停止队列中所有 MorphView 的动画：用卡片自己记录的 owner/view，
        // 这样活动顺序刚变化时也不会误动到别的视图（例如已经变成主岛的那个）
        foreach (var surface in _queueSurfaces)
        {
            AnimateMorphView(surface.Owner ?? "(未知)", surface.MorphView, expand: false);
        }

        // 从卡片中取出 MorphView.View（断开父子关系）
        // WinUI: 把 ContentPresenter.Content 置空即断开引用
        foreach (var surface in _queueSurfaces)
        {
            surface.SetContent(null);
        }

        _queueSurfaces.Clear();
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

    private string ActiveOwner => _live.Count > 0 ? _live[0].Owner : "(无)";

    /// <summary>
    /// 插件视图的形态动画属于插件代码：必须在每个调用点守卫，
    /// 否则插件动画抛错会变成 WinUI 未处理异常（stowed exception）直接崩掉宿主。
    /// </summary>
    private void AnimateMorphView(string owner, IMorphView? view, bool expand)
    {
        if (view == null) return;
        try
        {
            if (expand) view.AnimateToExpanded(ExpandDuration);
            else view.AnimateToCompact(CollapseDuration);
        }
        catch (Exception ex)
        {
            _log.Error($"插件「{owner}」的形态动画执行失败（已忽略，宿主继续运行）", ex);
        }
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
            AnimateIslandSize(ExpandedMainSize(), ExpandDuration);
            ApplyCornerRadius(live.ExpandedSize.Height, expanded: true);
            AnimateMorphView(ActiveOwner, live.MorphView, expand: true);
        }
        else
        {
            ContentHost.Content = live.ExpandedContent;
            AnimateIslandSize(ExpandedMainSize(), ExpandDuration);
            ApplyCornerRadius(live.ExpandedSize.Height, expanded: true);
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
            Size compact = CompactSizeOf(live);
            AnimateIslandSize(compact, CollapseDuration);
            ApplyCornerRadius(compact.Height, expanded: false);
            AnimateMorphView(ActiveOwner, live.MorphView, expand: false);
        }
        else
        {
            ContentHost.Content = live.CompactContent ?? _idleContent;
            Size compact = CompactSizeOf(live);
            AnimateIslandSize(compact, CollapseDuration);
            ApplyCornerRadius(compact.Height, expanded: false);
        }
    }

    #endregion

    #region 尺寸动画

    /// <summary>
    /// 按岛体高度与展开状态设置圆角。圆角是点击穿透形状的唯一来源
    /// （<see cref="UpdateHitRegion"/> 读取岛体与队列卡片的 CornerRadius），
    /// 因此所有圆角规则只能来自 <see cref="IslandStyle.ResolveRadius"/>。
    /// </summary>
    private void ApplyCornerRadius(double height, bool expanded)
    {
        _radiusHeight = height;
        _radiusExpanded = expanded;
        _mainSurface.SetRadius(IslandStyle.ResolveRadius(_style, height, expanded));
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
    /// 应用 island.position / island.horizontal：岛体在画布内锚定顶部（队列向下）或底部（队列向上）。
    /// 默认值就是 XAML 里写死的顶部居中，因此没写过这两个键时行为与改动前一致。
    /// </summary>
    private void ApplyPositionMode()
    {
        bool bottom = string.Equals(_settings.Get(PositionKey, PositionTop), PositionBottom, StringComparison.OrdinalIgnoreCase);
        string horizontal = _settings.Get(HorizontalKey, HorizontalCenter) ?? HorizontalCenter;
        if (_positionApplied && bottom == _bottomAnchored && horizontal == _horizontal) return;

        // 切换瞬间岛体的屏幕位置（用「旧」方向/对齐方式算），滑动从它开始，保证岛体不跳变
        bool animate = _positionApplied && _shown && _windowPhysH > 0;
        double islandWidth = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        double islandHeight = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;
        _slideOriginIslandTop = animate
            ? AppWindow.Position.Y + IslandTopInWindow(_bottomAnchored, Scale, islandHeight)
            : null;
        _slideOriginIslandLeft = animate
            ? AppWindow.Position.X + IslandLeftInWindow(Scale, islandWidth)
            : null;

        _bottomAnchored = bottom;
        _horizontal = horizontal;
        _positionApplied = true;

        // 岛体锚定到画布的另一侧，队列面板换到主岛的另一边
        IslandStack.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        IslandStack.Margin = bottom ? new Thickness(0, 0, 0, Pad) : new Thickness(0, Pad, 0, 0);
        Grid.SetRow(IslandRoot, bottom ? 1 : 0);
        Grid.SetRow(QueuePanel, bottom ? 0 : 1);

        // 水平对齐：整栈 + 岛体 + 队列一起靠同一侧。
        // 岛体跟着对齐侧生长（靠左向右长、靠右向左长），否则展开时会向两侧对称生长、
        // 贴边那一侧直接长到屏幕外（溢出）。
        var align = horizontal switch
        {
            HorizontalLeft => HorizontalAlignment.Left,
            HorizontalRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        IslandStack.HorizontalAlignment = align;
        IslandRoot.HorizontalAlignment = align;
        QueuePanel.HorizontalAlignment = align;

        // 已展开时按新方向重建队列（卡片顺序镜像：最先入队的活动仍离岛体最近）
        if (_expanded) BuildQueue();

        if (bottom)
        {
            _taskbarHidden = false;
            _taskbarWatch ??= CreateTaskbarWatch();
            _taskbarWatch.Start();
            RefreshFreeBands(force: true);      // 进入底部模式立刻量一次任务栏空闲段
            // 前台窗口一变（点任务栏/开始菜单/搜索）就立刻查一次置顶，不必等 250ms 轮询
            Win32.InstallForegroundWatcher(() =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_bottomAnchored) HealTopmost();
                }));
        }
        else
        {
            _taskbarWatch?.Stop();
            RefreshFreeBands();                 // 顶部模式不需要空闲段（清空，落点回到屏幕居中）
        }
    }

    /// <summary>
    /// 岛体（锚点侧）边缘距客户区顶部的物理像素偏移：点击形状与悬停判定的唯一来源，
    /// 顶部模式取距画布顶的 Pad，底部模式从客户区底部往上量（岛体在画布内向上生长）。
    /// </summary>
    private double IslandTopInWindow(bool bottom, double s, double islandHeight)
        => bottom ? _windowPhysH - (Pad + islandHeight) * s : Pad * s;

    /// <summary>
    /// 岛体（对齐侧）左边缘距客户区左侧的物理像素偏移：靠左 = 0、靠右 = 贴窗口右边、居中 = 两侧等分。
    /// 靠左/靠右时窗口按岛体边缘落位，岛体因此只朝远离屏幕边缘的一侧生长。
    /// </summary>
    private double IslandLeftInWindow(double s, double islandWidth)
    {
        double width = islandWidth * s;
        return _horizontal switch
        {
            HorizontalLeft => 0,
            HorizontalRight => _windowPhysW - width,
            _ => (_windowPhysW - width) / 2.0,
        };
    }

    /// <summary>DisplayArea 的矩形转 Win32 矩形（同为物理像素、同一坐标系）。</summary>
    private static Win32.RECT ToRect(Windows.Graphics.RectInt32 r) => new()
    {
        Left = r.X,
        Top = r.Y,
        Right = r.X + r.Width,
        Bottom = r.Y + r.Height,
    };

    private static Windows.Graphics.RectInt32 ToRectInt32(Win32.RECT r)
        => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>
    /// 底部模式：查一次任务栏空闲段（UIA 实测 explorer 自己占了哪几段，后台线程调用）。
    /// 节流 + 单飞；展开动画期间查到的新结果只标记待应用，等收起后再落位 —— 绝不在展开中移动窗口。
    /// </summary>
    private void RefreshFreeBands(bool force = false)
    {
        if (!_bottomAnchored)
        {
            _freeBands = Array.Empty<Windows.Graphics.RectInt32>();
            _freeBandsDirty = false;
            return;
        }

        if (_freeBandsInFlight) return;
        if (!force && Environment.TickCount64 - _freeBandsAt < FreeBandsThrottleMs) return;
        _freeBandsAt = Environment.TickCount64;

        var outer = ToRect(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).OuterBounds);
        if (Win32.GetTaskbarStrip(outer) is not { Visible: true } tray || tray.Docked.Bottom < outer.Bottom - Win32.SliverPx)
            return;

        var hwnd = Win32.GetTaskbarWindow();
        if (hwnd == nint.Zero) return;

        var strip = ToRectInt32(tray.Docked);
        _freeBandsInFlight = true;
        _ = Task.Run(() =>
        {
            var bands = TaskbarLayout.GetFreeBands(hwnd, strip);
            DispatcherQueue.TryEnqueue(() => OnFreeBandsReady(bands));
        });
    }

    private void OnFreeBandsReady(Windows.Graphics.RectInt32[] bands)
    {
        _freeBandsInFlight = false;
        bool changed = !bands.SequenceEqual(_freeBands);
        _freeBands = bands;

        if (bands.Length == 0)
        {
            if (!_freeBandsFallbackLogged)
            {
                _freeBandsFallbackLogged = true;
                _log.Info($"任务栏空闲段：未取到（{TaskbarLayout.LastError ?? "没有空白区"}），落点回退为按条带边缘对齐。");
            }
        }
        else
        {
            _freeBandsFallbackLogged = false;
            if (changed)
            {
                _log.Info("任务栏空闲段（物理像素）：" + string.Join("、", bands.Select(b => $"{b.X}..{b.X + b.Width}")));
            }
        }

        if (!changed) return;

        if (_expanded)
        {
            // 展开中不移动窗口（会打断动画、也会让岛在光标下乱跳）：收起后再落位
            _freeBandsDirty = true;
            return;
        }

        ApplyCanvasBounds();
    }

    /// <summary>
    /// 按对齐方式挑一条空闲段：靠左 = 最左、靠右 = 最右、居中 = 离条带中心最近。没有空闲段时返回 null。
    /// </summary>
    private Windows.Graphics.RectInt32? SelectFreeBand(double stripCenterX)
    {
        if (_freeBands.Length == 0) return null;
        return _horizontal switch
        {
            HorizontalLeft => _freeBands[0],
            HorizontalRight => _freeBands[^1],
            _ => _freeBands.OrderBy(b => Math.Abs(b.X + b.Width / 2.0 - stripCenterX)).First(),
        };
    }

    /// <summary>
    /// 紧凑尺寸：受 <see cref="CompactWidthMax"/> 限制 —— 上限对所有位置模式一致，不随任务栏动态变化。
    /// </summary>
    private static Size CompactSizeOf(IslandLiveContent live)
        => live.CompactSize.Width <= CompactWidthMax
            ? live.CompactSize
            : new Size(CompactWidthMax, live.CompactSize.Height);

    /// <summary>
    /// 定位用的岛体宽度（逻辑像素）：取「当前实际宽度」与「当前内容会占的宽度」中的较大者。
    /// 不能只用 ActualWidth —— 内容刚 SetLive 时宽度动画还没跑，ActualWidth 仍是空闲态宽度，
    /// 落点会按错的宽度算；而动画结束后窗口不会再移动（动画期间绝不移动窗口），于是就停在错的位置。
    /// 这里只用于算落点，尺寸本身由 <see cref="CompactSizeOf"/> 与插件声明决定。
    /// </summary>
    private double PlacementWidth()
    {
        double width = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        if (ActiveLive is { } live) width = Math.Max(width, CompactSizeOf(live).Width);
        return width;
    }

    /// <summary>
    /// 岛体左边缘的目标位置（物理像素）。底部模式优先落进实测空闲段：
    /// 靠左 = 最左空闲段、靠右 = 最右空闲段、居中 = 离屏幕中心最近的空闲段；
    /// 段内放得下就贴对齐侧内边，放不下就段内居中（两侧均摊溢出，压到的东西最少）。
    /// 查不到空闲段时退回「按条带边缘/托盘边界对齐」。
    /// </summary>
    private double ResolveIslandLeft(Win32.RECT strip, double islandPx, double s, bool embedded, int trayLeft)
    {
        if (embedded && SelectFreeBand((strip.Left + strip.Right) / 2.0) is { } band)
        {
            if (islandPx <= band.Width)
            {
                return _horizontal switch
                {
                    HorizontalLeft => band.X + EdgeInset * s,
                    HorizontalRight => band.X + band.Width - EdgeInset * s - islandPx,
                    _ => band.X + (band.Width - islandPx) / 2.0,
                };
            }

            return band.X + (band.Width - islandPx) / 2.0;
        }

        double leftBound = strip.Left + EdgeInset * s;
        double rightBound = trayLeft > 0
            ? trayLeft - EdgeInset * s
            : strip.Right - (embedded ? TrayReserve + EdgeInset : EdgeInset) * s;
        return _horizontal switch
        {
            HorizontalLeft => leftBound,
            HorizontalRight => rightBound - islandPx,
            _ => (strip.Left + strip.Right - islandPx) / 2.0,
        };
    }

    /// <summary>
    /// 应用预留画布的尺寸与位置。只在内容集合变化（活动上岛/临时消息）、设置变化或 DPI 变化时调用，
    /// 绝不在悬停展开/收起动画中调用 —— 见 <see cref="AnimateIslandSize"/>。
    /// </summary>
    private void ApplyCanvasBounds()
    {
        var s = Scale;
        int w = (int)Math.Round(_canvas.Width * s);
        int h = (int)Math.Round(_canvas.Height * s);

        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        Win32.RECT strip = ToRect(display.WorkArea);
        bool embedded = false;
        int trayLeft = 0;
        if (_bottomAnchored)
        {
            // 优先嵌进任务栏条带：与任务栏同一条带、同一高度基准。
            // 任务栏不可用（自动隐藏收起/不在屏幕底部/取不到）时退回屏幕外框底边。
            var outer = ToRect(display.OuterBounds);
            var tray = Win32.GetTaskbarStrip(outer);
            if (tray is { Visible: true } t && t.Docked.Bottom >= outer.Bottom - Win32.SliverPx)
            {
                embedded = true;
                strip = t.Docked;
                trayLeft = t.TrayLeft;
            }
            else
            {
                strip = outer;
            }

            _lastStripBottom = strip.Bottom;
        }

        _stripCenterX = (strip.Left + strip.Right) / 2.0;

        double offset = _settings.Get(_bottomAnchored ? BottomOffsetKey : TopOffsetKey, DefaultOffset);
        int y = _bottomAnchored
            ? strip.Bottom - h + (int)Math.Round((Pad - offset) * s)   // 岛体底边距条带底边 offset
            : strip.Top + (int)Math.Round((offset - Pad) * s);         // 岛体顶边距工作区顶部 offset

        // 水平落点：先算「岛体左边缘」的目标位置（空闲段/对齐落点 + 水平偏移，夹在条带内），
        // 再按当前对齐方式回推窗口 x —— 靠左/靠右时窗口随岛体边缘走，展开只朝一侧生长。
        double hOffset = _settings.Get(HorizontalOffsetKey, 0.0);
        int x;
        if (_horizontal == HorizontalCenter && hOffset == 0
            && !(_bottomAnchored && embedded && _freeBands.Length > 0))
        {
            // 居中且无偏移（默认；底部模式查到空闲段时改走空闲段落点）：与改动前逐像素一致
            x = strip.Left + (strip.Right - strip.Left - w) / 2;
        }
        else
        {
            double islandWidth = PlacementWidth();
            double islandPx = islandWidth * s;
            double islandLeft = Math.Clamp(
                ResolveIslandLeft(strip, islandPx, s, embedded, trayLeft) + hOffset * s,
                strip.Left,
                strip.Right - islandPx);
            x = (int)Math.Round(islandLeft - IslandLeftInWindow(s, islandWidth));
        }

        bool sizeChanged = w != _windowPhysW || h != _windowPhysH;
        _windowPhysW = w;
        _windowPhysH = h;
        _appliedScale = s;

        if ((_slideOriginIslandTop is not null || _slideOriginIslandLeft is not null) && !sizeChanged)
        {
            // 方向/水平对齐刚切换：先瞬移到「岛体屏幕位置不变」的起点（同一 UI 回合内完成，
            // 中间帧不可见），再缓动滑到终点。只移动窗口，尺寸不变。
            double islandWidth = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
            double islandHeight = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;

            int fromX = _slideOriginIslandLeft is double islandLeft0
                ? (int)Math.Round(islandLeft0 - IslandLeftInWindow(s, islandWidth))
                : AppWindow.Position.X;
            int fromY = _slideOriginIslandTop is double islandTop0
                ? (int)Math.Round(islandTop0 - IslandTopInWindow(_bottomAnchored, s, islandHeight))
                : AppWindow.Position.Y;

            _slideOriginIslandTop = null;
            _slideOriginIslandLeft = null;
            StartPositionSlide(fromX, fromY, x, y);
        }
        else
        {
            _slideOriginIslandTop = null;
            _slideOriginIslandLeft = null;
            StopPositionSlide();
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));

            if (sizeChanged)
                Win32.RemoveDwmBorder(_hwnd);
        }

        // resize 后新长出来的表面默认是不透明白，擦成透明，避免被形状暴露时闪白
        Win32.ClearClientTransparent(_hwnd);

        UpdateHitRegion(force: true);
    }

    // ---- 位置切换滑动：只移动窗口，绝不改窗口尺寸（改尺寸会让 DWM 用上一帧合成，出现残影）----

    private void StartPositionSlide(int fromX, int fromY, int toX, int toY)
    {
        StopPositionSlide();

        // 先瞬移到起点：岛体屏幕位置与切换前重合
        AppWindow.Move(new Windows.Graphics.PointInt32(fromX, fromY));
        if (fromX == toX && fromY == toY) return;

        _slideFromX = fromX;
        _slideFromY = fromY;
        _slideToX = toX;
        _slideToY = toY;
        _slideStartTick = Environment.TickCount64;
        _positionSlide ??= CreateSlideTimer();
        _positionSlide.Start();
    }

    private DispatcherQueueTimer CreateSlideTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => PositionSlideTick();
        return timer;
    }

    private void PositionSlideTick()
    {
        double p = (Environment.TickCount64 - _slideStartTick) / (double)PositionSlideMs;
        if (p >= 1)
        {
            StopPositionSlide();
            AppWindow.Move(new Windows.Graphics.PointInt32(_slideToX, _slideToY));
            UpdateHitRegion(force: true);
            return;
        }

        double eased = 1 - Math.Pow(1 - p, 3);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            _slideFromX + (int)Math.Round((_slideToX - _slideFromX) * eased),
            _slideFromY + (int)Math.Round((_slideToY - _slideFromY) * eased)));
    }

    private void StopPositionSlide() => _positionSlide?.Stop();

    // ---- 任务栏跟随（只在底部模式运行）----

    /// <summary>
    /// 岛体（含队列卡片）在屏幕上的外接矩形：置顶自愈判定「谁把岛压在下面」用。
    /// 直接取点击形状矩形（客户区坐标）平移窗口位置，天然只覆盖真正可见的那块。
    /// </summary>
    private Win32.RECT IslandScreenRect()
    {
        var pos = AppWindow.Position;
        if (_hitRects.Count == 0)
        {
            double s = Scale;
            double ih = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;
            int top = pos.Y + (int)IslandTopInWindow(_bottomAnchored, s, ih);
            return new Win32.RECT
            {
                Left = pos.X,
                Top = top,
                Right = pos.X + _windowPhysW,
                Bottom = top + (int)Math.Round(ih * s),
            };
        }

        int left = int.MaxValue, top2 = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var rect in _hitRects)
        {
            left = Math.Min(left, rect.X1);
            top2 = Math.Min(top2, rect.Y1);
            right = Math.Max(right, rect.X2);
            bottom = Math.Max(bottom, rect.Y2);
        }

        return new Win32.RECT
        {
            Left = pos.X + left,
            Top = pos.Y + top2,
            Right = pos.X + right,
            Bottom = pos.Y + bottom,
        };
    }

    private DispatcherQueueTimer CreateTaskbarWatch()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(TaskbarPollMs);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => TaskbarWatchTick();
        return timer;
    }

    /// <summary>
    /// 跟踪任务栏：自动隐藏/全屏时任务栏滑出屏幕，嵌进条带的岛必须一起收起 —— 否则它会压住
    /// 屏幕底边的唤出热区，任务栏再也叫不出来；任务栏弹回来时岛跟着回来。
    /// 条带移动/变高/换分辨率时重新贴合；Explorer 重启/全屏切换会把窗口踢出置顶带，顺手自愈。
    /// </summary>
    private void TaskbarWatchTick()
    {
        if (!_bottomAnchored) return;

        var outer = ToRect(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).OuterBounds);
        var tray = Win32.GetTaskbarStrip(outer);
        // 只有「停靠在屏幕底部、但被收起」的任务栏才让岛跟着收起（自动隐藏/全屏）。
        // 停靠位置看 ABM_GETTASKBARPOS 的停靠矩形 —— 任务栏收起时它仍报告停靠位置；
        // 任务栏不在底部（Win10 风格顶部任务栏）或根本没有任务栏时，岛退回屏幕底边并保持显示。
        bool hidden = tray is { } t && !t.Visible && t.Docked.Bottom >= outer.Bottom - Win32.SliverPx;

        if (hidden != _taskbarHidden)
        {
            _taskbarHidden = hidden;
            UpdateVisibility();
            if (!hidden) ApplyCanvasBounds();
            return;
        }
        if (hidden) return;

        // 展开期间查到的新落点，等收起后再应用 —— 绝不在动画中移动窗口
        if (_freeBandsDirty && !_expanded)
        {
            _freeBandsDirty = false;
            ApplyCanvasBounds();
            return;
        }

        // 只在条带真的变了才动窗口：4 次/秒的无谓 MoveAndResize 会白白惊动 DWM
        if (tray is { } strip && _lastStripBottom != strip.Docked.Bottom)
        {
            _lastStripBottom = strip.Docked.Bottom;
            RefreshFreeBands(force: true);      // 条带位置/分辨率变了，空闲段坐标要重新量
            ApplyCanvasBounds();
        }
        else
        {
            HealTopmost();
        }
    }

    /// <summary>
    /// 置顶自愈：被任何可见的相交窗口压在下面时立刻重升，并把元凶记进日志（同原因 2 秒内只记一次）。
    /// 触发点：250ms 轮询 + 前台窗口变化事件（任务栏/开始菜单/搜索抬层时不用等一个轮询周期）。
    /// </summary>
    private void HealTopmost()
    {
        if (Win32.EnsureTopmost(_hwnd, IslandScreenRect()) is not { } coverer) return;

        _topmostHealCount++;
        if (coverer == _topmostHealLast && Environment.TickCount64 - _topmostHealLoggedAt < 2000) return;
        _topmostHealLast = coverer;
        _topmostHealLoggedAt = Environment.TickCount64;
        _log.Warn($"岛被「{coverer}」压在下面，已重新置顶（本次运行第 {_topmostHealCount} 次）。");
    }

    /// <summary>
    /// 把岛屿的「真实布局形状」写入窗口形状（SetWindowRgn）：形状之外不绘制也不参与命中测试，
    /// 透明画布区域的点击/触摸因此会真正落到下层窗口（跨进程有效）。
    /// 形状按岛体的圆角生成 —— 若用矩形近似，圆角外侧那几块像素会被窗口表面填充成白色实块。
    /// 必须跟随尺寸动画逐帧更新，且绝不能为空：没有形状时整块画布都会拦截点击。
    /// </summary>
    private void UpdateHitRegion(bool force = false)
    {
        // 窗口还没定位（首次应用之前）时岛体位置无从计算，形状先不下发 ——
        // 否则底部模式会算出负数 y 的空形状/错形状。
        if (_windowPhysW <= 0 || _windowPhysH <= 0) return;

        var s = Scale;
        var rects = new List<ShapeRect>(MaxExpandedItems);

        // 主岛（空闲 / 活动 / 临时消息都走这里）：形状必须与岛体「当前渲染尺寸」严格一致。
        // 绝不能取动画目标尺寸 —— 目标比内容多出来的那一圈会落进形状里，
        // 由窗口表面填成不透明白色，切换瞬间就会闪白块。
        double iw = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        double ih = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;
        double ix = IslandLeftInWindow(s, iw);
        double iy = IslandTopInWindow(_bottomAnchored, s, ih);
        rects.Add(new ShapeRect(
            (int)Math.Round(ix), (int)Math.Round(iy),
            (int)Math.Round(ix + iw * s), (int)Math.Round(iy + ih * s),
            (int)Math.Round(IslandRoot.CornerRadius.TopLeft * s)));

        // 队列小岛：各子元素的实际布局矩形与圆角（DIP → 物理）
        foreach (var child in QueuePanel.Children)
        {
            if (child is not Border card || card.ActualWidth <= 0 || card.ActualHeight <= 0)
                continue;

            var box = card.TransformToVisual(RootGrid)
                          .TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            rects.Add(new ShapeRect(
                (int)Math.Round(box.X * s), (int)Math.Round(box.Y * s),
                (int)Math.Round((box.X + box.Width) * s), (int)Math.Round((box.Y + box.Height) * s),
                (int)Math.Round(card.CornerRadius.TopLeft * s)));
        }

        if (rects.Count == 0 || (!force && SameRects(_hitRects, rects))) return;
        _hitRects = rects;

        // 主机制：窗口形状 —— 跨进程、触控都生效的点击穿透
        Win32.ApplyWindowShape(_hwnd, BuildRegion(rects));
        // 兜底：WM_NCHITTEST 拦截（HTTRANSPARENT 只对同线程窗口有效，例如设置窗与岛重叠时）
        Win32.SetHitRegion(BuildRegion(rects));
    }

    private static bool SameRects(IReadOnlyList<ShapeRect> a, IReadOnlyList<ShapeRect> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>把一组圆角矩形合并成单个 GDI 区域。调用方保证 rects 非空。</summary>
    private static nint BuildRegion(IReadOnlyList<ShapeRect> rects)
    {
        var first = Win32.CreateRoundRectRegion(rects[0].X1, rects[0].Y1, rects[0].X2, rects[0].Y2, rects[0].Radius * 2, rects[0].Radius * 2);
        for (int i = 1; i < rects.Count; i++)
        {
            var next = Win32.CreateRoundRectRegion(rects[i].X1, rects[i].Y1, rects[i].X2, rects[i].Y2, rects[i].Radius * 2, rects[i].Radius * 2);
            Win32.CombineRgnInPlace(first, next);
            Win32.DeleteRegion(next);
        }
        return first;
    }

    /// <summary>形状用矩形（物理像素）：X1,Y1,X2,Y2 为外接矩形，Radius 为圆角半径。</summary>
    private readonly record struct ShapeRect(int X1, int Y1, int X2, int Y2, int Radius);

    private void UpdateVisibility(bool force = false)
    {
        bool hideIdle = _settings.Get("island.hideWhenIdle", false);
        bool visible = _settings.Get("island.visible", true)
                       && !(_bottomAnchored && _taskbarHidden)
                       && !(hideIdle && ActiveLive == null && _tempContent == null);
        if (!force && visible == _shown) return;
        _shown = visible;
        if (visible)
        {
            AppWindow.Show(false);
            _presenter.IsAlwaysOnTop = false;
            _presenter.IsAlwaysOnTop = true;
            Win32.MakeIslandStyle(_hwnd);
            // MakeIslandStyle 里的 SWP_FRAMECHANGED 可能让窗口形状失效，重新断言一次
            UpdateHitRegion(force: true);
        }
        else
        {
            StopPositionSlide();
            _hoverGuard.Stop();
            _hover = false;
            AppWindow.Hide();
        }
    }

    private void OnSettingChanged(string key)
    {
        // 宿主级优先级（plugin.<id>.priority）：立即重排活动并重建当前状态
        if (key.StartsWith("plugin.", StringComparison.Ordinal) && key.EndsWith(".priority", StringComparison.Ordinal))
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(ApplyPriorityChange);
                return;
            }
            ApplyPriorityChange();
            return;
        }

        if (!key.StartsWith("island.", StringComparison.Ordinal)) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshFromSettings);
            return;
        }
        RefreshFromSettings();
    }

    /// <summary>优先级变化后重排活动并重建当前状态（临时消息显示期间留到它结束后自然生效）。</summary>
    private void ApplyPriorityChange()
    {
        SortLive();
        if (_tempContent != null) return;
        TransitionToLiveState();
        UpdateVisibility();
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
        double ix = pos.X + IslandLeftInWindow(s, _currentTotalSize.Width);
        double iy = _bottomAnchored
            ? pos.Y + size.Height - (Pad + _currentTotalSize.Height) * s
            : pos.Y + Pad * s;
        const double slack = 6;
        return pt.X >= ix - slack && pt.X <= ix + iw + slack
               && pt.Y >= iy - slack && pt.Y <= iy + ih + slack;
    }

    #endregion

    #region 内置视图

    private static UIElement BuildIdleContent(Ellipse dot)
    {
        var grid = new Grid();
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

    #region 岛体表面（主岛与队列卡片共用）

    /// <summary>队列卡片：与主岛同款表面（底衬 + 描边 + 内容）。</summary>
    private static IslandSurface CreateCardSurface() => IslandSurface.CreateCard();

    /// <summary>
    /// 岛体表面：底衬、描边与内容，主岛与队列卡片共用同一套外观规则。
    /// 材质是窗口级的（见 <see cref="ApplyBackdrop"/>），这里只负责圆角、底色与描边 ——
    /// 圆角会被 <see cref="UpdateHitRegion"/> 读走，是点击穿透形状的唯一来源。
    /// </summary>
    private sealed class IslandSurface
    {
        private readonly ContentPresenter _content;

        public IslandSurface(Border border, ContentPresenter content)
        {
            Border = border;
            _content = content;
        }

        /// <summary>独立卡片：Border 与内容宿主自己搭起来（主岛的两个元素来自 XAML）。</summary>
        public static IslandSurface CreateCard()
        {
            var content = new ContentPresenter();
            return new IslandSurface(new Border { Child = content }, content);
        }

        public Border Border { get; }

        /// <summary>卡片对应的活动：拆卸时按这个记录停止形态动画，顺序变化也安全。</summary>
        public string? Owner { get; set; }

        /// <summary>卡片内容的形态视图（可能为 null，例如只有双视图的插件）。</summary>
        public IMorphView? MorphView { get; set; }

        public void ApplyStyle(IslandStyleKind style, bool materialApplied)
        {
            var stroke = IslandStyle.CreateStroke(style);
            Border.Background = IslandStyle.CreateSurfaceFill(style, materialApplied);
            Border.BorderBrush = stroke;
            Border.BorderThickness = stroke == null ? new Thickness(0) : new Thickness(1);
        }

        public void SetRadius(double radius) => Border.CornerRadius = new CornerRadius(radius);

        public void SetContent(UIElement? content) => _content.Content = content;
    }

    #endregion
}
