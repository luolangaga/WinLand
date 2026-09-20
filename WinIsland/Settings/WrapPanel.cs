using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinIsland.Settings;

/// <summary>
/// 横向自动换行的面板（WinUI 3 没有内置 WrapPanel，Community Toolkit 也不引入）。
/// 标签行、徽标行这类"不定宽、要换行"的内容都用它，避免横向 StackPanel 把内容顶出容器。
/// </summary>
internal sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 6;

    public double VerticalSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = availableSize.Width;
        double x = 0, y = 0, lineHeight = 0, widest = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;

            if (x > 0 && !double.IsInfinity(limit) && x + size.Width > limit)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }

            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
            widest = Math.Max(widest, x - HorizontalSpacing);
        }

        var width = double.IsInfinity(limit) ? widest : limit;
        return new Size(width, y + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return finalSize;
    }
}
