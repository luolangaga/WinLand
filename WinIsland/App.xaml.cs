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
    private MessageHistory _messages = null!;
    private StartupGuard? _startupGuard;
    private GlobalHotkeys? _hotkeys;
    private readonly PluginLogService _logs;
    private TrayIcon? _tray;
    internal SettingsWindow? _settingsWindow;
    internal OnboardingWindow? _onboardingWindow;
    private bool _exiting;

    /// <summary>
    /// 启动时抢到的单实例闸门，由 Program.Main 在构造之后塞进来（XAML 生成的入口里那句 <c>new App()</c>
    /// 必须能编译，所以构造函数不能带参数）。有人重复启动时靠它把设置窗口亮出来。
    /// </summary>
    internal SingleInstance? InstanceGate { get; set; }

    /// <summary>
    /// 首次启动（没有别的实例在跑）时带的自动化参数：<see cref="Program.Main"/> 解析好传进来，
    /// 等岛、托盘、插件都就位之后再执行。null = 普通启动。
    /// </summary>
    internal CommandRequest? StartupCommand { get; set; }

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
#if DEBUG
            if (Diagnostics.PluginCenterPreview.Requested)
            {
                await Diagnostics.PluginCenterPreview.ShowAsync(_logs);
                return;
            }
            if (Diagnostics.ReliabilityTests.Requested)
            {
                await Diagnostics.ReliabilityTests.RunAsync(_logs);
                return;
            }
            if (Diagnostics.MotionPreview.Requested)
            {
                new Diagnostics.MotionPreview(_logs).Show();
                return;
            }
#endif
            _settings = new SettingsService(onError: (message, exception) => _logs.Host.Error(message, exception));

            // 启动守卫：连续多次「启动后没跑够健康时长」就判定为启动崩溃，这一次不加载外部插件。
            // 必须在插件初始化之前跑，安全模式才有意义。
            _startupGuard = StartupGuard.Record(_logs.Host);
            bool safeMode = _startupGuard.SafeModeRequested;
            if (safeMode)
            {
                _settings.Set(StartupGuard.SafeModeKey, true);
                _logs.Host.Warn($"连续 {_startupGuard.Attempt} 次启动未正常完成：本次以安全模式启动（不加载外部插件）。");
            }

            _messages = new MessageHistory(onError: (message, exception) => _logs.Host.Error(message, exception));

            _island = new IslandWindow(_settings, _logs);
            _island.HostServices = new AppIslandHost(_settings, _messages, OpenSettingsWindow, ExitApp);
            // 岛的 Fluent 配色跟随系统明暗主题：进程存活期间的切换也要生效，所以从启动就开始监听
            SystemTheme.Start(_island.DispatcherQueue);
            _service = new IslandService(_island, _settings, _logs.Host);
            _service.SettingsOpenRequested += OpenSettingsWindow;

            _plugins = new PluginHost(_service, _settings, _island.DispatcherQueue, _logs)
            {
                // 安全模式：只跑内置插件，外部插件（含它们加载的程序集）完全不碰
                SkipExternalPlugins = safeMode,
            };
            _marketplace = new MarketplaceService(_settings, _logs.Host);
            _updates = new UpdateService(_settings, _logs.Host);

            // 全局快捷键：岛本身只响应鼠标，没有键盘入口。注册失败（被占用）只记日志。
            _hotkeys = new GlobalHotkeys(_island.Handle, _logs.Host, OnHotkey);
            _hotkeys.Apply(_settings);

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
                "automation", "自动化", "\uE756",
                () => new AutomationSettingsPage(_settings, _messages, RefreshGlobalSettings), 100));
            _service.AddSettingsPage(new SettingsPageDescriptor(
                "plugins", "插件中心", "\uE7B8", () => new PluginCenterPage(_marketplace, _plugins, _settings), 900));
            _service.AddSettingsPage(new SettingsPageDescriptor(
                "about", "关于", "\uE946", () => new AboutSettingsPage(_updates), 1100));

            // 宿主内置的文件投放动作（打开 / 所在位置 / 复制路径 / 复制文本 / 保存图片）
            HostDropTargets.Register(_service, _logs.Host, _island.Handle);

            _service.SendMessage(new IslandMessage
            {
                Title = safeMode ? "WinIsland 以安全模式启动" : "WinIsland 已启动",
                Text = safeMode ? "外部插件已跳过，可在插件中心排查后退出安全模式" : "右键托盘图标打开设置",
                Glyph = safeMode ? "\uE7BA" : "\uE7E8",
                Duration = TimeSpan.FromSeconds(4),
            });

            _logs.Host.Info(
                $"WinIsland 已启动（宿主 SDK {IslandSdk.HostVersion}，程序版本 {_updates.CurrentVersionText}，插件目录 {_plugins.PluginsDirectory}）。");

            // 有人重复启动时把自己亮出来。挂在这里而不是构造函数里：早到的通知会被闸门留到这一刻再送过来，
            // 因此回调跑的时候设置页、托盘、岛都已经就位。
            // 带自动化参数（--notify / winisland://）时执行那些请求，而不是弹设置窗口。
            // 回调在事件线程上：只负责把请求搬回 UI 线程。
            InstanceGate?.ListenForActivation(requests => _island.DispatcherQueue.TryEnqueue(() =>
            {
                if (requests.Count == 0)
                {
                    OnDuplicateLaunch();
                    return;
                }

                foreach (var request in requests) OnCommandRequest(request);
            }));

            _ = AutoCheckUpdatesAsync();

            // 首次启动就带着自动化参数：等岛、托盘、插件都就位之后再执行
            if (StartupCommand is { } startup)
            {
                StartupCommand = null;
                OnCommandRequest(startup);
            }

            // 上次运行期间没来得及处理的请求（进程正好在退出、或脚本先于本实例启动）：
            // 整批补执行一次，脚本调用不会凭空丢掉
            foreach (var pending in SingleInstance.TakeRequests())
            {
                OnCommandRequest(pending);
            }

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
    /// 检测到有人重复启动本应用（单实例闸门已经拦下那个进程）：把自己亮出来。
    /// 重复点图标的人多半就是想找设置窗口 —— 岛只有贴着屏幕边的一小条，光提醒「已经开了」等于什么都没说。
    /// </summary>
    private void OnDuplicateLaunch()
    {
        _logs.Host.Info("检测到重复启动：已拒绝新实例，并把设置窗口调到前面。");
        OpenSettingsWindow(null);
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

    /// <summary>
    /// 全局快捷键回调（UI 线程由热键窗口过程直接触发，动作都很轻）。
    /// </summary>
    private void OnHotkey(string action)
    {
        switch (action)
        {
            case GlobalHotkeys.ToggleIslandAction:
                _island.ToggleExpandedFromKeyboard();
                break;

            case GlobalHotkeys.PlayPauseAction:
                // 不直接调媒体插件（宿主不该依赖具体插件）：广播一条总线消息，
                // 由内置的「正在播放」自己接。没有插件响应时就是什么都没发生。
                _service.Bus.Publish(BuiltInTopics.MediaPlayPause, null);
                break;

            case GlobalHotkeys.QuietAction:
                if (QuietMode.IsActive(_settings)) QuietMode.End(_settings);
                else QuietMode.Snooze(_settings, TimeSpan.FromMinutes(30));
                break;
        }
    }

    /// <summary>
    /// 重复启动时转交过来的自动化请求（脚本、URI、其它程序）。在 UI 线程执行。
    /// 任何一条失败都只记日志 —— 自动化请求不该把应用带下水。
    /// </summary>
    private void OnCommandRequest(CommandRequest? request)
    {
        if (request == null)
        {
            // 只是又点了一次图标：把设置窗口亮出来，与之前的行为一致
            OnDuplicateLaunch();
            return;
        }

        foreach (var warning in request.Warnings)
        {
            _logs.Host.Warn($"命令行参数：{warning}");
        }

        try
        {
            ExecuteCommand(request);
        }
        catch (Exception ex)
        {
            _logs.Host.Error($"执行自动化请求失败（{request.Kind}）", ex);
        }
    }

    private void ExecuteCommand(CommandRequest request)
    {
        switch (request.Kind)
        {
            case CommandKind.Notify:
                _service.SendMessage(new IslandMessage
                {
                    Title = request.Title ?? "通知",
                    Text = request.Text,
                    Glyph = string.IsNullOrWhiteSpace(request.Glyph) ? "\uE8BD" : request.Glyph!,
                    AccentColor = ParseColor(request.Accent),
                    Duration = TimeSpan.FromSeconds(request.DurationSeconds ?? 4),
                });
                _logs.Host.Info($"命令行通知：{request.Title}");
                break;

            case CommandKind.Settings:
                OpenSettingsWindow(request.Page);
                break;

            case CommandKind.Show:
                _settings.Set("island.visible", true);
                _island.ShowIsland();
                break;

            case CommandKind.Quiet:
                QuietMode.Snooze(_settings, TimeSpan.FromMinutes(request.QuietMinutes));
                _logs.Host.Info($"命令行静默 {request.QuietMinutes} 分钟。");
                break;

            case CommandKind.Unquiet:
                QuietMode.End(_settings);
                break;

            case CommandKind.Help:
                Win32.ShowStartupMessage(CommandLine.HelpText, "WinIsland");
                break;
        }
    }

    /// <summary>把 <c>#RRGGBB</c> / <c>RRGGBB</c> 解析成颜色；解析不出来就返回 null（用默认强调色）。</summary>
    private static Windows.UI.Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int value))
            return null;
        return Windows.UI.Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
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
        bool quiet = QuietMode.IsActive(_settings);
        return new[]
        {
            new TrayMenuItem("打开设置", () => OpenSettingsWindow(null)),
            BuildUpdateMenuItem(),
            TrayMenuItem.Separator,
            new TrayMenuItem("显示灵动岛", () => _settings.Set("island.visible", !visible), visible),
            quiet
                ? new TrayMenuItem($"结束静默（还剩 {QuietMode.DescribeRemaining(_settings)}）", () => QuietMode.End(_settings))
                : new TrayMenuItem("静默 30 分钟", () => QuietMode.Snooze(_settings, TimeSpan.FromMinutes(30))),
            TrayMenuItem.Separator,
            new TrayMenuItem("退出", ExitApp),
        };
    }

    /// <summary>
    /// 设置页里那些「改完要立刻生效」的宿主能力（全局快捷键 / 勿扰）统一走这里：
    /// 重新注册热键 + 让岛按新设置重算一遍。设置页不需要知道这些细节。
    /// </summary>
    private void RefreshGlobalSettings()
    {
        try
        {
            // Apply 自己带总开关（island.hotkeys 关掉时一个热键都不注册），不必销毁再重建
            _hotkeys?.Apply(_settings);
        }
        catch (Exception ex)
        {
            _logs.Host.Warn($"刷新全局快捷键失败：{ex.Message}");
        }

        _island.RefreshFromSettings();
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
            _hotkeys?.Dispose();
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
        _messages?.Flush();
        // 正常退出即视为健康启动：清掉守卫标记，下次启动不再累加失败次数
        _startupGuard?.Dispose();
        await _settings.FlushAsync();
        _settings.Dispose();
        Environment.Exit(0);
    }
}
