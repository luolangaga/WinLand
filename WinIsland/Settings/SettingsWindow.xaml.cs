using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Animation;
using WinIsland.Island;
using WinIsland.Core;

namespace WinIsland.Settings;

/// <summary>
/// 设置窗口：左侧导航自动列出所有模块注册的设置页面。
/// 主程序不硬编码任何页面——全部由插件通过 AddSettingsPage 提供。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly IslandService _service;
    private string? _selectedPage;
    private int _selectedIndex = -1;

    public SettingsWindow(IslandService service, SettingsService settings)
    {
        _service = service;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ResizeLogical(1000, 700);

        _service.SettingsPagesChanged += RebuildNav;
        Closed += (_, _) => _service.SettingsPagesChanged -= RebuildNav;

        RebuildNav();
    }

    private void ResizeLogical(double w, double h)
    {
        var scale = Core.Win32.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)(w * scale), area.Width), Math.Min((int)(h * scale), area.Height)));
        AppWindow.Move(new Windows.Graphics.PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 2));
    }

    private void RebuildNav()
    {
        var selectedId = (Nav.SelectedItem as NavigationViewItem)?.Tag as string;
        // Keep the plugin center alive while enabling/disabling plugins adds or removes settings pages.
        // Recreating it here would cancel its own installation and discard search/selection state.
        if (_selectedPage != "plugins") _selectedPage = null;
        Nav.MenuItems.Clear();
        foreach (var page in _service.SettingsPages)
        {
            var item = new NavigationViewItem
            {
                Content = page.Title,
                Tag = page.Id,
                Icon = new FontIcon { Glyph = page.Glyph },
            };
            AutomationProperties.SetAutomationId(item, "SettingsNav." + page.Id);
            Nav.MenuItems.Add(item);
        }
        if (selectedId != null && Nav.MenuItems.OfType<NavigationViewItem>().Any(i => (string?)i.Tag == selectedId))
        {
            SelectPage(selectedId);
        }
        else if (Nav.MenuItems.Count > 0)
        {
            Nav.SelectedItem = Nav.MenuItems[0];
        }
        else
        {
            ContentFrame.Content = null;
        }
    }

    /// <summary>切换到指定页面。</summary>
    public void SelectPage(string pageId)
    {
        if (pageId == "marketplace") pageId = "plugins";
        var item = Nav.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == pageId);
        if (item != null) Nav.SelectedItem = item;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string id) return;
        var page = _service.SettingsPages.FirstOrDefault(p => p.Id == id);
        if (page == null) return;
        if (_selectedPage == id) return;

        // 页面交给 Frame 承载，切换时播放 NavigationThemeTransition（淡入 + 轻微位移）
        int index = Nav.MenuItems.IndexOf(item);
        NavigationTransitionInfo transition = Motion.Enabled
            ? new SlideNavigationTransitionInfo { Effect = index >= _selectedIndex
                ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft }
            : new SuppressNavigationTransitionInfo();
        ContentFrame.Navigate(typeof(SettingsPageHost), page.Factory(), transition);
        _selectedPage = id;
        _selectedIndex = index;
    }
}
