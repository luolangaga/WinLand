using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace DeviceIsland;

/// <summary>
/// 「超级展开」聚光卡：大岛只放得下 3 行，这里给出**当前所有设备**的完整列表，
/// 每行都是「打开」「安全弹出」。飞入飞回、遮罩、圆角、层级全由宿主负责，这里只给内容树。
///
/// 必须是**独立于岛视图的另一棵树**（每个窗口一棵树），所以实例与 <see cref="DeviceIslandView"/> 分开。
/// </summary>
public sealed class DeviceSpotlightView : UserControl
{
    private readonly Action<DeviceInfo> _onOpen;
    private readonly Action<DeviceInfo> _onEject;
    private readonly IIslandTheme _theme;
    private readonly StackPanel _list;
    private readonly TextBlock _summary;

    internal DeviceSpotlightView(PluginManifest manifest, Action<DeviceInfo> onOpen, Action<DeviceInfo> onEject, IIslandTheme theme)
    {
        _onOpen = onOpen;
        _onEject = onEject;
        _theme = theme;

        // 聚光卡也跟着岛体主题明暗：浅色卡片上写死白色就是白字压白底
        var title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Neutral(255)),
        };

        _summary = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Neutral(170)),
        };

        _list = new StackPanel { Spacing = 2 };

        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var hint = new TextBlock
        {
            Text = "「弹出」会先锁卷再卸载 —— 有文件被打开时会失败，先关掉再试。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Neutral(120)),
        };

        var header = new StackPanel { Spacing = 4, Children = { title, _summary } };

        var root = new Grid
        {
            Padding = new Thickness(28, 24, 28, 22),
            RowSpacing = 14,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(header);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(hint, 2);
        root.Children.Add(hint);

        Content = root;
    }

    /// <summary>刷新卡片内容（UI 线程调用）。卡片收起后仍然可以继续调用，没有副作用。</summary>
    internal void Apply(IReadOnlyList<DeviceInfo> devices)
    {
        _summary.Text = devices.Count > 0
            ? $"当前共 {devices.Count} 台设备 · 每台都可单独打开或安全弹出"
            : "当前没有可移动设备。插上 U 盘、耳机或手柄，这里就会列出来。";

        _list.Children.Clear();
        foreach (var device in devices)
        {
            _list.Children.Add(new DeviceRow(device, _theme, _onOpen, _onEject, dense: false));
        }
    }

    /// <summary>中性色：岛体深色时是白色系，浅色（Fluent + 浅色系统）时是黑色系。</summary>
    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>宿主收起卡片时的收尾（这里没有定时器/请求要取消，留作扩展点）。</summary>
    public void OnHostClosed()
    {
    }
}
