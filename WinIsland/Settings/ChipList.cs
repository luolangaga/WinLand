using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Settings;

/// <summary>
/// 会自动换行的标签（chip）行。
/// 不要用横向 StackPanel 排标签（按内容无限宽度测量，窄容器里直接溢出），
/// 也不要用 RichTextBlock + InlineUIContainer（换行时宽度测量不准，标签会被截断）。
/// </summary>
internal static class ChipList
{
    public static WrapPanel Build(IEnumerable<string> values, byte r = 0x80, byte g = 0x80, byte b = 0x80)
    {
        var panel = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 4 };
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                panel.Children.Add(BuildChip(value.Trim(), r, g, b));
            }
        }

        return panel;
    }

    private static Border BuildChip(string text, byte r, byte g, byte b) => new()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, r, g, b)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)),
        },
    };
}
