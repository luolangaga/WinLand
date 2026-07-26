using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace WinIsland.Settings;

/// <summary>
/// 设置窗口：左侧导航自动列出所有模块注册的设置页面。
/// 主程序不硬编码任何页面——全部由插件通过 AddSettingsPage 提供。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly IslandService _service;

    public SettingsWindow(IslandService service, SettingsService settings)
    {
        _service = service;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ResizeLogical(880, 640);

        _service.SettingsPagesChanged += RebuildNav;
        Closed += (_, _) => _service.SettingsPagesChanged -= RebuildNav;

        RebuildNav();
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

    private void RebuildNav()
    {
        var selectedId = (Nav.SelectedItem as NavigationViewItem)?.Tag as string;
        Nav.MenuItems.Clear();
        foreach (var page in _service.SettingsPages)
        {
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = page.Title,
                Tag = page.Id,
                Icon = new FontIcon { Glyph = page.Glyph },
            });
        }
        if (selectedId != null) SelectPage(selectedId);
        else if (Nav.MenuItems.Count > 0) Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>切换到指定页面。</summary>
    public void SelectPage(string pageId)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == pageId);
        if (item != null) Nav.SelectedItem = item;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string id) return;
        var page = _service.SettingsPages.FirstOrDefault(p => p.Id == id);
        if (page != null)
        {
            PageHost.Content = page.Factory();
        }
    }
}
