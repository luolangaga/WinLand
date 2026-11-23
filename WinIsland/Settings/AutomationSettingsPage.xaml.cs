using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;
using WinIsland.Island;

namespace WinIsland.Settings;

/// <summary>
/// 「自动化与快捷方式」设置页：把命令行 / URI / 全局快捷键 / 消息历史 / 安全模式这些
/// 偏"系统集成"的东西收在一起。通用页只放日常的岛体外观与交互，不再无限膨胀。
/// </summary>
public sealed partial class AutomationSettingsPage : UserControl
{
    private readonly ISettingsStore _settings;
    private readonly Action _refreshSettings;
    private readonly MessageHistory _history;

    private bool _loading = true;

    public AutomationSettingsPage(ISettingsStore settings, MessageHistory history, Action refreshSettings)
    {
        _settings = settings;
        _history = history;
        _refreshSettings = refreshSettings;

        InitializeComponent();
        Load();
        _loading = false;
    }

    private void Load()
    {
        ToggleHotkeys.IsOn = _settings.Get("island.hotkeys", true);
        ToggleBox.Text = _settings.Get("island.hotkey.toggle", GlobalHotkeys.DefaultFor(GlobalHotkeys.ToggleIslandAction));
        PlayPauseBox.Text = _settings.Get("island.hotkey.playpause", GlobalHotkeys.DefaultFor(GlobalHotkeys.PlayPauseAction));
        QuietBox.Text = _settings.Get("island.hotkey.quiet", GlobalHotkeys.DefaultFor(GlobalHotkeys.QuietAction));

        // 安全模式：只有在真的以安全模式启动过之后才有意义
        bool safeMode = _settings.Get(StartupGuard.SafeModeKey, false);
        SafeModePanel.Visibility = safeMode ? Visibility.Visible : Visibility.Collapsed;

        ProtocolStatus.Text = ProtocolRegistration.IsRegistered()
            ? "已登记：浏览器与其它程序可以直接唤起 winisland:// 链接"
            : "未登记：命令行仍然可用，但 winisland:// 链接不会被系统转给本应用";

        RefreshHistory();
    }

    private void RefreshHistory()
    {
        var items = _history.Recent;
        HistoryList.ItemsSource = items.Select(entry => new HistoryRow(entry)).ToList();
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCount.Text = items.Count == 0 ? "还没有消息" : $"最近 {items.Count} 条（最多保留 {MessageHistory.Capacity} 条）";
    }

    // ---- 全局快捷键 ----

    private void ToggleHotkeys_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("island.hotkeys", ToggleHotkeys.IsOn);
        _refreshSettings();
    }

    private void HotkeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not TextBox box) return;

        string action = ReferenceEquals(box, ToggleBox) ? GlobalHotkeys.ToggleIslandAction
                      : ReferenceEquals(box, PlayPauseBox) ? GlobalHotkeys.PlayPauseAction
                      : GlobalHotkeys.QuietAction;

        string text = box.Text.Trim();
        bool valid = string.IsNullOrEmpty(text) || GlobalHotkeys.TryParse(text, out _, out _);
        box.BorderBrush = valid ? null : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed);

        if (!valid)
        {
            HotkeyHint.Text = $"「{text}」看不懂：用 Ctrl / Alt / Shift / Win 加一个主键，例如 Ctrl+Alt+I。留空表示不启用";
            return;
        }

        HotkeyHint.Text = string.IsNullOrEmpty(text)
            ? "留空表示不启用这条快捷键"
            : $"{GlobalHotkeys.DescribeAction(action)}：{text}";

        _settings.Set($"island.hotkey.{action}", text);
        _refreshSettings();
    }

    // ---- 安全模式 ----

    private void LeaveSafeMode_Click(object sender, RoutedEventArgs e)
    {
        StartupGuard.LeaveSafeMode(_settings);
        SafeModePanel.Visibility = Visibility.Collapsed;
        _refreshSettings();
        SafeModeResult.Text = "已退出安全模式：重启 WinIsland 后会重新加载全部插件。";
        SafeModeResult.Visibility = Visibility.Visible;
    }

    // ---- winisland:// 协议 ----

    private void RegisterProtocol_Click(object sender, RoutedEventArgs e)
    {
        var (ok, error) = ProtocolRegistration.Register();
        ProtocolStatus.Text = ok
            ? "已登记：浏览器与其它程序可以直接唤起 winisland:// 链接"
            : $"登记失败：{error}";
    }

    private void UnregisterProtocol_Click(object sender, RoutedEventArgs e)
    {
        var (ok, error) = ProtocolRegistration.Unregister();
        ProtocolStatus.Text = ok
            ? "未登记：命令行仍然可用，但 winisland:// 链接不会被系统转给本应用"
            : $"取消登记失败：{error}";
    }

    // ---- 消息历史 ----

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        RefreshHistory();
    }

    /// <summary>历史列表的一行（纯展示，不持有任何 UIElement）。</summary>
    private sealed class HistoryRow
    {
        public HistoryRow(MessageHistoryEntry entry)
        {
            Title = string.IsNullOrWhiteSpace(entry.Owner) ? entry.Title : $"{entry.Title}　·　{entry.Owner}";
            Text = entry.Text ?? string.Empty;
            TextVisibility = string.IsNullOrWhiteSpace(entry.Text) ? Visibility.Collapsed : Visibility.Visible;
            Time = entry.At.ToString("MM-dd HH:mm:ss");
        }

        public string Title { get; }
        public string Text { get; }
        public Visibility TextVisibility { get; }
        public string Time { get; }
    }
}
