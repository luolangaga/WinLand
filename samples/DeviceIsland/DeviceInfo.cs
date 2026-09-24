namespace DeviceIsland;

/// <summary>设备大类：决定图标、可用动作和分组。</summary>
internal enum DeviceKind
{
    /// <summary>可移动磁盘 / U 盘 / 移动硬盘。</summary>
    Drive,

    /// <summary>音频输出端点（耳机、扬声器、蓝牙耳机）。</summary>
    Audio,

    /// <summary>游戏手柄 / 控制器。</summary>
    Gamepad,
}

/// <summary>
/// 岛上一台设备的最小信息集 —— 视图只认这个对象，三个「家族」的扫描器都产出它。
/// <see cref="Id"/> 全局唯一（带家族前缀），插拔判定靠它做 diff。
/// </summary>
internal sealed class DeviceInfo
{
    public DeviceKind Kind { get; init; }

    /// <summary>稳定标识，如 drive:E: / audio:\\?\SWD#... / gamepad:045E:02FD。</summary>
    public string Id { get; init; } = "";

    /// <summary>主标题（卷标 / 端点名 / 手柄名）。</summary>
    public string Name { get; init; } = "";

    /// <summary>副标题（盘符 + 容量 / 适配器 / VID:PID）。</summary>
    public string Detail { get; init; } = "";

    public string Glyph { get; init; } = "\uE88E";

    /// <summary>是否提供「安全弹出」。</summary>
    public bool CanEject { get; init; }

    /// <summary>「打开」的目标：驱动器根（"E:\"）；音频/手柄为 null，走系统设置面板。</summary>
    public string? Target { get; init; }

    /// <summary>
    /// 这台设备在 PnP 里的实例 ID（音频端点有；手柄、磁盘没有）。
    /// 用来沿设备树的父链找到它的物理节点，蓝牙电量就存在那条链上。为空的设备退化成按名字匹配。
    /// </summary>
    public string? PnpInstanceId { get; init; }

    /// <summary>
    /// 蓝牙设备上报的电量百分比。<b>读不到就是 null</b>（界面上不显示），从不猜测。
    /// 注意这是系统缓存值 —— 蓝牙电量由设备主动上报，Windows「设置 → 蓝牙」显示的就是同一份数据。
    /// </summary>
    public int? BatteryPercent { get; init; }

    /// <summary>换一份电量（其它字段原样保留）。读不到时传 null，等于把旧的读数清掉。</summary>
    internal DeviceInfo WithBattery(int? percent) => new()
    {
        Kind = Kind,
        Id = Id,
        Name = Name,
        Detail = Detail,
        Glyph = Glyph,
        CanEject = CanEject,
        Target = Target,
        PnpInstanceId = PnpInstanceId,
        BatteryPercent = percent,
    };
}
