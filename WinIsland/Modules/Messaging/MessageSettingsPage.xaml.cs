using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace WinIsland.Modules.Messaging;

public sealed partial class MessageSettingsPage : UserControl
{
    private readonly IIslandSurface _island;
    private readonly IIslandTheme _theme;

    public MessageSettingsPage(IPluginContext context)
    {
        _island = context.Island;
        _theme = context.Theme;
        InitializeComponent();
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        var title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "新消息" : TitleBox.Text.Trim();
        var seconds = double.IsNaN(DurationBox.Value) ? 4 : DurationBox.Value;

        _island.ShowMessage(new IslandMessage
        {
            Title = title,
            Text = string.IsNullOrWhiteSpace(BodyBox.Text) ? null : BodyBox.Text.Trim(),
            Duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60)),
        });
    }

    private void SendCustom_Click(object sender, RoutedEventArgs e)
    {
        // 这段内容也是"岛体上的内容"：岛体在浅色主题（Fluent）下是浅底，文字颜色必须跟着主题走
        bool light = _theme.IsLight;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 34,
            Height = 34,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 194, 255)),
        });
        var textPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = "自定义 XAML 控件",
            Foreground = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(255, 28, 28, 30)
                : Microsoft.UI.Colors.White),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = "通过 IIslandSurface.Show 展示",
            Foreground = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(160, 0, 0, 0)
                : Windows.UI.Color.FromArgb(160, 255, 255, 255)),
            FontSize = 12,
        });
        panel.Children.Add(textPanel);

        _island.Show(panel, new Windows.Foundation.Size(340, 92), TimeSpan.FromSeconds(5));
    }
}
