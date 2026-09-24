using Microsoft.UI.Xaml;
using WinIsland.Core;
using WinIsland.Core.DropTargets;
using WinIsland.Core.Marketplace;
using WinIsland.Core.Plugins;
using WinIsland.Core.Update;
using WinIsland.Island;
using WinIsland.Modules.Battery;
using WinIsland.Modules.Media;
using WinIsland.Modules.Messaging;
using WinIsland.Onboarding;
using WinIsland.Settings;

namespace WinIsland;

public partial class App : Application
{
    private SettingsService _settings = null!;
    private IslandWindow _island = null!;
    private IslandService _service = null!;
    private PluginHost _plugins = null!;
    private MarketplaceService _marketplace = null!;
    private UpdateService _updates = null!;
    private readonly PluginLogService _logs;
    private TrayIcon? _tray;
    internal SettingsWindow? _settingsWindow;
    internal OnboardingWindow? _onboardingWindow;
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
            _service = new IslandService(_island, _settings, _logs.Host);
            _service.SettingsOpenRequested += OpenSettingsWindow;

            _plugins = new PluginHost(_service, _settings, _island.DispatcherQueue, _logs);
            _marketplace = new MarketplaceService(_settings, _logs.Host);
            _updates = new UpdateService(_settings, _logs.Host);

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
                "general", "通用", "\uE713", () => new GeneralSettingsPage(_settings, OpenOnboardingWindow), 0));
            _service.AddSettingsPage(new SettingsPageDescriptor(
                "marketplace", "插件市场", "\uE719", () => new MarketplacePage(_marketplace, _plugins), 900));
            _service.AddSettingsPage(new SettingsPageDescriptor(
                "plugins", "插件管理", "\uE712", () => new PluginManagerPage(_plugins, _settings), 1000));
            _service.AddSettingsPage(new SettingsPageDescriptor(
                "about", "关于", "\uE946", () => new AboutSettingsPage(_updates), 1100));

            // 宿主内置的文件投放动作（打开 / 所在位置 / 复制路径）
            HostDropTargets.Register(_service, _logs.Host);

            _service.SendMessage(new IslandMessage
            {
                Title = "WinIsland 已启动",
                Text = "右键托盘图标打开设置",
                Glyph = "\uE7E8",
                Duration = TimeSpan.FromSeconds(4),
            });

            _logs.Host.Info(
                $"WinIsland 已启动（宿主 SDK {IslandSdk.HostVersion}，程序版本 {_updates.CurrentVersionText}，插件目录 {_plugins.PluginsDirectory}）。");

            _ = AutoCheckUpdatesAsync();

            if (!_settings.Get(OnboardingWindow.DoneSettingKey, false))
            {
                OpenOnboardingWindow();
            }
        }
        catch (Exception ex)
        {
            _logs.Host.Error("应用启动失败", ex);
        }
    }

    /// <summary>
    /// 启动后的后台更新检查：只提醒不安装。延迟一会儿再跑，避免和启动抢资源；
    /// 自动检查被关掉、或距上次检查不到 6 小时就直接跳过。
    /// </summary>
    private async Task AutoCheckUpdatesAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (!_updates.ShouldAutoCheck()) return;

            var result = await _updates.CheckAsync(CancellationToken.None);
            if (result.Status != UpdateStatus.UpdateAvailable || result.Release is not { } release) return;
            if (_updates.IsSkipped(release)) return;

            _service.SendMessage(new IslandMessage
            {
                Title = $"发现新版本 {release.Tag}",
                Text = "设置 → 关于 可查看更新说明",
                Glyph = "\uE72C",
                Duration = TimeSpan.FromSeconds(6),
            });
        }
        catch (Exception ex)
        {
            _logs.Host.Warn($"自动检查更新失败：{ex.Message}");
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
            BuildUpdateMenuItem(),
            TrayMenuItem.Separator,
            new TrayMenuItem("显示灵动岛", () => _settings.Set("island.visible", !visible), visible),
            TrayMenuItem.Separator,
            new TrayMenuItem("退出", ExitApp),
        };
    }

    /// <summary>托盘里的更新入口：已经发现新版本时直接把版本号写在菜单上。</summary>
    private TrayMenuItem BuildUpdateMenuItem()
    {
        if (_updates.Available is { } release && !_updates.IsSkipped(release))
        {
            return new TrayMenuItem($"发现新版本 {release.Tag}", () => OpenSettingsWindow("about"));
        }

        return new TrayMenuItem("检查更新…", () =>
        {
            OpenSettingsWindow("about");
            _ = _updates.CheckAsync(CancellationToken.None);
        });
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

    private void OpenOnboardingWindow()
    {
        try
        {
            if (_onboardingWindow == null)
            {
                _onboardingWindow = new OnboardingWindow(_settings, _marketplace, _plugins, () => OpenSettingsWindow(null));
                _onboardingWindow.Closed += (_, _) => _onboardingWindow = null;
            }
            _onboardingWindow.Activate();
        }
        catch (Exception ex)
        {
            _onboardingWindow = null;
            _logs.Host.Error("打开新手引导窗口失败", ex);
        }
    }

    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            _tray?.Dispose();
            await _plugins.ShutdownAllAsync();
            _plugins.Dispose();
        }
        catch (Exception ex)
        {
            _plugins?.Logs.Host.Error("退出清理时发生异常", ex);
        }

        _onboardingWindow?.Close();
        _settingsWindow?.Close();
        _island.Close();
        Environment.Exit(0);
    }
}
