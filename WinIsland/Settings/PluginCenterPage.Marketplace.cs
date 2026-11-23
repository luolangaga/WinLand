using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinIsland.Core;
using WinIsland.Core.Marketplace;
using WinIsland.Core.Plugins;

namespace WinIsland.Settings;

/// <summary>
/// 插件市场：列表只来自社区仓库的 index.json（一次请求，图标内嵌），
/// 安装时才下载 .lwp 并校验 SHA-256，然后交给 <see cref="PluginHost"/> 安装/更新。
/// </summary>
public sealed partial class PluginCenterPage
{
    private const string MakerSkillRepoGitHub = "https://github.com/luolangaga/WinLandPluginSkills";
    private const string MakerSkillRepoGitCode = "https://gitcode.com/luolangaga/WinLandPluginSkills";
    private const string MakerInstallPromptGitHub = "请帮我把这个仓库里的技能下载安装到我的技能目录，装好后告诉我怎么用：\n" + MakerSkillRepoGitHub;
    private const string MakerInstallPromptGitCode = "请帮我把这个仓库里的技能下载安装到我的技能目录，装好后告诉我怎么用：\n" + MakerSkillRepoGitCode;
    private const string MakerDemandPrompt = "我想给 WinIsland 灵动岛做一个插件：小岛显示当前温度，展开后看未来三天预报。";

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

    private Border BuildLogo(MarketplacePlugin plugin)
    {
        var border = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Resources["LocalLogo"],
        };

        if (plugin.HasLogo)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Width = 56, Height = 56 };
            border.Child = image;
            _ = ApplyLogoAsync(image, plugin);
        }
        else
        {
            border.Child = new FontIcon
            {
                Glyph = string.IsNullOrEmpty(plugin.IconGlyph) ? "\uE7B8" : plugin.IconGlyph,
                FontSize = 22,
                Style = (Style)Resources["LocalIcon"],
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
        if (installed?.IsBuiltIn == true) return MarketStatus.Installed;

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
        if (IsBusy)
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
        _installCts?.Dispose();
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
            _installCts?.Dispose();
            _installCts = null;
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

        UpdateBusyState();
    }

    private async Task ShowDetailsAsync(MarketplacePlugin plugin, MarketStatus status, PluginInfo? installed)
    {
        using var readmeCts = new CancellationTokenSource();
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
            Style = (Style)Resources["SecondaryText"],
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
                Style = (Style)Resources["CautionText"],
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
            _ = LoadReadmeAsync(readmeHost, plugin, readmeCts.Token);
        }

        panel.Children.Add(readmeHost);

        var installable = installed?.IsBuiltIn != true && status is (MarketStatus.NotInstalled or MarketStatus.UpdateAvailable or MarketStatus.Installed);
        var dialog = new ContentDialog
        {
            RequestedTheme = ActualTheme,
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
            PrimaryButtonText = installable ? (status == MarketStatus.UpdateAvailable ? $"更新到 v{plugin.Version}" : status == MarketStatus.Installed ? "重新安装" : "安装") : null,
            CloseButtonText = installable ? "关闭" : "确定",
            DefaultButton = installable ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        readmeCts.Cancel();
        if (installable && result == ContentDialogResult.Primary)
        {
            await InstallAsync(plugin);
        }
    }

    /// <summary>
    /// 「用 AI 做插件」教程：把 winland-plugin-maker 技能交给 AI 助手，一句需求从零做到上架。
    /// GitHub 与 GitCode 各给一段提示词（各带复制按钮），小白按三步走即可。
    /// </summary>
    private async void MakePlugin_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(MakerBody("不用写代码：把专门的「技能」装进你的 AI 助手，说一句需求，它就从建工程、写代码一路陪你做到装进 WinIsland 实测、打包、上架社区市场。"));

        panel.Children.Add(BuildToolCard());

        panel.Children.Add(MakerSection("第 1 步 · 给 AI 装上「做插件」的技能"));
        panel.Children.Add(MakerBody("把下面这段原样发给你的 AI 助手，它自己会把技能下载安装好，装完告诉你接下来怎么用。"));
        panel.Children.Add(MakerPrompt("首选 · GitHub", MakerInstallPromptGitHub));
        panel.Children.Add(MakerBody("GitHub 连不上就用下面这段，走 GitCode 国内镜像，技能内容一样："));
        panel.Children.Add(MakerPrompt("GitHub 打不开时 · GitCode 镜像", MakerInstallPromptGitCode));

        panel.Children.Add(MakerSection("第 2 步 · 直接说你要什么"));
        panel.Children.Add(MakerBody("不用提「技能」两个字，把点子说清楚就行，例如："));
        panel.Children.Add(MakerPrompt(null, MakerDemandPrompt));
        panel.Children.Add(MakerHint("把例子里的天气换成你自己的点子；暂时没头绪，也可以先问 AI：「灵动岛能做什么插件？帮我挑个最简单的。」"));

        panel.Children.Add(MakerSection("第 3 步 · 跟着 AI 走完剩下的"));
        panel.Children.Add(MakerBody("① 检查电脑环境（缺 .NET 等工具会帮你装好）\n② 从官方模板建好插件工程\n③ 写代码并编译\n④ 装进你正在用的 WinIsland，请你亲手测试到满意\n⑤ 打包成 .lwp 插件包\n⑥ 问你要不要上架——同意后投稿社区市场，所有 WinIsland 用户都能搜到"));

        panel.Children.Add(MakerNote("技能里写死了两条硬规矩：测试必须你亲口确认「没问题」才算通过；任何「发到网上」的动作（建仓库 / 推送 / 投稿）都必须先得到你的同意。"));

        var dialog = new ContentDialog
        {
            RequestedTheme = ActualTheme,
            XamlRoot = XamlRoot,
            Title = "用 AI 做插件",
            Content = new ScrollViewer
            {
                MaxWidth = 520,
                MaxHeight = 520,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
            },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    private static UIElement BuildToolCard()
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "推荐小白使用 WorkBuddy",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var line = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        line.Inlines.Add(new Run { Text = "腾讯出品的 AI 助手，免费：官网 " });
        var link = new Hyperlink { NavigateUri = new Uri("https://www.workbuddy.cn/") };
        link.Inlines.Add(new Run { Text = "workbuddy.cn" });
        line.Inlines.Add(link);
        line.Inlines.Add(new Run { Text = " 下载安装、微信扫码登录即用。已经在用 Claude Code / Command Code 等 AI 助手的，用手头这个就行，下面的步骤完全一样。" });
        panel.Children.Add(line);

        return new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = panel,
        };
    }

    private static TextBlock MakerSection(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private static TextBlock MakerBody(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
    };

    private TextBlock MakerHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Resources["SecondaryText"],
    };

    private UIElement MakerNote(string text) => new Border
    {
        Style = (Style)Resources["MakerNoteStyle"],
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(10, 2, 0, 2),
        Child = MakerHint(text),
    };

    /// <summary>一段提示词 + 右侧「复制」按钮；label 为空时不显示上方小标题。</summary>
    private UIElement MakerPrompt(string? label, string prompt)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new Border
        {
            Style = (Style)Resources["CodePanel"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        });

        var copy = new Button
        {
            Content = "复制",
            Tag = prompt,
            MinWidth = 64,
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Click += CopyPrompt_Click;
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        if (string.IsNullOrEmpty(label))
        {
            return grid;
        }

        var host = new StackPanel { Spacing = 4 };
        host.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Style = (Style)Resources["SecondaryText"],
        });
        host.Children.Add(grid);
        return host;
    }

    private static async void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string text)
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch
        {
            return;
        }

        button.Content = "已复制";
        await Task.Delay(1600);
        button.Content = "复制";
    }

    /// <summary>README 以 Markdown 渲染；相对链接解析到仓库 main 分支上的对应文件。</summary>
    private async Task LoadReadmeAsync(StackPanel host, MarketplacePlugin plugin, CancellationToken cancellationToken)
    {
        try
        {
            var text = await _market.GetReadmeAsync(plugin, cancellationToken);
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
            if (cancellationToken.IsCancellationRequested) return;
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

    private TextBlock Placeholder(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Style = (Style)Resources["SecondaryText"],
    };

    private static string BuildDetailLine(MarketplacePlugin plugin, PluginInfo? installed)
    {
        var parts = new List<string> { plugin.Id };
        if (!string.IsNullOrEmpty(plugin.Author)) parts.Add($"作者: {plugin.Author}");
        parts.Add(plugin.DescribeSize());
        if (installed != null) parts.Add($"本地状态: {PluginStateText.Describe(installed.State)}");
        return string.Join(" · ", parts);
    }

    private void AddMetaRow(Panel host, string label, string value)
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
    private void AddMetaRow(Panel host, string label, FrameworkElement value)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Top,
            Style = (Style)Resources["SecondaryText"],
        });

        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        host.Children.Add(row);
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _filter = sender.Text;
        Rebuild();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(force: true);

    private void Repo_Click(object sender, RoutedEventArgs e) => OpenUrl(MarketplaceService.RepositoryUrl);

    private void ResetUrl_Click(object sender, RoutedEventArgs e)
    {
        BaseUrlBox.Text = "";
        BaseUrlHint.Text = "点击「应用并刷新」恢复自动选择官方源和镜像。";
    }

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

    private enum MarketStatus
    {
        NotInstalled,
        Installed,
        UpdateAvailable,
        LocalNewer,
        Incompatible,
    }
}
