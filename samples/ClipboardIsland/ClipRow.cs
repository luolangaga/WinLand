using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClipboardIsland;

/// <summary>
/// 历史列表里的一行：[类型图标 / 图片缩略图] 内容摘要 [时间]。
///
/// 可点击的行（大岛展开态里的那几行、聚光卡里的每一行）点一下就会把这条重新复制并粘出去，
/// 带 hover 底色；点中时把 Tapped 标成 Handled，免得冒泡到岛体再弹一次聚光卡。
///
/// <paramref name="dense"/> 是「紧凑但可点」：大岛展开态只有 168 DIP 高，要塞下 3 行，
/// 沿用聚光卡那种带内边距的行会撑爆 —— 所以尺寸按紧凑走，只额外提供 hover 与点击。
/// </summary>
internal sealed class ClipRow : UserControl
{
    private static readonly SolidColorBrush TextBrush = new(Windows.UI.Color.FromArgb(235, 255, 255, 255));
    private static readonly SolidColorBrush HintBrush = new(Windows.UI.Color.FromArgb(125, 255, 255, 255));
    private static readonly SolidColorBrush GlyphBrush = new(Windows.UI.Color.FromArgb(190, 255, 255, 255));
    private static readonly SolidColorBrush HoverBrush = new(Windows.UI.Color.FromArgb(26, 255, 255, 255));
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private readonly ClipItem _item;
    private readonly Border _frame;

    public ClipRow(ClipItem item, bool interactive, Action<ClipItem>? invoke, bool dense = false)
    {
        _item = item;

        var leadingSize = dense ? 20d : interactive ? 24d : 20d;
        var fontSize = dense ? 12d : interactive ? 12.5 : 12;
        var padding = dense
            ? new Thickness(4, 3, 4, 3)
            : interactive ? new Thickness(10, 6, 10, 6) : new Thickness(0, 3, 0, 3);

        var glyph = new FontIcon
        {
            Glyph = item.IsImage ? "\uEB9F" : item.IsFiles ? "\uE8B7" : "\uE8A5",
            FontSize = leadingSize * 0.62,
            Foreground = GlyphBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var leading = new Grid
        {
            Width = leadingSize,
            Height = leadingSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leading.Children.Add(glyph);

        if (item.IsImage && item.ImagePath is not null)
        {
            var thumb = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
            var thumbFrame = new Border
            {
                CornerRadius = new CornerRadius(5),
                Opacity = 0,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(34, 255, 255, 255)),
                Child = thumb,
            };
            leading.Children.Add(thumbFrame);

            // 缩略图异步补齐；拿不到就继续显示类型图标，不报错
            _ = FillThumbnailAsync(thumb, thumbFrame, glyph, item.ImagePath, (int)(leadingSize * 3));
        }

        var summary = new TextBlock
        {
            Text = item.Summary,
            FontSize = fontSize,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextBrush,
        };

        var time = new TextBlock
        {
            Text = ClipText.Ago(item.CapturedAt),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = HintBrush,
        };

        var grid = new Grid { ColumnSpacing = dense ? 8 : interactive ? 10 : 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(leading);
        Grid.SetColumn(summary, 1);
        grid.Children.Add(summary);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        _frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = padding,
            // 命中测试需要非 null 的画刷（全透明也行），否则 Tapped 不会触发
            Background = interactive ? TransparentBrush : null,
            Child = grid,
        };

        if (interactive)
        {
            _frame.PointerEntered += (_, _) => _frame.Background = HoverBrush;
            _frame.PointerExited += (_, _) => _frame.Background = TransparentBrush;
            _frame.Tapped += (_, e) =>
            {
                // 必须吃掉事件：宿主只看得到 ButtonBase/Slider 这类控件，
                // 不标记 Handled 的话这次点击会继续冒泡到岛体，又弹一次聚光卡
                e.Handled = true;
                invoke?.Invoke(_item);
            };
        }

        Content = _frame;
    }

    public ClipItem Item => _item;

    private static async Task FillThumbnailAsync(Image target, Border frame, FontIcon glyph, string path, int decodeWidth)
    {
        try
        {
            var image = await ThumbnailLoader.LoadAsync(path, decodeWidth);
            if (image is null) return;

            target.Source = image;
            target.Opacity = 1;
            frame.Opacity = 1;
            glyph.Opacity = 0;
        }
        catch
        {
            // 缩略图加载失败：保持类型图标，什么都不用做（这里绝不能把异常抛给宿主）
        }
    }
}
