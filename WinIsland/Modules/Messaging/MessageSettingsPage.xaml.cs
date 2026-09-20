using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace WinIsland.Modules.Messaging;

public sealed partial class MessageSettingsPage : UserControl
{
    private readonly IIslandSurface _island;

    public MessageSettingsPage(IPluginContext context)
    {
        _island = context.Island;
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
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = "通过 IIslandSurface.Show 展示",
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255)),
            FontSize = 12,
        });
        panel.Children.Add(textPanel);

        _island.Show(panel, new Windows.Foundation.Size(340, 92), TimeSpan.FromSeconds(5));
    }
}
