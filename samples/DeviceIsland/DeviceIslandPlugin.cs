using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace DeviceIsland;

/// <summary>
/// 设备插拔岛：插 U 盘 / 耳机 / 手柄时在岛上自动冒一条提示，展开后逐台列出当前设备，
/// 每台都能「打开」，磁盘还能「安全弹出」。
///
/// 关于「一个设备一个岛」：当前插件 SDK 里一个插件只能注册**一条**活动岛
/// （宿主 <c>IslandWindow.SetLive</c> 按 owner=插件 id 去重），所以这里做成
/// 「一条岛 + 展开逐行列出」，每台设备仍是独立条目、独立动作。
///
/// 检测全部用 Windows 原生能力，零第三方依赖：
///   · 磁盘：枚举驱动器 + IOCTL 读总线类型（认 USB），由轮询 + 设备变更广播窗口共同触发；
///   · 耳机：Core Audio（MMDevice）读「枚举器总线 + 物理形态 + 设备状态」，**只认插着的外接耳机**
///     —— 虚拟声卡（网易、Nahimic…）和板载扬声器一律不算，见 <see cref="AudioEndpoints"/>；
///     每台的连接方式（蓝牙 / USB / 3.5mm）和蓝牙电量都顺设备树判定，见 <see cref="AudioDeviceTree"/>；
///   · 手柄：<c>RawGameController</c> 的 Added / Removed 事件。
/// </summary>
public sealed class DeviceIslandPlugin : IslandPluginBase
{
    private static readonly Windows.UI.Color Accent = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    private static readonly Windows.UI.Color WarnAccent = Windows.UI.Color.FromArgb(255, 0xE8, 0x6C, 0x3F);

    /// <summary>「刚插入 / 刚移除」这句话在摘要行上保留多久。</summary>
    private static readonly TimeSpan EventTextLifetime = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 默认岛上优先级。**宿主同时最多只渲染 10 条活动**（主岛 1 + 展开队列 9 张卡：每列 3 张、
    /// 最多 3 列，见宿主 <c>IslandWindow.MaxQueueCards</c>，多出来的换到右边一列），按优先级从大到小
    /// 取前 10 —— 排到第 11 名及以后的插件**连展开都看不到**（既不显示、也不报错，静默消失）。
    ///
    /// 常驻插件实测：媒体 100（播放时）/ 剪贴板 55 / 电池 50 / 天气 40。这里取 45：
    /// 刚好高于天气，稳进常驻插件那一档。要调就改设置里的「岛上优先级」
    /// （宿主「插件管理」里填的 <c>plugin.device-island.priority</c> 会覆盖它）。
    /// </summary>
    private const int DefaultPriority = 45;

    private readonly DriveSource _drive;
    private readonly AudioSource _audio;
    private readonly GamepadSource _gamepad;
    private readonly List<DeviceInfo> _devices = new();

    private DeviceIslandView _view = null!;
    private DeviceSpotlightView? _spotlight;
    private IslandLiveContent _content = null!;
    private DeviceNotifyWindow? _notifyWindow;

    /// <summary>首次枚举全部完成前不弹插拔提示 —— 否则启动那一刻会把"已存在的设备"全报一遍。</summary>
    private bool _notificationsArmed;

    private string? _eventText;
    private DateTimeOffset _eventAt;
    private bool _stopped;

    public DeviceIslandPlugin()
    {
        _drive = new DriveSource(LogMessage);
        _audio = new AudioSource(LogMessage);
        _gamepad = new GamepadSource(LogMessage);
    }

    /// <summary>设置页用它刷新「当前检测到的设备」（始终在 UI 线程触发）。</summary>
    internal event Action? DevicesChanged;

    internal IReadOnlyList<DeviceInfo> Devices => _devices;

    /// <summary>Manifest 在基类里是 protected，设置页要显示名字，这里开一个内部入口。</summary>
    internal string DisplayName => Manifest.Name;

    protected override Task OnInitializeAsync()
    {
        Log.Info($"启动：{Manifest.Id} {Manifest.Version}，插件目录 {PluginDirectory}");

        _view = new DeviceIslandView(Manifest, OnOpenDevice, OnEjectDevice, Theme);
        // 岛体换主题（Fluent 跟随系统明暗）：代码搭的视图颜色烘在画刷里，就地重刷一遍
        Context.Theme.Changed += OnThemeChanged;
        Context.Register(new ActionDisposable(() => Context.Theme.Changed -= OnThemeChanged));

        _content = BuildContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE713",
            () => new DeviceSettingsPage(this), order: 180));

        // 设置项实时生效（Sync 每次都重新读设置，所以一个回调统管）
        foreach (var key in new[] { "enabled", "watchUsb", "watchAudio", "watchGamepad", "notifyArrival", "notifyRemoval" })
        {
            Context.Register(Context.OnSettingsChanged(key, OnSettingChanged));
        }

        // 优先级单独一条：IslandLiveContent.Priority 是 init-only，改它必须换一个新内容对象
        Context.Register(Context.OnSettingsChanged("priority", OnPriorityChanged));

        _drive.Changed += OnSourceChanged;
        _audio.Changed += OnSourceChanged;
        _gamepad.Changed += OnSourceChanged;

        _drive.Start();
        _audio.Start();
        _gamepad.Start();

        // 设备变更广播窗口：插拔瞬间触发立即重扫（比等轮询快）。建不起来就只靠轮询，
        // 它会在自己的线程上把失败原因写进日志。构造函数不阻塞，插件初始化不受影响。
        _notifyWindow = new DeviceNotifyWindow(LogMessage);
        _notifyWindow.DeviceChanged += OnDeviceBroadcast;

        // 轮询兜底：广播窗口对个别虚拟盘/网络盘可能漏事件
        Context.CreateTimer(TimeSpan.FromMilliseconds(1200), repeat: true, OnPoll);

        Sync();
        return Task.CompletedTask;
    }

    /// <summary>组装岛上内容。抽成方法是因为改优先级要重建一份（<c>Priority</c> 是 init-only）。</summary>
    private IslandLiveContent BuildContent() => new()
    {
        // 设备在场时占岛，但让位给媒体；见 DefaultPriority 里的名额说明
        Priority = Settings.Get("priority", DefaultPriority),
        OwnerLabel = Manifest.Name,
        OwnerGlyph = Manifest.IconGlyph,
        OwnerAccent = Accent,
        MorphView = _view,
        CompactSize = new Windows.Foundation.Size(248, 40),
        ExpandedSize = new Windows.Foundation.Size(420, 176),
        OnTap = OpenSpotlight,      // 点击岛体 = 打开聚光卡看全部设备
    };

    protected override Task OnShutdownAsync()
    {
        _stopped = true;

        _drive.Changed -= OnSourceChanged;
        _audio.Changed -= OnSourceChanged;
        _gamepad.Changed -= OnSourceChanged;

        _drive.Dispose();
        _audio.Dispose();
        _gamepad.Dispose();

        if (_notifyWindow is not null)
        {
            _notifyWindow.DeviceChanged -= OnDeviceBroadcast;
            _notifyWindow.Dispose();
            _notifyWindow = null;
        }

        DevicesChanged = null;
        SetContent(null);
        Log.Info("已停用");
        return Task.CompletedTask;
    }

    // ---- 事件源 → UI 线程 ----

    private void OnSourceChanged()
    {
        if (_stopped) return;
        Context.RunOnUI(Sync);          // DeviceWatcher / Gamepad 的回调在别的线程上
    }

    private void OnDeviceBroadcast()
    {
        if (_stopped) return;
        _drive.RequestRefresh();        // RequestRefresh 自己是线程安全的，不必编组
        _audio.RequestRefresh();        // USB 耳机拔插会走这里；3.5mm 耳机口靠下面的轮询兜
    }

    private void OnSettingChanged()
    {
        if (_stopped) return;
        Sync();
    }

    /// <summary>
    /// 改优先级：重建内容对象并重新登记一次（宿主按 owner 去重后重排队列），
    /// 否则新优先级要等到下次设备插拔才会生效。
    /// </summary>
    private void OnPriorityChanged()
    {
        if (_stopped) return;
        _content = BuildContent();
        UpdateContent();
    }

    private void OnPoll()
    {
        if (_stopped) return;

        _drive.RequestRefresh();
        _audio.RequestRefresh();        // 3.5mm 耳机口插拔不产生设备广播，只有轮询能捕捉

        // 插拔文案过期后回落成常规摘要
        if (_eventText is not null && DateTimeOffset.UtcNow - _eventAt > EventTextLifetime)
        {
            _eventText = null;
            Sync();
        }
    }

    // ---- 汇总 + diff（UI 线程）----

    private void Sync()
    {
        if (_stopped) return;

        var next = new List<DeviceInfo>();
        if (Settings.Get("watchUsb", true)) next.AddRange(_drive.Current);
        if (Settings.Get("watchAudio", true)) next.AddRange(_audio.Current);
        if (Settings.Get("watchGamepad", true)) next.AddRange(_gamepad.Current);

        var oldIds = new HashSet<string>(_devices.Select(d => d.Id), StringComparer.Ordinal);
        var newIds = new HashSet<string>(next.Select(d => d.Id), StringComparer.Ordinal);
        var added = next.Where(d => !oldIds.Contains(d.Id)).ToList();
        var removed = _devices.Where(d => !newIds.Contains(d.Id)).ToList();

        _devices.Clear();
        _devices.AddRange(next);

        // 注意：先取 silent 再武装。首次枚举完成那一次同步必须静默，
        // 否则「本来就在的设备」会被当成刚插入全报一遍。
        var silent = !_notificationsArmed;
        if (!silent)
        {
            if (added.Count > 0) NotifyDevices("设备已插入", added, isArrival: true);
            else if (removed.Count > 0) NotifyDevices("设备已移除", removed, isArrival: false);
        }

        if (!_notificationsArmed && _drive.Seeded && _audio.Seeded && _gamepad.Seeded)
        {
            _notificationsArmed = true;
        }

        UpdateContent();
        RefreshViews();
        DevicesChanged?.Invoke();
    }

    /// <summary>有设备在场才占岛；一台都没有就撤掉，不去挤别的插件。</summary>
    private void UpdateContent()
    {
        var shouldShow = Settings.Get("enabled", true) && _devices.Count > 0;
        SetContent(shouldShow ? _content : null);
    }

    private void RefreshViews()
    {
        _view.Apply(_devices, _eventText);
        _spotlight?.Apply(_devices);
    }

    private void NotifyDevices(string title, List<DeviceInfo> devices, bool isArrival)
    {
        var enabled = isArrival ? Settings.Get("notifyArrival", true) : Settings.Get("notifyRemoval", true);
        if (devices.Count == 0) return;

        var first = devices[0];
        var summary = devices.Count == 1 ? first.Name : $"{first.Name} 等 {devices.Count} 台";
        var verb = isArrival ? "已插入" : "已移除";

        _eventText = $"{verb} · {summary}";
        _eventAt = DateTimeOffset.UtcNow;

        Log.Info($"{title}：{string.Join("、", devices.Select(d => d.Name))}");

        if (!enabled) return;

        // 有电量就捎上（蓝牙设备才有，且得设备上报过；读不到就不提）
        var text = string.IsNullOrWhiteSpace(first.Detail) ? summary : $"{summary} · {first.Detail}";
        if (first.BatteryPercent is int percent) text += $" · 电量 {percent}%";

        Context.Island.ShowMessage(new IslandMessage
        {
            Title = title,
            Text = text,
            Glyph = first.Glyph,
            AccentColor = Accent,
            Duration = TimeSpan.FromSeconds(Math.Clamp(Settings.Get("notifySeconds", 3), 1, 15)),
        });
    }

    // ---- 动作 ----

    private void OnOpenDevice(DeviceInfo device)
    {
        if (DeviceActions.Open(device, out var error))
        {
            Log.Info($"已打开：{device.Name}");
            return;
        }

        ShowActionFailure("打开失败", device, error);
    }

    private void OnEjectDevice(DeviceInfo device)
    {
        if (DeviceActions.Eject(device, out var error))
        {
            Log.Info($"已安全弹出：{device.Name}");
            Context.Island.ShowMessage(new IslandMessage
            {
                Title = "已安全弹出",
                Text = device.Name,
                Glyph = device.Glyph,
                AccentColor = Accent,
                Duration = TimeSpan.FromSeconds(2),
            });
            _drive.RequestRefresh();
            return;
        }

        ShowActionFailure("无法弹出", device, error);
    }

    private void ShowActionFailure(string title, DeviceInfo device, string error)
    {
        Log.Warn($"{title}：{device.Name} — {error}");

        Context.Island.ShowMessage(new IslandMessage
        {
            Title = title,
            Text = error,
            Glyph = "\uE7BA",
            AccentColor = WarnAccent,
            Duration = TimeSpan.FromSeconds(4),
        });
    }

    /// <summary>
    /// 岛体换主题：岛视图就地重刷配色；聚光卡下次打开时自然按新主题重建
    /// （它是另一棵可视树，开着的时候不折腾它）。
    /// </summary>
    private void OnThemeChanged()
    {
        Log.Info($"岛体主题已切换为{(Theme.IsLight ? "浅色" : "深色")}，重刷岛视图配色。");
        _view?.RefreshTheme();
        _spotlight = null;
    }

    /// <summary>点击岛体 → 打开「超级展开」聚光卡（当前所有设备的完整列表）。</summary>
    private void OpenSpotlight()
    {
        _spotlight ??= new DeviceSpotlightView(Manifest, OnOpenDevice, OnEjectDevice, Theme);
        _spotlight.Apply(_devices);

        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlight,
            Size = new Windows.Foundation.Size(560, 480),
            OnClosed = () => _spotlight?.OnHostClosed(),
        });
    }

    // ---- 给设置页用的小接口 ----

    internal bool GetBool(string key, bool fallback) => Settings.Get(key, fallback);

    internal void SetBool(string key, bool value) => Settings.Set(key, value);

    internal int GetInt(string key, int fallback) => Settings.Get(key, fallback);

    internal void SetInt(string key, int value) => Settings.Set(key, value);

    internal void PreviewMessage() => Context.Island.ShowMessage(new IslandMessage
    {
        Title = Manifest.Name,
        Text = "这是一条测试提示 —— 真实插入设备时就是这样冒出来的",
        Glyph = Manifest.IconGlyph ?? "\uE88E",
        AccentColor = Accent,
        Duration = TimeSpan.FromSeconds(Math.Clamp(Settings.Get("notifySeconds", 3), 1, 15)),
    });

    private void LogMessage(string message, bool isError)
    {
        if (isError) Log.Warn(message);
        else Log.Info(message);
    }
}
