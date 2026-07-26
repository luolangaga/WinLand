using Microsoft.UI.Xaml;
using WinIsland.Core;
using WinIsland.Island;
using WinIsland.Modules;
using WinIsland.Modules.Media;
using WinIsland.Modules.Messaging;
using WinIsland.Settings;

namespace WinIsland;

/// <summary>
/// 应用入口：创建灵动岛窗口、托盘图标，注册功能模块。
/// </summary>
public partial class App : Application
{
    private SettingsService _settings = null!;
    private IslandWindow _island = null!;
    private IslandService _service = null!;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private readonly List<IIslandModule> _modules = new();
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

        _island.ShowIsland();

        // 托盘图标
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _tray = new TrayIcon("WinIsland - 灵动岛", iconPath)
        {
            DoubleClicked = () => OpenSettingsWindow(null),
            MenuProvider = BuildTrayMenu,
        };

        // 注册功能模块（在这里加入你自己的 IIslandModule 实现）
        _modules.Add(new GeneralModule());
        _modules.Add(new MediaModule());
        _modules.Add(new MessageModule());
        foreach (var module in _modules)
        {
            try
            {
                await module.InitializeAsync(_service);
            }
            catch
            {
                // 单个模块初始化失败不影响整体
            }
        }

        // 首次启动打个招呼
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
        foreach (var module in _modules)
        {
            try
            {
                await module.ShutdownAsync();
            }
            catch
            {
                // 忽略模块退出异常
            }
        }
        _settingsWindow?.Close();
        _island.Close();
        Environment.Exit(0);
    }
}
