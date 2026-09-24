using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinIsland.Core;

namespace WinIsland.Island;

/// <summary>
/// 临时消息（<see cref="IslandMessage"/>）的视图：强调色图标芯片 + 标题 + 可选的第二行正文。
/// 宽度跟着内容走（<see cref="MinWidth"/>..<see cref="MaxWidth"/>），高度跟着行数走 ——
/// 短消息是一条紧凑的胶囊，而不是一条固定 280 宽、右边空一大截的横条。
///
/// 尺寸会写成视图自己的 Width/Height 钉住：岛体的尺寸 morph 逐帧改变岛体大小，
/// 内容如果跟着重排，正文会在过渡中反复换行；钉住之后内容只是被岛体的裁剪框一点点"露出来"。
/// </summary>
internal static class MessageView
{
    private const double ChipSize = 28;
    private const double ChipGlyphSize = 15;
    private const double TitleSize = 13;
    private const double TextSize = 11.5;
    private const double PadLeft = 5;
    private const double PadRight = 16;
    private const double PadY = 6;
    private const double ColumnSpacing = 10;

    /// <summary>最窄的消息条：比空闲小岛略宽，短消息也还是一条"岛"。</summary>
    public const double MinWidth = 152;

    /// <summary>最宽的消息条：再长的正文交给换行与省略号，别让一条消息横穿半个屏幕。</summary>
    public const double MaxWidth = 340;

    public const double MinHeight = 40;
    public const double MaxHeight = 72;

    /// <summary>消息卡的最大尺寸：画布按它预留（见 <c>IslandWindow.ComputeCanvasFootprint</c>）。</summary>
    public static readonly Size MaxSize = new(MaxWidth, MaxHeight);

    /// <summary>按内容建视图，并返回它需要的岛体尺寸（同宽同高，可直接交给尺寸动画）。</summary>
    public static (UIElement View, Size Size) Build(IslandMessage msg, IslandStyleKind style, bool light)
    {
        var stroke = IslandStyle.CreateMessageChipStroke(style, light);
        var chip = new Border
        {
            Width = ChipSize,
            Height = ChipSize,
            CornerRadius = new CornerRadius(IslandStyle.ResolveMessageChipRadius(style, ChipSize)),
            Background = IslandStyle.CreateMessageChipFill(style, msg.AccentColor, light),
            BorderBrush = stroke,
            BorderThickness = stroke == null ? new Thickness(0) : new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = msg.Glyph,
                FontSize = ChipGlyphSize,
                Foreground = new SolidColorBrush(IslandStyle.MessageChipGlyphColor(msg.AccentColor, light)),
            },
        };

        var title = new TextBlock
        {
            Text = msg.Title,
            FontSize = TitleSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandStyle.CreateMessageTitleBrush(light),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        var lines = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { title },
        };

        if (!string.IsNullOrWhiteSpace(msg.Text))
        {
            lines.Children.Add(new TextBlock
            {
                Text = msg.Text.Trim(),
                FontSize = TextSize,
                Foreground = IslandStyle.CreateMessageTextBrush(light),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
            });
        }

        var content = new Grid
        {
            Padding = new Thickness(PadLeft, PadY, PadRight, PadY),
            ColumnSpacing = ColumnSpacing,
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(chip, 0);
        Grid.SetColumn(lines, 1);
        content.Children.Add(chip);
        content.Children.Add(lines);

        // 先按"一行到底"量出自然宽度（无限约束下星号列按内容量），夹进上下限后再按最终宽度量一次高度
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Clamp(Math.Ceiling(content.DesiredSize.Width), MinWidth, MaxWidth);

        content.Measure(new Size(width, double.PositiveInfinity));
        double height = Math.Clamp(Math.Ceiling(content.DesiredSize.Height), MinHeight, MaxHeight);

        content.Width = width;
        content.Height = height;
        return (content, new Size(width, height));
    }
}
