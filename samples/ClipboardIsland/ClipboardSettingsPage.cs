using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace ClipboardIsland;

/// <summary>插件设置页：显示开关、复制提示方式、点击历史的行为、记录范围与清空。</summary>
public sealed class ClipboardSettingsPage : UserControl
{
    private static readonly int[] HistoryLimits = { 10, 20, 50, 100 };
    private static readonly int[] FlashSeconds = { 1, 2, 3, 5, 8 };

    private readonly ISettingsStore _settings;
    private readonly ClipboardIslandPlugin _plugin;

    private readonly ToggleSwitch _enabledToggle;
    private readonly ToggleSwitch _notifyToggle;
    private readonly ToggleSwitch _pasteToggle;
    private readonly ToggleSwitch _imagesToggle;
    private readonly ComboBox _limitBox;
    private readonly ComboBox _flashBox;
    private readonly TextBlock _stats;
    private readonly Button _clearButton;

    private bool _loading = true;
    private bool _confirmClear;

    public ClipboardSettingsPage(IPluginContext context, ClipboardIslandPlugin plugin)
    {
        _settings = context.Settings;
        _plugin = plugin;

        _enabledToggle = new ToggleSwitch
        {
            Header = "在灵动岛上显示",
            OnContent = "常驻显示，复制时给出提示",
            OffContent = "不在岛上显示（历史照常记录）",
            IsOn = plugin.IsEnabled,
        };
        _enabledToggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _settings.Set("enabled", _enabledToggle.IsOn);
        };

        _notifyToggle = new ToggleSwitch
        {
            Header = "复制后在岛上弹提示",
            OnContent = "岛临时变成一条「已复制 · 摘要」，几秒后自动还原",
            OffContent = "只在常驻小岛上高亮闪一下（不打断岛上的其他内容）",
            IsOn = plugin.NotifyOnCopy,
        };
        _notifyToggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _settings.Set("notify", _notifyToggle.IsOn);
        };

        _pasteToggle = new ToggleSwitch
        {
            Header = "点历史条目直接粘贴",
            OnContent = "复制后自动粘进你正在输入的那个窗口",
            OffContent = "只写回剪贴板，粘贴键自己按",
            IsOn = plugin.PasteOnPick,
        };
        _pasteToggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _settings.Set("paste", _pasteToggle.IsOn);
        };

        _imagesToggle = new ToggleSwitch
        {
            Header = "记录图片",
            OnContent = "文字和图片都记（图片存为缩略图缓存）",
            OffContent = "只记文字",
            IsOn = plugin.IncludeImages,
        };
        _imagesToggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _settings.Set("images", _imagesToggle.IsOn);
        };

        _limitBox = new ComboBox
        {
            Header = "最多保留几条",
            ItemsSource = HistoryLimits.Select(v => $"{v} 条").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(HistoryLimits, plugin.HistoryLimit)),
            MinWidth = 160,
        };
        if (_limitBox.SelectedIndex < 0) _limitBox.SelectedIndex = 1;
        _limitBox.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.Set("limit", HistoryLimits[Math.Max(0, _limitBox.SelectedIndex)]);
        };

        _flashBox = new ComboBox
        {
            Header = "复制后的提示显示几秒",
            ItemsSource = FlashSeconds.Select(v => $"{v} 秒").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(FlashSeconds, plugin.FlashSeconds)),
            MinWidth = 160,
        };
        if (_flashBox.SelectedIndex < 0) _flashBox.SelectedIndex = 2;
        _flashBox.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.Set("flash", FlashSeconds[Math.Max(0, _flashBox.SelectedIndex)]);
        };

        _stats = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hint(180),
        };

        _clearButton = new Button { Content = "清空历史" };
        _clearButton.Click += (_, _) => OnClearClick();

        Content = BuildContent();
        _loading = false;

        UpdateStats();

        // 历史一变（复制了新内容、被裁剪、被清空）就刷新这里的计数
        _plugin.HistoryChanged += OnHistoryChanged;
        Unloaded += (_, _) => _plugin.HistoryChanged -= OnHistoryChanged;
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
            Text = "小岛常驻显示最近一条内容的摘要和总条数；每次复制新内容，岛会弹一条提示。"
                   + "把鼠标移到岛上展开能看到最近 3 条，点某一条就直接粘进你正在输入的窗口；"
                   + "点岛体的空白处则弹出完整历史。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(180),
        });

        panel.Children.Add(Card(BuildVisibility()));
        panel.Children.Add(Card(BuildBehavior()));
        panel.Children.Add(Card(BuildData()));
        panel.Children.Add(Card(BuildAbout()));

        return panel;
    }

    private UIElement BuildVisibility()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Header("显示"));
        panel.Children.Add(_enabledToggle);
        panel.Children.Add(new TextBlock
        {
            Text = "多个插件同时有内容时谁上岛由优先级决定，可以在「设置 → 插件管理」里调整。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(150),
        });
        return panel;
    }

    private UIElement BuildBehavior()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Header("行为"));
        panel.Children.Add(_notifyToggle);
        panel.Children.Add(_flashBox);
        panel.Children.Add(_pasteToggle);
        panel.Children.Add(_imagesToggle);
        panel.Children.Add(_limitBox);
        return panel;
    }

    private UIElement BuildData()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Header("数据"));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var preview = new Button { Content = "试一下提示效果" };
        preview.Click += (_, _) =>
        {
            // 动作之前先把下拉框的值提交掉：按钮点击早于可能发生的设置变更回调
            CommitEditors();
            _plugin.PreviewFeedback();
        };

        actions.Children.Add(preview);
        actions.Children.Add(_clearButton);
        actions.Children.Add(_stats);
        panel.Children.Add(actions);

        panel.Children.Add(new TextBlock
        {
            Text = $"历史保存在：{_plugin.StoragePath}\n（文字存在 history.json 里，图片另存为 PNG 缩略图缓存）",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(150),
        });

        return panel;
    }

    private static UIElement BuildAbout()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Header("说明"));
        panel.Children.Add(new TextBlock
        {
            Text = "插件只在系统剪贴板内容发生变化时读一次内容，用来写进本地历史；不上传、不联网。"
                   + "关掉「记录图片」后新复制进来的图片不会被记录（已有的图片缓存可以点「清空历史」一并删除）。\n\n"
                   + "「直接粘贴」是模拟一次 Ctrl+V：岛和聚光卡都不会抢走焦点，所以粘贴目标是点历史那一刻"
                   + "你正在输入的那个窗口。如果目标程序是以管理员身份运行的，Windows 会拦下模拟按键，"
                   + "这时插件只把内容写回剪贴板，自己按 Ctrl+V 即可。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hint(190),
        });
        return panel;
    }

    /// <summary>把还没提交的下拉框值写进设置（值没变就不写，避免多余的通知与刷新）。</summary>
    private void CommitEditors()
    {
        if (_loading) return;

        var limit = HistoryLimits[Math.Max(0, _limitBox.SelectedIndex)];
        if (limit != _settings.Get("limit", 20)) _settings.Set("limit", limit);

        var flash = FlashSeconds[Math.Max(0, _flashBox.SelectedIndex)];
        if (flash != _settings.Get("flash", 3)) _settings.Set("flash", flash);
    }

    private void OnClearClick()
    {
        if (!_confirmClear)
        {
            _confirmClear = true;
            _clearButton.Content = "再点一次确认清空";
            return;
        }

        _confirmClear = false;
        _clearButton.Content = "清空历史";

        var removed = _plugin.ClearHistory();
        _stats.Text = removed > 0 ? $"已清空 {removed} 条记录" : "历史本来就是空的";
    }

    private void OnHistoryChanged()
    {
        UpdateStats();
        _clearButton.IsEnabled = _plugin.HistoryCount > 0;
    }

    private void UpdateStats()
    {
        var count = _plugin.HistoryCount;
        var limit = _plugin.HistoryLimit;
        _stats.Text = count == 0
            ? $"当前 0 条（上限 {limit} 条）"
            : $"当前 {count} 条（上限 {limit} 条）";
        _clearButton.IsEnabled = count > 0;
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
