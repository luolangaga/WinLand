using HelloLib;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace HelloPlugin;

public sealed class HelloPlugin : IslandPluginBase
{
    private HelloModel _model = null!;
    private IslandLiveContent? _content;
    private TextBlock? _compactText;
    private TextBlock? _expandedText;
    private bool _enabled;
    private int _ticks;

    protected override Task OnInitializeAsync()
    {
        _enabled = Settings.Get("enabled", true);
        _model = new HelloModel();
        Log.Info($"初始化开始：{_model.Describe()}；插件目录 = {PluginDirectory}；宿主版本 = {HostVersion}");

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            "hello", Manifest.Name, Manifest.IconGlyph ?? "\uE8BD", BuildSettingsPage, 200));

        _content = BuildContent();
        if (_enabled)
        {
            SetContent(_content);
        }

        Context.CreateTimer(TimeSpan.FromSeconds(10), repeat: true, OnTick);
        Context.OnSettingsChanged("enabled", () =>
        {
            _enabled = Settings.Get("enabled", true);
            SetContent(_enabled ? _content : null);
        });
        // 岛体换主题（Fluent 跟随系统明暗）：代码里搭的视图颜色是烘进画刷的，重建一份最省事。
        // XAML 视图不用这么做 —— 里面的 {ThemeResource ...} 自己会跟着换。
        Context.Theme.Changed += OnThemeChanged;
        Context.Register(new ActionDisposable(() => Context.Theme.Changed -= OnThemeChanged));
        Context.Register(new ActionDisposable(() => Log.Info("注册项已全部撤销（scope 清理完成）")));

        Log.Info("初始化完成，常驻内容已注册");
        return Task.CompletedTask;
    }

    private void OnThemeChanged()
    {
        Log.Info($"岛体主题已切换为{(Theme.IsLight ? "浅色" : "深色")}，重建岛体内容。");
        _content = BuildContent();
        if (_enabled)
        {
            SetContent(_content);
        }
    }

    /// <summary>
    /// 岛体内容的中性文字色：Fluent + 浅色系统下岛体是浅底，写死白色就是白字压白底。
    /// 设置页不走这里 —— 设置窗有自己的主题，那边用应用资源里的系统画刷。
    /// </summary>
    private SolidColorBrush IslandText(byte alpha) => new(Theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255));

    protected override Task OnShutdownAsync()
    {
        Log.Info($"收到停用通知（累计心跳 {_ticks} 次）");
        return Task.CompletedTask;
    }

    private void OnTick()
    {
        if (Settings.Get("boom", false))
        {
            Settings.Set("boom", false);
            throw new InvalidOperationException("来自 Hello 插件的受管测试异常（定时器回调内抛出）");
        }

        _ticks++;
        Log.Info($"心跳 #{_ticks}");
        if (_compactText != null) _compactText.Text = $"运行中 · {_ticks} 次心跳";
        if (_expandedText != null) _expandedText.Text = $"已运行 {_ticks} 个心跳周期";
    }

    private IslandLiveContent BuildContent()
    {
        _compactText = NewText($"运行中 · {_ticks} 次心跳");
        _expandedText = NewText($"已运行 {_ticks} 个心跳周期");

        var icon = new FontIcon
        {
            Glyph = Manifest.IconGlyph ?? "\uE8BD",
            FontSize = 16,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 194, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var compact = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Padding = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        compact.Children.Add(icon);
        compact.Children.Add(_compactText);

        var expanded = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(18, 14, 18, 14),
        };
        expanded.Children.Add(new TextBlock
        {
            Text = Manifest.Name,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = IslandText(255),
        });
        expanded.Children.Add(new TextBlock
        {
            Text = $"示例插件 · {Manifest.Version} · 私有依赖 {_model.Describe()}",
            FontSize = 12,
            Foreground = IslandText(200),
            TextWrapping = TextWrapping.Wrap,
        });
        expanded.Children.Add(_expandedText);
        expanded.Children.Add(new TextBlock
        {
            Text = "禁用后：内容清空、设置页移除、定时器停止、程序集尝试回收（见日志）",
            FontSize = 12,
            Foreground = IslandText(160),
            TextWrapping = TextWrapping.Wrap,
        });

        return new IslandLiveContent
        {
            Priority = Settings.Get("priority", 10),
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Windows.UI.Color.FromArgb(255, 76, 194, 255),
            CompactContent = compact,
            ExpandedContent = expanded,
            CompactSize = new Windows.Foundation.Size(240, 40),
            ExpandedSize = new Windows.Foundation.Size(400, 118),
        };
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
            Text = $"Id：{Manifest.Id} · 版本 {Manifest.Version} · 目录：{PluginDirectory}",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = PageHint(),
        });

        var toggle = new ToggleSwitch
        {
            Header = "启用常驻内容",
            IsOn = Settings.Get("enabled", true),
        };
        toggle.Toggled += (_, _) => Settings.Set("enabled", toggle.IsOn);
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = toggle,
        });

        var logButton = new Button { Content = "写一条插件日志" };
        logButton.Click += (_, _) => Log.Info($"来自设置页的日志（初始化后已心跳 {_ticks} 次）");
        panel.Children.Add(logButton);

        var boomButton = new Button { Content = "触发一次受管异常" };
        boomButton.Click += (_, _) =>
        {
            Settings.Set("boom", true);
            Log.Warn("已请求在下一次心跳时抛出受管异常。");
        };
        panel.Children.Add(boomButton);

        panel.Children.Add(new TextBlock
        {
            Text = "受管异常会在下一次心跳（最多 10 秒后）由宿主捕获，记入日志并计数；累计 5 次会自动停用插件。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = PageHint(),
        });

        return panel;
    }

    /// <summary>设置页的次级文字：设置窗跟随应用主题，取系统画刷（写死白色在浅色窗里看不见）。</summary>
    private static Brush PageHint() => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private TextBlock NewText(string text) => new()
    {
        Text = text,
        FontSize = 13,
        Foreground = IslandText(255),
        VerticalAlignment = VerticalAlignment.Center,
    };
}