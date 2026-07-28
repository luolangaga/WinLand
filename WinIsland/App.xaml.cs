using Microsoft.UI.Xaml;
using WinIsland.Core;
using WinIsland.Island;
using WinIsland.Modules;
using WinIsland.Modules.Media;
using WinIsland.Modules.Messaging;
using WinIsland.Modules.Battery;
using WinIsland.Modules.AiMonitor;
using WinIsland.Settings;

namespace WinIsland;

public partial class App : Application
{
    private SettingsService _settings = null!;
    private IslandWindow _island = null!;
    private IslandService _service = null!;
    private PluginLoader _pluginLoader = null!;
    private TrayIcon? _tray;
    internal SettingsWindow? _settingsWindow;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _settings = new SettingsService();
        _island = new IslandWindow(_settings);
        _service = new IslandService(_island, _settings);
        _service.SettingsOpenRequested += OpenSettingsWindow;

        _pluginLoader = new PluginLoader(_service, _settings);

        _island.ShowIsland();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _tray = new TrayIcon("WinIsland - 灵动岛", iconPath)
        {
            DoubleClicked = () => OpenSettingsWindow(null),
            MenuProvider = BuildTrayMenu,
        };

        _pluginLoader.RegisterBuiltIn(new GeneralModule());
        _pluginLoader.RegisterBuiltIn(new MediaModule());
        _pluginLoader.RegisterBuiltIn(new MessageModule());
        _pluginLoader.RegisterBuiltIn(new BatteryModule());
        _pluginLoader.RegisterBuiltIn(new AiMonitorModule());

        await _pluginLoader.LoadBuiltInModulesAsync();

        _service.AddSettingsPage(new SettingsPageDescriptor(
            "plugins", "插件管理", "\uE712",
            () => new PluginManagerPage(_pluginLoader, _service)));

        await _pluginLoader.DiscoverAndLoadAsync();

        _service.SendMessage(new IslandMessage
        {
            Title = "WinIsland 已启动",
            Text = "右键托盘图标打开设置",
            Glyph = "\uE7E8",
            Duration = TimeSpan.FromSeconds(4),
        });
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        bool visible = _settings.Get("island.visible", true);
        return new[]
        {
            new TrayMenuItem("打开设置", () => OpenSettingsWindow(null)),
            TrayMenuItem.Separator,
            new TrayMenuItem("显示灵动岛", () => _settings.Set("island.visible", !visible), visible),
            TrayMenuItem.Separator,
            new TrayMenuItem("退出", ExitApp),
        };
    }

    private void OpenSettingsWindow(string? pageId)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_service, _settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        if (pageId != null) _settingsWindow.SelectPage(pageId);
        _settingsWindow.Activate();
    }

    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        _tray?.Dispose();
        await _pluginLoader.ShutdownAllAsync();
        _pluginLoader.Dispose();
        _settingsWindow?.Close();
        _island.Close();
        Environment.Exit(0);
    }
}
