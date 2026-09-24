using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceIsland;

/// <summary>
/// 设置页。工厂每次进页面都会新建一份，所以订阅在 Loaded / Unloaded 里成对处理
/// （别让插件一直握着这个页面）。值没变就不写设置 —— Settings.Set 会触发一次刷新通知。
///
/// 注意：设置窗口跟的是宿主主题，这里的文字**不要自己指定白色**，靠主题色 + Opacity 分层次。
/// </summary>
internal sealed class DeviceSettingsPage : UserControl
{
    private readonly DeviceIslandPlugin _plugin;
    private readonly StackPanel _deviceList;
    private readonly NumberBox _secondsBox;
    private readonly NumberBox _priorityBox;
    private bool _loading = true;

    public DeviceSettingsPage(DeviceIslandPlugin plugin)
    {
        _plugin = plugin;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = plugin.DisplayName,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = "插上 U 盘 / 耳机 / 手柄时，岛上会自动冒一条提示；展开后逐台列出当前设备，"
                 + "每台都能「打开」；磁盘还能「安全弹出」；蓝牙设备会显示电量（系统里没有这份数据的就不显示）。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        AddToggle(panel, "在灵动岛上显示", "关掉后插件照常监听设备，但不占用岛。", "enabled", true);

        _priorityBox = new NumberBox
        {
            Header = "岛上优先级",
            Value = _plugin.GetInt("priority", 45),
            Minimum = 0,
            Maximum = 200,
            SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 220,
        };
        _priorityBox.ValueChanged += (_, _) => CommitPriority();
        _priorityBox.LostFocus += (_, _) => CommitPriority();
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    _priorityBox,
                    new TextBlock
                    {
                        Text = "宿主同时最多只显示 4 条活动（主岛 1 + 展开队列 3），按优先级从大到小取前 4 —— "
                             + "排到第 5 名的插件连展开都看不到。常驻插件的大致档位：媒体 100、剪贴板 55、电池 50、天气 40。"
                             + "宿主「插件管理」里填的优先级会覆盖这里。",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7,
                    },
                },
            },
        });

        AddToggle(panel, "插入时提示", "设备插上时在岛上弹一条提示。", "notifyArrival", true);
        AddToggle(panel, "移除时提示", "设备拔下时在岛上弹一条提示。", "notifyRemoval", true);

        _secondsBox = new NumberBox
        {
            Header = "提示显示秒数",
            Value = _plugin.GetInt("notifySeconds", 3),
            Minimum = 1,
            Maximum = 15,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 220,
        };
        _secondsBox.ValueChanged += (_, _) => CommitSeconds();
        _secondsBox.LostFocus += (_, _) => CommitSeconds();
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = _secondsBox,
        });

        AddToggle(panel, "监听 U 盘 / 移动硬盘", "总线为 USB 或标记为可移动的磁盘。", "watchUsb", true);
        AddToggle(panel, "监听耳机 / 扬声器", "只认插着的外接耳机：USB / 蓝牙 / 3.5mm 耳机口。虚拟声卡（网易、Nahimic…）和板载扬声器不算。", "watchAudio", true);
        AddToggle(panel, "监听游戏手柄", "Xbox / XInput 类控制器。", "watchGamepad", true);

        _deviceList = new StackPanel { Spacing = 3 };
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = "当前检测到的设备",
                        FontSize = 13,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    },
                    _deviceList,
                },
            },
        });

        var testButton = new Button { Content = "发一条测试提示" };
        testButton.Click += (_, _) =>
        {
            CommitSeconds();   // 动作之前先提交：按钮点击早于 LostFocus（见 sdk-api §15.1）
            _plugin.PreviewMessage();
        };
        panel.Children.Add(testButton);

        Content = new ScrollViewer
        {
            Content = panel,
            Padding = new Thickness(0, 0, 12, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _loading = false;
        RefreshDevices();

        Loaded += (_, _) =>
        {
            RefreshDevices();
            _plugin.DevicesChanged += OnDevicesChanged;
        };
        Unloaded += (_, _) => _plugin.DevicesChanged -= OnDevicesChanged;
    }

    private void AddToggle(StackPanel panel, string header, string description, string key, bool fallback)
    {
        var toggle = new ToggleSwitch
        {
            Header = header,
            IsOn = _plugin.GetBool(key, fallback),
        };
        toggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _plugin.SetBool(key, toggle.IsOn);
        };

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    toggle,
                    new TextBlock { Text = description, FontSize = 12, Opacity = 0.7 },
                },
            },
        });
    }

    private void CommitSeconds()
    {
        if (_loading) return;

        var value = double.IsNaN(_secondsBox.Value) ? 3 : (int)Math.Round(_secondsBox.Value);
        value = Math.Clamp(value, 1, 15);
        if (value == _plugin.GetInt("notifySeconds", 3)) return;   // 值没变就别写

        _plugin.SetInt("notifySeconds", value);
    }

    private void CommitPriority()
    {
        if (_loading) return;

        var value = double.IsNaN(_priorityBox.Value) ? 45 : (int)Math.Round(_priorityBox.Value);
        value = Math.Clamp(value, 0, 200);
        if (value == _plugin.GetInt("priority", 45)) return;   // 值没变就别写

        _plugin.SetInt("priority", value);
    }

    private void OnDevicesChanged() => RefreshDevices();

    private void RefreshDevices()
    {
        _deviceList.Children.Clear();

        var devices = _plugin.Devices;
        if (devices.Count == 0)
        {
            _deviceList.Children.Add(new TextBlock
            {
                Text = "暂无 —— 插一个 U 盘或耳机试试",
                FontSize = 13,
                Opacity = 0.7,
            });
            return;
        }

        foreach (var device in devices)
        {
            var parts = new List<string> { device.Name };
            if (!string.IsNullOrWhiteSpace(device.Detail)) parts.Add(device.Detail);
            if (device.BatteryPercent is int percent) parts.Add($"电量 {percent}%");

            _deviceList.Children.Add(new TextBlock
            {
                Text = "· " + string.Join(" · ", parts),
                FontSize = 13,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.85,
            });
        }
    }
}
