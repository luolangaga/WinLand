using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;
using XamlPlugin.Views;

namespace XamlPlugin;

public sealed class XamlPlugin : IslandPluginBase
{
    protected override Task OnInitializeAsync()
    {
        var view = new XamlPluginView();
        Log.Info("XAML 视图构造成功（PluginXaml.Load 生效）");

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            "xaml", Manifest.Name, Manifest.IconGlyph ?? "\uE7C3", BuildSettingsPage, 210));

        SetContent(new IslandLiveContent
        {
            Priority = Settings.Get("priority", 20),
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Windows.UI.Color.FromArgb(255, 139, 92, 246),
            MorphView = view,
            CompactSize = new Windows.Foundation.Size(240, 40),
            ExpandedSize = new Windows.Foundation.Size(400, 110),
        });

        return Task.CompletedTask;
    }

    private UIElement BuildSettingsPage()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = Manifest.Name,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = "该插件的紧凑/展开视图来自插件程序集内的 XAML 文件，通过 PluginXaml.Load 加载" +
                   "（动态加载的程序集不在宿主 resources.pri 中，生成的 InitializeComponent 找不到自己）。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
        });
        return panel;
    }
}
