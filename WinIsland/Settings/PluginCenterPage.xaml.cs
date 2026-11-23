using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;
using WinIsland.Core.Marketplace;
using WinIsland.Core.Plugins;
using WinIsland.Island;

namespace WinIsland.Settings;

/// <summary>One catalog, search and operation gate for community and installed plugins.</summary>
public sealed partial class PluginCenterPage : UserControl
{
    private readonly SettingsService _settings;
    private bool _managing;
    private bool _attached;
    private bool _ready;
    private bool IsBusy => _installing || _managing;
    private double _cardWidth = 300;
    private int _cardOrder;
    private readonly TextBox BaseUrlBox = new() { Header = "商店源地址", PlaceholderText = "留空以自动选择官方源和镜像" };
    private readonly TextBlock BaseUrlHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button ResetUrlButton = new() { Content = "恢复默认" };

    public PluginCenterPage(MarketplaceService market, PluginHost plugins, SettingsService settings)
    {
        _market = market;
        _plugins = plugins;
        _settings = settings;
        InitializeComponent();
        BaseUrlBox.Text = market.ConfiguredBaseUrl ?? "";
        // Apply explicitly: losing focus to Cancel must not change the source.
        ResetUrlButton.Click += ResetUrl_Click;
        AutomationProperties.SetAutomationId(BaseUrlBox, "BaseUrlBox");
        AutomationProperties.SetAutomationId(ResetUrlButton, "ResetUrlButton");
        _ready = true;
        Views.SelectedItem = DiscoverTab;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;
        _plugins.PluginsChanged += OnPluginsChanged;
        Rebuild();
        Motion.Reveal(Hero, 420, 12);
        Motion.Reveal(Views, 320, 6);
        _ = LoadAsync(false);
    }

    internal void ShowInstalled() => Views.SelectedItem = InstalledTab;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _attached = false;
        _plugins.PluginsChanged -= OnPluginsChanged;
        _loadCts?.Cancel();
        _installCts?.Cancel();
    }

    private void OnPluginsChanged()
    {
        if (!_attached) return;
        if (DispatcherQueue.HasThreadAccess) Rebuild();
        else DispatcherQueue.TryEnqueue(() => { if (_attached) Rebuild(); });
    }

    private async Task LoadAsync(bool force)
    {
        if (_loading) return;
        _loading = true;
        Busy.IsActive = true;
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        try
        {
            _index = await _market.GetIndexAsync(force, _loadCts.Token);
            if (_market.LastIndexFromCache && !string.IsNullOrEmpty(_market.LastIndexNote))
                ShowStatus("正在显示缓存", _market.LastIndexNote ?? "连接恢复后可刷新商店。", InfoBarSeverity.Warning);
            else if (_market.IsMirrorActive)
                ShowStatus("已连接镜像", $"当前来源：{_market.ActiveSourceLabel}", InfoBarSeverity.Informational);
            else StatusBar.IsOpen = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowStatus("商店暂时无法连接", $"{ex.Message}。你仍可管理已安装插件或安装本地插件包。", InfoBarSeverity.Warning);
        }
        finally
        {
            _loading = false;
            _loadCts?.Dispose();
            _loadCts = null;
            Busy.IsActive = false;
            Rebuild();
        }
    }

    private void Rebuild()
    {
        if (!_ready) return;
        _progressBars.Clear();
        _cardOrder = 0;
        var local = _plugins.Plugins.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var remote = (_index?.Plugins ?? []).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        int updates = remote.Count(p => GetStatus(p, out _, out _) == MarketStatus.UpdateAvailable);
        SummaryText.Text = $"{local.Count} 个已安装   ·   {local.Values.Count(p => p.State == PluginState.Active)} 个运行中" + (_index == null ? "   ·   更新待检查" : $"   ·   {updates} 个可更新");
        InstalledTab.Text = $"已安装  {local.Count}";
        UpdatesTab.Text = _index == null ? "可更新" : $"可更新  {updates}";
        WarningBar.Message = string.Join(Environment.NewLine, _plugins.DiscoveryWarnings);
        WarningBar.IsOpen = _plugins.DiscoveryWarnings.Count > 0;

        var cards = new List<Border>();
        if (Views.SelectedItem == InstalledTab)
        {
            foreach (var plugin in local.Values.OrderByDescending(p => p.State == PluginState.Faulted).ThenBy(p => p.Name))
            {
                var market = remote.FirstOrDefault(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
                if (MatchesLocal(plugin) || market != null && Matches(market, _filter))
                    cards.Add(BuildUnifiedCard(market, plugin));
            }
        }
        else
        {
            foreach (var plugin in remote)
            {
                if (!Matches(plugin, _filter)) continue;
                if (Views.SelectedItem == UpdatesTab && GetStatus(plugin, out _, out _) != MarketStatus.UpdateAvailable) continue;
                local.TryGetValue(plugin.Id, out var installed);
                cards.Add(BuildUnifiedCard(plugin, installed));
            }
        }

        PluginList.ItemsSource = cards;
        EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool searching = !string.IsNullOrWhiteSpace(_filter);
        EmptyText.Text = searching ? "没有找到相关插件" : Views.SelectedItem == UpdatesTab ? (_index == null ? "尚未检查更新" : "所有插件都已是最新") : Views.SelectedItem == InstalledTab ? "从第一个插件开始" : _loading ? "正在连接社区…" : "暂时没有可浏览的插件";
        EmptyHint.Text = searching ? "换一个名称、作者或标签试试。" : Views.SelectedItem == UpdatesTab ? (_index == null ? "连接商店后即可检查插件更新。" : "有新版本时，它们会出现在这里。") : "刷新商店，或使用「本地安装」添加 .lwp 插件包。";
        ResultText.Text = $"{cards.Count} 个插件" + (Views.SelectedItem == InstalledTab ? " · 启停与顺序即时生效" : $" · {_market.ActiveSourceLabel}" + (_market.LastIndexFromCache ? " · 本地缓存" : ""));
        if (_market.LastIndexTime is { } time) ToolTipService.SetToolTip(ResultText, $"清单更新于 {DescribeTime(time)}");
        UpdateBusyState();
    }

    private bool MatchesLocal(PluginInfo plugin) => string.IsNullOrWhiteSpace(_filter)
        || new[] { plugin.Name, plugin.Id, plugin.Author, plugin.Description }.Any(value => Contains(value, _filter.Trim()));

    private Border BuildUnifiedCard(MarketplacePlugin? market, PluginInfo? installed)
    {
        string id = installed?.Id ?? market!.Id;
        string name = installed?.Name ?? market!.Name;
        var body = new Grid { RowSpacing = 16 };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(market != null ? BuildLogo(market) : BuildLocalLogo(installed!));
        var titles = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTipService.SetToolTip(titles, name);
        titles.Children.Add(new TextBlock { Text = $"v{installed?.Version ?? market?.Version} · {installed?.Author ?? market?.Author ?? "社区开发者"}", FontSize = 11, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Resources["SecondaryText"] });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        body.Children.Add(head);

        var info = new StackPanel { Spacing = 8 };
        info.Children.Add(new TextBlock { Text = installed?.Description ?? market?.Description ?? "为你的灵动岛扩展更多可能。", FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Resources["SecondaryText"] });
        string state = installed != null ? (installed.IsBuiltIn ? "内置 · " : "") + PluginStateText.Describe(installed.State) : "社区插件";
        bool update = market != null && GetStatus(market, out _, out _) == MarketStatus.UpdateAvailable;
        var badge = new TextBlock { Text = update ? $"新版本 v{market!.Version}" : state, FontSize = 11 };
        badge.Style = (Style)Resources[update ? "AccentText" : installed?.State == PluginState.Faulted ? "ErrorText" : "SecondaryText"];
        info.Children.Add(badge);
        if (market != null)
        {
            var progress = new ProgressBar { Height = 3, Maximum = 100, Visibility = Visibility.Collapsed };
            _progressBars[id] = progress;
            info.Children.Add(progress);
        }
        Grid.SetRow(info, 1);
        body.Children.Add(info);

        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var primary = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(primary, $"Plugin.{id}.Primary");
        if (installed == null || update)
        {
            bool compatible = market!.IsCompatible(out var reason);
            primary.Content = compatible ? update ? "更新" : "安装" : "不兼容";
            primary.IsEnabled = compatible;
            primary.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            ToolTipService.SetToolTip(primary, reason ?? $"下载并安装 {name}");
            primary.Click += async (_, _) => await InstallAsync(market);
            actions.Children.Add(primary);
        }
        else
        {
            var toggle = new ToggleSwitch { IsOn = installed.State == PluginState.Active, OnContent = "运行中", OffContent = "已停用", MinWidth = 0, IsEnabled = installed.Manifest != null };
            AutomationProperties.SetName(toggle, $"启用 {name}");
            AutomationProperties.SetAutomationId(toggle, $"Plugin.{id}.Enabled");
            toggle.Toggled += async (_, _) => await RunOperationAsync(() => _plugins.SetEnabledAsync(id, toggle.IsOn), toggle.IsOn ? "启用" : "停用", installed);
            actions.Children.Add(toggle);
        }
        var details = new Button { Content = installed != null ? "管理" : "详情", Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        AutomationProperties.SetAutomationId(details, $"Plugin.{id}.Details");
        details.Click += async (_, _) =>
        {
            if (IsBusy) return;
            if (installed != null) await ShowManagementAsync(installed, market, (FrameworkElement)head.Children[0]);
            else await ShowDetailsAsync(market!, GetStatus(market!, out var current, out _), current);
        };
        Grid.SetColumn(details, 1);
        actions.Children.Add(details);
        Grid.SetRow(actions, 2);
        body.Children.Add(actions);
        var card = new Border { Width = _cardWidth, Height = 226, Child = body, Style = (Style)Resources["PluginCard"] };
        WireCardMotion(card, _cardOrder++);
        return card;
    }

    private Border BuildLocalLogo(PluginInfo plugin)
    {
        var icon = new FontIcon { Glyph = string.IsNullOrEmpty(plugin.IconGlyph) ? "\uE7B8" : plugin.IconGlyph, FontSize = 22 };
        icon.Style = (Style)Resources["LocalIcon"];
        var logo = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(16), Child = icon, Style = (Style)Resources["LocalLogo"] };
        return logo;
    }

    private static void WireCardMotion(Border card, int order)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        card.Loaded += (_, _) =>
        {
            if (!Motion.Enabled) return;
            var visual = Motion.Visual(card);
            visual.Opacity = 0;
            visual.Properties.InsertVector3("Translation", new Vector3(0, 16, 0));
            using var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, Motion.Ease(visual.Compositor));
            fade.Duration = TimeSpan.FromMilliseconds(320);
            fade.DelayTime = TimeSpan.FromMilliseconds(Math.Min(order, 5) * 32);
            using var rise = visual.Compositor.CreateVector3KeyFrameAnimation();
            rise.InsertKeyFrame(0, new Vector3(0, 16, 0));
            rise.InsertKeyFrame(1, Vector3.Zero, Motion.Ease(visual.Compositor));
            rise.Duration = TimeSpan.FromMilliseconds(380);
            rise.DelayTime = fade.DelayTime;
            visual.StartAnimation("Opacity", fade);
            visual.StartAnimation("Translation", rise);
        };
        card.PointerEntered += (_, _) => { if (Motion.Enabled) Motion.Vector(Motion.Visual(card), "Translation", new Vector3(0, -3, 0), 180); };
        card.PointerExited += (_, _) => Motion.Vector(Motion.Visual(card), "Translation", Vector3.Zero, 220);
        card.PointerCanceled += (_, _) => Motion.Vector(Motion.Visual(card), "Translation", Vector3.Zero, 160);
        card.Unloaded += (_, _) => { var visual = Motion.Visual(card); visual.StopAnimation("Translation"); visual.StopAnimation("Opacity"); visual.Opacity = 1; visual.Properties.InsertVector3("Translation", Vector3.Zero); };
    }

    private void Views_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_ready) return;
        Rebuild();
        Motion.Reveal(PluginList, 260, 8);
    }

    private void PluginList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(100, e.NewSize.Width - 16);
        int columns = width >= 880 ? 3 : width >= 560 ? 2 : 1;
        _cardWidth = Math.Max(100, width / columns - 12);
        if (PluginList.ItemsSource is IEnumerable<Border> cards)
            foreach (var card in cards) card.Width = _cardWidth;
        HeroArt.Visibility = e.NewSize.Width < 480 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateBusyState()
    {
        PluginList.IsEnabled = !IsBusy;
        InstallButton.IsEnabled = !IsBusy;
        SearchBox.IsEnabled = !IsBusy;
        Views.IsEnabled = !IsBusy;
    }

    private async void Source_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _loading) return;
        UpdateBaseUrlUi();
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(BaseUrlBox);
        panel.Children.Add(BaseUrlHint);
        panel.Children.Add(ResetUrlButton);
        var dialog = new ContentDialog { RequestedTheme = ActualTheme, XamlRoot = XamlRoot, Title = "商店源设置", Content = panel, PrimaryButtonText = "应用并刷新", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        var result = await dialog.ShowAsync();
        panel.Children.Clear();
        if (result == ContentDialogResult.Primary) ApplyBaseUrl();
        else BaseUrlBox.Text = _market.ConfiguredBaseUrl ?? "";
    }

    private async Task ShowManagementAsync(PluginInfo plugin, MarketplacePlugin? market, FrameworkElement? sourceLogo = null)
    {
        var panel = new StackPanel { Spacing = 16 };
        var logo = market != null ? BuildLogo(market) : BuildLocalLogo(plugin);
        logo.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(logo);
        panel.Children.Add(new TextBlock { Text = plugin.Description ?? plugin.Id, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"v{plugin.Version} · {PluginStateText.Describe(plugin.State)}", Style = (Style)Resources["SecondaryText"] });
        if (plugin.LastError is { } error)
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = error.Phase, Message = error.Describe() });
        panel.Children.Add(new TextBlock { Text = "岛上显示顺序", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "数值越大越靠前；清空恢复插件默认顺序。", TextWrapping = TextWrapping.Wrap, Style = (Style)Resources["SecondaryText"] });
        panel.Children.Add(BuildPriorityBox(plugin));
        var commands = new StackPanel { Spacing = 8 };
        string? command = null;
        var dialog = new ContentDialog { RequestedTheme = ActualTheme, XamlRoot = XamlRoot, Title = plugin.Name, CloseButtonText = "完成", Content = new ScrollViewer { MaxHeight = 480, Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        if (Motion.Enabled && sourceLogo != null)
        {
            var animations = Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView();
            animations.PrepareToAnimate("PluginCenter.Logo", sourceLogo);
            dialog.Opened += (_, _) => animations.GetAnimation("PluginCenter.Logo")?.TryStart(logo);
        }
        void AddCommand(string label, string key)
        {
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(button, $"Plugin.{plugin.Id}.{key}");
            button.Click += (_, _) => { command = key; dialog.Hide(); };
            commands.Children.Add(button);
        }
        foreach (var page in _plugins.Island.SettingsPages.Where(p => p.Id.StartsWith(plugin.Id + "/", StringComparison.OrdinalIgnoreCase)))
            AddCommand($"打开设置 · {page.Title}", "Settings:" + page.Id);
        AddCommand("重新加载插件", "Reload");
        AddCommand("查看运行日志", "Logs");
        if (market != null) AddCommand("社区说明与版本详情", "Readme");
        if (!plugin.IsBuiltIn) { AddCommand("打开插件目录", "Folder"); AddCommand("卸载插件…", "Uninstall"); }
        panel.Children.Add(commands);
        await dialog.ShowAsync();
        if (command?.StartsWith("Settings:", StringComparison.Ordinal) == true)
        {
            ((App)Application.Current)._settingsWindow?.SelectPage(command[9..]);
            return;
        }
        switch (command)
        {
            case "Reload": await RunOperationAsync(() => _plugins.ReloadAsync(plugin.Id), "重新加载", plugin); break;
            case "Logs": await ShowLogsAsync(plugin); break;
            case "Readme": await ShowDetailsAsync(market!, GetStatus(market!, out var installed, out _), installed); break;
            case "Folder": RevealInExplorer(plugin.Source); break;
            case "Uninstall": await DeleteAsync(plugin); break;
        }
    }
}
