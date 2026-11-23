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
/// 队列保持单列，通过末尾翻页位访问其余活动。
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
    private static TimeSpan PageOutDuration => Motion.Duration(100);
    private static TimeSpan PageInDuration => Motion.Duration(190);
    private static TimeSpan PageInDelay => Motion.Enabled ? TimeSpan.FromMilliseconds(55) : TimeSpan.Zero;
    /// <summary>翻页时新卡内容层的起始缩放：只缩不放（恒 ≤ 1），所以永远不会越出卡片矩形。</summary>
    private const double PageEnterScale = 0.985;
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
    private static TimeSpan ExpandDuration => Motion.Duration(320);
    private static TimeSpan CollapseDuration => Motion.Duration(240);
    private const double MinCanvasWidth = 420;
    /// <summary>临时内容（消息 / 插件自定义内容）的尺寸 morph：比悬停展开略长一点，与内容交接同拍落地。</summary>
    private static TimeSpan TemporaryMorphDuration => Motion.Duration(280);

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
    /// <summary>岛体圆角比例（百分比，100 = 风格默认）。</summary>
    private const string RadiusKey = "island.radius";
    /// <summary>岛体不透明度（百分比，10..100）。</summary>
    private const string OpacityKey = "island.opacity";
    private const double DefaultOffset = 6.0;
    /// <summary>靠左/靠右时距条带（或工作区）边缘的间距。Win11 图标居中排列，任务栏左端通常是空的。</summary>
    private const double EdgeInset = 12;
    /// <summary>底部模式靠右、且量不到托盘区（TrayNotifyWnd）时的兜底预留量。</summary>
    private const double TrayReserve = 220;
    private const int PositionSlideMs = 300;
    /// <summary>聚光卡打开时岛体淡出、关闭后淡入的时长（只动透明度，不碰窗口几何）。</summary>
    private const int SpotlightFadeOutMs = 140;
    private const int SpotlightFadeInMs = 180;
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

    /// <summary>宿主侧的额外能力（勿扰状态、消息历史、右键菜单动作）。岛体自己不认识 App。</summary>
    internal IslandHost? HostServices { get; set; }

    /// <summary>宿主服务的内部读法（各处频繁读，省掉一层属性名）。</summary>
    private IslandHost? _host => HostServices;

    // 常驻活动列表（按原有优先级降序排列，优先级相同则按插入顺序）：小岛常驻取 [0]
    private readonly List<(string Owner, IslandLiveContent Content)> _live = new();
    // 展开侧顺序（按展开优先级降序排列）：展开主卡取 [0]、队列取 [1..]。
    // 没有任何插件声明/设置展开优先级时，它与 _live 完全一致（旧行为不变）。
    private readonly List<(string Owner, IslandLiveContent Content)> _expandedOrder = new();
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
    /// <summary>输入会话（内容里的文本框获得焦点时临时解除 NOACTIVATE）：见 <see cref="InputSession"/>。</summary>
    private readonly InputSession _input;
    private int _fadeVersion;
    private Storyboard? _islandFade;
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
    // 用户外观：圆角比例（1.0 = 风格默认）与岛体不透明度（0.05..1）
    private double _radiusScale = 1.0;
    private double _opacity = 1.0;

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
    /// <summary>上一拍看到的勿扰状态：只为在「静默开始/结束」那一刻刷新一次可见性。</summary>
    private bool _lastQuiet;

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

    // 展开侧：_expandedOrder[0] = 展开主卡；小岛常驻固定取 _live[0]（原优先级最高者，与展开优先级无关）。
    /// <summary>展开态主岛显示的活动（展开优先级最高者；未设展开优先级时即原优先级最高者）。</summary>
    private IslandLiveContent? MainLive => _expandedOrder.Count > 0 ? _expandedOrder[0].Content : null;
    private string MainOwner => _expandedOrder.Count > 0 ? _expandedOrder[0].Owner : "(无)";

    /// <summary>
    /// 小岛常驻的活动：默认是原优先级最高者（<c>_live[0]</c>）；
    /// 用户用滚轮在小胶囊上翻过的话，优先显示被钉住的那张（见 <see cref="CycleCompact"/>）。
    /// 钉住的那张已被卸载/停用（不在表里）时自动回退到 <c>_live[0]</c>，不留下空岛。
    /// </summary>
    private (string Owner, IslandLiveContent Content)? CompactEntry
    {
        get
        {
            if (_pinnedCompactOwner != null)
            {
                foreach (var entry in _live)
                {
                    if (entry.Owner == _pinnedCompactOwner) return entry;
                }
            }
            return _live.Count == 0 ? null : _live[0];
        }
    }
    private IslandLiveContent? CompactLive => CompactEntry?.Content;
    private string CompactOwner => CompactEntry?.Owner ?? "(无)";

    /// <summary>
    /// 当前岛体上显示的内容：展开时是主卡，收起时是小岛常驻那张。
    /// 绝大多数调用点都在 <see cref="_expanded"/> 置位之后才读它，所以它们自动取到正确的一张。
    /// </summary>
    private IslandLiveContent? ActiveLive => _expanded ? MainLive : CompactLive;
    private string ActiveOwner => _expanded ? MainOwner : CompactOwner;

    // 队列活动（除展开主卡外的其余活动，按展开优先级排列）
    private IReadOnlyList<(string Owner, IslandLiveContent Content)> QueueItems
        => _expandedOrder.Count > 1 ? _expandedOrder.Skip(1).ToList() : Array.Empty<(string, IslandLiveContent)>();

    /// <summary>临时消息在队列里的"主人"标识（不是插件 id，翻页位永远不会落到它身上）。</summary>
    private const string TempCardOwner = "(message)";

    // 活动记账 key 的拼法在 IslandWindowKeys 里（纯逻辑，回归测试直接引用它）
    internal static string LiveKey(string owner, string? instanceId) => IslandWindowKeys.LiveKey(owner, instanceId);
    internal static string OwnerOfKey(string key) => IslandWindowKeys.OwnerOfKey(key);

    /// <summary>
    /// 展开队列的条目来源：临时消息的卡片（只有展开期间才存在）排在所有活动之前，
    /// 其余与 <see cref="QueueItems"/> 一致。队列尺寸、卡片高度、翻页与显示条目都读这里。
    /// </summary>
    private IReadOnlyList<(string Owner, IslandLiveContent Content)> QueueSource()
    {
        if (_tempCard == null) return QueueItems;

        var items = new List<(string Owner, IslandLiveContent Content)>(_expandedOrder.Count) { (TempCardOwner, _tempCard) };
        for (int i = 1; i < _expandedOrder.Count; i++) items.Add(_expandedOrder[i]);
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
        QueuePanel.PointerWheelChanged += (_, e) =>
        {
            if (_dropping || !_expanded || QueueSource().Count <= QueueVisibleCards) return;
            var delta = e.GetCurrentPoint(QueuePanel).Properties.MouseWheelDelta;
            if (delta == 0) return;
            PageQueueTail(delta < 0 ? 1 : -1);
            e.Handled = true;
        };

        // 紧凑态滚轮：在「小岛常驻」那张之外的活动之间切换（等价于临时换一次常驻内容）。
        // 展开态交给上面的队列翻页，两个处理器不会同时生效。
        IslandRoot.PointerWheelChanged += (_, e) =>
        {
            if (_dropping || _expanded || _live.Count <= 1) return;
            var delta = e.GetCurrentPoint(IslandRoot).Properties.MouseWheelDelta;
            if (delta == 0) return;
            CycleCompact(delta < 0 ? 1 : -1);
            e.Handled = true;
        };

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

        // 输入会话：默认不激活，只有内容里的文本框获得焦点/被点击时才临时放开（见 InputSession）。
        // 检测挂 RootGrid 而不是 IslandRoot：展开队列卡片里的输入控件同样要接住。
        _input = new InputSession(this, _log, () => UpdateHitRegion(force: true));
        _input.Attach(RootGrid);
        _input.Exited += OnInputSessionExited;

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
            // Sync native input/material after arrange; SizeChanged can still
            // carry the previous row position or bottom-anchor offset.
        };
        // Row swaps and bottom anchoring can move an unchanged-size card during arrange.
        // Read final layout before synchronizing the native shape and material mask.
        IslandStack.LayoutUpdated += (_, _) =>
        {
            if (IslandRoot.IsLoaded) UpdateHitRegion();
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
            _input.Dispose();
            _sizeStoryboard?.Stop();
            ReleaseSizeClock();
            _islandFade?.Stop();
            SettleContentSwap();
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
        // 小岛常驻被滚轮钉住的那张：同样按插件 id 记忆
        _pinnedCompactOwner = _settings.Get<string?>(CompactPinKey, null);
        ApplyScaleSettings();
        ApplyPositionMode();
        ApplyStyle();
        ApplyAppearance();
        ApplyDropSetting();
        EnsureCanvas();
    }

    #region 公开操作（由 IslandService 在 UI 线程调用）

    public void SetLive(string owner, IslandLiveContent? content) => SetLive(owner, null, content);

    /// <summary>
    /// 登记/更新/撤掉一条常驻内容。同一个插件可以挂多条（<paramref name="instanceId"/> 区分），
    /// 每条各自参与优先级排序、各占一张队列卡片。
    ///
    /// <paramref name="instanceId"/> 为 null 时记账 key 就是插件 id 本身 ——
    /// 单实例插件（所有既有插件）的语义与改动前逐字一致：同一个 id 重复登记仍然是覆盖。
    /// 多实例的 key 是 <c>id + U+001F + 实例名</c>，绝不会和单实例那条撞车。
    /// </summary>
    public void SetLive(string owner, string? instanceId, IslandLiveContent? content)
    {
        string key = LiveKey(owner, instanceId);
        bool hadActivityCount = _live.Count > 1;
        _live.RemoveAll(t => t.Owner == key);
        if (content != null)
        {
            _live.Add((key, content));
        }

        SortLive();

        // 内容集合变化时才允许扩容画布，之后悬停展开/收起不再动窗口几何
        EnsureCanvas();

        // 活动数量跨过显示门槛时，紧凑落点也要给圆点留出任务栏空闲段。
        if (_showActivityCount && hadActivityCount != (_live.Count > 1) && !_expanded && !_dropping)
            ApplyCanvasBounds();

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
        // 勿扰期内不打扰：消息照样记进历史（用户回头能翻到），只是不弹出来。
        // 静默只是「不主动弹」，用户在岛上的操作（悬停/点击）依旧正常。
        if (_host?.IsQuiet == true)
        {
            _host.RecordMessage(msg, owner);
            UpdateVisibility();
            return;
        }

        _host?.RecordMessage(msg, owner);
        var (view, size) = MessageView.Build(msg, _style, _light);
        ShowTemporary(view, size, msg.Duration, owner);
        // ShowTemporary 会把它清掉（那是对插件内容的语义），宿主消息在这里重新登记
        _tempMessage = msg;
    }

    public void RefreshFromSettings()
    {
        ApplyStyle();
        ApplyAppearance();
        ApplyPositionMode();
        ApplyDropSetting();
        ApplyScaleSettings();
        UpdateVisibility();
        EnsureCanvas();
        ApplyCanvasBounds();
        UpdateFullscreenState(force: true);
    }

    /// <summary>
    /// 用户外观设置：圆角比例（<c>island.radius</c>）与岛体不透明度（<c>island.opacity</c>）。
    /// 不透明度写在 <see cref="IslandStack"/> 上而不是岛体本身 —— 岛体的 Opacity 归聚光卡遮挡的
    /// 整块淡入淡出使用，挂在父级一次覆盖主岛＋队列卡片＋翻页按钮，且与那次淡出天然相乘。
    /// </summary>
    private void ApplyAppearance()
    {
        _showActivityCount = _settings.Get(ActivityCountKey, true);
        double radiusPercent = _settings.Get(RadiusKey, 100.0);
        _radiusScale = Math.Clamp(radiusPercent / 100.0, 0.0, 2.0);

        double opacityPercent = _settings.Get(OpacityKey, 100.0);
        _opacity = Math.Clamp(opacityPercent / 100.0, 0.05, 1.0);
        IslandStack.Opacity = _opacity;

        // 圆角是点击穿透形状的来源，必须重算并重下发
        ApplyCornerRadius(_radiusHeight, _radiusExpanded);
        RefreshQueueStyle();
        UpdateHitRegion(force: true);
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

    /// <summary>Spotlight closes onto the state that will actually return, not its old expanded pose.</summary>
    internal (Win32.RECT Rect, Size Size, double RadiusPixels) SpotlightReturnAnchor()
    {
        Size size = _tempContent != null ? _tempSize : CompactLive is { } live ? CompactSizeOf(live) : IdleSize;
        var pos = AppWindow.Position;
        double scale = Scale;
        double x = IslandLeftInWindow(scale, size.Width);
        double y = IslandTopInWindow(_bottomAnchored, scale, size.Height);
        return (new Win32.RECT
        {
            Left = pos.X + (int)Math.Round(x), Top = pos.Y + (int)Math.Round(y),
            Right = pos.X + (int)Math.Round(x + size.Width * scale),
            Bottom = pos.Y + (int)Math.Round(y + size.Height * scale),
        }, size, IslandStyle.ResolveRadius(_style, size.Height,
            expanded: _tempContent != null && size.Height > IslandStyle.MessageCardHeight, _radiusScale) * scale);
    }

    internal double MainIslandRadiusPixels => IslandRoot.CornerRadius.TopLeft * Scale;

    internal IslandStyleKind CurrentStyleKind => _style;

    internal bool IsMaterialApplied => _materialApplied;

    /// <summary>
    /// 聚光卡打开/关闭时遮挡或恢复岛体：这是岛体「被别的东西盖住」的唯一入口，
    /// 只做整块淡入淡出 + 窗口显示/隐藏，绝不改窗口几何（几何不变式优先）。
    /// </summary>
    internal void SetSpotlightOccluded(bool occluded, bool animate = true)
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
            _input.End();   // 输入会话随遮挡结束；_shown 已为 false，Exited 不会再触发收起
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

        // Restore while hidden so the first visible frame already has the compact geometry.
        RestoreSpotlightView();
        _islandFade?.Stop();
        _islandFade = null;
        ++_fadeVersion;
        IslandRoot.Opacity = animate ? 0 : 1;
        UpdateVisibility(force: true);
        if (!_shown) { IslandRoot.Opacity = 1; return; }
        if (animate) FadeIslandOpacity(1, SpotlightFadeInMs, null);
        else Motion.Reveal(ContentHost, 140, 0);
    }

    // Called at the start of the return flight, while this HWND remains hidden and occluded.
    internal void PrepareSpotlightReturn()
    {
        if (!_spotlightOccluded) return;
        RestoreSpotlightView();
        UpdateHitRegion(force: true);
    }

    /// <summary>聚光卡关闭后的内容还原：临时消息优先，否则按当前状态回到空闲/紧凑/展开。</summary>
    private void RestoreSpotlightView()
    {
        if (_tempContent != null)
        {
            // 岛体整块正在淡入，内容层直接就位（再套一层内容交接会变成两段淡入叠在一起）
            SetContentImmediate(_tempContent);
            _sizeStoryboard?.Stop();
            _sizeStoryboard = null;
            ReleaseSizeClock();
            ++_animVersion;
            _currentIsland = _currentTotalSize = _tempSize;
            IslandRoot.Width = _tempSize.Width;
            IslandRoot.Height = _tempSize.Height;
            ApplyTemporaryRadius(_tempSize.Height);
            return;
        }

        var live = CompactLive;
        SetContentImmediate(live?.MorphView?.View ?? live?.CompactContent ?? _idleContent);
        var size = live == null ? IdleSize : CompactSizeOf(live);
        _sizeStoryboard?.Stop();
        _sizeStoryboard = null;
        ReleaseSizeClock();
        ++_animVersion;
        _currentIsland = _currentTotalSize = size;
        IslandRoot.Width = size.Width;
        IslandRoot.Height = size.Height;
        ApplyCornerRadius(size.Height, expanded: false);
        AnimateMorphView(CompactEntry?.Owner ?? "(未知)", live?.MorphView, expand: false, duration: TimeSpan.FromMilliseconds(1));
    }

    /// <summary>岛体整块淡入/淡出：只动 Opacity 的独立动画，窗口几何全程不变。</summary>
    private void FadeIslandOpacity(double to, int milliseconds, Action? onDone)
    {
        double from = IslandRoot.Opacity;
        _islandFade?.Stop();
        IslandRoot.Opacity = from;
        int version = ++_fadeVersion;

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(Motion.Duration(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, IslandRoot);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (version != _fadeVersion) return;
            _islandFade = null;
            IslandRoot.Opacity = to;
            onDone?.Invoke();
        };
        _islandFade = storyboard;
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
        ApplyActivityCountStyle();
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

        if (_backdrop == null)
        {
            _backdrop = new IslandBackdrop(this);
            _backdrop.ClipSettled += () => UpdateHitRegion(force: true);
        }
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
            surface.SetRadius(IslandStyle.ResolveQueueRadius(_style, surface.RestingHeight, _radiusScale));
        }

        _pager?.ApplyStyle(_style, _materialApplied, _light);
    }

    public void ShowIsland()
    {
        UpdateVisibility(force: true);
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

        // 悬停展开关掉时，首次鼠标点击只负责展开（走触控那条"常驻展开"路径）。
        // 已展开的主卡与队列卡片一样，把点击交给插件；点岛外才收起。
        if (!HoverExpandEnabled && e.PointerDeviceType != PointerDeviceType.Touch && !_expanded)
        {
            if (TouchExpand()) return;
        }

        try
        {
            ActiveLive?.OnTap?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error($"插件「{ActiveOwner}」的点击回调抛异常（已忽略）", ex);
        }
    }

    /// <summary>
    /// 指针按下：右键出菜单、中键/Alt+左键准备拖拽、中键单击关消息。
    /// 三者的优先级是「右键 &gt; 拖拽 &gt; 中键关消息」——拖拽与中键关消息共用中键，
    /// 只能在松手时才知道用户是想拖还是想点（见 <see cref="IslandRoot_PointerReleased"/>）。
    /// </summary>
    private void IslandRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dropping) return;

        var point = e.GetCurrentPoint(IslandRoot);

        // 右键：弹出岛体自己的菜单。必须在 PointerPressed 里接 —— 岛窗是 WS_EX_NOACTIVATE、
        // 且没有 ContextFlyout，右键根本不会自动走到任何菜单。
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            ShowIslandMenu(point.Position);
            return;
        }

        // 中键 / Alt+左键：登记「可能开始拖拽」。真正拖动要等指针越过起始阈值，
        // 因此单击的语义完全不变 —— 中键单击关消息在 PointerReleased 里收尾。
        _pendingMiddleClick = point.Properties.IsMiddleButtonPressed && _tempContent != null;
        TryBeginDrag(e);
    }

    /// <summary>拖拽进行中的指针移动（没在拖时是空操作，其余交互完全不受影响）。</summary>
    private void IslandRoot_PointerMoved(object sender, PointerRoutedEventArgs e) => ContinueDrag(e);

    /// <summary>
    /// 松手：结束拖拽。真拖过就吃掉这次事件，避免顺手又触发插件点击；
    /// 中键按下后没移动 = 一次普通的「关掉临时消息」。
    /// </summary>
    private void IslandRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Win32.GetCursorPos(out var cursor);
        bool dragged = EndDrag(cursor);

        if (dragged)
        {
            e.Handled = true;
            _tapSuppressUntil = Environment.TickCount64 + TapSuppressMs;
            _pendingMiddleClick = false;
            return;
        }

        if (_pendingMiddleClick)
        {
            _pendingMiddleClick = false;
            e.Handled = true;
            _tapSuppressUntil = Environment.TickCount64 + TapSuppressMs;
            DismissTemporary();
        }
    }

    /// <summary>中键按下时是否压着一条临时消息：松手且没拖动时用它决定「关消息」。</summary>
    private bool _pendingMiddleClick;

    /// <summary>指针捕获丢失 / 取消：放弃这次拖拽（不落盘）。</summary>
    private void IslandRoot_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CancelDrag();
        _pendingMiddleClick = false;
    }

    /// <summary>
    /// 岛体右键菜单。所有条目都走宿主已有的入口，不新增状态：
    /// 打开设置 / 静默（一段时间）/ 隐藏岛（= island.visible 关掉，与托盘一致）/
    /// 取消当前活动（收起 + 关掉临时内容）/ 复制当前内容文本。
    ///
    /// 岛体本身不可激活，菜单由 XAML 的 <see cref="MenuFlyout"/> 承载：它挂在岛窗口上，
    /// 属于同一个 HWND，因此不需要输入会话那套临时解 NOACTIVATE 的手法。
    /// </summary>
    private void ShowIslandMenu(Point position)
    {
        var menu = new MenuFlyout();

        var settings = new MenuFlyoutItem { Text = "打开设置…", Icon = new FontIcon { Glyph = "\uE713" } };
        settings.Click += (_, _) => _host?.OpenSettings(null);
        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());

        // 静默：给出几档常用时长，正在静默时换成「结束静默」
        if (_host?.IsQuiet == true)
        {
            var remaining = QuietMode.DescribeRemaining(_settings);
            var end = new MenuFlyoutItem { Text = $"结束静默（还剩 {remaining}）", Icon = new FontIcon { Glyph = "\uE7E8" } };
            end.Click += (_, _) => QuietMode.End(_settings);
            menu.Items.Add(end);
        }
        else
        {
            var quiet = new MenuFlyoutSubItem { Text = "静默一段时间", Icon = new FontIcon { Glyph = "\uE7E8" } };
            foreach (int minutes in QuietMode.SnoozeMinutes)
            {
                var item = new MenuFlyoutItem { Text = $"{minutes} 分钟" };
                item.Click += (_, _) => QuietMode.Snooze(_settings, TimeSpan.FromMinutes(minutes));
                quiet.Items.Add(item);
            }
            menu.Items.Add(quiet);
        }

        bool visible = _settings.Get("island.visible", true);
        var hide = new MenuFlyoutItem
        {
            Text = visible ? "暂时隐藏灵动岛" : "显示灵动岛",
            Icon = new FontIcon { Glyph = visible ? "\uE7B3" : "\uE7B4" },
        };
        hide.Click += (_, _) => _settings.Set("island.visible", !visible);
        menu.Items.Add(hide);

        // 取消当前活动：只在确实有东西可取消时出现，避免菜单里挂一堆灰条
        if (_tempContent != null || _expanded || _live.Count > 0)
        {
            var cancel = new MenuFlyoutItem { Text = "取消当前活动", Icon = new FontIcon { Glyph = "\uE711" } };
            cancel.Click += (_, _) => CancelCurrentActivity();
            menu.Items.Add(cancel);
        }

        if (BuildCopyText() is { Length: > 0 } text)
        {
            var copy = new MenuFlyoutItem { Text = "复制当前内容", Icon = new FontIcon { Glyph = "\uE8C8" } };
            copy.Click += (_, _) => CopyToClipboard(text);
            menu.Items.Add(copy);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var exit = new MenuFlyoutItem { Text = "退出 WinIsland", Icon = new FontIcon { Glyph = "\uE7E8" } };
        exit.Click += (_, _) => _host?.RequestExit();
        menu.Items.Add(exit);

        // 右键路径下岛窗口通常不可激活：菜单自己抢不到焦点就会立刻消失。
        // 复用输入会话那套「临时解 NOACTIVATE + 抢前台」；菜单关闭后窗口会失活，
        // InputSession 的 Activated 监听会自动还原样式（见 OnActivated）。
        // 这条路径只是让菜单可见可点，不涉及任何文本输入。
        _input.TryBegin();
        menu.Closed += (_, _) =>
        {
            // 失活在某些环境不会立刻到（焦点弹回原窗口而非"无焦点"）：主动收尾，
            // 否则岛会一直停在可激活状态，接着 Alt+F4 之类的手势就落到岛上。
            _input.End();
        };
        menu.ShowAt(IslandRoot, new FlyoutShowOptions { Position = position });
    }

    /// <summary>
    /// 收起岛体并结束临时内容（右键菜单的「取消当前活动」）。
    /// 只收起、不停用插件 —— 插件下次报事依旧会浮上来，用户想彻底静音有「静默一段时间」。
    /// </summary>
    private void CancelCurrentActivity()
    {
        ClearTouchExpand();
        _hover = false;
        _hoverGuard.Stop();
        _hoverIntent.Stop();

        if (_tempContent != null)
        {
            _tapSuppressUntil = Environment.TickCount64 + TapSuppressMs;
            DismissTemporary();
        }

        if (_expanded) OnHoverExit();
    }

    /// <summary>
    /// 当前岛体上可见内容的文本（右键菜单「复制当前内容」）。
    /// 临时消息有原文；活动内容来自插件视图，宿主只能按「谁在报事」写一句可复制的描述。
    /// </summary>
    private string? BuildCopyText()
    {
        if (_tempMessage is { } message)
        {
            return string.IsNullOrWhiteSpace(message.Text) ? message.Title : $"{message.Title}\n{message.Text}";
        }

        if (ActiveLive is { } live)
        {
            string label = live.OwnerLabel ?? CompactOwner;
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }

        return null;
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            _log.Debug($"已复制当前内容（{text.Length} 字符）。");
        }
        catch (Exception ex)
        {
            _log.Warn($"复制到剪贴板失败：{ex.Message}");
        }
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
        if (_input.IsActive) return;   // 输入会话常驻：输入期间鼠标移开也不收起
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



    #region 内置视图

    private static UIElement BuildIdleContent(Ellipse dot)
    {
        var grid = new Grid();
        grid.Children.Add(dot);
        return grid;
    }

    #endregion


}
