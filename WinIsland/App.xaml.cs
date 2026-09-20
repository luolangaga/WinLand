using Microsoft.UI.Xaml;
using WinIsland.Core;
using WinIsland.Core.Marketplace;
using WinIsland.Core.Plugins;
using WinIsland.Island;
using WinIsland.Modules.Battery;
using WinIsland.Modules.Media;
using WinIsland.Modules.Messaging;
using WinIsland.Settings;

namespace WinIsland;

public partial class App : Application
{
    private SettingsService _settings = null!;
    private IslandWindow _island = null!;
    private IslandService _service = null!;
    private PluginHost _plugins = null!;
    private MarketplaceService _marketplace = null!;
    private readonly PluginLogService _logs;
    private TrayIcon? _tray;
    internal SettingsWindow? _settingsWindow;
    private TaskbarHookHost _taskbarHook = null!;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _hookWatchdog = null!;
    private bool _exiting;

    public App()
    {
        _logs = new PluginLogService();
        InitializeComponent();

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                _logs.Host.Error("非 UI 线程未处理异常（进程即将终止）", exception);
            }
            else
            {
                _logs.Host.Error($"非 UI 线程未处理异常（进程即将终止）：{e.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logs.Host.Error("未观察的 Task 异常", e.Exception);
            e.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _settings = new SettingsService();
            _island = new IslandWindow(_settings, _logs);
            _service = new IslandService(_island, _settings);
            _service.SettingsOpenRequested += OpenSettingsWindow;

            _plugins = new PluginHost(_service, _settings, _island.DispatcherQueue, _logs);
            _marketplace = new MarketplaceService(_settings, _logs.Host);

            // 任务栏嵌入 hook（实验性）：注入/探活/失败自动回滚，没开或没有 DLL 时什么也不做
            _taskbarHook = new TaskbarHookHost(_settings, _logs.Host, _island.DispatcherQueue);
            _hookWatchdog = _island.DispatcherQueue.CreateTimer();
            _hookWatchdog.Interval = TimeSpan.FromSeconds(1);
            _hookWatchdog.IsRepeating = true;
            _hookWatchdog.Tick += (_, _) =>
            {
                try
                {
                    _taskbarHook.Tick();
                }
                catch (Exception ex)
                {
                    // WinUI 定时器回调里抛异常 = stowed exception = 进程直接死，必须兜住
                    _logs.Host.Error("任务栏 hook 看门狗回调异常（已忽略）", ex);
                }
            };
            _hookWatchdog.Start();

            _island.ShowIsland();

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            _tray = new TrayIcon("WinIsland - 灵动岛", iconPath)
            {
                DoubleClicked = () => OpenSettingsWindow(null),
                MenuProvider = BuildTrayMenu,
            };

            _plugins.RegisterBuiltIn(
                BuiltInManifest("media", "正在播放", "\uE8D6", "系统媒体会话，展示当前播放内容"),
                () => new MediaPlugin());
            _plugins.RegisterBuiltIn(
                BuiltInManifest("battery", "充电监控", "\uE857", "充电状态与实时功率"),
                () => new BatteryPlugin());
            _plugins.RegisterBuiltIn(
                BuiltInManifest("messenger", "发送消息", "\uE724", "手动发送灵动岛消息与自定义内容"),
                () => new MessagePlugin());

            await _plugins.InitializeAsync();

            _service.AddSettingsPage(new SettingsPageDescriptor(
                "general", "通用", "\uE713", () => new GeneralSettingsPage(_settings), 0));
            _service.AddSettingsPage(new SettingsPageDescriptor(
                "marketplace", "插件市场", "\uE719", () => new MarketplacePage(_marketplace, _plugins), 900));
            _service.AddSettingsPage(new SettingsPageDescriptor(
                "plugins", "插件管理", "\uE712", () => new PluginManagerPage(_plugins, _settings), 1000));

            _service.SendMessage(new IslandMessage
            {
                Title = "WinIsland 已启动",
                Text = "右键托盘图标打开设置",
                Glyph = "\uE7E8",
                Duration = TimeSpan.FromSeconds(4),
            });

            _logs.Host.Info($"WinIsland 已启动（宿主 SDK {IslandSdk.HostVersion}，插件目录 {_plugins.PluginsDirectory}）。");
        }
        catch (Exception ex)
        {
            _logs.Host.Error("应用启动失败", ex);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logs.Host.Error("UI 线程未处理异常（已拦截，应用继续运行）", e.Exception);
        e.Handled = true;
    }

    private static PluginManifest BuiltInManifest(string id, string name, string glyph, string description) => new()
    {
        Id = id,
        Name = name,
        Version = "1.0.0",
        IconGlyph = glyph,
        Description = description,
    };

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
        try
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_service, _settings);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            if (pageId != null) _settingsWindow.SelectPage(pageId);
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            // 以前这里是静默失败：窗口建好了却不可见，日志里什么都看不到
            _settingsWindow = null;
            _logs.Host.Error("打开设置窗口失败", ex);
        }
    }

    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            _tray?.Dispose();
            _hookWatchdog?.Stop();
            _taskbarHook?.Dispose();   // 请求 hook 移除已插入元素并自行 detach
            await _plugins.ShutdownAllAsync();
            _plugins.Dispose();
        }
        catch (Exception ex)
        {
            _plugins?.Logs.Host.Error("退出清理时发生异常", ex);
        }

        _settingsWindow?.Close();
        _island.Close();
        Environment.Exit(0);
    }
}
