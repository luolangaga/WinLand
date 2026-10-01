using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using WinIsland.Core;
using WinIsland.Core.Plugins;

namespace WinIsland.Island;

/// <summary>
/// 灵动岛悬浮窗：无边框、透明、置顶、不抢焦点。
/// 状态机：空闲 → 紧凑 → 展开 → 临时消息。
/// 小岛时只展示优先级最高的一个活动；展开时其余活动按大岛样式排在主岛下方，
/// 一列放满就换到右边一列，每个都是完整的 MorphView 大岛。
/// </summary>
public sealed partial class IslandWindow : Window
{
    private const double Pad = 24;
    /// <summary>岛体与展开队列之间的间距（与 XAML 里 IslandStack 的 RowSpacing 一致）。</summary>
    private const double QueueSpacing = 8;
    /// <summary>展开队列一屏显示几张卡：前面几张固定，最后一张是翻页位（队列表太长时由按钮翻）。</summary>
    private const int QueueVisibleCards = 3;
    /// <summary>队列卡片之间、以及最后一张卡与翻页按钮之间的间距。</summary>
    private const double QueueCardGap = 6;
    /// <summary>队列翻页按钮的直径（正圆）。</summary>
    private const double QueuePagerSize = 32;
    /// <summary>翻页按钮的箭头（Segoe Fluent Icons）：指向远端，也就是「下一张从哪边来」。</summary>
    private const string QueuePagerGlyphDown = "\uE70D";
    private const string QueuePagerGlyphUp = "\uE70E";
    /// <summary>翻页动画：旧卡先走、新卡稍后进，与内容交接同一套节奏（两段错开一点点）。</summary>
    private static readonly TimeSpan PageOutDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PageInDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan PageInDelay = TimeSpan.FromMilliseconds(70);
    /// <summary>翻页时新卡内容层的起始缩放：只缩不放（恒 ≤ 1），所以永远不会越出卡片矩形。</summary>
    private const double PageEnterScale = 0.97;
    private static readonly string[] ScaleProperties = { "ScaleX", "ScaleY" };
    private static readonly Size IdleBaseSize = new(128, 34);

    // 缩放比（百分比，设置项）：大岛（活动/展开/队列/消息/投放面板）、空闲小岛、全屏小色条各自独立
    private const string ScaleIslandKey = "island.scale.island";
    private const string ScaleIdleKey = "island.scale.idle";
    private const string ScaleStripKey = "island.scale.strip";
    internal const int ScaleMinPercent = 50;
    internal const int ScaleMaxPercent = 200;

    // 全屏应用时的行为：normal = 照常显示，strip = 隐藏岛体、屏幕边缘留小色条，hide = 完全隐藏
    private const string FullscreenKey = "island.fullscreen";
    internal const string FullscreenNormal = "normal";
    internal const string FullscreenStrip = "strip";
    internal const string FullscreenHide = "hide";
    /// <summary>鼠标离开岛体（及色条）多久后，全屏下临时现身的岛体重新收回。</summary>
    private const long FullscreenPeekGraceMs = 700;
    /// <summary>色条靠近判定向屏幕内侧多扩的距离（DIP，色条本身只有几像素，不好瞄）。</summary>
    private const double StripReachDip = 14;

    /// <summary>空闲小岛尺寸 = 基准尺寸 × island.scale.idle（相对大岛缩放再叠加一层）。</summary>
    private Size IdleSize
    {
        get
        {
            double k = ReadScale(ScaleIdleKey);
            return new Size(Math.Round(IdleBaseSize.Width * k), Math.Round(IdleBaseSize.Height * k));
        }
    }

    private double ReadScale(string key)
        => Math.Clamp(_settings.Get(key, 100), ScaleMinPercent, ScaleMaxPercent) / 100.0;
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(340);
    private const double MinCanvasWidth = 420;
    /// <summary>临时内容（消息 / 插件自定义内容）的尺寸 morph：比悬停展开略长一点，与内容交接同拍落地。</summary>
    private static readonly TimeSpan TemporaryMorphDuration = TimeSpan.FromMilliseconds(340);

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
    /// <summary>岛锚定到哪块显示器：auto（跟随鼠标所在屏，默认）或某块屏的稳定 id。</summary>
    private const string DisplayKey = "island.display";
    /// <summary>文件投放总开关（默认开）。关掉后岛对文件拖放完全无感。</summary>
    private const string DropEnabledKey = "island.dropEnabled";
    /// <summary>悬停展开开关（默认开）：关掉后只有点击岛体/触摸才展开。</summary>
    private const string HoverExpandKey = "island.hoverExpand";
    /// <summary>悬停展开的意图延迟（毫秒，默认 0 = 立即展开）。</summary>
    private const string HoverDelayKey = "island.hoverDelay";
    /// <summary>悬停延迟的上限（毫秒）。</summary>
    private const double HoverDelayMaxMs = 600;
    /// <summary>展开/收起是否带回弹（默认开）。关掉后尺寸动画一律纯 EaseOut，不越过目标。</summary>
    private const string BounceKey = "island.bounce";
    /// <summary>关掉临时消息后短暂吞掉点击：双击的第一下已经关掉消息，第二下不该顺手点到插件内容。</summary>
    private const int TapSuppressMs = 400;
    /// <summary>翻页位记住的那张卡（插件 id）：纯界面状态，只用于「下次展开还看这张」。</summary>
    private const string QueueTailKey = "island.queueTail";
    private const double DefaultOffset = 6.0;
    /// <summary>靠左/靠右时距条带（或工作区）边缘的间距。Win11 图标居中排列，任务栏左端通常是空的。</summary>
    private const double EdgeInset = 12;
    /// <summary>底部模式靠右、且量不到托盘区（TrayNotifyWnd）时的兜底预留量。</summary>
    private const double TrayReserve = 220;
    private const int PositionSlideMs = 360;
    /// <summary>聚光卡打开时岛体淡出、关闭后淡入的时长（只动透明度，不碰窗口几何）。</summary>
    private const int SpotlightFadeOutMs = 160;
    private const int SpotlightFadeInMs = 240;
    /// <summary>岛体出现/消失（设置、任务栏、全屏避让、空闲隐藏）时的整块淡入/淡出时长。</summary>
    private const int AppearFadeMs = 260;
    private const int DisappearFadeMs = 180;
    /// <summary>
    /// 看门狗轮询周期：任务栏跟随 + 置顶自愈。shell 会在你点任务栏/开始菜单/搜索时把自己
    /// 抬到所有置顶窗口之上，岛整块被盖住，所以这个周期就是「被盖住到顶回来」的最坏延迟，取短一些。
    /// </summary>
    private const int WatchdogPollMs = 120;
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
    /// <summary>
    /// 最近一条宿主自造的消息（<see cref="ShowMessage"/>）。消息卡的配色是建视图时烘进画刷里的，
    /// 系统换主题时只能按原始数据重建一次（插件自己给的临时内容不在此列，那是插件的事）。
    /// </summary>
    private IslandMessage? _tempMessage;
    /// <summary>
    /// 展开期间临时消息的"队列首卡"身份（合成的一条活动）：消息视图在 ContentHost 与卡片之间搬家，
    /// 收起时再回到主岛。null 表示消息正显示在主岛（或没有消息）。
    /// </summary>
    private IslandLiveContent? _tempCard;

    private readonly DispatcherQueueTimer _tempTimer;
    private readonly DispatcherQueueTimer _hoverGuard;
    /// <summary>悬停展开的意图计时器：指针在岛上停够 island.hoverDelay 才展开（0 = 立即）。</summary>
    private readonly DispatcherQueueTimer _hoverIntent;

    private readonly UIElement _idleContent;

    private bool _shown;
    private bool _hover;
    private bool _expanded;
    /// <summary>在这个时刻之前，岛体点击一律忽略（关掉消息的那一下点击不该顺带点到插件）。</summary>
    private long _tapSuppressUntil;
    /// <summary>
    /// 触控展开：触摸没有悬停语义 —— 手指抬起就是 PointerExited，若按鼠标那样在退出时收起，
    /// 就成了「刚展开就瞬间收起」。所以触控展开是常驻的：不再由鼠标进出裁决，只由
    /// 「点了岛外」（<see cref="Win32.InstallMouseDownWatcher"/>）或前台窗口变化来收。
    /// volatile：全局按下监听跑在钩子线程上，它要读这个标志决定要不要切回 UI 线程。
    /// </summary>
    private volatile bool _touchExpand;
    /// <summary>聚光卡（超级展开）打开期间：岛体整块藏起来，看门狗/悬停也不再驱动它。</summary>
    private bool _spotlightOccluded;
    private int _fadeVersion;
    private Size _currentIsland;
    private int _windowPhysW;
    private int _windowPhysH;
    private Storyboard? _sizeStoryboard;
    private int _animVersion;
    private readonly RectangleGeometry _clip = new();

    // 内容交接（换内容时旧内容淡出、新内容稍后淡入）：见 SwapContent
    private Storyboard? _contentSwap;
    private int _contentSwapVersion;
    private readonly CompositeTransform _contentSlide = new();
    private readonly CompositeTransform _leavingSlide = new();

    // 外观风格（island.style / island.material）：材质、底衬、描边、圆角与空闲点颜色都由它决定
    private readonly IslandSurface _mainSurface;
    private readonly List<IslandSurface> _queueSurfaces = new();
    /// <summary>
    /// 翻页位（展开队列最后一张卡）指向的队列下标与卡面主人（插件 id）。插件 SetLive 刷新会不断重建队列、
    /// 优先级调整会改变顺序，所以这两个值绝不在重建或收起时重置 —— 用户翻到的那张卡要一直记得住：
    /// 重新展开时先按主人定位它，定位不到（插件不在了/被排进固定位）才退回下标。
    /// </summary>
    private int _tailIndex = QueueVisibleCards - 1;
    private string? _tailOwner;
    /// <summary>翻页位当前的卡面（与 <see cref="_tailIndex"/> 同源，翻页动画收尾时要用）。</summary>
    private IslandSurface? _tailSurface;
    /// <summary>队列翻页按钮（队列超过 <see cref="QueueVisibleCards"/> 条时才存在）。</summary>
    private QueuePager? _pager;
    /// <summary>翻页动画：正在退场的旧卡、动画本体与防串台版本号。</summary>
    private IslandSurface? _pageOutgoing;
    private Storyboard? _pageAnim;
    private int _pageAnimVersion;
    private readonly Ellipse _idleDot;
    private IslandStyleKind _style = IslandStyleKind.Apple;
    private IslandMaterialKind _material = IslandMaterialKind.Acrylic;
    /// <summary>系统明暗主题（Fluent 跟随，Apple 忽略）。变化时配色与内容就地重刷，见 <see cref="OnSystemThemeChanged"/>。</summary>
    private bool _systemLight = SystemTheme.IsLight;
    /// <summary>
    /// 生效的岛体明暗 = 外观风格 + 系统主题（<see cref="IslandStyle.IsLightChrome"/>）。
    /// 各处的配色判断都用它，不要再单独读系统主题 —— Apple 风格在浅色系统下依旧是深色岛。
    /// </summary>
    private bool _light;
    /// <summary>已经就"材质不可用"告过警的材质：系统不支持时每次主题切换都会重试，日志不该跟着刷屏。</summary>
    private IslandMaterialKind? _warnedMaterial;
    private bool _styleApplied;
    // 窗口级系统材质（仅 Fluent）：不支持时退回纯色底衬
    private IslandBackdrop? _backdrop;
    private bool _backdropOwned;
    private bool _materialApplied;
    // 最近一次圆角对应的岛体高度与展开态，风格切换时据此按当前状态重算圆角
    private double _radiusHeight = IdleBaseSize.Height;
    private bool _radiusExpanded;

    // 岛体锚定方向：false = 贴工作区顶部（队列向下），true = 贴任务栏条带（队列向上）
    private bool _bottomAnchored;
    private string _horizontal = HorizontalCenter;
    private bool _positionApplied;
    /// <summary>底部模式：任务栏已收起（自动隐藏/全屏）→ 岛一起收起。</summary>
    private bool _taskbarHidden;
    /// <summary>上次贴合的任务栏底边，用于识别条带移动/变高/换分辨率（只在真的变了才动窗口）。</summary>
    private int _lastStripBottom;
    /// <summary>看门狗：置顶自愈（两种位置模式）+ 任务栏跟随（仅底部模式）。</summary>
    private DispatcherQueueTimer? _watchdog;

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
    /// <summary>
    /// 岛当前锚定到的显示器外框（物理像素）。看门狗据此发现「岛跑到别的屏上去了」并拉回来。
    /// 光靠热插拔事件不够：有些环境不广播（远程桌面、驱动更新、部分虚拟机），
    /// 定期核对一次是免费的保险（只是比几个整数）。
    /// </summary>
    private Windows.Graphics.RectInt32 _displayOuter;

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
    private Size _canvas = new(MinCanvasWidth + Pad * 2, IdleBaseSize.Height + Pad * 2);
    // 最近一次应用（或将应用）到窗口的点击穿透形状（物理像素），用于逐帧去重。
    private List<ShapeRect> _hitRects = new();
    // 最近一次应用窗口尺寸时使用的缩放比，用于识别 DPI 变化。
    private double _appliedScale;

    // 主活动（优先级最高）
    private IslandLiveContent? ActiveLive => _live.Count > 0 ? _live[0].Content : null;
    // 队列活动（除主活动外的其余活动）
    private IReadOnlyList<(string Owner, IslandLiveContent Content)> QueueItems
        => _live.Count > 1 ? _live.Skip(1).ToList() : Array.Empty<(string, IslandLiveContent)>();

    /// <summary>临时消息在队列里的"主人"标识（不是插件 id，翻页位永远不会落到它身上）。</summary>
    private const string TempCardOwner = "(message)";

    /// <summary>
    /// 展开队列的条目来源：临时消息的卡片（只有展开期间才存在）排在所有活动之前，
    /// 其余与 <see cref="QueueItems"/> 一致。队列尺寸、卡片高度、翻页与显示条目都读这里。
    /// </summary>
    private IReadOnlyList<(string Owner, IslandLiveContent Content)> QueueSource()
    {
        if (_tempCard == null) return QueueItems;

        var items = new List<(string Owner, IslandLiveContent Content)>(_live.Count) { (TempCardOwner, _tempCard) };
        for (int i = 1; i < _live.Count; i++) items.Add(_live[i]);
        return items;
    }

    /// <summary>
    /// 把当前临时消息包成一条"可入队"的活动：展开时它排在第一张卡（离岛体最近），
    /// 点这张卡就是关掉这条消息。消息视图是同一个实例 —— 它在主岛与卡片之间搬家。
    /// </summary>
    private IslandLiveContent? CreateTempCard()
    {
        if (_tempContent == null) return null;
        return new IslandLiveContent
        {
            OwnerLabel = "消息",
            OwnerGlyph = "\uE8BD",
            CompactContent = _tempContent,
            ExpandedContent = _tempContent,
            CompactSize = _tempSize,
            ExpandedSize = _tempSize,
            OnTap = () => DismissTemporary(),
        };
    }

    public IslandWindow(SettingsService settings, PluginLogService logs)
    {
        _settings = settings;
        _log = logs.Host;
        InitializeComponent();

        _mainSurface = new IslandSurface(IslandRoot, ContentHost);

        // 内容交接用的位移/缩放（只动组合级属性，和尺寸 morph 逐帧共存）
        ContentHost.RenderTransform = _contentSlide;
        ContentHost.RenderTransformOrigin = new Point(0.5, 0.5);
        ContentLeaving.RenderTransform = _leavingSlide;
        ContentLeaving.RenderTransformOrigin = new Point(0.5, 0.5);

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
            // 底色只是初值：构造末尾的 ApplyStyle 会按当前风格与主题重写它
            Fill = new SolidColorBrush(IslandStyle.IdleDotColor(_style, light: false)),
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

        _hoverIntent = DispatcherQueue.CreateTimer();
        _hoverIntent.IsRepeating = false;
        _hoverIntent.Tick += (_, _) => HoverIntentTick();

        // 投放面板：边缘自动滚动（16ms 推进一次）与离开岛体后的宽限收起
        _dropScroll = DispatcherQueue.CreateTimer();
        _dropScroll.Interval = TimeSpan.FromMilliseconds(DropScrollTickMs);
        _dropScroll.IsRepeating = true;
        _dropScroll.Tick += (_, _) => DropScrollTick();

        _dropExitGrace = DispatcherQueue.CreateTimer();
        _dropExitGrace.Interval = TimeSpan.FromMilliseconds(DropExitGraceMs);
        _dropExitGrace.IsRepeating = false;
        _dropExitGrace.Tick += (_, _) => EndDropSession();

        _settings.Changed += OnSettingChanged;
        SystemTheme.Changed += OnSystemThemeChanged;

        // 多屏热插拔：拔/接外接屏、换分辨率、改显示器排列、切投影模式。
        // 系统只广播 DisplayInformation.DisplayContentsInvalidated（任意线程），这里编组到 UI 线程再重新定位。
        DisplayResolver.EnsureInvalidationWatcher();
        DisplayResolver.DisplaysInvalidated += OnDisplaysInvalidatedFromAnyThread;
        Closed += (_, _) =>
        {
            SystemTheme.Changed -= OnSystemThemeChanged;
            DisplayResolver.DisplaysInvalidated -= OnDisplaysInvalidatedFromAnyThread;
            StopPositionSlide();
            _positionSlide = null;
            _watchdog?.Stop();
            _watchdog = null;
            _strip?.Close();
            _strip = null;
            _hoverIntent.Stop();
            _dropScroll.Stop();
            _dropExitGrace.Stop();
            _backdrop?.Dispose();
        };

        _currentIsland = IdleSize;
        IslandRoot.Width = IdleSize.Width;
        IslandRoot.Height = IdleSize.Height;
        SetContentImmediate(_idleContent);
        // 上次翻到的那个活动（插件 id）：队列再展开时优先回到它（可能已被卸载/降级，定位不到就退回默认位）
        _tailOwner = _settings.Get<string?>(QueueTailKey, null);
        ApplyScaleSettings();
        ApplyPositionMode();
        ApplyStyle();
        ApplyDropSetting();
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

        // 临时消息与投放面板都占着岛体：到这里只更新活动集合与画布，内容等它们结束再切。
        // 例外：消息正挂在队列首卡上（展开态）—— 那时主岛显示的是活动内容，得跟着重建。
        if (_tempContent != null || _dropping)
        {
            if (!_dropping && _expanded && _tempContent != null) TransitionToLiveState();
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
        _tempMessage = null;
        _tempCard = null;
        _tempTimer.Stop();
        _tempTimer.Interval = duration;
        _tempTimer.Start();

        // 收起时先安全拆卸队列，再收起主岛（翻页位不动：临时消息结束后还能回到那张卡）
        TeardownQueue();
        _expanded = false;

        // 内容交接（旧内容淡出 → 新内容淡入）与尺寸 morph 同时开始：两者同拍落地才像"一次换气"
        SwapContent(content, animate: true);
        EnsureCanvas();
        AnimateIslandSize(size, TemporaryMorphDuration, isTemporary: true);
        ApplyTemporaryRadius(size.Height);
        UpdateVisibility();
    }

    public void DismissTemporary(string? owner = null)
    {
        if (_tempContent == null) return;
        if (owner != null && _tempOwner != owner) return;
        _tempTimer.Stop();
        _tempContent = null;
        _tempOwner = null;
        _tempMessage = null;
        _tempCard = null;
        EnsureCanvas();
        TransitionToLiveState();
        UpdateVisibility();
    }

    public void ShowMessage(IslandMessage msg, string? owner = null)
    {
        var (view, size) = MessageView.Build(msg, _style, _light);
        ShowTemporary(view, size, msg.Duration, owner);
        // ShowTemporary 会把它清掉（那是对插件内容的语义），宿主消息在这里重新登记
        _tempMessage = msg;
    }

    public void RefreshFromSettings()
    {
        ApplyStyle();
        ApplyPositionMode();
        ApplyDropSetting();
        ApplyScaleSettings();
        UpdateVisibility();
        EnsureCanvas();
        ApplyCanvasBounds();
        UpdateFullscreenState(force: true);
    }

    /// <summary>
    /// 读缩放设置：大岛缩放换了就重挂渲染缩放并重算画布；空闲小岛缩放换了就把空闲岛体改成新尺寸。
    /// 全屏小色条的缩放在落位时现读，不在这里。
    /// </summary>
    private void ApplyScaleSettings()
    {
        double k = ReadScale(ScaleIslandKey);
        if (Math.Abs(k - _islandScale) > 0.0005)
        {
            _islandScale = k;
            ApplyScaleTransform();
        }

        var idle = IdleSize;
        bool idleNow = ActiveLive == null && _tempContent == null && !_dropping && !_expanded;
        if (idleNow && (Math.Abs(_currentIsland.Width - idle.Width) > 0.5 || Math.Abs(_currentIsland.Height - idle.Height) > 0.5))
        {
            AnimateIslandSize(idle, CollapseDuration);
            ApplyCornerRadius(idle.Height, expanded: false);
        }
    }

    /// <summary>主岛当前渲染尺寸（DIP）。聚光卡的飞入起点/收回终点按它做缩放比。</summary>
    internal Size MainIslandSizeDip()
        => new(
            IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width,
            IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height);

    /// <summary>窗口句柄。宿主内置的投放动作要拿它给系统对话框（如「另存为」）当 owner。</summary>
    internal nint Handle => _hwnd;

    /// <summary>
    /// 主岛（不含队列卡片）的屏幕矩形，物理像素。岛体隐藏期间同样可算 ——
    /// 聚光卡收回动画的落点就用它。
    /// </summary>
    internal Win32.RECT MainIslandScreenRect()
    {
        var pos = AppWindow.Position;
        var rect = MainIslandShapeRect(Scale);
        return new Win32.RECT
        {
            Left = pos.X + rect.X1,
            Top = pos.Y + rect.Y1,
            Right = pos.X + rect.X2,
            Bottom = pos.Y + rect.Y2,
        };
    }

    internal IslandStyleKind CurrentStyleKind => _style;

    internal bool IsMaterialApplied => _materialApplied;

    /// <summary>
    /// 聚光卡打开/关闭时遮挡或恢复岛体：这是岛体「被别的东西盖住」的唯一入口，
    /// 只做整块淡入淡出 + 窗口显示/隐藏，绝不改窗口几何（几何不变式优先）。
    /// </summary>
    internal void SetSpotlightOccluded(bool occluded)
    {
        if (occluded == _spotlightOccluded) return;
        _spotlightOccluded = occluded;

        if (occluded)
        {
            // 先摘掉「在场」状态：看门狗与前台监听不会再尝试把岛顶回置顶层（会盖住聚光卡）
            _shown = false;
            TeardownQueue();
            _expanded = false;
            _tempCard = null;
            _hoverIntent.Stop();
            ClearDropSession();
            ClearTouchExpand();
            FadeIslandOpacity(0, SpotlightFadeOutMs, () =>
            {
                if (!_spotlightOccluded) return;
                StopPositionSlide();
                _hoverGuard.Stop();
                _hover = false;
                AppWindow.Hide();
            });
            return;
        }

        UpdateVisibility(force: true, fade: false);
        if (!_shown)
        {
            IslandRoot.Opacity = 1;
            return;
        }

        RestoreSpotlightView();
        IslandRoot.Opacity = 0;
        FadeIslandOpacity(1, SpotlightFadeInMs, null);
    }

    /// <summary>聚光卡关闭后的内容还原：临时消息优先，否则按当前状态回到空闲/紧凑/展开。</summary>
    private void RestoreSpotlightView()
    {
        if (_tempContent != null)
        {
            // 岛体整块正在淡入，内容层直接就位（再套一层内容交接会变成两段淡入叠在一起）
            SetContentImmediate(_tempContent);
            AnimateIslandSize(_tempSize, TemporaryMorphDuration, isTemporary: true);
            ApplyTemporaryRadius(_tempSize.Height);
            return;
        }

        TransitionToLiveState();
    }

    /// <summary>岛体整块淡入/淡出：只动 Opacity 的独立动画，窗口几何全程不变。</summary>
    private void FadeIslandOpacity(double to, int milliseconds, Action? onDone)
    {
        int version = ++_fadeVersion;

        var animation = new DoubleAnimation
        {
            From = IslandRoot.Opacity,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = to >= IslandRoot.Opacity ? EasingMode.EaseOut : EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, IslandRoot);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (version != _fadeVersion) return;
            IslandRoot.Opacity = to;
            onDone?.Invoke();
        };
        storyboard.Begin();
    }

    /// <summary>
    /// 应用外观风格：窗口材质、底衬、描边、圆角与空闲点颜色。风格、材质与系统明暗主题都没变时为 no-op
    /// （其余 island.* 设置改动也会触发刷新，避免每次都重建材质与画刷）。
    ///
    /// 顺带把 <c>RequestedTheme</c> 写到根节点上：Fluent 跟随系统主题，插件视图里所有
    /// <c>{ThemeResource ...}</c>（以及默认前景色）都跟着换成深色/浅色那一套，不必逐个插件去改。
    /// Apple 永远是深色 —— 黑胶囊上的文字必须是白的。
    /// </summary>
    private void ApplyStyle()
    {
        var style = IslandStyle.ParseStyle(_settings.Get(IslandStyle.StyleKey, IslandStyle.AppleValue));
        var material = IslandStyle.ParseMaterial(_settings.Get(IslandStyle.MaterialKey, IslandStyle.AcrylicValue));
        _systemLight = SystemTheme.IsLight;
        bool light = IslandStyle.IsLightChrome(style, _systemLight);
        if (_styleApplied && style == _style && material == _material && light == _light) return;

        _style = style;
        _material = material;
        _light = light;
        _styleApplied = true;

        RootGrid.RequestedTheme = IsLightChrome ? ElementTheme.Light : ElementTheme.Dark;

        ApplyBackdrop();
        _mainSurface.ApplyStyle(_style, _materialApplied, _light);
        ApplyCornerRadius(_radiusHeight, _radiusExpanded);
        RefreshQueueStyle();
        UpdateHitRegion(force: true);
        _idleDot.Fill = new SolidColorBrush(IslandStyle.IdleDotColor(_style, _light));
        _log.Debug($"外观已应用：风格 {_style.ToValue()}，材质 {_material.ToValue()}"
                   + $"{(_materialApplied ? "" : "（不可用，已回退纯色底衬）")}，岛体明暗 {(_light ? "浅色" : "深色")}。");
    }

    /// <summary>岛体是否按浅色配色渲染（= <see cref="_light"/>）：聚光卡、插件侧也都取这一个值。</summary>
    internal bool IsLightChrome => _light;

    /// <summary>
    /// 系统明暗主题变化：就地重刷配色，并把已经画在屏幕上的宿主内容按新主题重建
    /// （画刷是建视图时烘进去的，改不动）。插件视图不需要重建 —— 根节点的 RequestedTheme 已经翻了，
    /// 插件自己的 <see cref="WinIsland.Core.IIslandTheme.Changed"/> 会通知到它。
    /// </summary>
    private void OnSystemThemeChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnSystemThemeChanged);
            return;
        }

        if (SystemTheme.IsLight == _systemLight) return;

        bool wasLight = _light;
        ApplyStyle();
        // Apple 风格不跟随系统主题：系统换色与岛体无关，屏幕上的内容也别动
        if (wasLight == _light) return;

        if (_tempMessage is { } msg)
        {
            var (view, _) = MessageView.Build(msg, _style, _light);
            _tempContent = view;
            if (_tempCard != null)
            {
                // 消息正挂在队列首卡上（展开态）：换掉卡上的视图，主岛显示的是活动内容，不能动
                _tempCard = CreateTempCard();
                BuildQueue();
            }
            else
            {
                SetContentImmediate(view);
            }
        }

        if (_dropping) RebuildDropPanel();
    }

    /// <summary>
    /// 窗口级材质。Fluent 用控制器直接挂 Desktop Acrylic / Mica（并告诉它当前是深色还是浅色主题）——
    /// 岛体与队列卡片都在窗口形状内，材质因此只出现在岛屿轮廓里。Apple 保持原来的透明画布背景（材质让位）。
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

        if (_backdrop.TryApply(_material, _light))
        {
            _materialApplied = true;
            return;
        }

        if (_warnedMaterial != _material)
        {
            _warnedMaterial = _material;
            _log.Warn($"当前系统不支持 {_material.ToValue()} 材质，已回退为纯色底衬。");
        }

        _backdrop.Clear();
        _materialApplied = false;
        SystemBackdrop = new TransparentBackdrop();
        _backdropOwned = false;
    }

    /// <summary>就地刷新队列卡片与翻页按钮的外观（不重放形态动画）。</summary>
    private void RefreshQueueStyle()
    {
        foreach (var surface in _queueSurfaces)
        {
            surface.ApplyStyle(_style, _materialApplied, _light);
            surface.SetRadius(IslandStyle.ResolveQueueRadius(_style, surface.Border.Height));
        }

        _pager?.ApplyStyle(_style, _materialApplied, _light);
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

        var queue = QueueSource();
        if (expanded && queue.Count > 0)
        {
            // 单列：宽度恒为主岛宽（翻页按钮更窄、水平居中），高度 = 主岛 + 一屏卡片 +（有按钮时的）按钮行
            int shown = Math.Min(queue.Count, QueueVisibleCards);
            double height = main.Height + QueueSpacing
                            + shown * QueueCardHeight() + (shown - 1) * QueueCardGap
                            + (QueuePagerVisible ? QueueCardGap + QueuePagerSize : 0);
            return new Size(Math.Max(main.Width, StackWidth()), height);
        }

        return main;
    }

    /// <summary>
    /// 展开队列里还有没显示出来的条目 → 末尾要挂一个翻页按钮。
    /// 条目来源含主岛之外的队列与临时消息首卡（见 <see cref="QueueSource"/>）。
    /// </summary>
    private bool QueuePagerVisible => QueueSource().Count > QueueVisibleCards;

    /// <summary>
    /// 展开队列要渲染的条目（从岛侧到远端）：前 <see cref="QueueVisibleCards"/> - 1 条固定，
    /// 最后一条是翻页位 —— 队列表更长时，它在剩余活动里循环（见 <see cref="PageQueueTail"/>）。
    /// </summary>
    private IReadOnlyList<(string Owner, IslandLiveContent Content)> QueueDisplayItems()
    {
        var queue = QueueSource();
        int q = queue.Count;
        int shown = Math.Min(q, QueueVisibleCards);
        var list = new List<(string, IslandLiveContent)>(shown);
        for (int i = 0; i < shown - 1; i++) list.Add(queue[i]);
        if (shown > 0) list.Add(queue[q > QueueVisibleCards ? ResolveTailIndex(queue) : shown - 1]);
        return list;
    }

    /// <summary>
    /// 翻页位下标：先按记住的主人（插件 id）在当前队列表里定位 —— 这样刷新或优先级调整换了顺序，
    /// 翻页位也跟着那张卡走；定位不到（插件不在了、或被排进固定位）再退回上次的下标，
    /// 并夹到合法范围（永不低于默认位，队列变短时退到最后一条）。
    /// </summary>
    private int ResolveTailIndex(IReadOnlyList<(string Owner, IslandLiveContent Content)> queue)
    {
        if (_tailOwner != null)
        {
            for (int i = QueueVisibleCards - 1; i < queue.Count; i++)
            {
                if (queue[i].Owner == _tailOwner) return i;
            }
        }

        return Math.Clamp(_tailIndex, QueueVisibleCards - 1, Math.Max(QueueVisibleCards - 1, queue.Count - 1));
    }

    /// <summary>展开态主岛尺寸：高度由活动自己决定，宽度统一到栈宽（与卡片边缘对齐）。</summary>
    private Size ExpandedMainSize()
        => new(StackWidth(), ActiveLive?.ExpandedSize.Height ?? IdleSize.Height);

    /// <summary>展开态的统一宽度：主岛与所有队列卡片（跨列也一样）取最大展开宽度，边缘对齐。</summary>
    private double StackWidth()
    {
        double width = ActiveLive?.ExpandedSize.Width ?? IdleSize.Width;
        foreach (var (_, content) in _live)
        {
            width = Math.Max(width, content.ExpandedSize.Width);
        }
        return width;
    }

    /// <summary>
    /// 队列卡片的统一高度：取**所有**队列活动里最高的一张 —— 翻页位可能轮到任何一个活动，
    /// 只看当前展示的几张会让翻页时卡片高度（进而窗口几何）跟着变。
    /// </summary>
    private double QueueCardHeight()
    {
        double height = 0;
        foreach (var (_, content) in QueueSource())
        {
            height = Math.Max(height, content.ExpandedSize.Height);
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
        double w = Math.Max(MinCanvasWidth, Math.Max(IdleSize.Width, MessageView.MaxSize.Width));
        double h = Math.Max(IdleSize.Height, MessageView.MaxSize.Height);

        foreach (var (_, content) in _live)
        {
            w = Math.Max(w, Math.Max(content.CompactSize.Width, content.ExpandedSize.Width));
            h = Math.Max(h, Math.Max(content.CompactSize.Height, content.ExpandedSize.Height));
        }

        var expandedTotal = ComputeTotalSize(expanded: true);
        w = Math.Max(w, expandedTotal.Width);
        h = Math.Max(h, expandedTotal.Height);

        // 投放面板：尺寸是常量，启动时就预留出来 —— 拖拽开始时才扩容会让窗口在 OLE 拖放中途 resize
        w = Math.Max(w, DropPanelSize.Width);
        h = Math.Max(h, DropPanelSize.Height);

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
        // 投放会话进行中就切回投放面板：临时消息结束、聚光卡关闭等路径都会走到这里
        if (_dropping)
        {
            ShowDropPanel();
            return;
        }

        var live = ActiveLive;
        bool wantExpand = _hover || _touchExpand;

        // 临时消息：展开时改挂队列首卡（主岛让给活动内容），收起时回到主岛
        if (_tempContent != null && !wantExpand)
        {
            _expanded = false;
            _tempCard = null;
            TeardownQueue();
            SwapContent(_tempContent, animate: true);
            AnimateIslandSize(_tempSize, TemporaryMorphDuration, isTemporary: true);
            ApplyTemporaryRadius(_tempSize.Height);
            return;
        }

        if (live == null)
        {
            _expanded = false;
            // 没有活动内容了，常驻展开没有意义：留着它会让下一个注册的插件凭空自动展开
            ClearTouchExpand();
            TeardownQueue();
            SwapContent(_idleContent, animate: true);
            AnimateIslandSize(IdleSize, CollapseDuration);
            ApplyCornerRadius(IdleSize.Height, expanded: false);
            return;
        }

        // 展开态如果还带着临时消息：它此刻应该是队列首卡
        bool tempCard = _tempContent != null && wantExpand;
        if (tempCard) _tempCard = CreateTempCard();

        if (live.MorphView != null)
        {
            // 确保 View 在 ContentHost 里（同一棵树内换位时交接会自动跳过动画）
            if (tempCard) SwapContentForTempCard(live.MorphView.View);
            else SwapContent(live.MorphView.View, animate: true);
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
                if (tempCard) SwapContentForTempCard(live.ExpandedContent);
                else SwapContent(live.ExpandedContent, animate: true);
                AnimateIslandSize(ExpandedMainSize(), ExpandDuration);
                ApplyCornerRadius(live.ExpandedSize.Height, expanded: true);
                BuildQueue();
            }
            else
            {
                _expanded = false;
                TeardownQueue();
                SwapContent(live.CompactContent ?? _idleContent, animate: true);
                Size compact = CompactSizeOf(live);
                AnimateIslandSize(compact, CollapseDuration);
                ApplyCornerRadius(compact.Height, expanded: false);
            }
        }
    }

    /// <summary>
    /// 展开时把主岛内容从临时消息换成活动内容：消息要立刻改挂队列首卡，所以它不参加退场动画
    /// （退场动画会把它钉在 ContentLeaving 层 130ms，本次重建里的卡片就拿不到它了）。
    /// 进场动画照常：新内容稍后淡入，读起来依旧是"岛换了口气"。
    /// </summary>
    private void SwapContentForTempCard(UIElement? next)
    {
        SwapContent(next, animate: true);
        // 退场层立刻腾空：它上面那份就是马上要挂进卡片的消息视图。
        // 动画自身继续跑（目标没了也无害），收尾时还会再清一次。
        ClearLeaving();
    }

    /// <summary>
    /// 构建队列：单列。前 <see cref="QueueVisibleCards"/> - 1 条固定，最后一张是翻页位；
    /// 队列还有没显示出来的活动，就在末尾挂一个翻页按钮（<see cref="PageQueueTail"/>）。
    /// 卡片一律直接以静止态落位：形状内的像素必须始终被不透明内容盖满（见 AGENTS.md 队列那条），
    /// 任何「展开时淡入 / 位移」都会让点击形状里没被画到的那块被窗口材质/白底填成一片白。
    /// </summary>
    private void BuildQueue()
    {
        // 活动顺序可能刚变过（优先级调整）：先按各自记录的视图安全拆掉旧卡片，再重建
        TeardownQueue();

        var items = QueueDisplayItems();
        if (items.Count == 0)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            UpdateHitRegion(force: true);
            return;
        }

        double cardWidth = StackWidth();
        double cardHeight = QueueCardHeight();
        bool pager = QueuePagerVisible;
        int rows = items.Count + (pager ? 1 : 0);

        // 行定义按实际需要一次性给足（旧卡片已在 TeardownQueue 里清空）：
        // 少一行会漏掉行间距，多一行会让空行的间距挤进总高度（总尺寸算的是 rows - 1 个间距）。
        QueuePanel.RowSpacing = QueueCardGap;
        QueuePanel.RowDefinitions.Clear();
        for (int r = 0; r < rows; r++)
        {
            QueuePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var surface = CreateQueueCard(item.Owner, item.Content, cardWidth, cardHeight);
            // 自下而上（底部模式）/ 自上而下（顶部模式）填：最先入队的活动永远离岛体最近
            Grid.SetRow(surface.Border, _bottomAnchored ? rows - 1 - i : i);
            PlaceQueueCard(surface);
            // 列表最后一张就是翻页位（队列表更长时它才会被按钮换掉）
            if (i == items.Count - 1) _tailSurface = surface;
        }

        if (pager)
        {
            _pager = BuildQueuePager();
            // 按钮在最后一张卡的外侧：顶部模式在下面（最后一行），底部模式在上面（第一行）
            Grid.SetRow(_pager.Border, _bottomAnchored ? 0 : rows - 1);
            QueuePanel.Children.Add(_pager.Border);
        }

        QueuePanel.Visibility = Visibility.Visible;
        UpdateHitRegion(force: true);
    }

    /// <summary>建一张队列卡片：与主岛同款表面 + 该活动的展开内容 + 它自己的点击回调。</summary>
    private IslandSurface CreateQueueCard(string owner, IslandLiveContent content, double width, double height)
    {
        UIElement inner;
        if (content.MorphView != null)
        {
            inner = content.MorphView.View;
        }
        else
        {
            inner = content.ExpandedContent ?? content.CompactContent ?? BuildFallbackLabel(owner, content);
        }

        var surface = CreateCardSurface();
        surface.Owner = owner;
        surface.MorphView = content.MorphView;
        surface.Border.Width = width;
        surface.Border.Height = height;
        surface.ApplyStyle(_style, _materialApplied, _light);
        surface.SetRadius(IslandStyle.ResolveQueueRadius(_style, height));
        surface.SetContent(inner);

        // 队列卡片也是"那个插件的岛"：点它要触发它自己的 OnTap（通常就是打开它的聚光卡）。
        // 以前只有主岛接了点击，导致排在后面的插件永远打不开自己的超大卡。
        var tapped = content.OnTap;
        if (tapped != null)
        {
            surface.Border.Tag = owner;      // 便于排错时看清是哪张卡
            surface.Border.Tapped += (_, e) =>
            {
                // 卡片里的按钮/滑块自己处理点击，不算"点了卡片"
                if (IsInteractiveSource(e.OriginalSource)) return;
                e.Handled = true;
                try
                {
                    tapped();
                }
                catch (Exception ex)
                {
                    _log.Error($"插件「{owner}」的点击回调抛异常（已忽略）", ex);
                }
            };
        }

        return surface;
    }

    /// <summary>
    /// 把卡片放进面板。形态动画必须在视图进入可视树之后再启动
    /// （否则插件视图可能在未加载状态下执行属性路径动画而失败）。
    /// </summary>
    private void PlaceQueueCard(IslandSurface surface)
    {
        QueuePanel.Children.Add(surface.Border);
        _queueSurfaces.Add(surface);
        AnimateMorphView(surface.Owner ?? "(未知)", surface.MorphView, expand: true);
    }

    /// <summary>建翻页按钮：箭头指向远端，也就是「下一张从哪边来」。</summary>
    private QueuePager BuildQueuePager()
    {
        var pager = new QueuePager(
            QueuePagerSize,
            _bottomAnchored ? QueuePagerGlyphUp : QueuePagerGlyphDown,
            (_, _) => PageQueueTail());
        pager.ApplyStyle(_style, _materialApplied, _light);
        return pager;
    }

    /// <summary>
    /// 翻页：只把最后一张卡（翻页位）换成队列里的下一条隐藏活动，到末尾回卷到默认位 ——
    /// 主岛与前面几张固定不动。全程只动 Opacity 与内容层的缩放（组合级，且恒不越出卡片矩形），
    /// 所以窗口几何、点击形状都不用跟着走，也不会出现被裁掉的一条或白边。
    /// </summary>
    private void PageQueueTail()
    {
        if (_dropping || !_expanded) return;

        var queue = QueueSource();
        int q = queue.Count;
        if (q <= QueueVisibleCards) return;               // 没有隐藏项（按钮本不该在）

        FinishQueuePage();                                 // 连点：先把上一页收尾

        int hidden = q - (QueueVisibleCards - 1);
        int tail = (QueueVisibleCards - 1) + ((ResolveTailIndex(queue) - (QueueVisibleCards - 1) + 1) % hidden);
        var item = queue[tail];

        var outgoing = _tailSurface;
        int tailRow = outgoing != null
            ? Grid.GetRow(outgoing.Border)
            : (_bottomAnchored ? 1 : Math.Max(0, QueuePanel.RowDefinitions.Count - 2));

        var incoming = CreateQueueCard(item.Owner, item.Content, StackWidth(), QueueCardHeight());
        incoming.Border.Opacity = 0;
        incoming.ContentScale.ScaleX = incoming.ContentScale.ScaleY = PageEnterScale;
        Grid.SetRow(incoming.Border, tailRow);
        PlaceQueueCard(incoming);

        AnimateMorphView(outgoing?.Owner ?? "(未知)", outgoing?.MorphView, expand: false);
        _pageOutgoing = outgoing;
        _tailSurface = incoming;
        // 记住这张卡：收起/刷新/重启之后翻页位都回到它（见 ResolveTailIndex、QueueTailKey）
        _tailIndex = tail;
        _tailOwner = item.Owner;
        _settings.Set(QueueTailKey, item.Owner);

        var sb = new Storyboard();
        if (outgoing != null)
        {
            AddFadeAnim(sb, outgoing.Border, 1, 0, PageOutDuration, TimeSpan.Zero);
            AddScaleAnims(sb, outgoing.ContentScale, 1, PageEnterScale, PageOutDuration, TimeSpan.Zero);
        }
        AddFadeAnim(sb, incoming.Border, 0, 1, PageInDuration, PageInDelay);
        AddScaleAnims(sb, incoming.ContentScale, PageEnterScale, 1, PageInDuration, PageInDelay);

        var version = ++_pageAnimVersion;
        sb.Completed += (_, _) =>
        {
            if (version != _pageAnimVersion) return;
            _pageAnim = null;
            DropPagedOutCard();
            incoming.Border.Opacity = 1;
            incoming.ContentScale.ScaleX = incoming.ContentScale.ScaleY = 1;
            UpdateHitRegion();
        };
        _pageAnim = sb;
        sb.Begin();
    }

    /// <summary>
    /// 翻页动画收尾（连点或拆卸时调用）：停动画、该退场的旧卡立刻拿掉、
    /// 翻页位落回静止状态（动画是 HoldEnd 的，停掉之后必须把静止值写回去）。
    /// </summary>
    private void FinishQueuePage()
    {
        if (_pageAnim == null && _pageOutgoing == null) return;
        _pageAnimVersion++;                    // 先作废在跑的动画，再停它
        _pageAnim?.Stop();
        _pageAnim = null;
        DropPagedOutCard();
        if (_tailSurface != null)
        {
            _tailSurface.Border.Opacity = 1;
            _tailSurface.ContentScale.ScaleX = _tailSurface.ContentScale.ScaleY = 1;
        }
    }

    /// <summary>把正在退场的旧卡从面板与账本里清掉（断开与插件视图的引用）。</summary>
    private void DropPagedOutCard()
    {
        var outgoing = _pageOutgoing;
        if (outgoing == null) return;
        _pageOutgoing = null;
        outgoing.SetContent(null);
        QueuePanel.Children.Remove(outgoing.Border);
        _queueSurfaces.Remove(outgoing);
    }

    private void AddFadeAnim(Storyboard sb, DependencyObject target, double from, double to, TimeSpan duration, TimeSpan begin)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            BeginTime = begin,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
    }

    /// <summary>内容层缩放：两个轴各一条（组合级；只缩不放，所以永远不会越出卡片矩形）。</summary>
    private void AddScaleAnims(Storyboard sb, ScaleTransform target, double from, double to, TimeSpan duration, TimeSpan begin)
    {
        foreach (var property in ScaleProperties)
        {
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(duration),
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            sb.Children.Add(anim);
        }
    }

    /// <summary>安全拆卸队列：先停止各卡片的形态动画，把 MorphView.View 从卡片中取出，
    /// 确保它回到 ContentHost 的掌控，最后清空 QueuePanel（翻页按钮也一并清掉）。</summary>
    private void TeardownQueue()
    {
        FinishQueuePage();
        _pager = null;
        _tailSurface = null;

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

        // 确保主活动的 View 在 ContentHost 里（可能被队列 Border 占用过）。
        // 临时消息/投放面板占着岛体时不动内容层：那时 ContentHost 里是消息，
        // 换成插件视图会让紧随其后的交接动画从错误的内容起步（屏幕上会闪一下插件视图）。
        var live = ActiveLive;
        if (_tempContent == null && !_dropping
            && live?.MorphView != null && !ReferenceEquals(ContentHost.Content, live.MorphView.View))
        {
            SetContentImmediate(live.MorphView.View);
        }

        UpdateHitRegion(force: true);
    }

    /// <summary>没有视图的插件（只声明了尺寸）在队列里显示的兜底标签。</summary>
    private UIElement BuildFallbackLabel(string owner, IslandLiveContent content)
    {
        var accent = content.OwnerAccent ?? Windows.UI.Color.FromArgb(255, 90, 90, 95);
        var label = content.OwnerLabel ?? owner;
        var glyph = content.OwnerGlyph ?? "\uE946";
        // 强调色芯片上的图标恒为白（强调色都是饱和色）；标签文字跟着岛体明暗
        var labelBrush = new SolidColorBrush(_light
            ? Windows.UI.Color.FromArgb(255, 28, 28, 30)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255));

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
            Foreground = labelBrush,
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

    /// <summary>展开岛体。返回是否真的展开了 —— 没有可展开内容时返回 false（调用方据此决定要不要当成一次普通点击）。</summary>
    private bool OnHoverEnter()
    {
        if (_dropping) return false;

        var live = ActiveLive;
        if (live == null) return false;
        if (live.MorphView == null && live.ExpandedContent == null) return false;
        if (_expanded) return false;

        _expanded = true;
        // 临时消息显示期间也允许展开：消息改挂队列首卡，主岛让给活动内容（收起后消息回主岛）
        bool tempCard = _tempContent != null;
        if (tempCard) _tempCard = CreateTempCard();

        if (live.MorphView != null)
        {
            if (tempCard) SwapContentForTempCard(live.MorphView.View);
            else SwapContent(live.MorphView.View, animate: true);
            AnimateIslandSize(ExpandedMainSize(), ExpandDuration);
            ApplyCornerRadius(live.ExpandedSize.Height, expanded: true);
            AnimateMorphView(ActiveOwner, live.MorphView, expand: true);
        }
        else
        {
            if (tempCard) SwapContentForTempCard(live.ExpandedContent);
            else SwapContent(live.ExpandedContent, animate: true);
            AnimateIslandSize(ExpandedMainSize(), ExpandDuration);
            ApplyCornerRadius(live.ExpandedSize.Height, expanded: true);
        }

        BuildQueue();
        return true;
    }

    private void OnHoverExit()
    {
        if (_dropping) return;
        if (!_expanded) return;

        ClearTouchExpand();

        // 先安全拆卸队列（停止动画 + 断开 Border.Child + 把主 View 放回 ContentHost）。
        // 翻页位不动：下次展开还是用户翻到的那张卡
        TeardownQueue();

        _expanded = false;

        // 展开期间消息挂成队列首卡：收起后它回到主岛，剩余时长继续
        if (_tempContent != null)
        {
            _tempCard = null;
            SwapContent(_tempContent, animate: true);
            AnimateIslandSize(_tempSize, TemporaryMorphDuration, isTemporary: true);
            ApplyTemporaryRadius(_tempSize.Height);
            return;
        }

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
            SwapContent(live.CompactContent ?? _idleContent, animate: true);
            Size compact = CompactSizeOf(live);
            AnimateIslandSize(compact, CollapseDuration);
            ApplyCornerRadius(compact.Height, expanded: false);
        }
    }

    /// <summary>悬停展开开关（island.hoverExpand，默认开）。关掉后只有点击岛体（或触控轻点）才展开。</summary>
    private bool HoverExpandEnabled => _settings.Get(HoverExpandKey, true);

    /// <summary>悬停意图延迟（island.hoverDelay，0..600ms，默认 0 = 立即展开）。</summary>
    private double HoverDelayMs => Math.Clamp(_settings.Get(HoverDelayKey, 0.0), 0, HoverDelayMaxMs);

    /// <summary>指针进了岛：延迟为 0 就立刻展开，否则等"在岛上停够时间"再展开（从岛边扫过不算）。</summary>
    private void BeginHoverExpand()
    {
        if (!HoverExpandEnabled) return;

        double delay = HoverDelayMs;
        if (delay <= 0)
        {
            OnHoverEnter();
            return;
        }

        _hoverIntent.Stop();
        _hoverIntent.Interval = TimeSpan.FromMilliseconds(delay);
        _hoverIntent.Start();
    }

    private void HoverIntentTick()
    {
        _hoverIntent.Stop();
        if (!_hover || _dropping || _touchExpand) return;
        if (!IsCursorOverIsland()) return;
        OnHoverEnter();
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

    /// <summary>
    /// 临时内容（消息 / 插件自定义内容）的圆角档位：高过一条胶囊就按卡片算 ——
    /// 两行正文的高卡若还用 height/2，两端会圆成跑道形，不像卡片。
    /// </summary>
    private void ApplyTemporaryRadius(double height)
        => ApplyCornerRadius(height, expanded: height > IslandStyle.MessageCardHeight);

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
        bool growing = islandTarget.Width * islandTarget.Height >= fromW * fromH;
        var easing = SizeEasing(isTemporary, growing);
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

    private Size _currentTotalSize = IdleBaseSize;

    /// <summary>
    /// 尺寸动画的缓动：<b>长大</b>时用轻回弹（BackEase EaseOut，振幅小、时长长，读起来是一次顺滑的"弹出来"，
    /// 不是抖一下）；<b>缩小</b>时一律用 QuinticEase EaseOut —— 起步快、末段缓缓收住，**绝不越过目标**
    /// （回弹在收缩方向上会让胶囊缩得比目标还矮、内容被裁剪框切一刀再弹回来）。
    /// 临时内容（消息 / <c>Island.Show</c>）长大时回弹更轻一点。
    ///
    /// <c>island.bounce</c>（默认开）关掉后长大也用纯 EaseOut：回弹对某些内容（实时波形、进度弧）
    /// 会让动画看起来"多弹一下"，不想要的人可以整体关掉。
    /// </summary>
    private EasingFunctionBase SizeEasing(bool temporary, bool growing)
    {
        if (!growing || !_settings.Get(BounceKey, true))
            return new QuinticEase { EasingMode = EasingMode.EaseOut };
        return new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = temporary ? 0.2 : 0.3 };
    }

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

    #region 内容交接（换内容时的进出）

    // 旧内容先走、新内容稍后进，两段错开约 80ms：这样读起来是"岛换了口气"，
    // 而不是两张画面叠在一起 —— 全程只有一次可感知的切换。
    private static readonly TimeSpan ContentOutDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ContentInDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ContentInDuration = TimeSpan.FromMilliseconds(240);
    private const double ContentOutSlide = -7;      // 旧内容上移让位（DIP）
    private const double ContentInSlide = 9;        // 新内容从下方进（DIP）
    private const double ContentInScale = 0.96;     // 新内容带一点"长出来"的缩放

    /// <summary>
    /// 岛体内容交接：旧内容挪进 <see cref="ContentLeaving"/> 快速淡出并向上让位，
    /// 新内容稍后从下方淡入、微缩放回位。
    ///
    /// 旧内容是**按此刻的渲染尺寸钉住**的：它不参与新尺寸的布局 —— 否则插件视图会在收缩动画里
    /// 被一点点挤扁。全程只动组合级属性（Opacity / RenderTransform），不触发重排，与尺寸 morph 逐帧共存。
    ///
    /// **所有**岛体内容切换都必须走这里或 <see cref="SetContentImmediate"/>：交接动画在跑时若有人直接改
    /// <see cref="ContentHost"/>，内容层会停在半途的透明/位移上。
    /// </summary>
    private void SwapContent(UIElement? next, bool animate)
    {
        SettleContentSwap();

        var current = ContentHost.Content;
        if (!animate || current == null || ReferenceEquals(current, next) || !_shown)
        {
            ContentHost.Content = next;
            return;
        }

        ContentLeaving.Width = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        ContentLeaving.Height = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;

        // 同一个元素不能同时挂在两个 ContentPresenter 下：先断开再交接。
        // 基础值一律停在"已就位"状态（新内容不透明、两层无位移），动画用显式 From/To ——
        // 万一条动画没跑起来，退化结果也只是"瞬间换内容"，而不是内容停在半路的透明/位移上。
        ContentHost.Content = null;
        ContentLeaving.Content = current;
        ContentHost.Content = next;

        int version = ++_contentSwapVersion;
        var sb = new Storyboard();
        sb.Children.Add(SlideAnim(ContentLeaving, "Opacity", 1, 0, ContentOutDuration, TimeSpan.Zero));
        sb.Children.Add(SlideAnim(_leavingSlide, "TranslateY", 0, ContentOutSlide, ContentOutDuration, TimeSpan.Zero));
        sb.Children.Add(SlideAnim(ContentHost, "Opacity", 0, 1, ContentInDuration, ContentInDelay));
        sb.Children.Add(SlideAnim(_contentSlide, "TranslateY", ContentInSlide, 0, ContentInDuration, ContentInDelay));
        sb.Children.Add(SlideAnim(_contentSlide, "ScaleX", ContentInScale, 1, ContentInDuration, ContentInDelay));
        sb.Children.Add(SlideAnim(_contentSlide, "ScaleY", ContentInScale, 1, ContentInDuration, ContentInDelay));
        sb.Completed += (_, _) =>
        {
            if (version != _contentSwapVersion) return;
            _contentSwap = null;
            ClearLeaving();
        };
        _contentSwap = sb;
        sb.Begin();
    }

    /// <summary>不用交接动画地设置内容（树结构修正、聚光卡恢复、投放面板接管等路径）。</summary>
    private void SetContentImmediate(UIElement? content)
    {
        SettleContentSwap();
        ContentHost.Content = content;
    }

    /// <summary>立刻结束交接：停动画、清覆盖层、把两层复位。可随时调用（幂等）。</summary>
    private void SettleContentSwap()
    {
        _contentSwap?.Stop();
        _contentSwap = null;
        _contentSwapVersion++;
        ClearLeaving();
        ContentHost.Opacity = 1;
        ResetSlide(_contentSlide);
    }

    private void ClearLeaving()
    {
        ContentLeaving.Content = null;
        ContentLeaving.Opacity = 0;
        ResetSlide(_leavingSlide);
    }

    private static void ResetSlide(CompositeTransform transform)
    {
        transform.TranslateY = 0;
        transform.ScaleX = 1;
        transform.ScaleY = 1;
    }

    /// <summary>交接的单条动画：一律 EaseOut —— 交接要"落地"，回弹留给岛体自己的 morph。</summary>
    private static DoubleAnimation SlideAnim(
        DependencyObject target, string property, double from, double to, TimeSpan duration, TimeSpan delay)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            BeginTime = delay,
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    #endregion

    #region 窗口位置 / 可见性

    /// <summary>系统 DPI 比例（窗口所在屏）。</summary>
    private double DpiScale => Win32.GetDpiForWindow(_hwnd) / 96.0;

    /// <summary>大岛缩放比（island.scale.island）。</summary>
    private double _islandScale = 1.0;

    /// <summary>
    /// DIP → 物理像素的总系数 = DPI × 大岛缩放。整套几何（画布、落点、形状、悬停判定）都从它算；
    /// XAML 侧由 <see cref="ApplyScaleTransform"/> 给岛体栈挂等比渲染缩放，两边保持一致。
    /// </summary>
    private double Scale => DpiScale * _islandScale;

    /// <summary>
    /// 岛体栈等比缩放：窗口客户区的布局尺寸是「物理像素 ÷ DPI」= 画布 × 缩放，
    /// 所以栈以锚定侧为原点缩放，边距也同比缩放，渲染结果恰好落在 <see cref="Scale"/> 算出的位置。
    /// </summary>
    private void ApplyScaleTransform()
    {
        double k = _islandScale;
        IslandStack.RenderTransform = new ScaleTransform { ScaleX = k, ScaleY = k };
        double ox = _horizontal switch { HorizontalLeft => 0.0, HorizontalRight => 1.0, _ => 0.5 };
        IslandStack.RenderTransformOrigin = new Point(ox, _bottomAnchored ? 1.0 : 0.0);
        IslandStack.Margin = _bottomAnchored ? new Thickness(0, 0, 0, Pad * k) : new Thickness(0, Pad * k, 0, 0);
    }

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
        ApplyScaleTransform();

        // 已展开时按新方向重建队列（卡片顺序镜像：最先入队的活动仍离岛体最近）
        if (_expanded) BuildQueue();

        // 看门狗与位置模式无关：顶部模式一样会被后来断言过置顶的窗口（shell 浮层、任务管理器、
        // 各种悬浮窗/输入法候选框）压住，而顶部模式没有别的重升时机 —— 曾经只在底部模式启动它，
        // 结果顶部模式被盖住后要等重启才恢复。任务栏跟随只是它在底部模式下额外做的事。
        _watchdog ??= CreateWatchdog();
        _watchdog.Start();
        // 前台窗口一变（点任务栏/开始菜单/搜索）就立刻查一次置顶，不必等一个轮询周期
        Win32.InstallForegroundWatcher(() => DispatcherQueue.TryEnqueue(OnOtherWindowActivated));

        if (bottom)
        {
            _taskbarHidden = false;
            RefreshFreeBands(force: true);      // 进入底部模式立刻量一次任务栏空闲段
        }
        else
        {
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

    /// <summary>
    /// 岛该锚定到哪块显示器：按 <c>island.display</c>（auto = 跟随鼠标所在屏）解析。
    /// 这是全窗口唯一的显示器来源 —— 绝不要再出现 <c>DisplayAreaFallback.Primary</c>：
    /// 它只认主屏，多屏用户的岛会永远赖在主屏，跟随鼠标/指定屏都失效。
    /// </summary>
    private DisplayArea ResolveDisplay()
        => DisplayResolver.Resolve(_settings.Get<string?>(DisplayKey, null), AppWindow.Id);

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

        var outer = ToRect(ResolveDisplay().OuterBounds);
        if (Win32.GetTaskbarStripFor(outer) is not { Visible: true } tray || tray.Docked.Bottom < outer.Bottom - Win32.SliverPx)
            return;

        var hwnd = Win32.GetTaskbarWindow(outer);
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

        var display = ResolveDisplay();
        _displayOuter = display.OuterBounds;
        Win32.RECT strip = ToRect(display.WorkArea);
        bool embedded = false;
        int trayLeft = 0;
        if (_bottomAnchored)
        {
            // 优先嵌进任务栏条带：与任务栏同一条带、同一高度基准。
            // 任务栏不可用（自动隐藏收起/不在屏幕底部/取不到）时退回屏幕外框底边。
            var outer = ToRect(display.OuterBounds);
            var tray = Win32.GetTaskbarStripFor(outer);
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

        double eased = 1 - Math.Pow(1 - p, 4);
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

    private DispatcherQueueTimer CreateWatchdog()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(WatchdogPollMs);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => WatchdogTick();
        return timer;
    }

    /// <summary>DisplayInformation 的事件在任意线程触发：立刻编组回 UI 线程。</summary>
    private void OnDisplaysInvalidatedFromAnyThread()
        => DispatcherQueue.TryEnqueue(OnDisplaysInvalidated);

    /// <summary>
    /// 显示器配置变了（热插拔/分辨率/排列/投影）：重新解析目标屏并把岛搬过去。
    ///
    /// 为什么是两拍：窗口的 DPI 是**跟着窗口当前所在屏**走的（<c>GetDpiForWindow</c>），
    /// 而 <c>ApplyCanvasBounds</c> 里的尺寸/落点又全按 <c>Scale</c> 算 —— 第一拍里窗口还压在旧屏上，
    /// 算出来的是「新屏的坐标 + 旧屏的缩放」，落点必然偏。所以：
    /// 第一拍只负责把窗口挪到新屏（顺带把 <c>_displayOuter</c>/<c>_displayKey</c> 更新掉），
    /// 第二拍等 DWM 把 DPI 传播完，再按正确的缩放重算一次，这才是最终落点。
    /// 两拍都必须在展开动画之外 —— 展开中移动窗口会打断动画（同空闲段那条规矩）。
    /// </summary>
    private void OnDisplaysInvalidated()
    {
        if (!_shown) return;

        // 空闲段是旧屏的物理坐标：不丢的话 SelectFreeBand 会把岛摆到新屏之外去
        _freeBands = Array.Empty<Windows.Graphics.RectInt32>();
        _freeBandsDirty = false;
        _freeBandsAt = 0;

        // 展开中：收起时 WatchdogTick 会因为 _freeBandsDirty 之外的路径不会自动补位，
        // 所以这里再挂一次待办，等收起后落位（复用空闲段那套「稍后应用」的机制）。
        if (_expanded)
        {
            _freeBandsDirty = true;
            return;
        }

        // 第一拍：把窗口挪到新屏（此时 Scale 可能还是旧屏的，落点先粗对）
        ApplyCanvasBounds();

        // 第二拍：DPI 传播完后按正确缩放重算 —— 只在真的换了屏/换了 DPI 时才需要
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_shown || _expanded) return;
            ApplyCanvasBounds();
            RefreshFreeBands(force: true);
        });
    }

    /// <summary>
    /// 看门狗。先无条件做一次置顶自愈 —— 位置模式与置顶无关，顶部模式曾经没有任何重升时机，
    /// 被盖住后只能重启恢复。任务栏跟随只在底部模式有意义，顶部模式到此为止。
    /// </summary>
    private void WatchdogTick()
    {
        UpdateFullscreenState();
        HealTopmost();

        // 锚定屏漂移自愈（两种位置模式都要）：热插拔事件在某些环境不广播（远程桌面、驱动更新、
        // 部分虚拟机），这里按外框定期核对一次，发现目标屏换了/尺寸变了就重新落位。
        // 只是比几个整数，成本可以忽略；展开中不移动窗口，留到收起后由 _freeBandsDirty 那条路径落位。
        if (HealDisplayDrift()) return;

        if (!_bottomAnchored) return;

        var outer = ToRect(ResolveDisplay().OuterBounds);
        var tray = Win32.GetTaskbarStripFor(outer);
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
    }

    /// <summary>
    /// 锚定屏漂移自愈：目标屏的外框变了（热插拔 / 换了分辨率 / 显示器排列调整 / 用户把设置改成跟随鼠标
    /// 而鼠标挪到了别的屏）就重新落位。返回 true 表示已经处理并重新定位，调用方本轮不必再动。
    /// 展开中不移动窗口（会打断动画），改为标记待应用，收起后由看门狗那条 <c>_freeBandsDirty</c> 路径落位。
    /// </summary>
    private bool HealDisplayDrift()
    {
        // 还没落过位（_windowPhysW 为 0）：_displayOuter 是初始值，此刻比对必然"漂移"。
        // 首次定位由 RefreshFromSettings / 构造流程负责，不归看门狗管。
        if (_windowPhysW <= 0 || _displayOuter.Width <= 0) return false;

        var outer = ResolveDisplay().OuterBounds;
        if (outer.X == _displayOuter.X && outer.Y == _displayOuter.Y
            && outer.Width == _displayOuter.Width && outer.Height == _displayOuter.Height)
        {
            return false;
        }

        // 旧屏的空闲段在新屏上无效
        _freeBands = Array.Empty<Windows.Graphics.RectInt32>();
        _freeBandsAt = 0;

        if (_expanded)
        {
            _freeBandsDirty = true;
            return false;
        }

        ApplyCanvasBounds();
        RefreshFreeBands(force: true);
        return true;
    }

    /// <summary>
    /// 置顶自愈：被任何可见的相交窗口压在下面时立刻重升，并把元凶记进日志（同原因 2 秒内只记一次）。
    /// 触发点：看门狗轮询（所有位置模式）+ 前台窗口变化事件（任务栏/开始菜单/搜索抬层时不必等一个周期）。
    /// 隐藏期间不做：窗口不在屏幕上，重升没有意义。
    /// </summary>
    private void HealTopmost()
    {
        if (!_shown) return;

        if (Win32.EnsureTopmost(_hwnd, IslandScreenRect()) is not { } coverer) return;

        _topmostHealCount++;
        if (coverer == _topmostHealLast && Environment.TickCount64 - _topmostHealLoggedAt < 2000) return;
        _topmostHealLast = coverer;
        _topmostHealLoggedAt = Environment.TickCount64;
        _log.Warn($"岛被「{coverer}」压在下面，已重新置顶（本次运行第 {_topmostHealCount} 次）。");
    }

    /// <summary>
    /// 别的窗口被激活（前台窗口变化，点任务栏/开始菜单/别的应用都算；也包括焦点变化）：
    /// 既立刻查一次置顶，也收掉触控展开 —— 触摸点在其他应用上不一定产生鼠标消息
    /// （原生处理 WM_POINTER 的应用会吃掉它，低级鼠标钩子就看不到），
    /// 前台/焦点变化是这类情况的兜底信号。
    /// </summary>
    private void OnOtherWindowActivated()
    {
        HealTopmost();
        DismissTouchExpand();
    }

    /// <summary>
    /// 把岛屿的「真实布局形状」写入窗口形状（SetWindowRgn）：形状之外不绘制也不参与命中测试，
    /// 透明画布区域的点击/触摸因此会真正落到下层窗口（跨进程有效）。
    /// 形状按岛体的圆角生成 —— 若用矩形近似，圆角外侧那几块像素会被窗口表面填充成白色实块。
    /// 形状还要比岛体渲染范围外扩一圈（见 <see cref="ShapeSlackDip"/>）：贴着渲染边缘的形状
    /// 会把圆角的抗锯齿和 1px 描边切成硬阶梯，那才是肉眼看到的锯齿。
    /// 必须跟随尺寸动画逐帧更新，且绝不能为空：没有形状时整块画布都会拦截点击。
    /// </summary>
    private void UpdateHitRegion(bool force = false)
    {
        // 窗口还没定位（首次应用之前）时岛体位置无从计算，形状先不下发 ——
        // 否则底部模式会算出负数 y 的空形状/错形状。
        if (_windowPhysW <= 0 || _windowPhysH <= 0) return;

        var s = Scale;
        double dpi = DpiScale;      // TransformToVisual 的结果已含栈的渲染缩放，只差 DPI
        var rects = new List<ShapeRect>(QueueVisibleCards + 2);

        // 主岛（空闲 / 活动 / 临时消息都走这里）：形状必须跟随岛体「当前渲染尺寸」。
        // 绝不能取动画目标尺寸 —— 目标比内容多出来的那一圈会落进形状里，
        // 由窗口表面填成不透明白色，切换瞬间就会闪白块。
        rects.Add(GrowShape(MainIslandShapeRect(s), s));

        // 队列小岛：各子元素的实际布局矩形与圆角（DIP → 物理）
        foreach (var child in QueuePanel.Children)
        {
            if (child is not Border card || card.ActualWidth <= 0 || card.ActualHeight <= 0)
                continue;

            var box = card.TransformToVisual(RootGrid)
                          .TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            rects.Add(GrowShape(new ShapeRect(
                (int)Math.Round(box.X * dpi), (int)Math.Round(box.Y * dpi),
                (int)Math.Round((box.X + box.Width) * dpi), (int)Math.Round((box.Y + box.Height) * dpi),
                (int)Math.Round(card.CornerRadius.TopLeft * s)), s));
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

    /// <summary>
    /// 窗口形状相对岛体渲染边缘的外扩量（DIP，且至少 2 物理像素）。
    ///
    /// 形状是 1-bit 掩码，边界必然是硬阶梯；而岛体的圆角与 1px 描边是抗锯齿渲染的。
    /// 两者一旦重合，掩码就会把圆角的过渡像素和描边整条切掉 —— 屏幕上就是肉眼可见的锯齿
    /// （取整还会让不同边被切的程度不一样，有的边好、有的边碎）。外扩之后硬边界落到岛体渲染
    /// 范围**之外**：那里没有内容，形状边界不再可见，轮廓交回给 XAML 自己画的抗锯齿圆角。
    ///
    /// 取值依据：抗锯齿过渡约 1 物理像素，GDI 掩码相对理想圆弧最多内缩 0.5 像素，
    /// 布局矩形取整再占 0.5 像素 —— 2 物理像素刚好够，再大只是让下面那圈更宽。
    ///
    /// 代价：Fluent + 系统材质时，这圈形状里只有材质、没有岛体压的那层纱，会比岛体略亮，
    /// 在很暗的桌面上能看出一条 1~2px 的浅边（Apple 与材质不可用时这圈是透明的，没有此现象）。
    /// 这是「窗口级材质」与「平滑轮廓」之间无法两全的取舍：材质铺满整个形状，
    /// 形状边界只要可见，它就一定是硬阶梯。宁可留这圈浅边，也不要被切碎的描边。
    /// </summary>
    private const double ShapeSlackDip = 1;

    /// <summary>
    /// 按外扩量放大形状矩形（圆角半径同增，弧线与岛体保持同心）：硬边界落到岛体渲染范围之外，
    /// 圆角与 1px 描边的抗锯齿才不会被 1-bit 掩码切碎（见 <see cref="ShapeSlackDip"/>）。
    /// </summary>
    private ShapeRect GrowShape(ShapeRect r, double scale)
    {
        // Fluent + 系统材质：外扩圈里会露出未压纱的材质（一圈浅边），所以只留 1 物理像素；
        // 其余情况外扩圈是透明的，保持 2 物理像素以保住抗锯齿。
        int min = _style == IslandStyleKind.Fluent && _materialApplied ? 1 : 2;
        int slack = Math.Max(min, (int)Math.Ceiling(ShapeSlackDip * scale));
        if (min == 1) slack = Math.Max(1, (int)Math.Round(ShapeSlackDip * scale * 0.5));
        return new ShapeRect(r.X1 - slack, r.Y1 - slack, r.X2 + slack, r.Y2 + slack, r.Radius + slack);
    }

    /// <summary>形状用矩形（物理像素）：X1,Y1,X2,Y2 为外接矩形，Radius 为圆角半径。</summary>
    private readonly record struct ShapeRect(int X1, int Y1, int X2, int Y2, int Radius);

    /// <summary>
    /// 主岛当前的渲染矩形与圆角（物理像素），<see cref="UpdateHitRegion"/> 与
    /// <see cref="MainIslandScreenRect"/> 共用同一份算法，保证窗口形状与聚光卡的落点永远一致。
    /// 只看 ActualWidth/ActualHeight（当前渲染尺寸），不看动画目标。
    /// </summary>
    private ShapeRect MainIslandShapeRect(double scale)
    {
        double iw = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        double ih = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;
        double ix = IslandLeftInWindow(scale, iw);
        double iy = IslandTopInWindow(_bottomAnchored, scale, ih);
        return new ShapeRect(
            (int)Math.Round(ix), (int)Math.Round(iy),
            (int)Math.Round(ix + iw * scale), (int)Math.Round(iy + ih * scale),
            (int)Math.Round(IslandRoot.CornerRadius.TopLeft * scale));
    }

    #region 全屏应用：隐藏 / 小色条 / 照常

    private bool _fullscreen;
    /// <summary>strip 模式下鼠标靠近色条后，岛体临时现身。</summary>
    private bool _fsPeek;
    private long _fsPeekLastOverAt;
    private EdgeStripWindow? _strip;

    private string FullscreenMode
    {
        get
        {
            var v = _settings.Get(FullscreenKey, FullscreenStrip) ?? FullscreenStrip;
            return v.Equals(FullscreenHide, StringComparison.OrdinalIgnoreCase) ? FullscreenHide
                 : v.Equals(FullscreenNormal, StringComparison.OrdinalIgnoreCase) ? FullscreenNormal
                 : FullscreenStrip;
        }
    }

    /// <summary>全屏应用在前台、且设置要求避让时，岛体不显示（strip 模式下色条被靠近后临时现身除外）。</summary>
    private bool FullscreenSuppressed => _fullscreen && !_fsPeek && FullscreenMode != FullscreenNormal;

    /// <summary>
    /// 看门狗每拍调用：检测全屏、推进「靠近色条 → 岛体现身 → 离开后收回」，并同步色条窗口。
    /// 只在状态变化时才动窗口；检测本身是几次 Win32 查询，120ms 一次的成本可以忽略。
    /// </summary>
    private void UpdateFullscreenState(bool force = false)
    {
        string mode = FullscreenMode;
        bool fs = mode != FullscreenNormal && Win32.IsFullscreenForeground(ToRect(ResolveDisplay().OuterBounds));
        bool changed = fs != _fullscreen;
        _fullscreen = fs;

        if (!fs || mode != FullscreenStrip)
        {
            if (_fsPeek) { _fsPeek = false; changed = true; }
        }
        else if (!_fsPeek)
        {
            if (!_spotlightOccluded && CursorNearStrip())
            {
                _fsPeek = true;
                _fsPeekLastOverAt = Environment.TickCount64;
                changed = true;
            }
        }
        else
        {
            if (IsCursorOverIsland() || CursorNearStrip()) _fsPeekLastOverAt = Environment.TickCount64;
            else if (Environment.TickCount64 - _fsPeekLastOverAt > FullscreenPeekGraceMs && !_dropping)
            {
                _fsPeek = false;
                changed = true;
            }
        }

        if (!changed && !force)
        {
            // 色条在场期间岛体换位置/缩放/显示器后也要跟上：落位本身只是一次 MoveAndResize，状态不变时不重复做
            return;
        }

        if (_fsPeek && changed && _windowPhysW > 0)
        {
            UpdateVisibility();
            if (_shown)
            {
                _hover = true;
                _hoverGuard.Start();
                OnHoverEnter();
            }
        }
        else
        {
            if (FullscreenSuppressed && _expanded)
            {
                _hover = false;
                _hoverGuard.Stop();
                OnHoverExit();
            }
            UpdateVisibility();
        }

        SyncStrip();
    }

    /// <summary>色条的屏幕矩形（物理像素）：贴目标屏顶/底边，横向跟着岛体的水平落点。</summary>
    private Win32.RECT StripRect()
    {
        var outer = ToRect(ResolveDisplay().OuterBounds);
        double dpi = DpiScale;
        double k = ReadScale(ScaleStripKey);
        int w = Math.Max(8, (int)Math.Round(EdgeStripWindow.BaseSize.Width * k * dpi));
        int h = Math.Max(3, (int)Math.Round(EdgeStripWindow.BaseSize.Height * k * dpi));

        double centerX = (outer.Left + outer.Right) / 2.0;
        if (_windowPhysW > 0)
        {
            var r = MainIslandScreenRect();
            double c = (r.Left + r.Right) / 2.0;
            if (c > outer.Left && c < outer.Right) centerX = c;
        }

        int x = (int)Math.Round(Math.Clamp(centerX - w / 2.0, outer.Left, outer.Right - w));
        int y = _bottomAnchored ? outer.Bottom - h : outer.Top;
        return new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    private bool CursorNearStrip()
    {
        var r = StripRect();
        int reach = (int)Math.Round(StripReachDip * DpiScale);
        Win32.GetCursorPos(out var pt);
        int top = _bottomAnchored ? r.Top - reach : r.Top - 1;
        int bottom = _bottomAnchored ? r.Bottom + 1 : r.Bottom + reach;
        return pt.X >= r.Left - reach && pt.X <= r.Right + reach && pt.Y >= top && pt.Y <= bottom;
    }

    private void SyncStrip()
    {
        bool want = _fullscreen && !_fsPeek && FullscreenMode == FullscreenStrip && !_spotlightOccluded;
        if (!want)
        {
            _strip?.HideStrip();
            return;
        }

        _strip ??= new EdgeStripWindow();
        var r = StripRect();
        _strip.ShowAt(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    #endregion

    private void UpdateVisibility(bool force = false, bool fade = true)
    {
        bool hideIdle = _settings.Get("island.hideWhenIdle", false);
        bool visible = _settings.Get("island.visible", true)
                       && !(_bottomAnchored && _taskbarHidden)
                       && !_spotlightOccluded
                       && !FullscreenSuppressed
                       // 投放会话期间必须留在屏幕上：用户正拖着文件对着岛
                       && !(hideIdle && ActiveLive == null && _tempContent == null && !_dropping);
        if (!force && visible == _shown) return;
        _shown = visible;
        if (visible)
        {
            // 窗口此刻是隐藏的：先把整块压成全透明，Show 之后再淡入（中途被重新要求显示则从当前透明度接着淡入）
            bool fadeIn = fade && !AppWindow.IsVisible;
            if (fadeIn) IslandRoot.Opacity = 0;
            AppWindow.Show(false);
            _presenter.IsAlwaysOnTop = false;
            _presenter.IsAlwaysOnTop = true;
            Win32.MakeIslandStyle(_hwnd);
            // MakeIslandStyle 里的 SWP_FRAMECHANGED 可能让窗口形状失效，重新断言一次
            UpdateHitRegion(force: true);
            if (fade && (fadeIn || IslandRoot.Opacity < 1))
                FadeIslandOpacity(1, AppearFadeMs, null);
        }
        else
        {
            StopPositionSlide();
            _hoverGuard.Stop();
            _hover = false;
            ClearTouchExpand();
            if (fade && AppWindow.IsVisible)
            {
                // 整块淡出后再真正隐藏；淡出期间若又要显示，FadeIslandOpacity 的版本号会让这次隐藏作废
                FadeIslandOpacity(0, DisappearFadeMs, () =>
                {
                    if (!_shown && !_spotlightOccluded) AppWindow.Hide();
                });
            }
            else
            {
                AppWindow.Hide();
            }
        }
    }

    private void OnSettingChanged(string key)
    {
        // 翻页位是岛体自己写的界面状态：别为它重扫一遍设置 —— 那会重建队列、把刚点出来的翻页动画打断
        if (key == QueueTailKey) return;

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

    /// <summary>优先级变化后重排活动并重建当前状态（临时消息独自占着主岛时留到它结束后自然生效）。</summary>
    private void ApplyPriorityChange()
    {
        SortLive();
        if (_dropping) return;
        if (_tempContent != null && !_expanded) return;
        TransitionToLiveState();
        UpdateVisibility();
    }

    #endregion

    #region 指针交互 — 主程序统一处理

    private void IslandRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // 投放会话期间指针被源进程捕获，拖拽开始时还会连带产生一次 Exited/Entered，
        // 这些都不是"用户在悬停岛体"：一律不参与悬停裁决
        if (_dropping) return;

        // 触控没有悬停语义（按下即进入、抬起即退出），不参与悬停状态：
        // 触控的展开由 IslandRoot_Tapped 里的 TouchExpand 负责。
        if (!IsTouchPointer(e))
        {
            _hover = true;
            _hoverGuard.Start();
        }

        if (_tempContent != null)
        {
            // 悬停期间消息不过期；岛体展开后它改挂队列首卡（见 OnHoverEnter），主岛让给活动内容
            _tempTimer.Stop();
            _tempTimer.Start();
        }

        if (IsTouchPointer(e)) return;
        BeginHoverExpand();
    }

    private void IslandRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dropping) return;

        // 手指抬起就是 PointerExited —— 这不是「离开」，触控展开要留着，点岛外才收
        if (IsTouchPointer(e)) return;
        // 触控展开常驻期间，鼠标进出也不裁决收起：一次触摸点击会连带产生鼠标事件，
        // 不然手指一抬就被鼠标语义收掉，又回到「刚展开就瞬间收起」
        if (_touchExpand) return;
        if (QueuePanel.Visibility == Visibility.Visible) return;
        if (IsCursorOverIsland()) return;
        _hoverIntent.Stop();
        _hover = false;
        _hoverGuard.Stop();
        OnHoverExit();
    }

    private void IslandStack_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_dropping) return;
        if (IsTouchPointer(e)) return;
        _hover = true;
        _hoverGuard.Start();
    }

    private void IslandStack_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dropping) return;
        if (IsTouchPointer(e)) return;
        if (_touchExpand) return;
        if (IsCursorOverIsland()) return;
        _hoverIntent.Stop();
        _hover = false;
        _hoverGuard.Stop();
        OnHoverExit();
    }

    private void IslandRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_dropping) return;
        // 刚关掉消息的余波：双击的第一下已经把它关了，第二下不该顺带点到插件内容
        if (Environment.TickCount64 < _tapSuppressUntil) return;

        // 点插件自己的按钮/滑块不算"点岛体"：按钮内部的图标和文字才是 OriginalSource，
        // 必须顺着可视树上溯找祖先控件 —— 以前只看 OriginalSource 是不是 ButtonBase，
        // 结果点播控按钮也会触发 OnTap（点一下播放就弹出静音卡/聚光卡）。
        if (IsInteractiveSource(e.OriginalSource)) return;

        // 消息正显示在主岛（紧凑态）时，点一下就是关掉它；展开态它挂在队列首卡上，这里不接管
        if (_tempContent != null && _tempCard == null)
        {
            _tapSuppressUntil = Environment.TickCount64 + TapSuppressMs;
            DismissTemporary();
            return;
        }

        // 触控点一下岛 = 展开（触摸没有悬停，展开只能由点击驱动）：这一下点击就是「展开」本身，
        // 不再往下传给插件的 OnTap；岛已经展开后再点，才是一次真正的插件点击。
        if (e.PointerDeviceType == PointerDeviceType.Touch && !_expanded && TouchExpand()) return;

        // 悬停展开关掉时，鼠标点岛体空白处 = 展开/收起（走触控那条"常驻展开"路径，点岛外才收）
        if (!HoverExpandEnabled && e.PointerDeviceType != PointerDeviceType.Touch)
        {
            if (_expanded)
            {
                if (_touchExpand) DismissTouchExpand();
                else OnHoverExit();
                return;
            }

            if (TouchExpand()) return;
        }

        ActiveLive?.OnTap?.Invoke();
    }

    /// <summary>中键点岛体：关掉当前临时消息（没有消息时什么都不做）。</summary>
    private void IslandRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dropping || _tempContent == null) return;
        if (!e.GetCurrentPoint(IslandRoot).Properties.IsMiddleButtonPressed) return;
        e.Handled = true;
        _tapSuppressUntil = Environment.TickCount64 + TapSuppressMs;
        DismissTemporary();
    }

    /// <summary>双击岛体：同样关掉当前临时消息（没有消息时双击照旧传给插件）。</summary>
    private void IslandRoot_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_dropping || _tempContent == null) return;
        e.Handled = true;
        _tapSuppressUntil = Environment.TickCount64 + TapSuppressMs;
        DismissTemporary();
    }

    /// <summary>
    /// 触控展开：展开并常驻，同时开始监听全局按下 —— 岛窗拿不到岛外的任何输入（形状裁剪 +
    /// 不抢焦点），「点其他地方收起」只能靠系统级的按下事件。返回是否真的展开了。
    /// </summary>
    private bool TouchExpand()
    {
        if (!OnHoverEnter()) return false;
        _touchExpand = true;
        Win32.InstallMouseDownWatcher(OnGlobalMouseDown);
        return true;
    }

    /// <summary>结束触控展开的常驻状态（撤销全局按下监听）；收起岛体不在这里。</summary>
    private void ClearTouchExpand()
    {
        if (!_touchExpand) return;
        _touchExpand = false;
        Win32.UninstallMouseDownWatcher();
    }

    /// <summary>点了岛外：结束触控展开并收起岛体。</summary>
    private void DismissTouchExpand()
    {
        if (!_touchExpand) return;
        ClearTouchExpand();
        OnHoverExit();
    }

    /// <summary>
    /// 全局按下（监听线程）：触控展开期间点在岛体（含队列卡片）以外就收起。
    /// 判定要用 UI 线程上的实时岛体矩形，所以这里只把坐标搬过去，逻辑全在 UI 线程跑。
    /// </summary>
    private void OnGlobalMouseDown(Win32.POINT pt)
    {
        if (!_touchExpand) return;
        DispatcherQueue.TryEnqueue(() => DismissTouchExpandIfOutside(pt));
    }

    private void DismissTouchExpandIfOutside(Win32.POINT pt)
    {
        if (!_touchExpand) return;

        var island = IslandScreenRect();
        bool inside = pt.X >= island.Left && pt.X <= island.Right
                      && pt.Y >= island.Top && pt.Y <= island.Bottom;
        if (inside) return;

        DismissTouchExpand();
    }

    /// <summary>触控指针：没有悬停（按下 Entered、抬起 Exited），所有悬停语义都要绕开它。</summary>
    private static bool IsTouchPointer(PointerRoutedEventArgs e)
        => e.Pointer.PointerDeviceType == PointerDeviceType.Touch;

    /// <summary>点击源是否落在"自己处理点击"的控件里（按钮、滑块、开关、可拖动的进度条等）。</summary>
    private static bool IsInteractiveSource(object? source)
    {
        for (var element = source as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase or Slider or ToggleSwitch or CheckBox or ComboBox or TextBox or ProgressBar)
            {
                return true;
            }
        }

        return false;
    }

    private void HoverGuardTick()
    {
        if (!_hover) return;
        if (_dropping) return;         // 投放会话期间岛体是投放面板，悬停看门狗不参与收起裁决
        if (_touchExpand) return;      // 触控展开常驻：鼠标位置不参与收起裁决
        if (!IsCursorOverIsland())
        {
            _hoverIntent.Stop();
            _hover = false;
            _hoverGuard.Stop();
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

    #region 文件投放（拖文件进岛）

    /// <summary>
    /// 投放面板尺寸（DIP）。它是常量，并且由 <see cref="ComputeCanvasFootprint"/> 在启动时就预留进画布 ——
    /// 拖拽全程绝不 resize 窗口：客户区尺寸一变，DWM 会用上一帧合成新窗口矩形（残影），
    /// OLE 拖放的命中也会被那次 resize 打断。
    /// </summary>
    private static readonly Size DropPanelSize = new(460, 104);
    /// <summary>边缘自动滚动触发带宽度（DIP，相对滚动视口）。</summary>
    private const double DropEdgeZone = 44;
    /// <summary>边缘滚动最高速度（DIP/秒），按指针压入深度线性缩放。</summary>
    private const double DropScrollMaxSpeed = 1100;
    private const int DropScrollTickMs = 16;
    /// <summary>指针离开岛体后的宽限收起时间：XAML 在子元素之间移动时偶发 DragLeave，立刻收会抖。</summary>
    private const int DropExitGraceMs = 160;
    /// <summary>速度一阶滤波系数：进出边缘带时加速/减速都是渐变的（那口"顺滑"就靠它）。</summary>
    private const double DropScrollSmoothing = 0.25;
    /// <summary>图片载荷的最大字节数：拖一张几十 MB 的图进来不值得把内存拉满。</summary>
    private const int MaxDropImageBytes = 32 * 1024 * 1024;

    /// <summary>可投放目标快照（IslandService 在台账变化时推来，已按 Order 排好）。</summary>
    private IReadOnlyList<(string Owner, IslandDropTarget Target)> _dropTargets
        = Array.Empty<(string, IslandDropTarget)>();
    /// <summary>投放会话进行中：岛体是投放面板，悬停/内容切换都要为它让路。</summary>
    private bool _dropping;
    private DropStripView? _dropView;
    private readonly DispatcherQueueTimer _dropScroll;
    private readonly DispatcherQueueTimer _dropExitGrace;
    /// <summary>本次会话读出来的载荷（卡片可投状态与摘要都按它算；未知表示还没读完）。</summary>
    private DropPayload _dropPayload = DropPayload.Unknown;
    /// <summary>这次拖拽的内容能不能接（格式判断只做一次，DragOver 复用）。</summary>
    private bool _dropSupported;
    /// <summary>这次拖拽的内容读不出来（未知格式/浏览器虚拟文件）：本次拖拽期间不再展开面板，免得反复开合。</summary>
    private bool _dropUnreadable;
    /// <summary>最近一次 DragOver 的位置（滚动视口坐标，DIP）。</summary>
    private double _dropPointerX = double.NaN;
    private double _dropOffset;
    private double _dropVelocity;
    private int _dropArmedIndex = -1;
    /// <summary>会话序号：异步读载荷期间用户可能已经拖走或又拖回来，回填时用它对齐。</summary>
    private int _dropSessionId;

    /// <summary>投放目标台账变化（插件注册/注销、宿主内置注册）。</summary>
    public void SetDropTargets(IReadOnlyList<(string Owner, IslandDropTarget Target)> targets)
    {
        _dropTargets = targets;
        _log.Debug($"投放目标已更新：{targets.Count} 个（{string.Join("、", targets.Select(t => t.Target.Title))}）");
        if (_dropping) RefreshDropTiles();
        EnsureCanvas();
    }

    /// <summary>按最新台账重建卡片：正在投放时立刻生效，已经被高亮的卡片不再存在就解除高亮。</summary>
    private void RefreshDropTiles()
    {
        _dropView?.SetTargets(_dropTargets);
        _dropArmedIndex = -1;
        _dropOffset = Math.Min(_dropOffset, _dropView?.ScrollableWidth ?? 0);
    }

    /// <summary>
    /// 系统换主题时把投放面板整块换新：面板底色、文字与边缘渐隐都是建视图时烘进画刷的，改不动只能重建。
    /// 会话状态（载荷、滚动位置）由岛窗口持有，重建后原样回填；尺寸不变，所以不碰窗口几何，
    /// 也不打断正在进行的拖放。
    /// </summary>
    private void RebuildDropPanel()
    {
        var view = new DropStripView(_style, _materialApplied, _light);
        view.SetTargets(_dropTargets);
        view.SetPayload(_dropPayload);

        _dropView = view;
        _dropArmedIndex = -1;      // 换了一批卡片，高亮由下一次 DragOver 重新裁决
        SetContentImmediate(view.Root);
        view.ScrollTo(_dropOffset);
    }

    private void IslandRoot_DragEnter(object sender, DragEventArgs e)
    {
        // 格式判断会同步调用源进程（Contains）：每次拖拽只问一次，DragOver 用缓存结果 ——
        // 拖拽期间 DragOver 触发频率很高，不能每次都去问源进程
        _dropSupported = DescribeDrag(e.DataView) != IslandDropKind.None;
        if (!_dropSupported)
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;

        if (_spotlightOccluded) return;      // 聚光卡期间岛体整块隐藏，拖放不该发生
        _dropExitGrace.Stop();
        BeginDropSession(e);
    }

    private void IslandRoot_DragOver(object sender, DragEventArgs e)
    {
        if (!_dropSupported)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        if (!_dropping) BeginDropSession(e);

        e.AcceptedOperation = _dropping ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.Handled = true;

        if (!_dropping) return;

        _dropExitGrace.Stop();
        UpdateDropHover(e);
    }

    private void IslandRoot_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropUnreadable = false;   // 这次拖拽离开岛体：下一次（新拖拽）重新允许展开
        if (!_dropping) return;

        _dropExitGrace.Stop();
        _dropExitGrace.Start();
    }

    private async void IslandRoot_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        int armed = _dropArmedIndex;
        bool hasTarget = _dropping && armed >= 0 && armed < _dropTargets.Count;
        e.AcceptedOperation = hasTarget ? DataPackageOperation.Copy : DataPackageOperation.None;

        var deferral = e.GetDeferral();
        var target = hasTarget ? _dropTargets[armed] : default;
        // 载荷可能在松手时还没读完（拖得很快）：先用缓存的那份，没有就现读一次
        var payload = _dropPayload;

        // 先收回岛体，再去读载荷/执行动作：插件动作可能等上几秒，不能让它把投放面板吊在屏幕上
        EndDropSession();

        try
        {
            if (!hasTarget) return;      // 没对准卡片：什么都不做（绝不误触发打开文件）

            if (!payload.IsKnown) payload = await ReadPayloadAsync(e.DataView);
            if (!payload.IsKnown)
            {
                _log.Warn($"投放「{target.Target.Title}」已忽略：这次拖拽的载荷读不出来。");
                return;
            }

            // 卡片是在"载荷未知"时点亮的，这里按真实载荷复核一次（类型 + 扩展名）
            if (!DropStripView.Accepts(target.Target, payload))
            {
                _log.Warn($"投放「{target.Target.Title}」已忽略：它不接受{Describe(payload)}。");
                return;
            }

            _log.Info($"投放「{target.Target.Title}」（{target.Owner}）：{Describe(payload)}。");
            var message = await target.Target.Handler(BuildContext(payload));

            // 每个动作都要有反馈：插件没返回文案时，宿主补一条默认的「已完成」
            ShowMessage(new IslandMessage
            {
                Title = string.IsNullOrWhiteSpace(message) ? $"已完成「{target.Target.Title}」" : message,
                Glyph = target.Target.Glyph,
                AccentColor = target.Target.AccentColor,
            });
        }
        catch (Exception ex)
        {
            _log.Error("处理文件投放失败", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>展开成投放面板：岛体变宽、内容换成投放卡片。</summary>
    private void BeginDropSession(DragEventArgs e)
    {
        if (_dropping) return;
        if (_dropUnreadable) return;        // 这次拖拽的内容读不出来，别再反复开合
        if (!_settings.Get(DropEnabledKey, true)) return;

        _dropping = true;
        _dropSessionId++;
        _dropArmedIndex = -1;
        _dropPayload = DropPayload.Unknown;
        _dropPointerX = double.NaN;
        _dropOffset = 0;
        _dropVelocity = 0;

        // 投放面板与展开队列不能共存：队列排到岛体外面，会把面板顶出点击形状
        TeardownQueue();
        _expanded = false;
        ClearTouchExpand();

        _dropView = new DropStripView(_style, _materialApplied, _light);
        _dropView.SetTargets(_dropTargets);
        SetContentImmediate(_dropView.Root);     // 面板自带进场动画，不再套一层内容交接

        EnsureCanvas();
        AnimateIslandSize(DropPanelSize, ExpandDuration);
        ApplyCornerRadius(DropPanelSize.Height, expanded: false);
        _dropView.PlayEnter();
        UpdateVisibility();

        _ = RefreshDropPayloadAsync(e.DataView, _dropSessionId);
    }

    /// <summary>结束投放会话：收起面板、恢复原本的内容（与悬停收起同一套时序）。</summary>
    private void EndDropSession()
    {
        if (!ClearDropSession()) return;

        TransitionToLiveState();
        UpdateVisibility();
    }

    /// <summary>只清会话状态与定时器，不动岛体外观（聚光卡遮挡等路径自己接管后续）。</summary>
    private bool ClearDropSession()
    {
        if (!_dropping) return false;

        _dropping = false;
        _dropSessionId++;
        _dropArmedIndex = -1;
        _dropPayload = DropPayload.Unknown;
        StopDropScroll();
        _dropExitGrace.Stop();
        _dropView = null;
        return true;
    }

    /// <summary>会话仍在进行时把内容切回投放面板（临时消息结束、聚光卡关闭等路径会走到）。</summary>
    private void ShowDropPanel()
    {
        if (_dropView == null) return;
        SetContentImmediate(_dropView.Root);
        AnimateIslandSize(DropPanelSize, ExpandDuration);
        ApplyCornerRadius(DropPanelSize.Height, expanded: false);
    }

    /// <summary>
    /// 拖拽期间的"悬停"：OLE 拖放期间指针被源进程捕获，我们收不到任何指针事件，
    /// 位置只能从 DragOver 的坐标推。这里只记录位置 + 更新高亮与系统提示，
    /// 滚动交给 16ms 定时器 —— 指针停着不动时 OLE 不再发 DragOver，光靠事件永远滚不起来。
    /// </summary>
    private void UpdateDropHover(DragEventArgs e)
    {
        if (_dropView == null) return;

        var point = e.GetPosition(_dropView.Root);
        _dropPointerX = _dropView.ToViewportPoint(point).X;
        // 卡片增减后视口会自己夹一次滚动位置，这里跟它对齐，免得下一拍往回跳
        _dropOffset = _dropView.HorizontalOffset;

        int index = _dropView.HitTest(point);
        if (_dropView.SetArmed(index)) _dropArmedIndex = index;

        e.DragUIOverride.Caption = index >= 0 ? DropCaption(index) : "拖到卡片上松开";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;

        // 在边缘带里才启动定时器；离开由定时器自己减速停下，不硬切
        if (_dropView.ScrollableWidth > 1 && DropScrollTargetVelocity() != 0) _dropScroll.Start();
    }

    /// <summary>
    /// 系统拖拽气泡的文案：卡片带副标题就一起写上（例如「记入历史」· 存进历史列表）——
    /// 卡片只有 72px 宽，副标题在卡片上放不下，气泡是唯一能把"这张卡到底做什么"说清楚的地方。
    /// </summary>
    private string DropCaption(int index)
    {
        var title = _dropView?.TitleOf(index) ?? string.Empty;
        var hint = index >= 0 && index < _dropTargets.Count ? _dropTargets[index].Target.Hint : null;
        return string.IsNullOrWhiteSpace(hint) ? $"投放到「{title}」" : $"投放到「{title}」· {hint}";
    }

    /// <summary>目标滚速：越往边缘带里压越快（线性），带外为 0。</summary>
    private double DropScrollTargetVelocity()
    {
        if (_dropView == null || double.IsNaN(_dropPointerX)) return 0;

        double viewport = _dropView.ViewportWidth;
        if (viewport <= 0) return 0;

        double zone = Math.Min(DropEdgeZone, viewport / 3);
        if (zone <= 0) return 0;

        if (_dropPointerX < zone)
        {
            return -DropScrollMaxSpeed * Math.Clamp((zone - _dropPointerX) / zone, 0, 1);
        }

        if (_dropPointerX > viewport - zone)
        {
            return DropScrollMaxSpeed * Math.Clamp((_dropPointerX - (viewport - zone)) / zone, 0, 1);
        }

        return 0;
    }

    private void DropScrollTick()
    {
        if (!_dropping || _dropView == null)
        {
            StopDropScroll();
            return;
        }

        double target = DropScrollTargetVelocity();
        _dropVelocity += (target - _dropVelocity) * DropScrollSmoothing;

        if (target == 0 && Math.Abs(_dropVelocity) < 4)
        {
            StopDropScroll();
            return;
        }

        double max = _dropView.ScrollableWidth;
        _dropOffset = Math.Clamp(_dropOffset + _dropVelocity * (DropScrollTickMs / 1000.0), 0, max);

        // 撞到两端就把速度清零，免得在边界上顶着不动却还在加速
        if ((_dropOffset <= 0 && _dropVelocity < 0) || (_dropOffset >= max && _dropVelocity > 0))
        {
            _dropVelocity = 0;
        }

        _dropView.ScrollTo(_dropOffset);
    }

    private void StopDropScroll()
    {
        _dropVelocity = 0;
        _dropScroll.Stop();
    }

    /// <summary>载荷读取：面板先出来，摘要与卡片可投状态随后回填（读源进程的数据可能要等一拍）。</summary>
    private async Task RefreshDropPayloadAsync(DataPackageView view, int sessionId)
    {
        try
        {
            var payload = await ReadPayloadAsync(view);
            if (sessionId != _dropSessionId || !_dropping) return;

            if (!payload.IsKnown)
            {
                // 读不出来（未知格式、浏览器虚拟文件等）：收回面板，并且这次拖拽期间不再展开 ——
                // 否则会停在"正在读取…"上一直亮着，看着像卡死
                _log.Info("这次拖入的内容读不出来，投放面板已收回。");
                _dropUnreadable = true;
                EndDropSession();
                return;
            }

            _dropPayload = payload;
            _dropView?.SetPayload(payload);
            // 卡片刚按真实载荷筛过一遍，高亮索引跟着视图走（被收起来的那张不能再算数）
            _dropArmedIndex = _dropView?.ArmedIndex ?? -1;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取拖入的内容失败（投放面板仍可用）：{ex.Message}");
        }
    }

    /// <summary>
    /// 读某个格式的超时时间。拖拽源（浏览器等）偶尔会对某个格式**一直不回应**，
    /// 每次读都必须带超时，读不出来就按该格式不存在处理、走下一步退化。
    /// </summary>
    private static readonly TimeSpan DropProbeTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DropFormatTimeout = TimeSpan.FromMilliseconds(2000);
    /// <summary>整次载荷读取的总上限（各步超时之和的安全网）。</summary>
    private static readonly TimeSpan DropReadTimeout = TimeSpan.FromMilliseconds(6000);

    /// <summary>给一次读取套上超时；超时返回 default，并保证迟到的异常不会变成未观察异常。</summary>
    private async Task<T?> ReadWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            _log.Warn($"读取拖入的{what}超时（{timeout.TotalMilliseconds:0} ms），按读不出来处理");
            _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return default;
        }

        return await task;
    }

    /// <summary>
    /// 读一次拖入内容。**整段跑在线程池上**：<see cref="DataPackageView"/> 的每个成员（
    /// <c>Contains</c> / <c>AvailableFormats</c> / <c>GetXxxAsync</c>）都是**同步 COM 调用**，
    /// 源进程不回应时会直接阻塞调用线程 —— 放在 UI 线程上就会把整个界面冻住，超时也救不回来。
    /// </summary>
    private async Task<DropPayload> ReadPayloadAsync(DataPackageView view)
    {
        var payload = await ReadWithTimeoutAsync(
            Task.Run(() => ReadPayloadCoreAsync(view)), DropReadTimeout, "拖入内容");

        return payload ?? DropPayload.Unknown;
    }

    /// <summary>
    /// 载荷读取的**退化链**（每一步都带超时，读不出来就往下走）：
    /// 真实文件 → 图片 → 文本。
    ///
    /// 「真实文件」只认 <c>FileDrop</c>（CF_HDROP）：浏览器拖图片时会用
    /// <c>FileGroupDescriptorW</c> + <c>FileContents</c> 提供**虚拟文件**（没有 FileDrop），
    /// 那种 <c>GetStorageItemsAsync</c> 读不出真实路径（会一直挂着）—— 让它落回图片/文本。
    /// </summary>
    private async Task<DropPayload> ReadPayloadCoreAsync(DataPackageView view)
    {
        string formats;
        try
        {
            formats = string.Join("、", view.AvailableFormats);
        }
        catch (Exception ex)
        {
            formats = $"(读不到格式列表：{ex.Message})";
        }

        var payload = DropPayload.Unknown;

        // 1) 存储项：真文件给路径；浏览器拖图片给的是"虚拟文件"（没有真实路径，但内容可读）→ 按图片读出字节
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            payload = await ReadStorageItemsAsync(view);
        }

        // 2) 位图（能拿到位图字节才算；SVG 这类浏览器给不出位图的会落到第 3 步）
        if (!payload.IsKnown && await LooksLikeImageAsync(view))
        {
            var bytes = await ReadBitmapBytesAsync(view);
            if (bytes is { Length: > 0 })
            {
                payload = new DropPayload(IslandDropKind.Image, Array.Empty<string>(), Array.Empty<string>(), ImageBytes: bytes);
            }
        }

        // 3) 文本（纯文本选区、链接、虚拟文件/矢量图的地址都落在这里）
        if (!payload.IsKnown && view.Contains(StandardDataFormats.Text))
        {
            var text = await SafeGetTextAsync(view);
            if (!string.IsNullOrEmpty(text))
            {
                payload = new DropPayload(IslandDropKind.Text, Array.Empty<string>(), Array.Empty<string>(), Text: text);
            }
        }

        _log.Debug($"拖入载荷：可用格式 [{formats}] → {(payload.IsKnown ? payload.Kind.ToString() : "读不出来")}");
        return payload;
    }

    /// <summary>
    /// 读存储项。两类要分开：
    /// ① 真实文件（有绝对路径）→ 文件载荷；
    /// ② **虚拟文件** —— 浏览器拖网页图片时给的就是这种（只有 <c>FileGroupDescriptorW</c> + <c>FileContents</c>，
    ///   没有真实路径），内容仍可读：按扩展名认出是图片就把它读成字节，走图片载荷（SVG 也能原样保存）。
    /// </summary>
    private async Task<DropPayload> ReadStorageItemsAsync(DataPackageView view)
    {
        var items = await ReadWithTimeoutAsync(view.GetStorageItemsAsync().AsTask(), DropFormatTimeout, "文件列表");
        if (items is null || items.Count == 0) return DropPayload.Unknown;

        var paths = new List<string>();
        var names = new List<string>();
        StorageFile? virtualImage = null;

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                paths.Add(item.Path);
                names.Add(item.Name);
                continue;
            }

            // 虚拟机位（浏览器拖的图片、压缩包内、网络位置）：没有绝对路径
            if (virtualImage is null && item is StorageFile file && IsImageExtension(file.FileType))
            {
                virtualImage = file;
            }
        }

        if (paths.Count > 0) return new DropPayload(IslandDropKind.Files, paths, names);

        if (virtualImage is not null)
        {
            var bytes = await ReadStorageFileBytesAsync(virtualImage);
            if (bytes is { Length: > 0 })
            {
                _log.Info($"拖入的是虚拟文件「{virtualImage.Name}」（浏览器提供，没有真实路径），已按图片读出内容：{bytes.Length / 1024} KB");
                return new DropPayload(IslandDropKind.Image, Array.Empty<string>(), Array.Empty<string>(), ImageBytes: bytes);
            }
        }

        return DropPayload.Unknown;
    }

    private async Task<byte[]?> ReadStorageFileBytesAsync(StorageFile file)
    {
        try
        {
            return await ReadWithTimeoutAsync(ReadStorageFileBytesCoreAsync(file), DropFormatTimeout, "文件内容");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadStorageFileBytesCoreAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > (ulong)MaxDropImageBytes) return null;

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>像不像图片拖拽：有位图格式，或 HTML 片段里带 &lt;img&gt;，或文本本身就是图片地址。</summary>
    private async Task<bool> LooksLikeImageAsync(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.Bitmap)) return true;
        if (await HasImageHtmlAsync(view)) return true;
        return IsImageUrl(await SafeGetTextAsync(view));
    }

    /// <summary>HTML 片段里有没有 &lt;img&gt;：网页图片拖拽的典型特征（文字选区不会有）。</summary>
    private async Task<bool> HasImageHtmlAsync(DataPackageView view)
    {
        try
        {
            if (!view.Contains(StandardDataFormats.Html)) return false;

            // 探测用的超时更短：读不到就当没有，别为了判断类型把面板卡住
            var html = await ReadWithTimeoutAsync(view.GetHtmlFormatAsync().AsTask(), DropProbeTimeout, "HTML 片段");
            return html is not null && html.Contains("<img", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;   // 读不到就当没有，按文本处理
        }
    }

    private async Task<string?> SafeGetTextAsync(DataPackageView view)
    {
        try
        {
            return await ReadWithTimeoutAsync(view.GetTextAsync().AsTask(), DropFormatTimeout, "文本");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>文本是不是一个"图片地址"（http(s)/file/data/绝对路径，且扩展名是图片）。</summary>
    private static bool IsImageUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.Length > 2048 || trimmed.Contains('\n')) return false;   // 多行文本显然不是单个地址

        bool looksLikeAddress = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                || trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                                || trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                                || (trimmed.Length > 2 && trimmed[1] == ':');     // C:\...
        if (!looksLikeAddress) return false;

        // 注意：本文件同时 using 了 Microsoft.UI.Xaml.Shapes（里面也有 Path），必须写全名
        return IsImageExtension(System.IO.Path.GetExtension(trimmed));
    }

    /// <summary>扩展名是不是图片（含矢量图 svg）。传 ".png" 或 "png" 都行。</summary>
    private static bool IsImageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;

        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        switch (normalized.ToLowerInvariant())
        {
            case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp":
            case ".webp": case ".tif": case ".tiff": case ".ico": case ".avif":
            case ".svg": case ".heic":
                return true;
            default:
                return false;
        }
    }

    /// <summary>读图片字节（保留源格式）。超时、超限或读不出来返回 null，调用方按"不支持"处理。</summary>
    private async Task<byte[]?> ReadBitmapBytesAsync(DataPackageView view)
    {
        try
        {
            return await ReadWithTimeoutAsync(ReadBitmapCoreAsync(view), DropFormatTimeout, "图片数据");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadBitmapCoreAsync(DataPackageView view)
    {
        var reference = await view.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > (ulong)MaxDropImageBytes) return null;

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// 「这次拖拽能不能接」的同步判定（DragEnter/DragOver 用）：有文件、图片或文本之一就接。
    /// 到底算哪种载荷由 <see cref="ResolveKindAsync"/> 读到内容后再定 ——
    /// 同步阶段分辨不出「文字选区附带的那张快照位图」。
    /// </summary>
    private static IslandDropKind DescribeDrag(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.StorageItems)) return IslandDropKind.Files;
        if (view.Contains(StandardDataFormats.Bitmap)) return IslandDropKind.Image;
        if (view.Contains(StandardDataFormats.Text)) return IslandDropKind.Text;
        return IslandDropKind.None;
    }

    private static IslandDropContext BuildContext(DropPayload payload) => new()
    {
        Kind = payload.Kind,
        Paths = payload.Paths,
        Names = payload.Names,
        Text = payload.Text,
        ImageBytes = payload.ImageBytes,
    };

    /// <summary>日志用的载荷描述。</summary>
    private static string Describe(DropPayload payload) => payload.Kind switch
    {
        IslandDropKind.Files => $"{payload.Paths.Count} 个文件",
        IslandDropKind.Text => $"文本（{payload.Text?.Length ?? 0} 字符）",
        IslandDropKind.Image => $"图片（{(payload.ImageBytes?.LongLength ?? 0) / 1024} KB）",
        _ => "未知载荷",
    };

    /// <summary>island.dropEnabled：关掉后岛对文件拖放完全无感（连 DragEnter 都不会触发）。</summary>
    private void ApplyDropSetting()
    {
        bool enabled = _settings.Get(DropEnabledKey, true);
        IslandRoot.AllowDrop = enabled;
        if (!enabled && _dropping) EndDropSession();
    }

    #endregion

    #region 内置视图

    private static UIElement BuildIdleContent(Ellipse dot)
    {
        var grid = new Grid();
        grid.Children.Add(dot);
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
        /// <summary>上缘 1px 渐变高光（只有 Fluent 有；Apple 的哑光纯黑胶囊不加）。</summary>
        private readonly Border _highlight;

        public IslandSurface(Border border, ContentPresenter content)
        {
            Border = border;
            _content = content;
            _highlight = new Border
            {
                Height = 1,
                Margin = new Thickness(1, 1, 1, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Opacity = 0,
            };

            if (border.Child is Grid host)
            {
                // 主岛：XAML 里那层 Grid（ContentHost + ContentLeaving），高光挂在最上面
                host.Children.Add(_highlight);
            }
            else
            {
                // 卡片：必须先把 Border 的 Child 换成包裹层（这时 content 才从 Border 上摘下来），
                // 再往包裹层里挂 —— 顺序反了就是"元素已挂在另一个父级下"，构造当场抛 COMException。
                var wrapper = new Grid();
                border.Child = wrapper;
                wrapper.Children.Add(content);
                wrapper.Children.Add(_highlight);
            }
        }

        /// <summary>
        /// 独立卡片：Border 与内容宿主自己搭起来（主岛的两个元素来自 XAML）。
        /// 内容层的缩放变换是给翻页动画用的（只动内容、不动卡片矩形，所以点击形状不受影响）——
        /// 只有卡片有它，主岛那个 ContentHost 的变换归内容交接（<c>_contentSlide</c>）所有。
        /// </summary>
        public static IslandSurface CreateCard()
        {
            var content = new ContentPresenter();
            var surface = new IslandSurface(new Border { Child = content }, content);
            content.RenderTransform = surface.ContentScale;
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            return surface;
        }

        public Border Border { get; }

        /// <summary>卡片内容层的缩放（翻页时的进出微缩放）。</summary>
        public ScaleTransform ContentScale { get; } = new();

        /// <summary>卡片对应的活动：拆卸时按这个记录停止形态动画，顺序变化也安全。</summary>
        public string? Owner { get; set; }

        /// <summary>卡片内容的形态视图（可能为 null，例如只有双视图的插件）。</summary>
        public IMorphView? MorphView { get; set; }

        public void ApplyStyle(IslandStyleKind style, bool materialApplied, bool light)
        {
            var stroke = IslandStyle.CreateStroke(style, light);
            Border.Background = IslandStyle.CreateSurfaceFill(style, materialApplied, light);
            Border.BorderBrush = stroke;
            Border.BorderThickness = stroke == null ? new Thickness(0) : new Thickness(1);

            var highlight = IslandStyle.CreateTopHighlight(style, light);
            _highlight.Background = highlight;
            _highlight.Opacity = highlight == null ? 0 : 1;
        }

        public void SetRadius(double radius) => Border.CornerRadius = new CornerRadius(radius);

        public void SetContent(UIElement? content) => _content.Content = content;
    }

    /// <summary>
    /// 队列翻页按钮：圆形外壳（<see cref="Border"/>，走和卡片同一套点击形状与配色通道）
    /// 加一枚内嵌的原生 <see cref="Button"/> —— 交互与悬停/按下态交给控件模板自己的主题态，不手搓。
    /// </summary>
    private sealed class QueuePager
    {
        private readonly FontIcon _glyph;

        public QueuePager(double size, string glyph, RoutedEventHandler clicked)
        {
            _glyph = new FontIcon
            {
                Glyph = glyph,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var button = new Button
            {
                Width = size - 2,
                Height = size - 2,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                IsTabStop = false,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(size / 2 - 1),
                Content = _glyph,
            };
            button.Click += clicked;

            Border = new Border
            {
                Width = size,
                Height = size,
                // 正圆：不走卡片那套 ResolveQueueRadius（那是给矩形卡片用的）
                CornerRadius = new CornerRadius(size / 2),
                Child = button,
            };
            ToolTipService.SetToolTip(Border, "下一个活动");
        }

        /// <summary>面板里的那个 Border：点击形状按它的矩形与圆角生成。</summary>
        public Border Border { get; }

        public void ApplyStyle(IslandStyleKind style, bool materialApplied, bool light)
        {
            var stroke = IslandStyle.CreateStroke(style, light);
            Border.Background = IslandStyle.CreateSurfaceFill(style, materialApplied, light);
            Border.BorderBrush = stroke;
            Border.BorderThickness = stroke == null ? new Thickness(0) : new Thickness(1);
            _glyph.Foreground = IslandStyle.CreateMessageTextBrush(light);
        }
    }

    #endregion
}
