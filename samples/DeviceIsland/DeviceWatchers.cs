using Windows.Gaming.Input;

namespace DeviceIsland;

/// <summary>日志回调：(消息, 是否错误)。插件把它接到自己的 Log 上。</summary>
internal delegate void DeviceLog(string message, bool isError);

/// <summary>
/// 一个「设备家族」的扫描器。三个实现各自负责发现自己的设备，
/// 插件只做汇总 + diff，不关心底层用的是扫描、DeviceWatcher 还是系统事件。
/// <see cref="Changed"/> 可能在**任意线程**触发，订阅方需自行编组回 UI 线程。
/// </summary>
internal interface IDeviceSource : IDisposable
{
    DeviceKind Kind { get; }

    IReadOnlyList<DeviceInfo> Current { get; }

    /// <summary>首次枚举是否已完成 —— 完成前的设备视为「本来就在」，不弹插拔提示。</summary>
    bool Seeded { get; }

    event Action? Changed;

    void Start();
}

/// <summary>
/// 磁盘（U 盘 / 移动硬盘 / 存储卡）。
///
/// 识别方式：枚举所有驱动器，用 IOCTL 读**总线类型**，只保留「总线 = USB」或「系统标为可移动」的。
/// 这样内部固定盘（NVMe/SATA）会被排除，而 USB 移动硬盘（系统常报 Fixed）也能认出来。
///
/// 扫描本身放在后台线程（<see cref="DriveInfo.GetDrives"/> 遇到掉线的网络盘会卡），
/// 由插件的轮询定时器和「设备变更广播窗口」共同触发；重叠触发会被直接跳过。
/// </summary>
internal sealed class DriveSource : IDeviceSource
{
    private readonly DeviceLog _log;
    private readonly List<DeviceInfo> _current = new();
    private readonly object _gate = new();
    private int _scanning;

    public DriveSource(DeviceLog log) => _log = log;

    public DeviceKind Kind => DeviceKind.Drive;
    public event Action? Changed;

    public bool Seeded { get; private set; }

    public IReadOnlyList<DeviceInfo> Current
    {
        get { lock (_gate) return _current.ToArray(); }
    }

    public void Start() => RequestRefresh();

    /// <summary>请求扫一次。已经在扫就直接返回（不排队：下一次轮询/广播会再来）。</summary>
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;

        _ = Task.Run(() =>
        {
            try
            {
                var next = Scan();
                var changed = false;
                lock (_gate)
                {
                    changed = !SameSnapshot(_current, next);
                    if (changed)
                    {
                        _current.Clear();
                        _current.AddRange(next);
                    }
                }
                Seeded = true;
                if (changed) Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _log($"扫描磁盘失败（已忽略）：{ex.Message}", true);
            }
            finally
            {
                Interlocked.Exchange(ref _scanning, 0);
            }
        });
    }

    private static List<DeviceInfo> Scan()
    {
        var list = new List<DeviceInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            DriveType type;
            try
            {
                root = drive.Name;          // "E:\"
                type = drive.DriveType;
            }
            catch
            {
                continue;
            }

            var (busType, vendor, product) = DeviceActions.QueryStorage(root);
            var isExternal = busType == NativeMethods.BusTypeUsb || type == DriveType.Removable;
            if (!isExternal) continue;

            bool ready;
            long total;
            string label;
            try
            {
                ready = drive.IsReady;
                total = ready ? drive.TotalSize : 0;
                label = ready ? drive.VolumeLabel : "";
            }
            catch
            {
                continue;
            }

            if (!ready) continue;           // 空读卡器等：没介质就不算一台设备

            var letter = root.TrimEnd('\\'); // "E:"
            var name = !string.IsNullOrWhiteSpace(label) ? label
                     : !string.IsNullOrWhiteSpace(product) ? product
                     : "可移动磁盘";

            var parts = new List<string> { letter };
            if (!string.IsNullOrWhiteSpace(vendor) && !name.Contains(vendor, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(vendor);
            }
            if (total > 0) parts.Add(FormatSize(total));

            list.Add(new DeviceInfo
            {
                Kind = DeviceKind.Drive,
                Id = "drive:" + letter,
                Name = name,
                Detail = string.Join(" · ", parts),
                Glyph = "\uE88E",
                CanEject = true,
                Target = root,
            });
        }

        return list;
    }

    private static bool SameSnapshot(List<DeviceInfo> a, List<DeviceInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name || a[i].Detail != b[i].Detail) return false;
        }
        return true;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    public void Dispose()
    {
        // 没有常驻句柄要放；扫描任务自带 try/catch，会自己结束
    }
}

/// <summary>
/// 音频输出端点 —— <b>只认「用户插进来的外接耳机」</b>：USB 耳机、蓝牙耳机、3.5mm 耳机口。
///
/// <para>
/// 不用 WinRT 的 <c>DeviceInformation.CreateWatcher(DeviceClass.AudioRender)</c>：它只按
/// 「是不是活动的输出端点」筛，于是**虚拟声卡**（网易虚拟音频设备、Nahimic、VB-Audio…）和
/// **板载扬声器**都会被报出来 —— 这些不是插进来的设备，不该在岛上冒头。
/// </para>
///
/// <para>
/// 改走 Core Audio（<see cref="AudioEndpoints"/>），按「枚举器总线 + 物理形态 + 设备状态」三把尺子筛：
/// 虚拟/软件设备一律排掉，板载声卡只留耳机形态，且必须处于 ACTIVE（真的插着）。
/// 3.5mm 耳机口没插东西时系统正是把它标成 UNPLUGGED，所以插拔能被准确捕捉。
/// </para>
///
/// <para>
/// 每台耳机都会顺设备树摸一次底细（<see cref="AudioDeviceTree.Inspect"/>）：副标题上的连接方式以
/// **设备树**为准（实测蓝牙耳机的枚举器会谎报 <c>INTELAUDIO</c>），蓝牙的顺带读出电量。
/// 有线耳机、3.5mm 耳机口本来就没有电量，读不到就不显示。
/// </para>
///
/// <para>
/// 扫描放后台线程（COM 调用不占 UI 线程），由插件的轮询定时器和设备变更广播共同触发；重叠触发直接跳过。
/// </para>
/// </summary>
internal sealed class AudioSource : IDeviceSource
{
    private readonly DeviceLog _log;
    private readonly List<DeviceInfo> _current = new();
    private readonly object _gate = new();
    private int _scanning;
    private bool _loggedOnce;

    public AudioSource(DeviceLog log) => _log = log;

    public DeviceKind Kind => DeviceKind.Audio;
    public event Action? Changed;

    public bool Seeded { get; private set; }

    public IReadOnlyList<DeviceInfo> Current
    {
        get { lock (_gate) return _current.ToArray(); }
    }

    public void Start() => RequestRefresh();

    /// <summary>请求扫一次。已经在扫就直接返回（不排队：下一次轮询/广播会再来）。</summary>
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;

        _ = Task.Run(() =>
        {
            try
            {
                var all = AudioEndpoints.EnumerateAll();
                var kept = AudioEndpoints.SelectExternalHeadsets(all);

                // 顺设备树摸每台耳机的底细：到底挂在蓝牙/USB/板载哪条总线上，蓝牙的顺带取电量。
                // 电量只对蓝牙设备有（有线耳机、3.5mm 耳机口本来就没有），读不到就是 null，界面不显示。
                var battery = kept.Count > 0
                    ? AudioDeviceTree.ReadBatteryByAddress()
                    : new Dictionary<string, int>();
                var next = kept
                    .Select(e => ToDevice(e, AudioDeviceTree.Inspect(e.DevNodeId, battery)))
                    .ToList();

                var changed = false;
                lock (_gate)
                {
                    changed = !SameSnapshot(_current, next);
                    if (changed)
                    {
                        _current.Clear();
                        _current.AddRange(next);
                    }
                }

                // 首次 + 每次「换了一批」时打一行日志：顺带交代被忽略的都是些什么，省得以后怀疑漏检
                if (changed || !_loggedOnce)
                {
                    _loggedOnce = true;
                    var skipped = all.Where(e => !AudioEndpoints.IsExternalHeadset(e))
                                     .GroupBy(AudioEndpoints.ExplainSkip)
                                     .Select(g => $"{g.Key}×{g.Count()}");

                    _log($"音频端点共 {all.Count} 个 → 认出外接耳机 {next.Count} 个"
                         + (next.Count > 0 ? $"（{string.Join("、", kept.Select(k => k.Name))}）" : "")
                         + $"；已忽略：{(skipped.Any() ? string.Join("、", skipped) : "无")}", false);
                }

                Seeded = true;
                if (changed) Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _log($"扫描音频端点失败（已忽略）：{ex.Message}", true);
            }
            finally
            {
                Interlocked.Exchange(ref _scanning, 0);
            }
        });
    }

    private static DeviceInfo ToDevice(AudioEndpoint endpoint, AudioLinkage linkage) => new()
    {
        Kind = DeviceKind.Audio,
        Id = "audio:" + endpoint.Id,
        Name = string.IsNullOrWhiteSpace(endpoint.Name) ? "耳机" : endpoint.Name,
        Detail = AudioEndpoints.DescribeLink(linkage.Link, endpoint),
        Glyph = "\uE7F6",
        CanEject = false,
        Target = null,
        PnpInstanceId = endpoint.DevNodeId,
        BatteryPercent = linkage.BatteryPercent,
    };

    private static bool SameSnapshot(List<DeviceInfo> a, List<DeviceInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            // 电量也算进快照：蓝牙电量由设备上报，变了要能刷到界面上
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name || a[i].Detail != b[i].Detail
                || a[i].BatteryPercent != b[i].BatteryPercent) return false;
        }
        return true;
    }

    public void Dispose()
    {
        // 没有常驻句柄要放：每次扫描自建自销 COM 对象，扫描任务自带 try/catch，会自己结束
    }
}

/// <summary>
/// 游戏手柄。用 <c>RawGameController</c> 而不是 <c>Gamepad</c>：
/// 前者带 <c>NonRoamableId</c>（稳定标识）、<c>DisplayName</c>、厂商/产品 ID、是否无线，
/// 后者这些都没有（只有 Gamepads / IsWireless / Headset / Vibration）。
/// 两者的事件都在插拔时实时回调（已在本机非打包进程实测可用）。
/// </summary>
internal sealed class GamepadSource : IDeviceSource
{
    private readonly DeviceLog _log;
    private readonly List<DeviceInfo> _current = new();
    private readonly object _gate = new();
    private bool _hooked;

    public GamepadSource(DeviceLog log) => _log = log;

    public DeviceKind Kind => DeviceKind.Gamepad;
    public event Action? Changed;

    public bool Seeded { get; private set; }

    public IReadOnlyList<DeviceInfo> Current
    {
        get { lock (_gate) return _current.ToArray(); }
    }

    public void Start()
    {
        try
        {
            foreach (var pad in RawGameController.RawGameControllers) Add(ToDevice(pad), quiet: true);

            RawGameController.RawGameControllerAdded += OnAdded;
            RawGameController.RawGameControllerRemoved += OnRemoved;
            _hooked = true;
            _log("手柄监听已启动", false);
        }
        catch (Exception ex)
        {
            _log($"手柄监听不可用（已跳过）：{ex.Message}", true);
        }
        finally
        {
            Seeded = true;
            Changed?.Invoke();
        }
    }

    private static DeviceInfo ToDevice(RawGameController pad)
    {
        var id = string.IsNullOrWhiteSpace(pad.NonRoamableId) ? Guid.NewGuid().ToString("N") : pad.NonRoamableId;

        var connection = pad.IsWireless ? "无线" : "有线";
        var hardware = $"{pad.HardwareVendorId:X4}:{pad.HardwareProductId:X4}";

        return new DeviceInfo
        {
            Kind = DeviceKind.Gamepad,
            Id = "gamepad:" + id,
            Name = string.IsNullOrWhiteSpace(pad.DisplayName) ? "游戏手柄" : pad.DisplayName,
            Detail = $"{connection} · {hardware}",
            Glyph = "\uE7FC",
            CanEject = false,
            Target = null,
        };
    }

    private void OnAdded(object? sender, RawGameController pad) => Add(ToDevice(pad), quiet: false);

    private void OnRemoved(object? sender, RawGameController pad)
    {
        var id = "gamepad:" + pad.NonRoamableId;
        bool removed;
        lock (_gate)
        {
            removed = _current.RemoveAll(d => d.Id == id) > 0;
        }
        if (removed) Changed?.Invoke();
    }

    private void Add(DeviceInfo device, bool quiet)
    {
        lock (_gate)
        {
            if (_current.Any(d => d.Id == device.Id)) return;
            _current.Add(device);
        }
        if (!quiet) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (!_hooked) return;
        _hooked = false;
        try
        {
            RawGameController.RawGameControllerAdded -= OnAdded;
            RawGameController.RawGameControllerRemoved -= OnRemoved;
        }
        catch
        {
            // 卸载阶段的收尾失败不影响宿主
        }
    }
}
