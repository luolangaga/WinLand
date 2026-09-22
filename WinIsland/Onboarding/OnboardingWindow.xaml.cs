using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using WinIsland.Core;
using WinIsland.Core.Marketplace;
using WinIsland.Core.Plugins;
using WinIsland.Island;
using WinIsland.Settings;

namespace WinIsland.Onboarding;

/// <summary>
/// 首次启动的新手引导：位置 / 外观材质 / 开机自启 / 推荐插件 / 系统托盘。
/// 所有选项即改即生效——直接写 SettingsService，运行中的岛会自行刷新，
/// 引导窗本身就是操作面板，实时预览就是引导的一部分。
/// </summary>
public sealed partial class OnboardingWindow : Window
{
    /// <summary>看过标记：窗口关闭（完成 / 跳过 / 点 X）时写入，之后不再自动弹出。</summary>
    public const string DoneSettingKey = "island.onboardingDone";

    private const string PositionKey = "island.position";
    private const string HorizontalKey = "island.horizontal";
    private const int RecommendCount = 4;

    private static readonly string[] StepTitles =
    {
        "欢迎使用",
        "调整岛位置",
        "挑选外观",
        "开机自启动",
        "推荐插件",
        "系统托盘与设置",
    };

    private static readonly string[] HorizontalValues = { "center", "left", "right" };

    private static readonly Windows.UI.Color AccentColor = Windows.UI.Color.FromArgb(255, 0, 122, 255);

    private readonly ISettingsStore _settings;
    private readonly MarketplaceService _marketplace;
    private readonly PluginHost _plugins;
    private readonly Action _openSettings;
    private readonly List<Border> _dots = new();
    private readonly Dictionary<string, Button> _installButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProgressBar> _progressBars = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    private ChoiceCard[] _positionCards = Array.Empty<ChoiceCard>();
    private ChoiceCard[] _horizontalCards = Array.Empty<ChoiceCard>();
    private ChoiceCard[] _styleCards = Array.Empty<ChoiceCard>();
    private ChoiceCard[] _materialCards = Array.Empty<ChoiceCard>();
    private ChoiceCard[] _autoStartCards = Array.Empty<ChoiceCard>();

    private bool _loading = true;
    private int _step;
    private bool _recommendLoaded;
    private bool _installing;
    private bool _autoStartBusy;
    /// <summary>当前实际生效的自启动模式（卡片回滚与状态文案都用它）。</summary>
    private AutoStartMode _autoStartApplied = AutoStartMode.Off;

    public OnboardingWindow(
        ISettingsStore settings,
        MarketplaceService marketplace,
        PluginHost plugins,
        Action openSettings)
    {
        _settings = settings;
        _marketplace = marketplace;
        _plugins = plugins;
        _openSettings = openSettings;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        ResizeLogical(760, 640);

        for (int i = 0; i < StepTitles.Length; i++)
        {
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4) };
            _dots.Add(dot);
            StepDots.Children.Add(dot);
        }

        BuildChoiceCards();
        WelcomeDiagram.Children.Add(ChoiceCard.ScreenDiagram(
            IsBottom(), HorizontalIndex(_settings.Get(HorizontalKey, "center"))));

        _autoStartApplied = AutoStartService.Current(
            AutoStartService.Parse(_settings.Get(AutoStartService.SettingKey, "off")));
        SelectCard(_autoStartCards, (int)_autoStartApplied);
        UpdateAutoStartStatus(null);

        _loading = false;
        ShowStep(0, animate: false);

        Closed += (_, _) =>
        {
            _cts.Cancel();
            _settings.Set(DoneSettingKey, true);
        };
    }

    private void ResizeLogical(double w, double h)
    {
        var scale = Core.Win32.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(w * scale), (int)(h * scale)));

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 2));
    }

    // ---- 步骤导航 ----

    /// <summary>
    /// 整页横向推入：前进时新页从右侧滑入、旧页整体向左滑出，后退时完全镜像。
    /// 两页位移曲线一致（同 From/To/时长/缓动）=> 始终像一页纸在滑动，不会出现两页互相穿插。
    /// </summary>
    private void ShowStep(int index, bool animate = true)
    {
        int direction = index >= _step ? 1 : -1;
        _step = index;

        var panels = new FrameworkElement[] { StepWelcome, StepPosition, StepAppearance, StepAutoStart, StepPlugins, StepTray };
        double width = StepHost.ActualWidth > 0 ? StepHost.ActualWidth : 696;

        for (int i = 0; i < panels.Length; i++)
        {
            var panel = panels[i];
            bool current = i == index;

            if (!animate)
            {
                ResetPanel(panel);
                panel.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
                continue;
            }

            if (current)
            {
                panel.Visibility = Visibility.Visible;
                AnimateSlide(panel, from: direction * width, to: 0, onCompleted: null);
            }
            else if (panel.Visibility == Visibility.Visible)
            {
                int panelIndex = i;
                AnimateSlide(panel, from: 0, to: -direction * width, onCompleted: () =>
                {
                    // 快速连点时，旧步骤的回调不能把「正在显示的步骤」收起来
                    if (_step != panelIndex)
                    {
                        panel.Visibility = Visibility.Collapsed;
                    }
                });
            }
            else
            {
                ResetPanel(panel);
            }
        }

        StepTitle.Text = StepTitles[index];
        StepCounter.Text = $"第 {index + 1} 步 / 共 {StepTitles.Length} 步";
        if (animate)
        {
            AnimateHeader(direction);
        }
        UpdateDots(index);

        BackButton.IsEnabled = index > 0;
        NextButton.Content = index == StepTitles.Length - 1 ? "完成" : "下一步";
        ContentScroll.ChangeView(null, 0, null, true);

        if (index == 4)
        {
            _ = LoadRecommendationsAsync();
        }
    }

    private void UpdateDots(int index)
    {
        for (int i = 0; i < _dots.Count; i++)
        {
            bool current = i == index;
            _dots[i].Width = current ? 20 : 8;
            _dots[i].Background = new SolidColorBrush(current
                ? AccentColor
                : i < index
                    ? Windows.UI.Color.FromArgb(0x66, 0x00, 0x7A, 0xFF)
                    : Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        }
    }

    private static void AnimateSlide(FrameworkElement panel, double from, double to, Action? onCompleted)
    {
        // 与岛展开动画同节奏（333ms）；基准值 = 终值 + FillBehavior.Stop，
        // 动画结束后不回弹、也不留下悬挂的动画覆盖后续对 X 的直接赋值
        const double durationMs = 333;

        var transform = new TranslateTransform { X = to };
        panel.RenderTransform = transform;

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            FillBehavior = FillBehavior.Stop,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");
        storyboard.Children.Add(animation);

        if (onCompleted != null)
        {
            storyboard.Completed += (_, _) => onCompleted();
        }

        storyboard.Begin();
    }

    /// <summary>标题、步骤计数与圆点同方向轻微滑入 + 淡入，跟页面推入保持一个方向感。</summary>
    private void AnimateHeader(int direction)
    {
        const double durationMs = 240;

        var transform = new TranslateTransform { X = 0 };
        StepHeader.RenderTransform = transform;

        var storyboard = new Storyboard();

        var slide = new DoubleAnimation
        {
            From = direction * 26,
            To = 0,
            FillBehavior = FillBehavior.Stop,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");
        storyboard.Children.Add(slide);

        var fade = new DoubleAnimation
        {
            From = 0.2,
            To = 1,
            FillBehavior = FillBehavior.Stop,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, StepHeader);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        storyboard.Begin();
    }

    private static void ResetPanel(FrameworkElement panel)
    {
        panel.Opacity = 1;
        if (panel.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
            transform.Y = 0;
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step >= StepTitles.Length - 1)
        {
            Close();
            return;
        }

        ShowStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            ShowStep(_step - 1);
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => _openSettings();

    // ---- 选择卡片：即改即生效 ----

    private void BuildChoiceCards()
    {
        var style = IslandStyle.ParseStyle(_settings.Get(IslandStyle.StyleKey, IslandStyle.AppleValue));
        var material = IslandStyle.ParseMaterial(_settings.Get(IslandStyle.MaterialKey, IslandStyle.AcrylicValue));
        var horizontal = _settings.Get(HorizontalKey, "center");
        bool bottom = IsBottom();

        _positionCards = new[]
        {
            ChoiceCard.Build("position.top", ChoiceCard.ScreenDiagram(bottom: false, HorizontalIndex(horizontal)),
                "屏幕顶部", "贴在工作区顶部，悬停展开", () => PickPosition(false)),
            ChoiceCard.Build("position.bottom", ChoiceCard.ScreenDiagram(bottom: true, HorizontalIndex(horizontal)),
                "屏幕底部（嵌入任务栏）", "嵌进任务栏，展开向岛体上方生长", () => PickPosition(true)),
        };
        ChoiceCard.FillRow(PositionCards, _positionCards);
        SelectCard(_positionCards, bottom ? 1 : 0);

        RebuildHorizontalCards(horizontal);

        _styleCards = new[]
        {
            ChoiceCard.Build("style.apple", ChoiceCard.StylePreview(IslandStyleKind.Apple),
                "Apple 灵动岛", "纯黑胶囊，经典灵动岛", () => PickStyle(IslandStyleKind.Apple)),
            ChoiceCard.Build("style.fluent", ChoiceCard.StylePreview(IslandStyleKind.Fluent),
                "Windows Fluent", "系统材质 + 更小圆角", () => PickStyle(IslandStyleKind.Fluent)),
        };
        ChoiceCard.FillRow(StyleCards, _styleCards);
        SelectCard(_styleCards, IslandStyle.IndexOf(style));

        _materialCards = new[]
        {
            ChoiceCard.Build("material.acrylic", ChoiceCard.MaterialPreview(IslandMaterialKind.Acrylic),
                "Desktop Acrylic", "磨砂玻璃，透出背后的内容", () => PickMaterial(IslandMaterialKind.Acrylic)),
            ChoiceCard.Build("material.mica", ChoiceCard.MaterialPreview(IslandMaterialKind.Mica),
                "Mica", "取壁纸色调，更内敛不透明", () => PickMaterial(IslandMaterialKind.Mica)),
        };
        ChoiceCard.FillRow(MaterialCards, _materialCards);
        SelectCard(_materialCards, IslandStyle.IndexOf(material));
        SetMaterialCardsEnabled(style == IslandStyleKind.Fluent);

        _autoStartCards = new[]
        {
            ChoiceCard.Build("autostart.off", null, "关闭自启动", "需要时手动打开", () => PickAutoStart(AutoStartMode.Off)),
            ChoiceCard.Build("autostart.user", null, "普通权限自启动", "登录后直接启动，不弹 UAC", () => PickAutoStart(AutoStartMode.User)),
            ChoiceCard.Build("autostart.admin", null, "管理员权限自启动", "登录后以管理员身份静默启动（需一次 UAC 授权）", () => PickAutoStart(AutoStartMode.Admin)),
        };
        ChoiceCard.FillRow(AutoStartCards, _autoStartCards);
    }

    private void PickPosition(bool bottom)
    {
        if (_loading) return;
        _settings.Set(PositionKey, bottom ? "bottom" : "top");
        SelectCard(_positionCards, bottom ? 1 : 0);
        RebuildHorizontalCards(_settings.Get(HorizontalKey, "center"));
    }

    private void PickHorizontal(string value)
    {
        if (_loading) return;
        _settings.Set(HorizontalKey, value);
        SelectCard(_horizontalCards, Math.Max(0, Array.IndexOf(HorizontalValues, value)));
    }

    private void PickStyle(IslandStyleKind style)
    {
        if (_loading) return;
        _settings.Set(IslandStyle.StyleKey, style.ToValue());
        SelectCard(_styleCards, IslandStyle.IndexOf(style));
        SetMaterialCardsEnabled(style == IslandStyleKind.Fluent);
    }

    private void PickMaterial(IslandMaterialKind material)
    {
        if (_loading) return;
        _settings.Set(IslandStyle.MaterialKey, material.ToValue());
        SelectCard(_materialCards, IslandStyle.IndexOf(material));
    }

    private async void PickAutoStart(AutoStartMode mode)
    {
        if (_loading || _autoStartBusy) return;
        if (mode == _autoStartApplied)
        {
            SelectCard(_autoStartCards, (int)_autoStartApplied);
            UpdateAutoStartStatus(null);
            return;
        }

        // 管理员模式会弹 UAC 并等待系统命令：必须放到后台线程，绝不能在 UI 线程上同步等待，
        // 否则整个应用（含岛与所有窗口）会在用户响应 UAC 之前被冻结
        _autoStartBusy = true;
        SetAutoStartCardsEnabled(false);
        UpdateAutoStartStatus(null, busy: true);

        var (success, error) = await AutoStartService.TryApplyAsync(mode);

        _autoStartBusy = false;
        SetAutoStartCardsEnabled(true);

        if (!success)
        {
            // 失败：选中态退回实际生效值，别让界面说谎
            SelectCard(_autoStartCards, (int)_autoStartApplied);
            UpdateAutoStartStatus(error);
            return;
        }

        _autoStartApplied = mode;
        _settings.Set(AutoStartService.SettingKey, AutoStartService.ToValue(mode));
        SelectCard(_autoStartCards, (int)mode);
        UpdateAutoStartStatus(null);
    }

    /// <summary>水平对齐示意图随位置模式重画：岛出现在顶部还是任务栏条里，跟随当前选择。</summary>
    private void RebuildHorizontalCards(string selected)
    {
        bool bottom = IsBottom();
        _horizontalCards = HorizontalValues
            .Select((value, index) => ChoiceCard.Build(
                $"horizontal.{value}",
                ChoiceCard.ScreenDiagram(bottom, index),
                value switch { "left" => "靠左", "right" => "靠右", _ => "居中" },
                null,
                () => PickHorizontal(value)))
            .ToArray();
        ChoiceCard.FillRow(HorizontalCards, _horizontalCards);
        SelectCard(_horizontalCards, Math.Max(0, Array.IndexOf(HorizontalValues, selected)));
    }

    private void SetMaterialCardsEnabled(bool enabled)
    {
        foreach (var card in _materialCards) card.IsEnabled = enabled;
    }

    private void SetAutoStartCardsEnabled(bool enabled)
    {
        foreach (var card in _autoStartCards) card.IsEnabled = enabled;
    }

    private static void SelectCard(ChoiceCard[] cards, int index)
    {
        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].IsSelected = i == index;
        }
    }

    private static int HorizontalIndex(string value) => value switch
    {
        "left" => 1,
        "right" => 2,
        _ => 0,
    };

    private bool IsBottom()
        => string.Equals(_settings.Get(PositionKey, "top"), "bottom", StringComparison.OrdinalIgnoreCase);

    private void UpdateAutoStartStatus(string? error, bool busy = false)
    {
        if (busy)
        {
            AutoStartStatus.Text = "正在等待管理员授权，请在 UAC 弹窗里确认…";
            return;
        }

        if (!string.IsNullOrEmpty(error))
        {
            AutoStartStatus.Text = $"设置失败：{error}";
            return;
        }

        AutoStartStatus.Text = _autoStartApplied switch
        {
            AutoStartMode.User => "已开启：登录后自动启动",
            AutoStartMode.Admin => "已开启：登录后以管理员权限自动启动（计划任务 WinIsland）",
            _ => "当前未开启",
        };
    }

    // ---- 推荐插件 ----

    private async Task LoadRecommendationsAsync()
    {
        if (_recommendLoaded) return;
        _recommendLoaded = true;

        RecommendList.Children.Add(new TextBlock
        {
            Text = "正在获取插件列表…",
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush", 0x90, 0x90, 0x95),
        });

        try
        {
            var index = await _marketplace.GetIndexAsync(force: false, _cts.Token);
            var picks = index.Plugins.Take(RecommendCount).ToList();

            RecommendList.Children.Clear();
            if (picks.Count == 0)
            {
                RecommendList.Children.Add(new TextBlock
                {
                    Text = "插件市场暂时还没有插件，稍后再来看看吧。",
                    FontSize = 12,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush", 0x90, 0x90, 0x95),
                });
                return;
            }

            foreach (var plugin in picks)
            {
                RecommendList.Children.Add(BuildPluginCard(plugin));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RecommendList.Children.Clear();
            ShowPluginStatus(InfoBarSeverity.Warning, "无法连接插件市场",
                $"可以稍后在 设置 → 插件市场 里浏览安装。（{ex.Message}）");
        }
    }

    private Border BuildPluginCard(MarketplacePlugin plugin)
    {
        var root = new Grid { ColumnSpacing = 14, Padding = new Thickness(4) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        root.Children.Add(BuildLogo(plugin));

        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(info, 1);

        // 名称、版本用 Grid 的星号列约束，横向 StackPanel 会被长名称撑出卡片
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        var versionBadge = BuildBadge($"v{plugin.Version}", 0x00, 0x7A, 0xFF);
        Grid.SetColumn(versionBadge, 1);
        titleRow.Children.Add(versionBadge);
        info.Children.Add(titleRow);

        if (!string.IsNullOrEmpty(plugin.Description))
        {
            info.Children.Add(new TextBlock
            {
                Text = plugin.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeBrush("TextFillColorTertiaryBrush", 0x80, 0x80, 0x85),
            });
        }

        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 3,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        info.Children.Add(progress);
        _progressBars[plugin.Id] = progress;

        root.Children.Add(info);

        var button = new Button { MinWidth = 96, VerticalAlignment = VerticalAlignment.Center };
        if (IsInstalled(plugin.Id))
        {
            button.Content = "已安装";
            button.IsEnabled = false;
            ToolTipService.SetToolTip(button, "启用 / 禁用请到 设置 → 插件管理");
        }
        else
        {
            button.Content = "安装";
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.Click += async (_, _) => await InstallAsync(plugin);
        }

        _installButtons[plugin.Id] = button;
        Grid.SetColumn(button, 2);
        root.Children.Add(button);

        return new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = root,
        };
    }

    private Border BuildLogo(MarketplacePlugin plugin)
    {
        var border = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush("ControlFillColorSecondaryBrush", 0x1A, 0x1A, 0x1A),
        };

        if (plugin.HasLogo)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Width = 48, Height = 48 };
            border.Child = image;
            _ = ApplyLogoAsync(image, plugin);
        }
        else
        {
            border.Background = new SolidColorBrush(AccentColor);
            border.Child = new FontIcon
            {
                Glyph = string.IsNullOrEmpty(plugin.IconGlyph) ? "\uE7B8" : plugin.IconGlyph,
                FontSize = 20,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return border;
    }

    private async Task ApplyLogoAsync(Image target, MarketplacePlugin plugin)
    {
        try
        {
            var dataUri = plugin.LogoData!;
            var comma = dataUri.IndexOf(',');
            var payload = comma >= 0 ? dataUri[(comma + 1)..] : dataUri;
            var bytes = Convert.FromBase64String(payload.Trim());

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
        }
        catch
        {
            // 图标解码失败就保留灰色底衬，不打扰用户
        }
    }

    private bool IsInstalled(string pluginId)
        => _plugins.Plugins.Any(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    private async Task InstallAsync(MarketplacePlugin plugin)
    {
        if (_installing) return;

        if (!plugin.IsCompatible(out var reason))
        {
            ShowPluginStatus(InfoBarSeverity.Warning, "无法安装", reason ?? "该插件与当前宿主不兼容");
            return;
        }

        _installing = true;
        SetCardBusy(plugin.Id, true);
        ShowPluginStatus(InfoBarSeverity.Informational, "正在安装", $"正在下载 {plugin.Name} {plugin.Version}…");

        try
        {
            var progress = new Progress<double>(value =>
            {
                if (_progressBars.TryGetValue(plugin.Id, out var bar))
                {
                    bar.Value = Math.Clamp(value, 0, 1) * 100;
                }
            });

            var packagePath = await _marketplace.DownloadPackageAsync(plugin, progress, _cts.Token);
            ShowPluginStatus(InfoBarSeverity.Informational, "正在安装", $"已下载并校验通过，正在安装 {plugin.Name}…");

            var result = await _plugins.InstallAsync(packagePath);
            if (result.Success && result.Manifest != null)
            {
                var version = result.IsUpdate ? $"{result.OldVersion} → {result.Manifest.Version}" : result.Manifest.Version;
                var message = $"{result.Manifest.Name} {version}";
                if (result.Warnings.Count > 0)
                {
                    message += Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);
                }

                ShowPluginStatus(InfoBarSeverity.Success, result.IsUpdate ? "插件已更新" : "插件已安装", message);
                MarkInstalled(plugin.Id);
            }
            else
            {
                ShowPluginStatus(InfoBarSeverity.Error, "安装失败", result.Error ?? "未知错误");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowPluginStatus(InfoBarSeverity.Error, "安装失败", ex.Message);
        }
        finally
        {
            _installing = false;
            SetCardBusy(plugin.Id, false);
        }
    }

    private void MarkInstalled(string pluginId)
    {
        if (!_installButtons.TryGetValue(pluginId, out var button)) return;

        button.Content = "已安装";
        button.IsEnabled = false;
        ToolTipService.SetToolTip(button, "启用 / 禁用请到 设置 → 插件管理");
    }

    private void SetCardBusy(string pluginId, bool busy)
    {
        if (_progressBars.TryGetValue(pluginId, out var bar))
        {
            bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) bar.Value = 0;
        }

        if (_installButtons.TryGetValue(pluginId, out var button) && button.Content as string == "安装")
        {
            button.IsEnabled = !busy;
        }
    }

    private void ShowPluginStatus(InfoBarSeverity severity, string title, string message)
    {
        PluginStatus.Severity = severity;
        PluginStatus.Title = title;
        PluginStatus.Message = message;
        PluginStatus.IsOpen = true;
    }

    // ---- 辅助 ----

    private static Border BuildBadge(string text, byte r, byte g, byte b) => new()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, r, g, b)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)),
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static Brush ThemeBrush(string key, byte r, byte g, byte b)
        => Application.Current.Resources[key] as Brush
           ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
}
