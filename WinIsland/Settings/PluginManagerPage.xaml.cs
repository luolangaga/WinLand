using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace WinIsland.Settings;

public sealed partial class PluginManagerPage : UserControl
{
    private readonly PluginLoader _loader;
    private readonly IDynamicIslandApi _api;

    public PluginManagerPage(PluginLoader loader, IDynamicIslandApi api)
    {
        _loader = loader;
        _api = api;
        InitializeComponent();

        PluginsPathText.Text = PluginConstants.GetPluginDirectory(AppContext.BaseDirectory);
        RefreshList();

        _loader.PluginsChanged += RefreshList;
        Unloaded += (_, _) => _loader.PluginsChanged -= RefreshList;
    }

    private void RefreshList()
    {
        _api.Dispatcher.TryEnqueue(RebuildList);
    }

    private void RebuildList()
    {
        PluginList.Children.Clear();

        foreach (var plugin in _loader.Plugins)
        {
            PluginList.Children.Add(CreatePluginCard(plugin));
        }
    }

    private Border CreatePluginCard(PluginInfo plugin)
    {
        var isInitialized = plugin.State == PluginState.Initialized;
        var isDisabled = plugin.State == PluginState.Disabled;
        var isError = plugin.State == PluginState.Error;

        var root = new Grid
        {
            ColumnSpacing = 14,
            Padding = new Thickness(4),
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                isInitialized ? Microsoft.UI.ColorHelper.FromArgb(255, 0, 122, 255)
                    : isError ? Microsoft.UI.ColorHelper.FromArgb(255, 196, 43, 28)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = plugin.IconGlyph ?? "\uE712",
                FontSize = 18,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        root.Children.Add(icon);

        var info = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(info, 1);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        titleRow.Children.Add(new TextBlock
        {
            Text = plugin.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (plugin.IsBuiltIn)
        {
            titleRow.Children.Add(new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(40, 0, 122, 255)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Child = new TextBlock
                {
                    Text = "内置",
                    FontSize = 11,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.ColorHelper.FromArgb(255, 0, 122, 255)),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        var stateText = plugin.State switch
        {
            PluginState.Initialized => "运行中",
            PluginState.Loaded => "已加载",
            PluginState.Disabled => "已禁用",
            PluginState.Error => $"错误: {plugin.ErrorMessage}",
            _ => "已发现",
        };
        var stateColor = plugin.State switch
        {
            PluginState.Initialized => Microsoft.UI.ColorHelper.FromArgb(255, 16, 124, 16),
            PluginState.Disabled => Microsoft.UI.ColorHelper.FromArgb(255, 128, 128, 128),
            PluginState.Error => Microsoft.UI.ColorHelper.FromArgb(255, 196, 43, 28),
            _ => Microsoft.UI.ColorHelper.FromArgb(255, 128, 128, 128),
        };

        titleRow.Children.Add(new TextBlock
        {
            Text = stateText,
            FontSize = 11,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(stateColor),
            VerticalAlignment = VerticalAlignment.Center,
        });

        info.Children.Add(titleRow);

        if (!string.IsNullOrEmpty(plugin.Description))
        {
            info.Children.Add(new TextBlock
            {
                Text = plugin.Description,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(180, 255, 255, 255)),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
            });
        }

        var metaParts = new List<string>();
        if (!string.IsNullOrEmpty(plugin.Author)) metaParts.Add($"作者: {plugin.Author}");
        if (!string.IsNullOrEmpty(plugin.Version)) metaParts.Add($"v{plugin.Version}");
        if (metaParts.Count > 0)
        {
            info.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", metaParts),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(140, 255, 255, 255)),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        }

        root.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(actions, 2);

        var toggle = new ToggleSwitch
        {
            IsOn = isInitialized,
            Margin = new Thickness(0, 0, -110, 0),
        };
        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn)
            {
                _ = _loader.EnablePluginAsync(plugin.Id);
            }
            else
            {
                _ = _loader.DisablePluginAsync(plugin.Id);
            }
        };
        actions.Children.Add(toggle);

        if (!plugin.IsBuiltIn)
        {
            var removeBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 },
                Padding = new Thickness(8, 4, 8, 4),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            removeBtn.Click += (_, _) =>
            {
                _ = _loader.UnloadPluginAsync(plugin.Id);
            };
            actions.Children.Add(removeBtn);
        }

        root.Children.Add(actions);

        return new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = root,
        };
    }

    private async void LoadPlugin_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".dll");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
            ((App)Application.Current)._settingsWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        var success = await _loader.LoadDllAsync(file.Path);
        if (!success)
        {
            _api.SendMessage(new IslandMessage
            {
                Title = "插件加载失败",
                Text = file.Name,
                Glyph = "\uE783",
                Duration = TimeSpan.FromSeconds(4),
            });
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = PluginConstants.GetPluginDirectory(AppContext.BaseDirectory);
        Directory.CreateDirectory(dir);
        OpenFolderAndSelect(dir);
    }

    private static void OpenFolderAndSelect(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
