using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace HardwareMonitor;

public sealed class HardwareSettingsPage : UserControl
{
    private readonly ISettingsStore _settings;
    private readonly HardwareMonitorPlugin _plugin;
    private bool _loading = true;

    public HardwareSettingsPage(IPluginContext context, HardwareMonitorPlugin plugin)
    {
        _settings = context.Settings;
        _plugin = plugin;

        Content = BuildContent();
        _loading = false;
    }

    private UIElement BuildContent()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = _plugin.DisplayName,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = "小岛显示你勾选的指标；展开小岛可看到前台窗口帧率、CPU、GPU 与上下传速度。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(180),
        });

        panel.Children.Add(Card(BuildVisibility()));
        panel.Children.Add(Card(BuildCompactOptions()));

        var devices = BuildDevices();
        if (devices != null) panel.Children.Add(Card(devices));

        panel.Children.Add(Card(BuildFpsHint()));

        return panel;
    }

    private UIElement BuildVisibility()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "显示", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(Toggle("在灵动岛上显示", "enabled", _settings.Get("enabled", true)));
        panel.Children.Add(new TextBlock
        {
            Text = "关闭后不再占用小岛（插件继续在后台采样，随时可以再打开）。"
                   + "多个插件同时有内容时，谁上岛由优先级决定，可在「设置 → 插件管理」里逐个调整。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(150),
        });
        return panel;
    }

    private UIElement BuildCompactOptions()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "小岛显示项", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(Toggle("CPU 占用", "compact.cpu", _plugin.CurrentCompactOptions.ShowCpu));
        panel.Children.Add(Toggle("GPU 占用", "compact.gpu", _plugin.CurrentCompactOptions.ShowGpu));
        panel.Children.Add(Toggle("上下传速度", "compact.net", _plugin.CurrentCompactOptions.ShowNet));
        panel.Children.Add(Toggle("前台窗口帧率", "compact.fps", _plugin.CurrentCompactOptions.ShowFps));
        return panel;
    }

    private UIElement? BuildDevices()
    {
        var cpu = _plugin.CpuDevices;
        var gpu = _plugin.GpuDevices;
        var network = _plugin.NetworkDevices;

        var rows = new StackPanel { Spacing = 10 };
        rows.Children.Add(new TextBlock { Text = "设备", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        if (cpu.Count > 1)
        {
            rows.Children.Add(Picker("CPU", "device.cpu", cpu));
        }

        if (gpu.Count > 1)
        {
            rows.Children.Add(Picker("GPU", "device.gpu", gpu));
        }
        else
        {
            rows.Children.Add(StaticRow("GPU", gpu.Count == 0 ? "未检测到独立 GPU，使用系统 GPU 计数器" : gpu[0]));
        }

        if (network.Count > 1)
        {
            rows.Children.Add(Picker("网卡", "device.net", network));
        }
        else
        {
            rows.Children.Add(StaticRow("网卡", network.Count == 0 ? "未检测到联网网卡" : network[0]));
        }

        return rows;
    }

    private UIElement BuildFpsHint()
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "前台窗口帧率", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        var requiresAdmin = _plugin.FpsRequiresAdmin;
        panel.Children.Add(new TextBlock
        {
            Text = requiresAdmin
                ? "读取前台窗口帧率需要以管理员身份启动 WinIsland（通过 ETW 采集 DxgKrnl present 事件）。"
                  + "当前显示的是桌面合成帧率；以管理员启动后这里会自动切换为前台窗口帧率。"
                : "已以管理员身份运行，正在通过 ETW 采集前台窗口的 present 事件。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(200),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"当前数据源：{_plugin.FpsStatus}",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(150),
        });
        return panel;
    }

    private ToggleSwitch Toggle(string header, string key, bool value)
    {
        var toggle = new ToggleSwitch { Header = header, IsOn = value };
        toggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _settings.Set(key, toggle.IsOn);
        };
        return toggle;
    }

    private ComboBox Picker(string label, string key, IReadOnlyList<string> devices)
    {
        var combo = new ComboBox
        {
            Header = label,
            ItemsSource = devices.ToList(),
            SelectedIndex = Math.Clamp(_plugin.GetDeviceIndex(key.Split('.')[1]), 0, devices.Count - 1),
            MinWidth = 260,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.Set(key, combo.SelectedIndex);
        };
        return combo;
    }

    private static UIElement StaticRow(string label, string value)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 48,
        });

        var text = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(200),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static Border Card(UIElement child) => new()
    {
        Style = (Style)Application.Current.Resources["SettingsCardStyle"],
        Child = child,
    };

    /// <summary>次级文字：设置窗跟随应用主题，浅色窗里必须用深色（写死白色等于看不见）。</summary>
    private static SolidColorBrush Hint(byte alpha) => new(Application.Current.RequestedTheme == ApplicationTheme.Light
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255));
}
