using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace WeatherIsland;

/// <summary>
/// 岛上的视图：同一棵可视树同时承载「小岛（图标 + 当前温度）」和
/// 「大岛（地点、更新时间 + 今天/明天/后天三天预报）」。
///
/// 四条要点：
///   1. 所有元素一开始就常驻可视树，展开态的内容靠 Height=0 + Opacity=0 藏起来；
///   2. 不用 Visibility.Collapsed（无法过渡）；
///   3. **形态动画必须逐帧直接赋值，不能用 Storyboard**（见 <see cref="StartMorph"/> 的注释）；
///   4. 缓动公式与宿主内置视图一致：BackEase(EaseOut, Amplitude: 0.45)。
/// </summary>
public sealed class WeatherIslandView : UserControl, IMorphView
{
    private const double CompactIconSize = 18;
    private const double ExpandedIconSize = 24;
    private const double CompactTempSize = 20;
    private const double ExpandedTempSize = 26;
    private const double ExpandedDetailHeight = 88;
    private const int ForecastDays = 3;

    /// <summary>与宿主内置视图同一条 BackEase 曲线的幅度，手感保持一致。</summary>
    private const double BackAmplitude = 0.45;

    private static readonly string[] DefaultLabels = { "今天", "明天", "后天" };

    private readonly Viewbox _icon;
    private readonly TextBlock _temperature;
    private readonly TextBlock _condition;
    private readonly StackPanel _detail;
    private readonly TextBlock _place;
    private readonly ForecastCell[] _cells = new ForecastCell[ForecastDays];

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;

    /// <summary>当前形态进度：0 = 紧凑态，1 = 展开态。反转动画时从这里接着走。</summary>
    private double _progress;

    public WeatherIslandView()
    {
        _icon = new Viewbox
        {
            Width = CompactIconSize,
            Height = CompactIconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Child = WeatherIcon.Create(0),
        };

        _temperature = new TextBlock
        {
            Text = "--°",
            FontSize = CompactTempSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _condition = new TextBlock
        {
            Text = "正在获取天气…",
            FontSize = 12.5,
            Foreground = Hint(180),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(_icon);
        Grid.SetColumn(_temperature, 1);
        header.Children.Add(_temperature);
        Grid.SetColumn(_condition, 2);
        header.Children.Add(_condition);

        _place = new TextBlock
        {
            Text = "--",
            FontSize = 11,
            Foreground = Hint(150),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        _detail = new StackPanel
        {
            Height = 0,
            Opacity = 0,
            Spacing = 6,
            Padding = new Thickness(0, 8, 0, 0),
            Children =
            {
                _place,
                BuildForecastGrid(),
            },
        };

        Content = new StackPanel
        {
            Padding = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { header, _detail },
        };

        // 逐帧动画用的 UI 线程定时器；拿不到就退化成「直接切终态」，绝不留下中间态
        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    /// <summary>把一次天气快照铺到界面上（UI 线程调用）。</summary>
    public void Apply(WeatherSnapshot snapshot)
    {
        var hasData = snapshot.HasData;
        var error = snapshot.Error ?? "暂无数据";

        _temperature.Text = hasData ? $"{snapshot.Temperature:0.#}°" : "--°";

        // 小岛只有一行位置，长错误信息会挤爆布局：短的照原样显示，长的收成一句，详情放到展开态
        _condition.Text = hasData
            ? WeatherCodes.Describe(snapshot.Code)
            : error.Length > 8 ? "获取失败" : error;

        if (hasData)
        {
            _icon.Child = WeatherIcon.Create(snapshot.Code);
            _place.Text = $"{snapshot.Place} · {snapshot.UpdatedAt:HH:mm} 更新";
        }
        else
        {
            _place.Text = error;
        }

        for (var i = 0; i < _cells.Length; i++)
        {
            var cell = _cells[i];
            var day = hasData && i < snapshot.Days.Count ? snapshot.Days[i] : null;
            if (day is null)
            {
                cell.Label.Text = DefaultLabels[i];
                cell.Icon.Child = WeatherIcon.Create(-1);
                cell.High.Text = "--";
                cell.Low.Text = "/--";
                continue;
            }

            cell.Label.Text = day.Label;
            cell.Icon.Child = WeatherIcon.Create(day.Code);
            cell.High.Text = $"{day.Max:0}°";
            cell.Low.Text = $"/{day.Min:0}°";
        }
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    /// <summary>
    /// 形态动画走「逐帧属性赋值」，刻意不用 Storyboard。
    ///
    /// 原因：动态加载的插件程序集里，属性路径动画（Storyboard.SetTargetProperty 的 "Height"）
    /// 解析不出类型信息，会在动画 tick 上抛
    /// <c>COMException (0x800F1001) Invalid attribute value Unknown for property Height</c>。
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

    /// <summary>把 0~1 的形态进度铺到各元素上。progress 会因 BackEase 略微越过 0/1。</summary>
    private void ApplyMorph(double progress)
    {
        _progress = progress;

        // Height 不能为负（BackEase 在终点附近会略微下降到目标值以下）
        _detail.Height = Math.Max(0, ExpandedDetailHeight * progress);
        _detail.Opacity = Math.Clamp(progress, 0, 1);

        var iconSize = CompactIconSize + (ExpandedIconSize - CompactIconSize) * progress;
        _icon.Width = iconSize;
        _icon.Height = iconSize;

        _temperature.FontSize = CompactTempSize + (ExpandedTempSize - CompactTempSize) * progress;
    }

    private UIElement BuildForecastGrid()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < ForecastDays; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new ForecastCell();
            _cells[i] = cell;
            Grid.SetColumn(cell.Root, i);
            grid.Children.Add(cell.Root);
        }
        return grid;
    }

    private static SolidColorBrush Hint(byte alpha)
        => new(Windows.UI.Color.FromArgb(alpha, 255, 255, 255));

    /// <summary>一天一列：标签 / 图标 / 最高最低温。</summary>
    private sealed class ForecastCell
    {
        public StackPanel Root { get; } = new()
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        public Viewbox Icon { get; } = new()
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = WeatherIcon.Create(-1),
        };

        public TextBlock Label { get; } = new()
        {
            Text = "—",
            FontSize = 11,
            Foreground = Hint(150),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        public TextBlock High { get; } = new()
        {
            Text = "--",
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };

        public TextBlock Low { get; } = new()
        {
            Text = "/--",
            FontSize = 12.5,
            Foreground = Hint(140),
        };

        public ForecastCell()
        {
            Root.Children.Add(Label);
            Root.Children.Add(Icon);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            row.Children.Add(High);
            row.Children.Add(Low);
            Root.Children.Add(row);
        }
    }
}
