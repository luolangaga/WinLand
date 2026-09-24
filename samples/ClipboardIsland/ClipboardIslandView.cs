using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace ClipboardIsland;

/// <summary>
/// 岛上的视图：同一棵可视树同时承载「小岛（图标 + 最近一条摘要 + 条数）」和
/// 「大岛（最近 3 条历史）」，宿主切换形态时调用 AnimateToExpanded / AnimateToCompact。
///
/// 几条要点：
///   1. 所有元素（含只在展开态出现的）一开始就常驻可视树；
///   2. 隐藏用 Height = 0 + Opacity = 0，不要用 Visibility.Collapsed（无法过渡）；
///   3. 形态动画逐帧直接赋值，**不要用 Storyboard**（原因见 StartMorph 注释）；
///   4. 缓动与宿主一致：BackEase(EaseOut, Amplitude: 0.45)。
///
/// 「复制时闪一下」也是逐帧做的：一条 16ms 定时器跑闪光包络，
/// 只动一个小小的强调条和图标颜色，不碰布局，避免每帧触发重排。
/// （默认的复制提示是宿主那边的岛通知，这条闪光只在关掉通知开关时才会被用到。）
///
/// 展开态的那 3 行是**可点击**的：点一条就把这条重新复制并粘回用户当前输入的窗口。
/// 行自己吃掉 Tapped，所以点行不会连带把聚光卡弹出来；点岛体的其他位置才是开聚光卡。
/// </summary>
public sealed class ClipboardIslandView : UserControl, IMorphView
{
    /// <summary>点展开态某一行时的回调（复制 + 粘贴）。</summary>
    private readonly Action<ClipItem> _onPick;

    private const double CompactLeading = 24;
    private const double ExpandedLeading = 28;
    private const double CompactIconSize = 18;
    private const double ExpandedIconSize = 22;
    private const double CompactTitleSize = 13;
    private const double ExpandedTitleSize = 15;
    private const double ExpandedDetailHeight = 104;
    private const int ExpandedRows = 3;

    /// <summary>与宿主内置视图同一条 BackEase 曲线的幅度，手感保持一致。</summary>
    private const double BackAmplitude = 0.45;

    /// <summary>闪光包络：0 → 快速拉满 → 保持 → 淡出。</summary>
    private const double FlashAttack = 0.14;
    private const double FlashHoldUntil = 0.70;

    private readonly FontIcon _icon;
    private readonly Image _thumb;
    private readonly Border _thumbFrame;
    private readonly Grid _leading;
    private readonly Border _accentBar;
    private readonly TextBlock _headline;
    private readonly TextBlock _count;
    private readonly TextBlock _detailHint;
    private readonly StackPanel _detailRows;
    private readonly StackPanel _detail;

    private readonly SolidColorBrush _idleIconBrush;
    private readonly SolidColorBrush _accentIconBrush;
    private readonly IIslandTheme _theme;

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;

    /// <summary>当前形态进度：0 = 紧凑态，1 = 展开态。反转动画时从这里接着走。</summary>
    private double _progress;

    private readonly DispatcherQueueTimer? _flashTimer;
    private DateTimeOffset _flashStart;
    private TimeSpan _flashDuration = TimeSpan.FromSeconds(3);
    private double _flashLevel;
    private bool _flashTextOn;

    /// <summary>当前历史里最新的一条（也就是"没在闪的时候"小岛该显示的那条）。</summary>
    private ClipItem? _headItem;

    /// <summary>正在闪的那一条。默认就是 _headItem，但"试一下闪现效果"会用一条示例内容。</summary>
    private ClipItem? _flashItem;

    private string? _loadedThumbId;

    public ClipboardIslandView(PluginManifest manifest, Action<ClipItem> onPick, IIslandTheme theme)
    {
        _onPick = onPick;
        _theme = theme;

        var accent = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
        // 中性色跟着岛体主题走：Fluent + 浅色系统下岛体是浅底，写死白色就是白字压白底
        _idleIconBrush = new SolidColorBrush(Neutral(theme, 235));
        _accentIconBrush = new SolidColorBrush(accent);
        var neutralFill = new SolidColorBrush(Neutral(theme, 34));

        _accentBar = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(accent),
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _icon = new FontIcon
        {
            Glyph = manifest.IconGlyph ?? "\uE77F",
            FontSize = CompactIconSize,
            Foreground = _idleIconBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _thumb = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
        _thumbFrame = new Border
        {
            CornerRadius = new CornerRadius(5),
            Opacity = 0,
            Background = neutralFill,
            Child = _thumb,
        };

        _leading = new Grid
        {
            Width = CompactLeading,
            Height = CompactLeading,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _leading.Children.Add(_icon);
        _leading.Children.Add(_thumbFrame);

        _headline = new TextBlock
        {
            Text = "剪贴板已就绪",
            FontSize = CompactTitleSize,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Neutral(theme, 255)),
        };

        _count = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Neutral(theme, 140)),
        };

        _detailHint = new TextBlock
        {
            FontSize = 11,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Neutral(theme, 130)),
        };

        _detailRows = new StackPanel { Spacing = 0 };

        // 展开态才显示的内容：Height/Opacity 归零藏起来（元素仍常驻可视树）
        _detail = new StackPanel
        {
            Height = 0,
            Opacity = 0,
            Spacing = 4,
            Padding = new Thickness(0, 6, 0, 0),
            Children = { _detailHint, _detailRows },
        };

        var root = new Grid
        {
            Padding = new Thickness(16, 0, 16, 0),
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(_accentBar);

        Grid.SetColumn(_leading, 1);
        root.Children.Add(_leading);

        Grid.SetColumn(_headline, 2);
        root.Children.Add(_headline);

        Grid.SetColumn(_count, 3);
        root.Children.Add(_count);

        Grid.SetRow(_detail, 1);
        Grid.SetColumn(_detail, 0);
        Grid.SetColumnSpan(_detail, 4);
        root.Children.Add(_detail);

        Content = root;

        // 逐帧动画用的 UI 线程定时器；拿不到就退化成「直接切终态」，绝不留下中间值
        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        _flashTimer = DispatcherQueue?.CreateTimer();
        if (_flashTimer is not null)
        {
            _flashTimer.Interval = TimeSpan.FromMilliseconds(16);
            _flashTimer.IsRepeating = true;
            _flashTimer.Tick += (_, _) => OnFlashTick();
        }

        Unloaded += (_, _) =>
        {
            _morphTimer?.Stop();
            _flashTimer?.Stop();
        };
    }

    public UIElement View => this;

    /// <summary>把当前历史铺到界面上（UI 线程调用）。</summary>
    public void Apply(IReadOnlyList<ClipItem> items)
    {
        _headItem = items.Count > 0 ? items[0] : null;
        _count.Text = items.Count > 0 ? items.Count.ToString() : string.Empty;

        _detailRows.Children.Clear();
        var shown = Math.Min(ExpandedRows, items.Count);
        for (var i = 0; i < shown; i++)
        {
            _detailRows.Children.Add(new ClipRow(items[i], _theme, interactive: true, invoke: _onPick, dense: true));
        }

        _detailHint.Text = items.Count switch
        {
            0 => "还没有记录 · 复制点什么试试",
            _ when items.Count > ExpandedRows => $"最近 {shown} 条 · 点一条粘贴 · 点空白看全部 {items.Count} 条",
            _ => $"共 {items.Count} 条 · 点一条粘贴 · 点空白看全部",
        };

        ApplyHead(_headItem);
        UpdateHeadline();
    }

    /// <summary>闪光时文案的前缀：复制说「已复制」，点历史粘贴说「已粘贴」。</summary>
    private string _flashLabel = "已复制";

    /// <summary>复制到新内容时闪一下：强调条亮起 + 图标变色 + 文案加「已复制 · 」前缀，几秒后自动复原。</summary>
    public void ShowFlash(ClipItem item, TimeSpan duration, string? label = null)
    {
        _flashLabel = string.IsNullOrWhiteSpace(label) ? "已复制" : label;
        _flashItem = item;
        ApplyHead(item);

        if (_flashTimer is null)
        {
            // 拿不到 UI 定时器就干脆不闪，避免留下"永远亮着"的中间态
            EndFlash();
            return;
        }

        _flashDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(2);
        _flashStart = DateTimeOffset.UtcNow;

        ApplyFlash(1);
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    /// <summary>结束闪光：文案和缩略图都退回"最新一条记录"的状态。</summary>
    private void EndFlash()
    {
        _flashItem = null;
        ApplyFlash(0);
        ApplyHead(_headItem);
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    private void ApplyHead(ClipItem? head)
    {
        if (head is null || !head.IsImage || string.IsNullOrEmpty(head.ImagePath))
        {
            _thumb.Source = null;
            _thumbFrame.Opacity = 0;
            _icon.Opacity = 1;
            _loadedThumbId = null;
            return;
        }

        if (string.Equals(_loadedThumbId, head.Id, StringComparison.Ordinal)) return;

        _loadedThumbId = head.Id;
        _ = LoadHeadThumbnailAsync(head);
    }

    private async Task LoadHeadThumbnailAsync(ClipItem item)
    {
        try
        {
            var image = await ThumbnailLoader.LoadAsync(item.ImagePath, 72);
            if (image is null) return;

            // 加载期间最新一条可能又变了，对不上就丢掉
            if (!string.Equals(_headItem?.Id, item.Id, StringComparison.Ordinal)) return;

            _thumb.Source = image;
            _thumbFrame.Opacity = 1;
            _icon.Opacity = 0;
        }
        catch
        {
            // 缩略图失败不影响主流程，继续显示类型图标
        }
    }

    private void UpdateHeadline()
    {
        var summary = _flashTextOn && _flashItem is not null
            ? _flashItem.Summary
            : _headItem?.Summary ?? "剪贴板已就绪";

        _headline.Text = _flashTextOn ? _flashLabel + " · " + summary : summary;
    }

    /// <summary>
    /// 形态动画走「逐帧属性赋值」，刻意不用 Storyboard。
    ///
    /// 原因：动态加载的插件程序集里，属性路径动画（Storyboard.SetTargetProperty 的 "Height"）
    /// 解析不出类型信息，会在动画 tick 上抛
    /// COMException (0x800F1001): Invalid attribute value Unknown for property Height。
    /// 这个异常发生在 tick 里，调用点的 try/catch 拦不住，会冒到宿主的未处理异常处理器，
    /// 结果是岛体尺寸收回去了、插件的 Height 却卡在中间值，整块内容错位且不再响应 hover。
    /// 宿主内置视图能安全使用 Storyboard，是因为它们在宿主程序集里；插件侧必须逐帧赋值。
    /// </summary>
    private void StartMorph(double target, TimeSpan duration)
    {
        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(target);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);

        ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

        if (t >= 1)
        {
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);   // 收尾时锁定终态，避免残留中间值
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)³ + A(t-1)²，与 XAML 那条曲线等价。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private void ApplyMorph(double progress)
    {
        _progress = progress;

        // Height 不能为负（BackEase 在终点附近会把曲线推到目标值以下）
        _detail.Height = Math.Max(0, ExpandedDetailHeight * progress);
        _detail.Opacity = Math.Clamp(progress, 0, 1);

        var leading = CompactLeading + (ExpandedLeading - CompactLeading) * progress;
        _leading.Width = Math.Max(0, leading);
        _leading.Height = Math.Max(0, leading);

        _icon.FontSize = Math.Max(1, CompactIconSize + (ExpandedIconSize - CompactIconSize) * progress);
        _headline.FontSize = Math.Max(1, CompactTitleSize + (ExpandedTitleSize - CompactTitleSize) * progress);
    }

    private void OnFlashTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _flashStart).TotalMilliseconds;
        var total = Math.Max(1, _flashDuration.TotalMilliseconds);
        var t = elapsed / total;

        if (t >= 1)
        {
            _flashTimer?.Stop();
            EndFlash();
            return;
        }

        ApplyFlash(FlashEnvelope(t));
    }

    /// <summary>0 → 1 → 保持 → 0 的闪光包络。</summary>
    private static double FlashEnvelope(double t)
    {
        if (t < FlashAttack) return t / FlashAttack;
        if (t < FlashHoldUntil) return 1;
        return Math.Clamp((1 - t) / (1 - FlashHoldUntil), 0, 1);
    }

    /// <summary>把 0~1 的闪光强度铺到强调条和图标上。只动这两个属性，不触发重排。</summary>
    private void ApplyFlash(double level)
    {
        _flashLevel = Math.Clamp(level, 0, 1);

        _accentBar.Opacity = 0.15 + 0.85 * _flashLevel;
        _accentBar.Height = 16 + 8 * _flashLevel;
        _icon.Foreground = _flashLevel > 0.5 ? _accentIconBrush : _idleIconBrush;

        var wantText = _flashLevel > 0.15 && _flashItem is not null;
        if (wantText == _flashTextOn) return;

        _flashTextOn = wantText;
        _headline.FontWeight = wantText
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
        UpdateHeadline();
    }

    /// <summary>
    /// 岛体换主题时就地重刷所有中性色。代码搭的视图颜色是烘在画刷里的，
    /// 不重刷就会留在旧主题（浅色时建的深色文字会原封不动留在深色岛上）。
    /// </summary>
    internal void RefreshTheme()
    {
        _idleIconBrush.Color = Neutral(_theme, 235);
        _thumbFrame.Background = new SolidColorBrush(Neutral(_theme, 34));
        _headline.Foreground = new SolidColorBrush(Neutral(_theme, 255));
        _count.Foreground = new SolidColorBrush(Neutral(_theme, 140));
        _detailHint.Foreground = new SolidColorBrush(Neutral(_theme, 130));

        foreach (var child in _detailRows.Children)
        {
            if (child is ClipRow row) row.RefreshTheme();
        }
    }

    /// <summary>
    /// 中性色的唯一来源：岛体深色时是白色系，浅色（Fluent + 浅色系统）时是黑色系。
    /// 只写死白色的话，浅色岛体上就是白字压白底。
    /// </summary>
    private static Windows.UI.Color Neutral(IIslandTheme theme, byte alpha) => theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);
}
