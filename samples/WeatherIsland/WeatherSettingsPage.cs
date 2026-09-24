using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace WeatherIsland;

/// <summary>插件设置页：显示开关、城市、刷新间隔、立即刷新。</summary>
public sealed class WeatherSettingsPage : UserControl
{
    private static readonly int[] RefreshMinutes = { 10, 15, 30, 60, 120 };

    private readonly ISettingsStore _settings;
    private readonly WeatherIslandPlugin _plugin;
    private readonly TextBox _cityBox;
    private readonly ComboBox _refreshBox;
    private readonly TextBlock _status;
    private readonly Button _refreshButton;
    private bool _loading = true;

    public WeatherSettingsPage(IPluginContext context, WeatherIslandPlugin plugin)
    {
        _settings = context.Settings;
        _plugin = plugin;

        _cityBox = new TextBox
        {
            Header = "城市",
            Text = plugin.CurrentCity,
            PlaceholderText = "中文城市名，或直接填「纬度,经度」",
            MinWidth = 280,
        };
        _cityBox.LostFocus += (_, _) => SaveCity();
        _cityBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) SaveCity();
        };

        _refreshBox = new ComboBox
        {
            Header = "刷新间隔",
            ItemsSource = RefreshMinutes.Select(m => $"{m} 分钟").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(RefreshMinutes, plugin.CurrentRefreshMinutes)),
            MinWidth = 160,
        };
        if (_refreshBox.SelectedIndex < 0) _refreshBox.SelectedIndex = 2;
        _refreshBox.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.Set("refresh", RefreshMinutes[Math.Max(0, _refreshBox.SelectedIndex)]);
        };

        _refreshButton = new Button { Content = "立即刷新" };
        _refreshButton.Click += OnRefreshClick;

        _status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(180),
            VerticalAlignment = VerticalAlignment.Center,
        };
        UpdateStatus(plugin.LastSnapshot);

        Content = BuildContent();
        _loading = false;

        // 订阅插件的快照更新：改完城市后台取回新数据时，这里的状态行会立刻跟着变，
        // 不会停在上一座城市（页面关掉就退订，别让插件一直握着这个页面）
        _plugin.SnapshotApplied += OnSnapshotApplied;
        Unloaded += (_, _) => _plugin.SnapshotApplied -= OnSnapshotApplied;
    }

    private void OnSnapshotApplied(WeatherSnapshot snapshot)
    {
        // 只在输入框没有焦点时回填，免得把正在输入的城市名冲掉
        if (snapshot.HasData && _cityBox.FocusState == FocusState.Unfocused)
        {
            _cityBox.Text = snapshot.Place;
        }

        UpdateStatus(snapshot);
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
            Text = "小岛显示天气图标和当前温度；把鼠标移到岛上展开，可以看到地点、更新时间和今天 / 明天 / 后天三天的预报。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(180),
        });

        panel.Children.Add(Card(BuildVisibility()));
        panel.Children.Add(Card(BuildLocation()));
        panel.Children.Add(Card(BuildAbout()));

        return panel;
    }

    private UIElement BuildVisibility()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Header("显示"));

        var toggle = new ToggleSwitch
        {
            Header = "在灵动岛上显示",
            IsOn = _settings.Get("enabled", true),
        };
        toggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _settings.Set("enabled", toggle.IsOn);
        };
        panel.Children.Add(toggle);

        panel.Children.Add(new TextBlock
        {
            Text = "关闭后不再占用小岛（天气照常在后台刷新）。多个插件同时有内容时谁上岛由优先级决定，"
                   + "可以在「设置 → 插件管理」里逐个调整。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(150),
        });

        return panel;
    }

    private UIElement BuildLocation()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Header("位置与刷新"));

        panel.Children.Add(_cityBox);
        panel.Children.Add(new TextBlock
        {
            Text = "填中文城市名（如「杭州」）会自动查经纬度；也可以直接填「30.25,120.17」这类坐标，省掉一次查询。"
                   + "改完点一下别处或按回车即保存。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(150),
        });

        panel.Children.Add(_refreshBox);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(_refreshButton);
        actions.Children.Add(_status);
        panel.Children.Add(actions);

        return panel;
    }

    private static UIElement BuildAbout()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Header("数据来源"));
        panel.Children.Add(new TextBlock
        {
            Text = "天气数据来自 Open-Meteo（open-meteo.com）：免费、不需要注册，也不用填 API Key。"
                   + "插件只把你填的城市名 / 坐标发给它，不发送任何其他信息。"
                   + "网络不通时会继续显示上一次拿到的数据。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(190),
        });
        return panel;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        // 先把输入框里还没提交的城市存下来，再取数：
        // 否则「改完城市直接点刷新」会拿旧城市去查（曾经的真实 bug）
        SaveCity();

        _refreshButton.IsEnabled = false;
        _status.Text = "正在刷新…";

        try
        {
            var snapshot = await _plugin.RefreshNowAsync();
            UpdateStatus(snapshot);
        }
        catch (Exception ex)
        {
            _status.Text = $"刷新失败：{ex.Message}";
        }
        finally
        {
            _refreshButton.IsEnabled = true;
        }
    }

    private void SaveCity()
    {
        if (_loading) return;

        var text = (_cityBox.Text ?? string.Empty).Trim();
        if (string.Equals(text, _settings.Get("city", "北京"), StringComparison.Ordinal)) return;

        _settings.Set("city", text);
        _status.Text = $"已改为「{(text.Length == 0 ? "北京" : text)}」，正在刷新…";
    }

    private void UpdateStatus(WeatherSnapshot snapshot)
    {
        if (!snapshot.HasData)
        {
            _status.Text = $"状态：{snapshot.Error ?? "暂无数据"}";
            return;
        }

        var today = snapshot.Days.Count > 0 ? snapshot.Days[0] : null;
        var range = today is null ? string.Empty : $"，今天 {today.Max:0}° / {today.Min:0}°";
        _status.Text = $"状态：{snapshot.Place} {snapshot.Temperature:0.#}°C {WeatherCodes.Describe(snapshot.Code)}{range}"
                       + $"（{snapshot.UpdatedAt:HH:mm} 更新）";
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    };

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
