using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace WeatherIsland;

/// <summary>
/// 「超级展开」聚光卡：点岛体后宿主从岛体位置带倾角飞入、居中放大的一张天气大卡片。
///
/// 三条要点：
///   1. 必须是**独立于岛视图**的另一棵可视树（每个窗口一棵树），实例由插件缓存复用；
///   2. 不需要实现 IMorphView：飞入飞回、遮罩、圆角、层级全由宿主负责，这里只管最终形态；
///   3. 配色跟着岛体主题走（聚光卡跟着岛体明暗），中性色做成共享画刷、主题一变只改 Color。
///
/// 卡片内容（信息量按「岛里塞不下」原则塞满）：
///   实况大字 + 地点/更新时间/日出日落 → 8 项实况指标 → 未来 24 小时逐时 → 未来 7 天温区条。
/// </summary>
public sealed class WeatherSpotlightView : UserControl
{
    /// <summary>插件请求的期望尺寸（宿主会夹到显示器工作区内）。</summary>
    public static readonly Windows.Foundation.Size PreferredSize = new(840, 700);

    private static readonly Windows.UI.Color Accent = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    private static readonly Windows.UI.Color Warm = Windows.UI.Color.FromArgb(255, 0xFF, 0xB4, 0x54);

    private const int TileCount = 8;

    private readonly IIslandTheme _theme;
    private readonly Func<Task> _onRefresh;

    /// <summary>中性色共享画刷：主题一变只改它们的 Color，所有用到的地方当场跟着变。</summary>
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _tileBrush = new();
    private readonly SolidColorBrush _dividerBrush = new();
    private readonly SolidColorBrush _trackBrush = new();

    /// <summary>强调色在两套主题下都够亮，不随主题变。</summary>
    private readonly SolidColorBrush _accentBrush = new(Accent);

    private readonly Viewbox _heroIcon;
    private readonly TextBlock _heroTemp;
    private readonly TextBlock _heroCondition;
    private readonly TextBlock _place;
    private readonly TextBlock _updated;
    private readonly TextBlock _sunLine;
    private readonly Button _refreshButton;

    private readonly TextBlock[] _tileValues = new TextBlock[TileCount];
    private readonly TextBlock[] _tileNotes = new TextBlock[TileCount];

    private readonly StackPanel _hoursPanel;
    private readonly StackPanel _daysPanel;
    private readonly TextBlock _hoursEmpty;
    private readonly TextBlock _daysEmpty;

    public WeatherSpotlightView(IIslandTheme theme, Func<Task> onRefresh)
    {
        _theme = theme;
        _onRefresh = onRefresh;
        ApplyThemeColors();

        // 视图与主题同生命周期（插件持有本实例）：构造时订阅，插件停用时由 Detach 退订
        _theme.Changed += ApplyThemeColors;

        // ---------------- 头部：大图标 + 大字温度 + 地点 / 更新时间 / 日出日落 ----------------
        _heroIcon = new Viewbox
        {
            Width = 64,
            Height = 64,
            VerticalAlignment = VerticalAlignment.Center,
            Child = WeatherIcon.Create(0),
        };

        _heroTemp = new TextBlock
        {
            Text = "--°",
            FontSize = 46,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        _heroCondition = new TextBlock
        {
            Text = "正在获取天气…",
            FontSize = 15,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var heroLeftCenter = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        heroLeftCenter.Children.Add(_heroTemp);
        heroLeftCenter.Children.Add(_heroCondition);

        _place = new TextBlock
        {
            Text = "--",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        _updated = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = _faintBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        _sunLine = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = _faintBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        _refreshButton = new Button
        {
            Content = "立即刷新",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _refreshButton.Click += OnRefreshClick;

        var metaPanel = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        metaPanel.Children.Add(_place);
        metaPanel.Children.Add(_updated);
        metaPanel.Children.Add(_sunLine);
        metaPanel.Children.Add(_refreshButton);

        var hero = new Grid { ColumnSpacing = 16 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.Children.Add(_heroIcon);
        Grid.SetColumn(heroLeftCenter, 1);
        hero.Children.Add(heroLeftCenter);
        Grid.SetColumn(metaPanel, 2);
        hero.Children.Add(metaPanel);

        // ---------------- 8 项实况指标 ----------------
        var tiles = BuildTiles();

        // ---------------- 未来 24 小时 ----------------
        _hoursPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
        };

        _hoursEmpty = new TextBlock
        {
            Text = "还没有逐小时数据",
            FontSize = 12.5,
            Foreground = _faintBrush,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(2, 8, 0, 8),
        };

        var hoursScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Height = 104,
            Content = _hoursPanel,
        };

        // ---------------- 未来 7 天 ----------------
        _daysPanel = new StackPanel { Spacing = 2 };

        _daysEmpty = new TextBlock
        {
            Text = "还没有多日预报",
            FontSize = 12.5,
            Foreground = _faintBrush,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(2, 8, 0, 8),
        };

        var daysScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = _daysPanel,
        };

        // ---------------- 组装 ----------------
        var root = new Grid
        {
            Padding = new Thickness(26, 22, 26, 22),
            RowSpacing = 11,
        };
        for (var i = 0; i < 8; i++)
        {
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = i == 6 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }

        root.Children.Add(hero);

        var divider1 = Divider();
        Grid.SetRow(divider1, 1);
        root.Children.Add(divider1);

        Grid.SetRow(tiles, 2);
        root.Children.Add(tiles);

        var hoursHeader = SectionHeader("未来 24 小时");
        Grid.SetRow(hoursHeader, 3);
        root.Children.Add(hoursHeader);

        Grid.SetRow(hoursScroll, 4);
        root.Children.Add(hoursScroll);

        var daysHeader = SectionHeader("未来 7 天预报");
        Grid.SetRow(daysHeader, 5);
        root.Children.Add(daysHeader);

        Grid.SetRow(daysScroll, 6);
        root.Children.Add(daysScroll);

        Grid.SetRow(_hoursEmpty, 4);
        root.Children.Add(_hoursEmpty);
        Grid.SetRow(_daysEmpty, 6);
        root.Children.Add(_daysEmpty);

        Content = root;
    }

    /// <summary>把一次天气快照铺到卡片上（UI 线程调用）。</summary>
    public void Apply(WeatherSnapshot snapshot)
    {
        if (!snapshot.HasData)
        {
            ApplyEmpty(snapshot.Error ?? "暂无数据");
            return;
        }

        _heroIcon.Child = WeatherIcon.Create(snapshot.Code, snapshot.IsDay);
        _heroTemp.Text = $"{snapshot.Temperature:0.#}°";
        _heroCondition.Text = WeatherCodes.Describe(snapshot.Code)
                              + (snapshot.IsDay ? "" : " · 夜间");

        _place.Text = snapshot.Place;
        _updated.Text = $"{snapshot.UpdatedAt:HH:mm} 更新 · 今日 {snapshot.TodayMin:0}° ~ {snapshot.TodayMax:0}°";
        _sunLine.Text = snapshot.Sunrise.Length > 0 || snapshot.Sunset.Length > 0
            ? $"日出 {Or(snapshot.Sunrise)} · 日落 {Or(snapshot.Sunset)}"
              + (snapshot.Daylight.Length > 0 ? $" · 白昼 {snapshot.Daylight}" : "")
            : "";

        ApplyTiles(snapshot);
        ApplyHours(snapshot);
        ApplyDays(snapshot);
    }

    /// <summary>插件停用时退订主题事件（视图与主题同生命周期，不退订会留下悬空引用）。</summary>
    public void Detach() => _theme.Changed -= ApplyThemeColors;

    /// <summary>宿主收起卡片时回调（点卡片外 / Esc / 被别的插件替换 / 插件停用）。</summary>
    public void OnHostClosed()
    {
        _refreshButton.IsEnabled = true;
        _refreshButton.Content = "立即刷新";
    }

    // ---------------------------------------------------------------- 分块铺数据

    private void ApplyEmpty(string reason)
    {
        _heroIcon.Child = WeatherIcon.Create(-1);
        _heroTemp.Text = "--°";
        _heroCondition.Text = reason;
        _place.Text = "--";
        _updated.Text = "";
        _sunLine.Text = "";

        for (var i = 0; i < TileCount; i++)
        {
            _tileValues[i].Text = "--";
            _tileNotes[i].Text = "";
        }

        _hoursPanel.Children.Clear();
        _daysPanel.Children.Clear();
        _hoursEmpty.Visibility = Visibility.Visible;
        _daysEmpty.Visibility = Visibility.Visible;
    }

    private void ApplyTiles(WeatherSnapshot s)
    {
        _tileValues[0].Text = $"{s.ApparentTemperature:0.#}°";
        _tileNotes[0].Text = $"今日 {s.TodayApparentMin:0}° ~ {s.TodayApparentMax:0}°";

        _tileValues[1].Text = $"{s.Humidity:0}%";
        _tileNotes[1].Text = $"露点 {s.DewPoint:0.#}°";

        _tileValues[2].Text = $"{WeatherCodes.Compass(s.WindDirection)} {s.WindSpeed:0.#} m/s";
        _tileNotes[2].Text = $"阵风 {s.WindGust:0.#} m/s";

        _tileValues[3].Text = s.PrecipProbability > 0 ? $"{s.PrecipProbability}%" : "无";
        _tileNotes[3].Text = s.Precipitation > 0
            ? $"正在下 {s.Precipitation:0.#} mm · 今日累计 {s.PrecipSum:0.#} mm"
            : $"今日累计 {s.PrecipSum:0.#} mm";

        _tileValues[4].Text = $"{s.UvIndexMax:0.#}（{WeatherCodes.UvLabel(s.UvIndexMax)}）";
        _tileNotes[4].Text = UvAdvice(s.UvIndexMax);

        _tileValues[5].Text = $"{s.CloudCover:0}%";
        _tileNotes[5].Text = $"能见度 {s.Visibility:0.#} km · {WeatherCodes.VisibilityLabel(s.Visibility)}";

        _tileValues[6].Text = $"{s.Pressure:0} hPa";
        _tileNotes[6].Text = "地面气压";

        if (s.Air is { } air)
        {
            _tileValues[7].Text = $"{air.Pm25:0}（{WeatherCodes.Pm25Label(air.Pm25)}）";
            _tileNotes[7].Text = $"PM2.5 μg/m³ · PM10 {air.Pm10:0} · AQI {air.UsAqi:0}";
        }
        else
        {
            _tileValues[7].Text = "--";
            _tileNotes[7].Text = "未取到空气质量数据";
        }
    }

    private void ApplyHours(WeatherSnapshot s)
    {
        _hoursPanel.Children.Clear();

        if (s.Hours.Count == 0)
        {
            _hoursEmpty.Visibility = Visibility.Visible;
            return;
        }

        _hoursEmpty.Visibility = Visibility.Collapsed;

        foreach (var hour in s.Hours)
        {
            _hoursPanel.Children.Add(BuildHourCell(hour));
        }
    }

    private void ApplyDays(WeatherSnapshot s)
    {
        _daysPanel.Children.Clear();

        if (s.Days.Count == 0)
        {
            _daysEmpty.Visibility = Visibility.Visible;
            return;
        }

        _daysEmpty.Visibility = Visibility.Collapsed;

        // 温度条要跨全部天数共用一把尺子，先求总区间
        var globalMin = s.Days.Min(d => d.Min);
        var globalMax = s.Days.Max(d => d.Max);

        foreach (var day in s.Days)
        {
            _daysPanel.Children.Add(BuildDayRow(day, globalMin, globalMax));
        }
    }

    // ---------------------------------------------------------------- 分块构建

    private FrameworkElement BuildTiles()
    {
        var captions = new[]
        {
            "体感温度", "相对湿度", "风", "降水",
            "紫外线", "云量与能见度", "气压", "空气质量",
        };

        var grid = new Grid { ColumnSpacing = 9, RowSpacing = 9 };
        for (var c = 0; c < 4; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var r = 0; r < 2; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var i = 0; i < TileCount; i++)
        {
            var tile = BuildTile(i, captions[i]);
            Grid.SetColumn(tile, i % 4);
            Grid.SetRow(tile, i / 4);
            grid.Children.Add(tile);
        }

        return grid;
    }

    private FrameworkElement BuildTile(int index, string caption)
    {
        var captionBlock = new TextBlock
        {
            Text = caption,
            FontSize = 11.5,
            Foreground = _faintBrush,
        };

        var value = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        var note = new TextBlock
        {
            FontSize = 11.5,
            Foreground = _mutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        _tileValues[index] = value;
        _tileNotes[index] = note;

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(captionBlock);
        stack.Children.Add(value);
        stack.Children.Add(note);

        return new Border
        {
            Background = _tileBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11),
            Child = stack,
        };
    }

    private FrameworkElement BuildHourCell(HourlyForecast hour)
    {
        var stack = new StackPanel
        {
            Width = 60,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        stack.Children.Add(new TextBlock
        {
            Text = hour.Label,
            FontSize = 11.5,
            Foreground = _faintBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        stack.Children.Add(new Viewbox
        {
            Width = 26,
            Height = 26,
            Child = WeatherIcon.Create(hour.Code, hour.IsDay),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"{hour.Temperature:0}°",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // 无降水时留一个空位（不写 "0%"），避免整条全是数字噪音、也对齐得住
        stack.Children.Add(new TextBlock
        {
            Text = hour.PrecipProbability > 0 ? $"{hour.PrecipProbability}%" : "",
            FontSize = 11,
            Foreground = _accentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        ToolTipService.SetToolTip(stack,
            $"{hour.Label} · {WeatherCodes.Describe(hour.Code)} · {hour.Temperature:0.#}°C · "
            + $"降水概率 {hour.PrecipProbability}% · 风速 {hour.WindSpeed:0.#} m/s");

        return stack;
    }

    private FrameworkElement BuildDayRow(DailyForecast day, double globalMin, double globalMax)
    {
        var row = new Grid { ColumnSpacing = 12, Height = 34 };

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });              // 今天 / 明天 / 周X
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });              // 图标
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });              // 降水概率
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 温区条
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });             // 最低 ~ 最高

        var label = new TextBlock
        {
            Text = day.Label,
            FontSize = 13.5,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var icon = new Viewbox
        {
            Width = 24,
            Height = 24,
            Child = WeatherIcon.Create(day.Code),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var precip = new TextBlock
        {
            Text = day.PrecipProbability > 0 ? $"{day.PrecipProbability}%" : "",
            FontSize = 12,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var bar = BuildTempBar(day, globalMin, globalMax);

        var range = new TextBlock
        {
            Text = $"{day.Min:0}° ~ {day.Max:0}°",
            FontSize = 13,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        row.Children.Add(label);
        Grid.SetColumn(icon, 1);
        row.Children.Add(icon);
        Grid.SetColumn(precip, 2);
        row.Children.Add(precip);
        Grid.SetColumn(bar, 3);
        row.Children.Add(bar);
        Grid.SetColumn(range, 4);
        row.Children.Add(range);

        // 悬停看这一天的细节：日出日落 / 紫外线 / 最大风速 / 累计降水
        var details = $"{day.Label}（{day.Weekday}）· {WeatherCodes.Describe(day.Code)}\n"
                      + $"{day.Min:0.#}° ~ {day.Max:0.#}°C · 降水概率 {day.PrecipProbability}%";
        if (day.PrecipSum > 0) details += $" · 累计降水 {day.PrecipSum:0.#} mm";
        details += $"\n紫外线 {day.UvIndex:0.#}（{WeatherCodes.UvLabel(day.UvIndex)}） · 最大风速 {day.WindMax:0.#} m/s";
        if (day.Sunrise.Length > 0 || day.Sunset.Length > 0)
        {
            details += $"\n日出 {Or(day.Sunrise)} · 日落 {Or(day.Sunset)}";
        }
        ToolTipService.SetToolTip(row, details);

        return row;
    }

    /// <summary>
    /// 温区条：用三列星号宽度把「这一天的温度区间」按全局区间等比画出来。
    /// 星号宽度支持小数比例，所以不需要知道实际像素宽度，自适应即可。
    /// </summary>
    private FrameworkElement BuildTempBar(DailyForecast day, double globalMin, double globalMax)
    {
        var span = Math.Max(1, globalMax - globalMin);
        var left = Math.Clamp((day.Min - globalMin) / span, 0, 1);
        var width = Math.Clamp((day.Max - day.Min) / span, 0.08, 1);
        if (left + width > 1) left = 1 - width;
        var right = Math.Max(0, 1 - left - width);

        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Accent },
                    new GradientStop { Offset = 1, Color = Warm },
                },
            },
        };

        var bar = new Grid
        {
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Background = _trackBrush,
            CornerRadius = new CornerRadius(3),
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(right, GridUnitType.Star) });
        Grid.SetColumn(fill, 1);
        bar.Children.Add(fill);

        return bar;
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 14.5,
        FontWeight = FontWeights.SemiBold,
    };

    private Border Divider() => new() { Height = 1, Background = _dividerBrush };

    // ---------------------------------------------------------------- 刷新 / 主题

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _refreshButton.IsEnabled = false;
        _refreshButton.Content = "正在刷新…";

        try
        {
            await _onRefresh();
        }
        catch (Exception)
        {
            // 插件那边已经把失败原因写进快照 / 日志，这里不重复弹错
        }
        finally
        {
            _refreshButton.IsEnabled = true;
            _refreshButton.Content = "立即刷新";
        }
    }

    /// <summary>
    /// 中性色：岛体深色时白色系，浅色（Fluent + 浅色系统）时黑色系。
    ///
    /// 这几个画刷是**共享实例**（所有文字 / 底色 / 分隔线都指向同一支），
    /// 所以换主题时只需要改它们的 Color —— 用到的地方全部当场跟着变，不用遍历可视树。
    /// </summary>
    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(185);
        _faintBrush.Color = Neutral(130);
        _tileBrush.Color = Neutral(16);
        _dividerBrush.Color = Neutral(28);
        _trackBrush.Color = Neutral(26);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    private static string Or(string value) => string.IsNullOrEmpty(value) ? "--:--" : value;

    private static string UvAdvice(double uv) => uv switch
    {
        < 3 => "无需特别防护",
        < 6 => "建议戴帽子、墨镜",
        < 8 => "注意防晒，少在正午外出",
        < 11 => "尽量避免正午外出",
        _ => "强烈建议不要外出",
    };
}
