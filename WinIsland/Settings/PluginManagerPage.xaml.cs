using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;
using WinIsland.Core.Plugins;

namespace WinIsland.Settings;

public sealed partial class PluginManagerPage : UserControl
{
    private readonly PluginHost _plugins;
    private bool _busy;

    public PluginManagerPage(PluginHost plugins)
    {
        _plugins = plugins;
        InitializeComponent();

        PluginsPathText.Text = plugins.PluginsDirectory;
        Rebuild();

        _plugins.PluginsChanged += Refresh;
        Unloaded += (_, _) => _plugins.PluginsChanged -= Refresh;
    }

    private void Refresh()
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

    private void Rebuild()
    {
        var warnings = _plugins.DiscoveryWarnings;
        WarningBar.Message = string.Join(Environment.NewLine, warnings);
        WarningBar.IsOpen = warnings.Count > 0;

        PluginList.Children.Clear();
        foreach (var plugin in _plugins.Plugins)
        {
            PluginList.Children.Add(CreatePluginCard(plugin));
        }

        PluginList.IsHitTestVisible = !_busy;
    }

    private Border CreatePluginCard(PluginInfo plugin)
    {
        var isActive = plugin.State == PluginState.Active;
        var isFaulted = plugin.State == PluginState.Faulted;

        var root = new Grid { ColumnSpacing = 14, Padding = new Thickness(4) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        root.Children.Add(BuildIcon(plugin, isActive, isFaulted));

        var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(info, 1);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        if (plugin.IsBuiltIn)
        {
            titleRow.Children.Add(BuildBadge("内置", 0x00, 0x7A, 0xFF));
        }

        titleRow.Children.Add(BuildStateBadge(plugin));
        info.Children.Add(titleRow);

        var meta = new List<string> { plugin.Id };
        if (!string.IsNullOrEmpty(plugin.Version)) meta.Add($"v{plugin.Version}");
        if (!string.IsNullOrEmpty(plugin.Author)) meta.Add($"作者: {plugin.Author}");
        if (!plugin.IsBuiltIn) meta.Add(plugin.Source);

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

        if (plugin.LastError != null)
        {
            info.Children.Add(BuildErrorBlock(plugin.LastError));
        }

        if (!plugin.IsBuiltIn && !File.Exists(Path.Combine(plugin.Source, IslandSdk.ManifestFileName)))
        {
            var note = new TextBlock
            {
                Text = "该条目没有有效的 plugin.json，无法启用。",
                FontSize = 11,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush", 0x90, 0x90, 0x95),
            };
            info.Children.Add(note);
        }

        root.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(actions, 2);

        var toggle = new ToggleSwitch
        {
            IsOn = isActive,
            IsEnabled = !_busy && plugin.Manifest != null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Toggled += async (_, _) =>
        {
            var enable = toggle.IsOn;
            toggle.IsEnabled = false;
            await RunOperationAsync(() => _plugins.SetEnabledAsync(plugin.Id, enable), enable ? "启用" : "禁用", plugin);
        };
        actions.Children.Add(toggle);

        actions.Children.Add(IconButton("\uE72C", "重新加载（停用 → 卸载 → 重新加载并启用）", async () =>
            await RunOperationAsync(() => _plugins.ReloadAsync(plugin.Id), "重新加载", plugin)));

        if (!plugin.IsBuiltIn)
        {
            actions.Children.Add(IconButton("\uE74D", "删除插件（移除插件目录）", async () => await DeleteAsync(plugin)));
            actions.Children.Add(IconButton("\uE838", "打开插件目录", () =>
            {
                RevealInExplorer(plugin.Source);
                return Task.CompletedTask;
            }));
        }

        actions.Children.Add(IconButton("\uE8A5", "查看日志", async () =>
        {
            await ShowLogsAsync(plugin);
        }));

        root.Children.Add(actions);

        return new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = root,
        };
    }

    private UIElement BuildIcon(PluginInfo plugin, bool isActive, bool isFaulted)
    {
        var color = isActive
            ? Microsoft.UI.ColorHelper.FromArgb(255, 0, 122, 255)
            : isFaulted
                ? Microsoft.UI.ColorHelper.FromArgb(255, 196, 43, 28)
                : Microsoft.UI.ColorHelper.FromArgb(255, 128, 128, 128);

        return new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = string.IsNullOrEmpty(plugin.IconGlyph) ? "\uE712" : plugin.IconGlyph,
                FontSize = 18,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private Border BuildStateBadge(PluginInfo plugin)
    {
        var text = PluginStateText.Describe(plugin.State);
        var color = plugin.State switch
        {
            PluginState.Active => Windows.UI.Color.FromArgb(255, 16, 124, 16),
            PluginState.Faulted => Windows.UI.Color.FromArgb(255, 196, 43, 28),
            _ => Windows.UI.Color.FromArgb(255, 128, 128, 128),
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

    private static UIElement BuildErrorBlock(PluginError error)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{error.Phase}：{error.Describe()}",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)),
        });
        panel.Children.Add(new TextBlock
        {
            Text = error.Time.ToString("yyyy-MM-dd HH:mm:ss"),
            FontSize = 11,
            Foreground = ThemeBrush("TextFillColorTertiaryBrush", 0x80, 0x80, 0x85),
        });
        return panel;
    }

    private Button IconButton(string glyph, string tooltip, Func<Task> action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 0,
            MinHeight = 0,
            IsEnabled = !_busy,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task RunOperationAsync(Func<Task<bool>> operation, string action, PluginInfo plugin)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        PluginList.IsHitTestVisible = false;
        try
        {
            var success = await operation();
            if (success)
            {
                ShowResult($"{plugin.Name}：{action}成功", "详情可查看该插件的日志。", InfoBarSeverity.Success);
            }
            else
            {
                ShowResult($"{plugin.Name}：{action}失败", plugin.LastError?.Describe() ?? "详情请查看该插件的日志。", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowResult($"{plugin.Name}：{action}异常", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            Rebuild();
        }
    }

    private async Task DeleteAsync(PluginInfo plugin)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除插件",
            Content = $"将删除「{plugin.Name}」{(string.IsNullOrEmpty(plugin.Version) ? "" : plugin.Version)} 的插件目录：\n{plugin.Source}",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunOperationAsync(() => _plugins.UninstallAsync(plugin.Id), "删除", plugin);
    }

    private async Task ShowLogsAsync(PluginInfo plugin)
    {
        var lines = _plugins.Logs.GetLines(plugin.Id);
        var text = lines.Count == 0 ? "（暂无日志）" : string.Join(Environment.NewLine, lines);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{plugin.Name} · 日志（最近 {lines.Count} 行）",
            Content = new ScrollViewer
            {
                Width = 760,
                Height = 420,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.NoWrap,
                    IsTextSelectionEnabled = true,
                },
            },
            PrimaryButtonText = "打开日志文件",
            CloseButtonText = "关闭",
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            RevealInExplorer(_plugins.Logs.GetLogPath(plugin.Id));
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add(IslandSdk.PackageExtension);

            var window = ((App)Application.Current)._settingsWindow;
            if (window == null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var result = await _plugins.InstallAsync(file.Path);
            if (result.Success && result.Manifest != null)
            {
                var version = result.IsUpdate ? $"{result.OldVersion} → {result.Manifest.Version}" : result.Manifest.Version;
                var message = $"{result.Manifest.Name} {version}";
                if (result.Warnings.Count > 0)
                {
                    message += Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);
                }

                ShowResult(result.IsUpdate ? "插件已更新" : "插件已安装", message, InfoBarSeverity.Success);
            }
            else
            {
                ShowResult("安装失败", result.Error ?? "未知错误", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowResult("安装失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
        => RevealInExplorer(_plugins.PluginsDirectory);

    private void ShowResult(string title, string message, InfoBarSeverity severity)
    {
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.Severity = severity;
        ResultBar.IsOpen = true;
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch
        {
        }
    }

    private static Brush ThemeBrush(string key, byte r, byte g, byte b)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
}
