using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace DeviceIsland;

/// <summary>
/// 岛上的视图：同一棵可视树承载「小岛（图标 + 一句话 + 台数）」和「大岛（逐台列出，带按钮）」。
///
/// 几条要点：
///   1. 所有元素（含只在展开态出现的）一开始就常驻可视树；
///   2. 隐藏用 Height = 0 + Opacity = 0，不要用 Visibility.Collapsed（无法过渡）；
///   3. 形态动画逐帧直接赋值，**不要用 Storyboard**（原因见 StartMorph 注释）；
///   4. 缓动与宿主一致：BackEase(EaseOut, Amplitude: 0.45)。
///
/// 「刚插上」的强调也是逐帧做的：一条 16ms 定时器跑一个包络，
/// 只动左侧强调条和图标颜色，不碰布局，避免每帧触发重排。
/// </summary>
public sealed class DeviceIslandView : UserControl, IMorphView
{
    /// <summary>点某一行「打开」「弹出」时的回调。</summary>
    private readonly Action<DeviceInfo> _onOpen;
    private readonly Action<DeviceInfo> _onEject;

    private const double CompactLeading = 24;
    private const double ExpandedLeading = 28;
    private const double CompactIconSize = 18;
    private const double ExpandedIconSize = 22;
    private const double CompactTitleSize = 13;
    private const double ExpandedTitleSize = 15;
    private const double ExpandedDetailHeight = 118;
    private const int ExpandedRows = 3;

    /// <summary>与宿主内置视图同一条 BackEase 曲线的幅度，手感保持一致。</summary>
    private const double BackAmplitude = 0.45;

    /// <summary>强调包络：0 → 快速拉满 → 保持 → 淡出。</summary>
    private const double FlashAttack = 0.14;
    private const double FlashHoldUntil = 0.70;

    private readonly FontIcon _icon;
    private readonly TextBlock _headline;
    private readonly TextBlock _count;
    private readonly TextBlock _detailHint;
    private readonly StackPanel _detailRows;
    private readonly StackPanel _detail;
    private readonly Border _accentBar;
    private readonly Grid _leading;

    private readonly SolidColorBrush _idleIconBrush;
    private readonly SolidColorBrush _accentIconBrush;

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;                        // 0 = 紧凑，1 = 展开

    private readonly DispatcherQueueTimer? _flashTimer;
    private DateTimeOffset _flashStart;
    private TimeSpan _flashDuration = TimeSpan.FromSeconds(3);
    private readonly IIslandTheme _theme;

    internal DeviceIslandView(PluginManifest manifest, Action<DeviceInfo> onOpen, Action<DeviceInfo> onEject, IIslandTheme theme)
    {
        _onOpen = onOpen;
        _onEject = onEject;
        _theme = theme;

        var accent = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
        // 中性色跟着岛体主题走：浅色岛体上写死白色就是白字压白底
        _idleIconBrush = Hint(235);
        _accentIconBrush = new SolidColorBrush(accent);

        _accentBar = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(accent),
            Opacity = 0.18,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _icon = new FontIcon
        {
            Glyph = manifest.IconGlyph ?? "\uE88E",
            FontSize = CompactIconSize,
            Foreground = _idleIconBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _leading = new Grid
        {
            Width = CompactLeading,
            Height = CompactLeading,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _leading.Children.Add(_icon);

        _headline = new TextBlock
        {
            Text = "设备已就绪",
            FontSize = CompactTitleSize,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hint(255),
        };

        _count = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hint(140),
        };

        _detailHint = new TextBlock
        {
            FontSize = 11,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Hint(130),
        };

        _detailRows = new StackPanel { Spacing = 1 };

        // 展开态才显示的内容：Height/Opacity 归零藏起来（元素仍常驻可视树）
        _detail = new StackPanel
        {
            Height = 0,
            Opacity = 0,
            Spacing = 4,
            Padding = new Thickness(0, 4, 0, 0),
            Children = { _detailRows, _detailHint },
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

    /// <summary>
    /// 把当前设备铺到界面上（UI 线程调用）。
    /// <paramref name="eventText"/> 非空表示刚刚发生了插拔（摘要行显示这件事并闪一下），
    /// 为空则显示常规的「已连接 N 台设备」。
    /// </summary>
    internal void Apply(IReadOnlyList<DeviceInfo> devices, string? eventText)
    {
        _count.Text = devices.Count > 0 ? devices.Count.ToString() : string.Empty;

        _detailRows.Children.Clear();
        var shown = Math.Min(ExpandedRows, devices.Count);
        for (var i = 0; i < shown; i++)
        {
            _detailRows.Children.Add(new DeviceRow(devices[i], _theme, _onOpen, _onEject, dense: true));
        }

        _detailHint.Text = devices.Count switch
        {
            0 => "暂无可移动设备 · 插一个试试",
            _ when devices.Count > ExpandedRows => $"共 {devices.Count} 台 · 点岛体看全部",
            _ => "点岛体看全部 · 点条目上的按钮执行动作",
        };

        _headline.Text = eventText
            ?? devices.Count switch
            {
                0 => "暂无外接设备",
                1 => devices[0].Name,
                _ => $"已连接 {devices.Count} 台设备",
            };

        if (eventText is not null) StartFlash();
    }

    /// <summary>闪一下强调条（刚插上 / 刚拔掉）。</summary>
    private void StartFlash()
    {
        if (_flashTimer is null)
        {
            EndFlash();
            return;
        }

        _flashStart = DateTimeOffset.UtcNow;
        ApplyFlash(1);
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void EndFlash() => ApplyFlash(0);

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

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

    /// <summary>0 → 1 → 保持 → 0 的包络。</summary>
    private static double FlashEnvelope(double t)
    {
        if (t < FlashAttack) return t / FlashAttack;
        if (t < FlashHoldUntil) return 1;
        return Math.Clamp((1 - t) / (1 - FlashHoldUntil), 0, 1);
    }

    /// <summary>把 0~1 的强度铺到强调条和图标上。只动这两个属性，不触发重排。</summary>
    private void ApplyFlash(double level)
    {
        var clamped = Math.Clamp(level, 0, 1);

        _accentBar.Opacity = 0.18 + 0.82 * clamped;
        _accentBar.Height = 16 + 8 * clamped;
        _icon.Foreground = clamped > 0.5 ? _accentIconBrush : _idleIconBrush;
    }

    /// <summary>
    /// 岛体换主题时就地重刷所有中性色。代码搭的视图颜色是烘在画刷里的，
    /// 不重刷就会留在旧主题（浅色时建的深色文字会原封不动留在深色岛上）。
    /// </summary>
    internal void RefreshTheme()
    {
        _idleIconBrush.Color = Neutral(235);
        _headline.Foreground = Hint(255);
        _count.Foreground = Hint(140);
        _detailHint.Foreground = Hint(130);

        foreach (var child in _detailRows.Children)
        {
            if (child is DeviceRow row) row.RefreshTheme();
        }
    }

    /// <summary>中性色：岛体深色时是白色系，浅色（Fluent + 浅色系统）时是黑色系。</summary>
    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    private SolidColorBrush Hint(byte alpha) => new(Neutral(alpha));
}
