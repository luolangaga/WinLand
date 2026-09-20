using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using WinIsland.Core;
using WinIsland.Core.Marketplace;
using WinIsland.Core.Plugins;

namespace WinIsland.Settings;

/// <summary>
/// 插件市场：列表只来自社区仓库的 index.json（一次请求，图标内嵌），
/// 安装时才下载 .lwp 并校验 SHA-256，然后交给 <see cref="PluginHost"/> 安装/更新。
/// </summary>
public sealed partial class MarketplacePage : UserControl
{
    private readonly MarketplaceService _market;
    private readonly PluginHost _plugins;
    private readonly Dictionary<string, ImageSource?> _logos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<ImageSource?>> _logoJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProgressBar> _progressBars = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _installCts;
    private MarketplaceIndex? _index;
    private bool _loading;
    private bool _installing;
    private string _filter = "";

    public MarketplacePage(MarketplaceService market, PluginHost plugins)
    {
        _market = market;
        _plugins = plugins;
        InitializeComponent();

        BaseUrlBox.Text = market.ConfiguredBaseUrl ?? "";
        UpdateBaseUrlUi();

        _plugins.PluginsChanged += OnPluginsChanged;
        Unloaded += (_, _) =>
        {
            _plugins.PluginsChanged -= OnPluginsChanged;
            _loadCts?.Cancel();
            _installCts?.Cancel();
        };

        _ = LoadAsync(force: false);
    }

    private void OnPluginsChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            Rebuild();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Rebuild);
        }
    }

    private async Task LoadAsync(bool force)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        Busy.IsActive = true;
        RefreshButton.IsEnabled = false;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();

        try
        {
            var index = await _market.GetIndexAsync(force, _loadCts.Token);
            _index = index;
            Rebuild();

            if (_market.LastIndexFromCache && force)
            {
                ShowStatus("已使用本地缓存", $"无法连接市场源，显示的是 {DescribeTime(_market.LastIndexTime)} 的缓存清单。", InfoBarSeverity.Warning);
            }
            else if (_market.IsMirrorActive)
            {
                ShowStatus("已切换到镜像源", $"官方 GitHub 源不可用，当前使用 {_market.ActiveSourceLabel}（内容一致，镜像同步可能略有延迟）。", InfoBarSeverity.Informational);
            }
            else
            {
                StatusBar.IsOpen = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowStatus("无法获取插件列表", ex.Message, InfoBarSeverity.Error);
            if (_index == null)
            {
                PluginList.Children.Clear();
            }
        }
        finally
        {
            _loading = false;
            Busy.IsActive = false;
            RefreshButton.IsEnabled = true;
            UpdateUpdatedText();
        }
    }

    private void Rebuild()
    {
        _progressBars.Clear();
        PluginList.Children.Clear();

        var plugins = _index?.Plugins ?? new List<MarketplacePlugin>();
        var shown = 0;

        foreach (var plugin in plugins)
        {
            if (!Matches(plugin, _filter))
            {
                continue;
            }

            PluginList.Children.Add(BuildCard(plugin));
            shown++;
        }

        PluginList.IsHitTestVisible = !_installing;
        EmptyText.Text = plugins.Count == 0 ? "市场里还没有插件" : "没有匹配的插件";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateUpdatedText();
    }

    private static bool Matches(MarketplacePlugin plugin, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var needle = filter.Trim();
        return Contains(plugin.Name, needle)
            || Contains(plugin.Id, needle)
            || Contains(plugin.Description, needle)
            || Contains(plugin.Author, needle)
            || plugin.Tags.Any(tag => Contains(tag, needle));
    }

    private static bool Contains(string? value, string needle)
        => !string.IsNullOrEmpty(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private Border BuildCard(MarketplacePlugin plugin)
    {
        var status = GetStatus(plugin, out var installed, out var reason);

        var root = new Grid { ColumnSpacing = 14, Padding = new Thickness(4) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        root.Children.Add(BuildLogo(plugin));

        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(info, 1);

        // 名称、版本、状态用 Grid 的星号列约束，横向 StackPanel 会被长名称撑出卡片
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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

        var stateBadge = BuildStatusBadge(status);
        Grid.SetColumn(stateBadge, 2);
        titleRow.Children.Add(stateBadge);

        info.Children.Add(titleRow);

        var meta = new List<string> { plugin.Id };
        if (!string.IsNullOrEmpty(plugin.Author)) meta.Add($"作者: {plugin.Author}");
        meta.Add(plugin.DescribeSize());
        if (!string.IsNullOrEmpty(plugin.License)) meta.Add(plugin.License);
        if (installed != null && !string.IsNullOrEmpty(installed.Version)) meta.Add($"本地 {installed.Version}");

        info.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", meta),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush", 0x90, 0x90, 0x95),
        });

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

        if (plugin.Tags.Count > 0)
        {
            var tags = ChipList.Build(plugin.Tags.Take(5));
            tags.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(tags);
        }

        if (status == MarketStatus.Incompatible && reason != null)
        {
            info.Children.Add(new TextBlock
            {
                Text = reason,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 157, 93, 0)),
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

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(actions, 2);

        actions.Children.Add(BuildInstallButton(plugin, status, installed));
        actions.Children.Add(BuildDetailsButton(plugin, status, installed));
        root.Children.Add(actions);

        return new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = root,
        };
    }

    private Button BuildInstallButton(MarketplacePlugin plugin, MarketStatus status, PluginInfo? installed)
    {
        var button = new Button
        {
            MinWidth = 96,
            IsEnabled = !_installing && status is MarketStatus.NotInstalled or MarketStatus.UpdateAvailable or MarketStatus.Installed or MarketStatus.LocalNewer,
            VerticalAlignment = VerticalAlignment.Center,
        };

        switch (status)
        {
            case MarketStatus.NotInstalled:
                button.Content = "安装";
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                button.Click += async (_, _) => await InstallAsync(plugin);
                break;
            case MarketStatus.UpdateAvailable:
                button.Content = "更新";
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                ToolTipService.SetToolTip(button, $"更新到 v{plugin.Version}（本地 {installed?.Version}）");
                button.Click += async (_, _) => await InstallAsync(plugin);
                break;
            case MarketStatus.LocalNewer:
                button.Content = "本地已较新";
                button.IsEnabled = false;
                ToolTipService.SetToolTip(button, $"本地版本 {installed?.Version} 高于市场版本 {plugin.Version}，已跳过");
                break;
            case MarketStatus.Installed:
                button.Content = "重新安装";
                ToolTipService.SetToolTip(button, $"已安装 v{installed?.Version}。启用/禁用请到「插件管理」");
                button.Click += async (_, _) => await InstallAsync(plugin);
                break;
            default:
                button.Content = "不可用";
                button.IsEnabled = false;
                break;
        }

        return button;
    }

    private Button BuildDetailsButton(MarketplacePlugin plugin, MarketStatus status, PluginInfo? installed)
    {
        var button = new Button
        {
            Content = "详情",
            IsEnabled = !_installing,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += async (_, _) => await ShowDetailsAsync(plugin, status, installed);
        return button;
    }

    private Border BuildLogo(MarketplacePlugin plugin)
    {
        var border = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush("ControlFillColorSecondaryBrush", 0x1A, 0x1A, 0x1A),
        };

        if (plugin.HasLogo)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Width = 56, Height = 56 };
            border.Child = image;
            _ = ApplyLogoAsync(image, plugin);
        }
        else
        {
            border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 122, 255));
            border.Child = new FontIcon
            {
                Glyph = string.IsNullOrEmpty(plugin.IconGlyph) ? "\uE7B8" : plugin.IconGlyph,
                FontSize = 22,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return border;
    }

    private async Task ApplyLogoAsync(Image target, MarketplacePlugin plugin)
    {
        var source = await GetLogoAsync(plugin);
        if (source != null)
        {
            target.Source = source;
        }
    }

    private async Task<ImageSource?> GetLogoAsync(MarketplacePlugin plugin)
    {
        if (!plugin.HasLogo)
        {
            return null;
        }

        if (_logos.TryGetValue(plugin.Id, out var cached))
        {
            return cached;
        }

        if (_logoJobs.TryGetValue(plugin.Id, out var running))
        {
            return await running;
        }

        var job = DecodeLogoAsync(plugin);
        _logoJobs[plugin.Id] = job;
        var image = await job;
        _logoJobs.Remove(plugin.Id);
        _logos[plugin.Id] = image;
        return image;
    }

    private async Task<ImageSource?> DecodeLogoAsync(MarketplacePlugin plugin)
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
            return bitmap;
        }
        catch (Exception ex)
        {
            _plugins.Logs.Host.Warn($"插件 {plugin.Id} 的图标解码失败：{ex.Message}");
            return null;
        }
    }

    private MarketStatus GetStatus(MarketplacePlugin plugin, out PluginInfo? installed, out string? reason)
    {
        installed = _plugins.Plugins.FirstOrDefault(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
        reason = null;

        if (!plugin.IsCompatible(out reason))
        {
            return MarketStatus.Incompatible;
        }

        if (installed == null || string.IsNullOrEmpty(installed.Version))
        {
            return MarketStatus.NotInstalled;
        }

        if (System.Version.TryParse(installed.Version, out var local) && System.Version.TryParse(plugin.Version, out var remote))
        {
            if (remote > local) return MarketStatus.UpdateAvailable;
            if (local > remote) return MarketStatus.LocalNewer;
        }

        return MarketStatus.Installed;
    }

    private async Task InstallAsync(MarketplacePlugin plugin)
    {
        if (_installing)
        {
            return;
        }

        if (!plugin.IsCompatible(out var reason))
        {
            ShowStatus("无法安装", reason ?? "该插件与当前宿主不兼容", InfoBarSeverity.Warning);
            return;
        }

        _installing = true;
        _installCts?.Cancel();
        _installCts = new CancellationTokenSource();
        SetCardBusy(plugin.Id, true);
        ShowStatus("正在安装", $"正在下载 {plugin.Name} {plugin.Version}…", InfoBarSeverity.Informational);

        try
        {
            var progress = new Progress<double>(value =>
            {
                if (_progressBars.TryGetValue(plugin.Id, out var bar))
                {
                    bar.Value = Math.Clamp(value, 0, 1) * 100;
                }
            });

            var packagePath = await _market.DownloadPackageAsync(plugin, progress, _installCts.Token);
            ShowStatus("正在安装", $"已下载并校验通过，正在安装 {plugin.Name}…", InfoBarSeverity.Informational);

            var result = await _plugins.InstallAsync(packagePath);
            if (result.Success && result.Manifest != null)
            {
                var version = result.IsUpdate ? $"{result.OldVersion} → {result.Manifest.Version}" : result.Manifest.Version;
                var message = $"{result.Manifest.Name} {version}";
                if (result.Warnings.Count > 0)
                {
                    message += Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);
                }

                ShowStatus(result.IsUpdate ? "插件已更新" : "插件已安装", message, InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus("安装失败", result.Error ?? "未知错误", InfoBarSeverity.Error);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowStatus("安装失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _installing = false;
            SetCardBusy(plugin.Id, false);
            Rebuild();
        }
    }

    private void SetCardBusy(string pluginId, bool busy)
    {
        if (_progressBars.TryGetValue(pluginId, out var bar))
        {
            bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                bar.Value = 0;
            }
        }

        PluginList.IsHitTestVisible = !busy;
    }

    private async Task ShowDetailsAsync(MarketplacePlugin plugin, MarketStatus status, PluginInfo? installed)
    {
        var panel = new StackPanel { Spacing = 14 };

        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var logo = BuildLogo(plugin);
        logo.Width = 72;
        logo.Height = 72;
        header.Children.Add(logo);

        var head = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(head, 1);
        head.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        head.Children.Add(new TextBlock
        {
            Text = BuildDetailLine(plugin, installed),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush", 0x90, 0x90, 0x95),
        });
        header.Children.Add(head);
        panel.Children.Add(header);

        if (!string.IsNullOrEmpty(plugin.Description))
        {
            panel.Children.Add(new TextBlock { Text = plugin.Description, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        }

        var meta = new StackPanel { Spacing = 6 };
        AddMetaRow(meta, "Id", plugin.Id);
        AddMetaRow(meta, "版本", $"v{plugin.Version}" + (string.IsNullOrEmpty(installed?.Version) ? "" : $"（本地 v{installed!.Version}）"));
        AddMetaRow(meta, "作者", plugin.Author ?? "未知");
        AddMetaRow(meta, "大小", plugin.DescribeSize());
        AddMetaRow(meta, "许可证", string.IsNullOrEmpty(plugin.License) ? "未声明" : plugin.License);
        if (plugin.UpdatedAt != default)
        {
            AddMetaRow(meta, "更新时间", plugin.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        }

        if (!string.IsNullOrEmpty(plugin.Sha256))
        {
            AddMetaRow(meta, "SHA-256", plugin.Sha256.Length > 16 ? plugin.Sha256[..16] + "…" : plugin.Sha256);
        }

        if (plugin.Tags.Count > 0)
        {
            AddMetaRow(meta, "标签", string.Join("、", plugin.Tags));
        }

        if (!string.IsNullOrEmpty(plugin.Homepage))
        {
            var link = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            if (Uri.TryCreate(plugin.Homepage, UriKind.Absolute, out var homepage))
            {
                var hyperlink = new Hyperlink { NavigateUri = homepage };
                hyperlink.Inlines.Add(new Run { Text = plugin.Homepage });
                link.Inlines.Add(hyperlink);
            }
            else
            {
                link.Text = plugin.Homepage;
            }

            AddMetaRow(meta, "主页", link);
        }

        if (!plugin.IsCompatible(out var reason) && reason != null)
        {
            meta.Children.Add(new TextBlock
            {
                Text = reason,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 157, 93, 0)),
            });
        }

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = meta,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "插件说明",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });

        var readmeHost = new StackPanel { Spacing = 8 };
        if (string.IsNullOrEmpty(plugin.Readme))
        {
            readmeHost.Children.Add(Placeholder("（该插件未提供说明）"));
        }
        else
        {
            readmeHost.Children.Add(Placeholder("正在加载说明…"));
            LoadReadme(readmeHost, plugin);
        }

        panel.Children.Add(readmeHost);

        var installable = status is MarketStatus.NotInstalled or MarketStatus.UpdateAvailable or MarketStatus.Installed or MarketStatus.LocalNewer;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "插件详情",
            Content = new ScrollViewer
            {
                // 不要写死宽度：ContentDialog 自身有最大宽度，写死后内容会顶出弹窗外
                MaxWidth = 520,
                MaxHeight = 520,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
            },
            PrimaryButtonText = installable ? (status == MarketStatus.UpdateAvailable ? $"更新到 v{plugin.Version}" : "安装") : null,
            CloseButtonText = installable ? "关闭" : "确定",
            DefaultButton = installable ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (installable && result == ContentDialogResult.Primary)
        {
            await InstallAsync(plugin);
        }
    }

    /// <summary>README 以 Markdown 渲染；相对链接解析到仓库 main 分支上的对应文件。</summary>
    private async void LoadReadme(StackPanel host, MarketplacePlugin plugin)
    {
        try
        {
            var text = await _market.GetReadmeAsync(plugin, CancellationToken.None);
            host.Children.Clear();
            if (string.IsNullOrWhiteSpace(text))
            {
                host.Children.Add(Placeholder("（该插件未提供说明）"));
                return;
            }

            host.Children.Add(MarkdownRenderer.Render(text, ResolveLinkBase(plugin)));
        }
        catch (Exception ex)
        {
            host.Children.Clear();
            host.Children.Add(Placeholder($"（说明加载失败：{ex.Message}）"));
        }
    }

    private static Uri? ResolveLinkBase(MarketplacePlugin plugin)
    {
        var directory = "";
        if (plugin.Readme is { Length: > 0 } readme && readme.LastIndexOf('/') is var slash && slash >= 0)
        {
            directory = readme[..(slash + 1)];
        }

        return Uri.TryCreate($"{MarketplaceService.RepositoryUrl}/blob/main/{directory}", UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    private static TextBlock Placeholder(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = ThemeBrush("TextFillColorSecondaryBrush", 0x90, 0x90, 0x95),
    };

    private static string BuildDetailLine(MarketplacePlugin plugin, PluginInfo? installed)
    {
        var parts = new List<string> { plugin.Id };
        if (!string.IsNullOrEmpty(plugin.Author)) parts.Add($"作者: {plugin.Author}");
        parts.Add(plugin.DescribeSize());
        if (installed != null) parts.Add($"本地状态: {PluginStateText.Describe(installed.State)}");
        return string.Join(" · ", parts);
    }

    private static void AddMetaRow(Panel host, string label, string value)
        => AddMetaRow(host, label, new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

    /// <summary>
    /// 元数据行：标签列定宽 + 内容列星号。**不要用横向 StackPanel** —— 它按内容无限宽度测量子元素，
    /// TextWrapping 不会生效，长文本（SHA-256、更新时间、主页 URL）会直接顶出对话框。
    /// </summary>
    private static void AddMetaRow(Panel host, string label, FrameworkElement value)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 133)),
        });

        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        host.Children.Add(row);
    }

    private Border BuildStatusBadge(MarketStatus status)
    {
        var (text, color) = status switch
        {
            MarketStatus.Installed => ("已安装", Windows.UI.Color.FromArgb(255, 16, 124, 16)),
            MarketStatus.UpdateAvailable => ("可更新", Windows.UI.Color.FromArgb(255, 0, 95, 184)),
            MarketStatus.LocalNewer => ("本地较新", Windows.UI.Color.FromArgb(255, 128, 128, 128)),
            MarketStatus.Incompatible => ("不兼容", Windows.UI.Color.FromArgb(255, 196, 43, 28)),
            _ => ("未安装", Windows.UI.Color.FromArgb(255, 128, 128, 128)),
        };

        return BuildBadge(text, color.R, color.G, color.B);
    }

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

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _filter = sender.Text;
        Rebuild();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(force: true);

    private void Repo_Click(object sender, RoutedEventArgs e) => OpenUrl(MarketplaceService.RepositoryUrl);

    private void ResetUrl_Click(object sender, RoutedEventArgs e)
    {
        _market.ConfiguredBaseUrl = null;
        BaseUrlBox.Text = "";
        UpdateBaseUrlUi();
        _ = LoadAsync(force: true);
    }

    private void BaseUrlBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ApplyBaseUrl();
        }
    }

    private void BaseUrlBox_LostFocus(object sender, RoutedEventArgs e) => ApplyBaseUrl();

    private void ApplyBaseUrl()
    {
        var value = BaseUrlBox.Text.Trim();
        if (string.Equals(value, _market.ConfiguredBaseUrl ?? "", StringComparison.OrdinalIgnoreCase))
        {
            UpdateBaseUrlUi();
            return;
        }

        _market.ConfiguredBaseUrl = value.Length == 0 ? null : value;
        BaseUrlBox.Text = _market.ConfiguredBaseUrl ?? "";
        UpdateBaseUrlUi();
        _ = LoadAsync(force: true);
    }

    private void UpdateBaseUrlUi()
    {
        ResetUrlButton.IsEnabled = !_market.IsAuto;
        if (_market.IsAuto)
        {
            BaseUrlHint.Text = $"留空 = 自动：官方 GitHub 源优先，不可用时依次尝试 GitCode 国内镜像与 jsDelivr CDN（当前来源：{_market.ActiveSourceLabel}）";
        }
        else if (_market.ConfiguredBaseUrl is { } configured && !configured.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            BaseUrlHint.Text = "当前为本机目录源（开发调试用）：直接读取该目录下的 index.json 与插件包";
        }
        else
        {
            BaseUrlHint.Text = $"当前为自定义源（镜像前缀，路径会拼在其后）：{_market.ConfiguredBaseUrl}";
        }
    }

    private void UpdateUpdatedText()
    {
        var count = _index?.Plugins.Count ?? 0;
        if (_market.LastIndexTime is { } time)
        {
            UpdatedText.Text = $"共 {count} 个插件 · 更新于 {DescribeTime(time)} · 来源 {_market.ActiveSourceLabel}";
        }
        else
        {
            UpdatedText.Text = count > 0 ? $"共 {count} 个插件" : "";
        }
    }

    private static string DescribeTime(DateTimeOffset? time)
        => time is { } value ? value.ToLocalTime().ToString("MM-dd HH:mm") : "未知时间";

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static Brush ThemeBrush(string key, byte r, byte g, byte b)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));

    private enum MarketStatus
    {
        NotInstalled,
        Installed,
        UpdateAvailable,
        LocalNewer,
        Incompatible,
    }
}
