using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using WinIsland.Core;
using WinIsland.Core.Plugins;

namespace WinIsland.Settings;

public sealed partial class PluginCenterPage
{
    private UIElement BuildPriorityBox(PluginInfo plugin)
    {
        var column = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        column.Children.Add(BuildPriorityRow(
            $"plugin.{plugin.Id}.priority", "优先级",
            "岛上优先级：数值越大越靠前，小岛常驻显示数值最高的活动；留空使用插件默认值"));
        column.Children.Add(BuildPriorityRow(
            $"plugin.{plugin.Id}.expandedPriority", "展开优先级",
            "展开优先级：只决定展开后的排列（展开主卡与队列，数值越大越靠前）；留空＝与优先级相同（不改动旧行为）"));
        return column;
    }

    /// <summary>一行「标签 + NumberBox」：写 <paramref name="key"/>，留空则移除该键（回退插件默认值）。</summary>
    private UIElement BuildPriorityRow(string key, string label, string tooltip)
    {
        var box = new NumberBox
        {
            Value = _settings.Get<double?>(key, null) ?? double.NaN,
            PlaceholderText = "默认",
            Minimum = 0,
            Maximum = 999,
            SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 136,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !IsBusy,
        };
        ToolTipService.SetToolTip(box, tooltip);
        AutomationProperties.SetAutomationId(box, key);
        AutomationProperties.SetName(box, label);

        box.ValueChanged += (_, args) =>
        {
            if (IsBusy) return;
            if (double.IsNaN(args.NewValue))
            {
                _settings.Remove(key);
            }
            else
            {
                _settings.Set(key, args.NewValue);
            }
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            MinWidth = 74,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Resources["SecondaryText"],
        });
        row.Children.Add(box);
        return row;
    }

    private async Task RunOperationAsync(Func<Task<bool>> operation, string action, PluginInfo plugin)
    {
        if (IsBusy)
        {
            return;
        }

        _managing = true;
        UpdateBusyState();
        try
        {
            var success = await operation();
            if (success)
            {
                ShowStatus($"{plugin.Name}：{action}成功", "详情可查看该插件的日志。", InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus($"{plugin.Name}：{action}失败", plugin.LastError?.Describe() ?? "详情请查看该插件的日志。", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"{plugin.Name}：{action}异常", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _managing = false;
            Rebuild();
        }
    }

    private async Task DeleteAsync(PluginInfo plugin)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = ActualTheme,
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
            RequestedTheme = ActualTheme,
            XamlRoot = XamlRoot,
            Title = $"{plugin.Name} · 日志（最近 {lines.Count} 行）",
            Content = new ScrollViewer
            {
                MaxWidth = 520,
                MaxHeight = 420,
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
        if (IsBusy) return;
        _managing = true;
        UpdateBusyState();
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

                ShowStatus(result.IsUpdate ? "插件已更新" : "插件已安装", message, InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus("安装失败", result.Error ?? "未知错误", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("安装失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _managing = false;
            Rebuild();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
        => RevealInExplorer(_plugins.PluginsDirectory);

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

}
