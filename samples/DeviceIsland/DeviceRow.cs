using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace DeviceIsland;

/// <summary>
/// 设备列表里的一行：<c>[图标] 名称 说明 [打开] [安全弹出]</c>。
///
/// 两个按钮是真正的 <see cref="Button"/>，所以宿主识别得出「这是交互控件」——
/// 点它们不会冒泡到岛体去弹聚光卡，也不需要自己标 Handled。
/// 行本身不做点击（点行的空白处会冒泡到岛体，也就是打开聚光卡看完整列表）。
///
/// <paramref name="dense"/> 是「紧凑版」：大岛展开态只有约 118 DIP 高，要竖着塞下 3 行，
/// 所以字号/内边距都收着走；聚光卡里用宽松版。
///
/// 配色跟着岛体主题走（浅色岛体上写死白色就是白字压白底）：中性色全部由 <paramref name="theme"/> 决定。
/// </summary>
internal sealed class DeviceRow : UserControl
{
    private readonly SolidColorBrush _nameBrush;
    private readonly SolidColorBrush _detailBrush;
    private readonly SolidColorBrush _glyphBrush;
    private readonly SolidColorBrush _hoverBrush;

    /// <summary>电量芯片的中性色画刷；这台设备没有电量（读不到）时保持 null，界面上就不画。</summary>
    private readonly SolidColorBrush? _batteryOutlineBrush;
    private readonly SolidColorBrush? _batteryFillBrush;
    private readonly SolidColorBrush? _batteryTextBrush;

    private readonly IIslandTheme _theme;
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    public DeviceRow(DeviceInfo device, IIslandTheme theme, Action<DeviceInfo> open, Action<DeviceInfo> eject, bool dense)
    {
        _theme = theme;
        _nameBrush = new SolidColorBrush(Neutral(theme, 240));
        _detailBrush = new SolidColorBrush(Neutral(theme, 132));
        _glyphBrush = new SolidColorBrush(Neutral(theme, 205));
        _hoverBrush = new SolidColorBrush(Neutral(theme, 24));

        var glyphSize = dense ? 18d : 22d;
        var nameSize = dense ? 12d : 13.5;
        var detailSize = dense ? 10.5 : 11.5;
        var padding = dense ? new Thickness(4, 2, 4, 2) : new Thickness(8, 5, 8, 5);

        var glyph = new FontIcon
        {
            Glyph = device.Glyph,
            FontSize = glyphSize,
            Foreground = _glyphBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var leading = new Grid
        {
            Width = glyphSize + 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leading.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = device.Name,
            FontSize = nameSize,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _nameBrush,
        };

        var detail = new TextBlock
        {
            Text = device.Detail,
            FontSize = detailSize,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _detailBrush,
        };

        // 副标题 +（有的话）电量 —— 横着排在名称右侧、动作按钮左侧
        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = dense ? 5 : 7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        trailing.Children.Add(detail);

        if (device.BatteryPercent is int batteryPercent)
        {
            _batteryOutlineBrush = new SolidColorBrush(Neutral(theme, 150));
            _batteryFillBrush = new SolidColorBrush(Neutral(theme, 235));
            _batteryTextBrush = new SolidColorBrush(Neutral(theme, 215));
            trailing.Children.Add(MakeBatteryChip(
                batteryPercent, dense, _batteryOutlineBrush, _batteryFillBrush, _batteryTextBrush));
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = dense ? 6 : 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(MakeButton("打开", device, open, dense));
        if (device.CanEject)
        {
            actions.Children.Add(MakeButton("弹出", device, eject, dense));
        }

        var grid = new Grid { ColumnSpacing = dense ? 6 : 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(leading);

        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = padding,
            Background = TransparentBrush,   // 非 null 才参与命中测试（否则鼠标悬浮事件不触发）
            Child = grid,
        };

        frame.PointerEntered += (_, _) => frame.Background = _hoverBrush;
        frame.PointerExited += (_, _) => frame.Background = TransparentBrush;

        Content = frame;
    }

    /// <summary>岛体换主题时就地重刷中性色（视图颜色烘在画刷里，不重刷会留在旧主题）。</summary>
    internal void RefreshTheme()
    {
        _nameBrush.Color = Neutral(_theme, 240);
        _detailBrush.Color = Neutral(_theme, 132);
        _glyphBrush.Color = Neutral(_theme, 205);
        _hoverBrush.Color = Neutral(_theme, 24);

        if (_batteryOutlineBrush is not null) _batteryOutlineBrush.Color = Neutral(_theme, 150);
        if (_batteryFillBrush is not null) _batteryFillBrush.Color = Neutral(_theme, 235);
        if (_batteryTextBrush is not null) _batteryTextBrush.Color = Neutral(_theme, 215);
    }

    /// <summary>中性色：岛体深色时是白色系，浅色（Fluent + 浅色系统）时是黑色系。</summary>
    private static Windows.UI.Color Neutral(IIslandTheme theme, byte alpha) => theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>
    /// 电量芯片：一个**画出来的**小电池（外壳 + 正极小帽 + 按比例填充）配上百分比数字。
    ///
    /// 不用字体图标：图标字体的代码点是「有就有、没有就是豆腐块」，而这是自己拼的形状，
    /// 任何主题、任何字体回退下都不会变形。颜色全部来自调用方传入的中性色画刷。
    /// </summary>
    private static UIElement MakeBatteryChip(
        int percent, bool dense, SolidColorBrush outline, SolidColorBrush fill, SolidColorBrush textBrush)
    {
        var level = Math.Clamp(percent, 0, 100);
        const double borderThickness = 1;
        const double innerPadding = 1;

        var bodyWidth = dense ? 15d : 18d;
        var bodyHeight = dense ? 8d : 9d;
        var innerWidth = bodyWidth - 2 * borderThickness - 2 * innerPadding;
        var innerHeight = bodyHeight - 2 * borderThickness - 2 * innerPadding;

        var body = new Border
        {
            Width = bodyWidth,
            Height = bodyHeight,
            BorderThickness = new Thickness(borderThickness),
            BorderBrush = outline,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(innerPadding),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (level > 0)
        {
            body.Child = new Border
            {
                Width = Math.Max(1, innerWidth * level / 100.0),
                Height = innerHeight,
                Background = fill,
                CornerRadius = new CornerRadius(1),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        }

        var cap = new Border
        {
            Width = 2,
            Height = dense ? 3d : 4d,
            Background = outline,
            CornerRadius = new CornerRadius(0, 1, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var gauge = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { body, cap },
        };

        var label = new TextBlock
        {
            Text = $"{level}%",
            FontSize = dense ? 10 : 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = textBrush,
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = dense ? 4 : 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { gauge, label },
        };
    }

    private static Button MakeButton(string text, DeviceInfo device, Action<DeviceInfo> action, bool dense)
    {
        var button = new Button
        {
            Content = text,
            FontSize = dense ? 10.5 : 12,
            Padding = dense ? new Thickness(7, 1, 7, 1) : new Thickness(10, 3, 10, 3),
            MinWidth = 0,    // WinUI 默认 Button 的 MinWidth/MinHeight 会把行撑高
            MinHeight = 0,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => action(device);
        return button;
    }
}
