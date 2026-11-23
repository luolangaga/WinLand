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

public sealed partial class IslandWindow
{
    #region 状态机

    private Size ComputeTotalSize(bool expanded)
    {
        // expanded 是入参，可能与 _expanded 字段不一致（画布预留要在两态都算对），所以显式挑。
        var live = expanded ? MainLive : CompactLive;
        if (live == null) return IdleSize;

        Size main = expanded ? ExpandedMainSize() : CompactSizeOf(live);

        var queue = QueueSource();
        if (expanded && queue.Count > 0)
        {
            // 单列：宽度恒为主岛宽（翻页按钮更窄、水平居中），高度 = 主岛 + 一屏卡片 +（有按钮时的）按钮行
            double height = QueueLayoutPolicy.TotalHeight(main.Height, queue.Count, QueueVisibleCards,
                QueueCardHeight(), QueueSpacing, QueueCardGap, QueuePagerSize);
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
        return QueueLayoutPolicy.ResolveTailIndex(queue.Select(item => item.Owner).ToArray(),
            _tailOwner, _tailIndex, QueueVisibleCards);
    }

    /// <summary>展开态主岛尺寸：高度由活动自己决定，宽度统一到栈宽（与卡片边缘对齐）。</summary>
    private Size ExpandedMainSize()
        => new(StackWidth(), MainLive?.ExpandedSize.Height ?? IdleSize.Height);

    /// <summary>展开态的统一宽度：主岛与所有队列卡片（跨列也一样）取最大展开宽度，边缘对齐。</summary>
    private double StackWidth()
    {
        double width = MainLive?.ExpandedSize.Width ?? IdleSize.Width;
        foreach (var (_, content) in _live)
        {
            width = Math.Max(width, content.ExpandedSize.Width);
        }
        return width;
    }

    /// <summary>
    /// 队列卡片统一高度：所有活动的最高声明高度，上限 180 DIP，并按目标屏剩余空间收紧。
    /// 翻页仍使用同一个高度；超过槽位的内容在卡内滚动，不把整列撑出屏幕。
    /// </summary>
    private double QueueCardHeight()
    {
        double height = 0;
        foreach (var (_, content) in QueueSource())
        {
            height = Math.Max(height, content.ExpandedSize.Height);
        }
        if (height <= 0) return IdleSize.Height;
        var work = WorkOf(ResolveDisplay());
        double offset = Math.Max(0, _settings.Get(_bottomAnchored ? BottomOffsetKey : TopOffsetKey, DefaultOffset));
        double available = (work.Bottom - work.Top) / Scale - 2 * Pad - offset;
        return QueueLayoutPolicy.CardHeight(height, available, ExpandedMainSize().Height,
            QueueSource().Count, QueueVisibleCards, QueueSpacing, QueueCardGap, QueuePagerSize);
    }

    /// <summary>
    /// 重排活动。`_live` 按原优先级降序（小岛常驻取第一张，行为与之前完全一致）；
    /// `_expandedOrder` 按「展开优先级」降序（未设置的插件回退到原优先级），决定展开主卡与队列。
    /// 两者都是稳定排序：数值相同则保持注册顺序。
    /// </summary>
    private void SortLive()
    {
        // 从同一份「注册顺序」快照各自稳定排序：并列时两张表都保持注册顺序
        var snapshot = _live.ToList();

        _live.Clear();
        _live.AddRange(snapshot.OrderByDescending(t => EffectivePriority(t.Owner, t.Content)));

        _expandedOrder.Clear();
        _expandedOrder.AddRange(snapshot.OrderByDescending(t => ExpandedPriority(t.Owner, t.Content)));
    }

    /// <summary>原优先级：宿主设置 <c>plugin.&lt;id&gt;.priority</c> 覆盖插件自己声明的 <c>Priority</c>（既有行为）。</summary>
    private double EffectivePriority(string owner, IslandLiveContent content)
        => _settings.Get<double?>($"plugin.{OwnerOfKey(owner)}.priority", null) ?? content.Priority;

    /// <summary>
    /// 展开优先级：宿主设置 <c>plugin.&lt;id&gt;.expandedPriority</c> 覆盖插件声明的
    /// <see cref="IslandLiveContent.ExpandedPriority"/>；两者都没有时回退到原优先级 ——
    /// 所以只要没人设置这个新值，展开顺序与旧行为逐字一致。
    /// 多实例内容共用同一份插件级覆盖（优先级是"这个插件有多重要"，不是"这条内容有多重要"）。
    /// </summary>
    private double ExpandedPriority(string owner, IslandLiveContent content)
        => _settings.Get<double?>($"plugin.{OwnerOfKey(owner)}.expandedPriority", null)
           ?? content.ExpandedPriority
           ?? EffectivePriority(owner, content);

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

        // 数量圆点两侧都预留：靠右模式在主岛左侧显示，开关/拖放不触发扩容。
        w += 2 * (ActivityCountSize + ActivityCountGap);
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

        bool wantExpand = _hover || _touchExpand;
        // 展开显示主卡、收起显示小岛常驻那张：此刻 _expanded 可能还是旧值，显式按意图挑。
        var live = wantExpand ? MainLive : CompactLive;

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
                CompactOutgoingMain(live);
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
                CompactOutgoingMain(live);
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
    /// 底板不能淡入或位移；只允许其中的内容做不越界的缩放与淡入。
    /// </summary>
    private void BuildQueue()
    {
        // 活动顺序可能刚变过（优先级调整）：先按各自记录的视图安全拆掉旧卡片，再重建
        TeardownQueue();

        // 展开时被换下的「小岛常驻」视图正要变成队列卡片：它此刻若还被 ContentLeaving 层攥着，
        // 挂进卡片就是"元素已在另一个父级下"（COMException）。队列马上要接管它的归属，先释放。
        ClearLeaving();

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
        surface.RestingHeight = height;
        surface.ApplyStyle(_style, _materialApplied, _light);
        surface.SetRadius(IslandStyle.ResolveQueueRadius(_style, height, _radiusScale));
        if (content.ExpandedSize.Height > height)
            surface.SetScrollableContent(inner, content.ExpandedSize.Height);
        else
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
                    _log.Error($"插件「{OwnerOfKey(owner)}」的点击回调抛异常（已忽略）", ex);
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
        AnimateMorphView(surface.Owner ?? "(未知)", surface.MorphView, expand: true,
            duration: TimeSpan.FromMilliseconds(1));
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
    private void PageQueueTail(int direction = 1)
    {
        if (_dropping || !_expanded) return;

        var queue = QueueSource();
        int q = queue.Count;
        if (q <= QueueVisibleCards) return;               // 没有隐藏项（按钮本不该在）

        double outgoingOpacity = _tailSurface?.Content.Opacity ?? 1;
        double outgoingScale = _tailSurface?.ContentScale.ScaleX ?? 1;
        FinishQueuePage();                                 // 收尾时保留当前内容的渲染姿态

        int hidden = q - (QueueVisibleCards - 1);
        int tail = (QueueVisibleCards - 1) + ((ResolveTailIndex(queue) - (QueueVisibleCards - 1) + direction + hidden) % hidden);
        var item = queue[tail];

        var outgoing = _tailSurface;
        int tailRow = outgoing != null
            ? Grid.GetRow(outgoing.Border)
            : (_bottomAnchored ? 1 : Math.Max(0, QueuePanel.RowDefinitions.Count - 2));

        var incoming = CreateQueueCard(item.Owner, item.Content, StackWidth(), QueueCardHeight());
        // Keep the old painted body underneath both content layers until the handoff finishes.
        // An opaque incoming body would hide the outgoing content before its fade could be seen.
        incoming.IsPageOverlay = outgoing != null;
        incoming.ApplyStyle(_style, _materialApplied, _light);
        incoming.Border.Opacity = 1;
        incoming.Content.Opacity = 0;
        incoming.ContentScale.ScaleX = incoming.ContentScale.ScaleY = PageEnterScale;
        Grid.SetRow(incoming.Border, tailRow);
        PlaceQueueCard(incoming);

        _pageOutgoing = outgoing;
        _tailSurface = incoming;
        // 记住这张卡：收起/刷新/重启之后翻页位都回到它（见 ResolveTailIndex、QueueTailKey）
        _tailIndex = tail;
        _tailOwner = item.Owner;
        _settings.Set(QueueTailKey, item.Owner);

        var sb = new Storyboard();
        if (outgoing != null)
        {
            AddFadeAnim(sb, outgoing.Content, outgoingOpacity, 0, PageOutDuration, TimeSpan.Zero);
            AddScaleAnims(sb, outgoing.ContentScale, outgoingScale, PageEnterScale, PageOutDuration, TimeSpan.Zero);
        }
        AddFadeAnim(sb, incoming.Content, 0, 1, PageInDuration, PageInDelay);
        AddScaleAnims(sb, incoming.ContentScale, PageEnterScale, 1, PageInDuration, PageInDelay);

        var version = ++_pageAnimVersion;
        sb.Completed += (_, _) =>
        {
            if (version != _pageAnimVersion) return;
            _pageAnim = null;
            incoming.IsPageOverlay = false;
            incoming.ApplyStyle(_style, _materialApplied, _light);
            DropPagedOutCard();
            incoming.Border.Opacity = 1;
            incoming.Content.Opacity = 1;
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
        if (_tailSurface != null)
        {
            _tailSurface.IsPageOverlay = false;
            _tailSurface.ApplyStyle(_style, _materialApplied, _light);
        }
        DropPagedOutCard();
        if (_tailSurface != null)
        {
            _tailSurface.Border.Opacity = 1;
            _tailSurface.Content.Opacity = 1;
            _tailSurface.ContentScale.ScaleX = _tailSurface.ContentScale.ScaleY = 1;
        }
    }

    /// <summary>把正在退场的旧卡从面板与账本里清掉（断开与插件视图的引用）。</summary>
    private void DropPagedOutCard()
    {
        var outgoing = _pageOutgoing;
        if (outgoing == null) return;
        AnimateMorphView(outgoing.Owner ?? "(未知)", outgoing.MorphView, expand: false);
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

    /// <summary>
    /// 插件视图的形态动画属于插件代码：必须在每个调用点守卫，
    /// 否则插件动画抛错会变成 WinUI 未处理异常（stowed exception）直接崩掉宿主。
    /// </summary>
    private void AnimateMorphView(string owner, IMorphView? view, bool expand, TimeSpan? duration = null)
    {
        if (view == null) return;
        try
        {
            if (expand) view.AnimateToExpanded(duration ?? ExpandDuration);
            else view.AnimateToCompact(duration ?? CollapseDuration);
        }
        catch (Exception ex)
        {
            _log.Error($"插件「{OwnerOfKey(owner)}」的形态动画执行失败（已忽略，宿主继续运行）", ex);
        }
    }

    /// <summary>
    /// 收起时把正在退场的「展开主卡」视图收回到紧凑态：小岛常驻（原优先级最高者）与展开主卡
    /// （展开优先级最高者）可能是两张不同的视图，主卡视图被换下后仍是展开态，插件内部的展开计时器/
    /// 辉光动画会一直跑。让插件自己的 morph 收回它（不是同一张视图才需要）。
    /// </summary>
    private void CompactOutgoingMain(IslandLiveContent? compact)
    {
        if (MainLive is not { MorphView: { } mainView }) return;
        if (compact?.MorphView != null && ReferenceEquals(compact.MorphView, mainView)) return;
        AnimateMorphView(MainOwner, mainView, expand: false);
    }

    /// <summary>展开岛体。返回是否真的展开了 —— 没有可展开内容时返回 false（调用方据此决定要不要当成一次普通点击）。</summary>
    private bool OnHoverEnter()
    {
        if (_dropping) return false;

        // 展开目标恒为展开主卡（此处 _expanded 尚为 false，ActiveLive 会取到小岛那张）
        var live = MainLive;
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
        // 输入会话期间不收起：用户可能正打着字，鼠标移开不能把输入框收走。
        // 会话结束（窗口失活）时由 OnInputSessionExited 补一次收起裁决。
        if (_input.IsActive) return;
        if (!_expanded) return;

        ClearTouchExpand();

        // 先摘掉展开态：TeardownQueue 与下面的内容选择据此取到「小岛常驻」那一张
        // （不是展开主卡），否则收起后会停在主卡的视图上。
        _expanded = false;

        // 安全拆卸队列（停止动画 + 断开 Border.Child + 把常驻 View 放回 ContentHost）。
        // 翻页位不动：下次展开还是用户翻到的那张卡
        TeardownQueue();

        // 展开期间消息挂成队列首卡：收起后它回到主岛，剩余时长继续
        if (_tempContent != null)
        {
            _tempCard = null;
            SwapContent(_tempContent, animate: true);
            AnimateIslandSize(_tempSize, TemporaryMorphDuration, isTemporary: true);
            ApplyTemporaryRadius(_tempSize.Height);
            return;
        }

        var live = CompactLive;
        if (live == null) return;

        // 展开主卡若是另一张视图，收回它的展开态（否则插件内部计时器/动画一直跑）
        CompactOutgoingMain(live);

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

    /// <summary>
    /// 输入会话结束（窗口失活，样式已在 InputSession 里还原）：把被压住的悬停收起裁决补回来。
    /// 隐藏/遮挡路径（_shown 已为 false）与投放会话不参与。
    /// </summary>
    private void OnInputSessionExited()
    {
        if (!_shown || _dropping || !_expanded) return;
        if (IsCursorOverIsland()) return;   // 鼠标还在岛上：交回正常的悬停逻辑
        _hover = false;
        _hoverGuard.Stop();
        OnHoverExit();
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

    /// <summary>
    /// 紧凑态滚轮切换：在活动列表里循环「小岛常驻」那一张（<c>island.compactPinned</c>）。
    /// 展开主卡与队列顺序仍然由优先级决定 —— 这里只是让用户在收起的小胶囊上也能翻看
    /// 其它在报事的插件，而不必先展开、再去翻队列。
    ///
    /// 只写一个纯界面设置（与 <c>island.queueTail</c> 同性质），不动插件状态：
    /// 被换下的插件继续报事，滚回去就恢复；重启后记住上次翻到的那张。
    /// </summary>
    private void CycleCompact(int direction)
    {
        if (_live.Count <= 1) return;

        // 先把「当前实际显示的那一张」定位到排序表里（可能是被钉住的那张，也可能是 _live[0]）
        int current = 0;
        if (_pinnedCompactOwner != null)
        {
            int found = _live.FindIndex(t => t.Owner == _pinnedCompactOwner);
            if (found >= 0) current = found;
        }

        int next = ((current + direction) % _live.Count + _live.Count) % _live.Count;
        _pinnedCompactOwner = _live[next].Owner;
        _settings.Set(CompactPinKey, _pinnedCompactOwner);
        TransitionToLiveState();
        UpdateVisibility();
    }

    /// <summary>
    /// 「小岛常驻」被用户用滚轮钉住的那张（插件 id）；null = 按优先级取 <c>_live[0]</c>（默认行为）。
    /// 钉住的那张仍然参与优先级排序与队列排布，只是占用了小胶囊的位置。
    /// </summary>
    private string? _pinnedCompactOwner;

    /// <summary>小岛常驻被钉住的那张活动的设置键（纯界面状态，与 island.queueTail 同性质）。</summary>
    private const string CompactPinKey = "island.compactPinned";

    /// <summary>
    /// 全局快捷键「展开 / 收起」：展开态收起，收起态展开。
    /// 展开走的是触控那条「常驻展开」路径 —— 没有鼠标悬停在上面，用悬停态展开会立刻被看门狗收回去。
    /// </summary>
    internal void ToggleExpandedFromKeyboard()
    {
        if (_dropping || _spotlightOccluded) return;

        if (_expanded)
        {
            ClearTouchExpand();
            _hover = false;
            _hoverGuard.Stop();
            _hoverIntent.Stop();
            OnHoverExit();
            return;
        }

        if (TouchExpand()) return;

        // 没有可展开的内容：至少把岛亮出来一次，让用户确认快捷键有效
        _settings.Set("island.visible", true);
        UpdateVisibility(force: true);
    }

    #endregion
}

