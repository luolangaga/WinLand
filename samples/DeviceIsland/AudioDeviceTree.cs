using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceIsland;

/// <summary>音频端点**实际**挂在哪种物理总线上（顺设备树看出来的）。</summary>
internal enum AudioLink
{
    /// <summary>没认出来 —— 父链走不到物理总线，通常是虚拟/软件音频设备。</summary>
    Unknown,

    /// <summary>蓝牙。</summary>
    Bluetooth,

    /// <summary>USB。</summary>
    Usb,

    /// <summary>板载声卡：3.5mm 耳机口、扬声器口、HDMI、SPDIF 都在这条总线上。</summary>
    Onboard,
}

/// <summary>一次设备树探测的结果：走哪种物理总线 +（蓝牙的话）读到的电量。</summary>
internal readonly record struct AudioLinkage(AudioLink Link, int? BatteryPercent);

/// <summary>
/// 顺设备树摸音频端点的「底细」：它到底插在蓝牙、USB 还是板载声卡上，以及蓝牙设备的电量。
///
/// <para>
/// <b>为什么不能只看端点自报的枚举器：</b>实测一副**蓝牙耳机**的端点枚举器报的是
/// <c>INTELAUDIO</c>（Windows 把蓝牙音频挂在 Intel 音频 offload 路径下），容易被说成
/// 「3.5mm 耳机口」；而它的设备树父链 <c>↑1</c> 是实打实的 <c>BTHENUM\…</c>。
/// 设备树不说谎，所以这里一律以父链为准。
/// </para>
///
/// <para>
/// 端点在 PnP 里的节点是 <c>SWD\MMDEVAPI\</c> + 端点 ID（构造出来的，见
/// <see cref="AudioEndpoints"/>），往上是它的物理设备节点。
/// </para>
///
/// <para>
/// <b>电量从哪来：</b>蓝牙电量存在 PnP 节点属性集
/// <c>{104EA319-6EE2-4701-BD47-8DDBF425BBE5}</c>（<c>DEVPKEY_Bluetooth_BatteryLevel</c> 家族）
/// 的 pid 2 上，「设置 → 蓝牙」显示的就是同一份（由设备主动上报、Windows 缓存）。
/// 同一台设备在设备树里的各个节点实例 ID 里都带同一个 **12 位十六进制蓝牙地址**，
/// 所以「端点 → 蓝牙祖先 → 取地址 → 查电量」可以精确匹配，不必按设备名做模糊猜测。
/// 读不到就返回 null —— 界面不显示，绝不猜。
/// </para>
///
/// <para>
/// <b>为什么不用 WinRT 的 GATT：</b>实测设备未连接时 <c>BluetoothLEDevice.FromIdAsync</c> 直接
/// 返回 null，GATT 到不了；而且只有支持标准 0x180F 服务的设备才读得到。这条路更短更稳，零第三方依赖。
/// </para>
/// </summary>
internal static class AudioDeviceTree
{
    /// <summary><c>DEVPKEY_Bluetooth_*</c> 属性集。</summary>
    private static readonly Guid BluetoothPropertySet = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

    /// <summary>pid 2 = 电量百分比（实测为 <c>DEVPROP_TYPE_UINT8</c>，值 0~100）。</summary>
    private const uint BatteryPercentPid = 2;

    /// <summary>顺设备树往上最多找几层 —— 实测卡在 1~2 层就够（最深的板载路径到 ↑6），留足余量。</summary>
    private const int MaxParentHops = 8;

    /// <summary>CfgMgr32 用 NULL 缓冲探属性大小时返回这个码，它是**正常**的，不是失败。</summary>
    private const int CrBufferSmall = 0x1A;

    /// <summary>设备实例 ID 尾段里「一段独立的 12 位十六进制」= 蓝牙地址。</summary>
    private static readonly Regex AddressPattern =
        new(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])", RegexOptions.Compiled);

    /// <summary>
    /// 顺父链往上走，第一眼认出物理总线就停：蓝牙 / USB / 板载。
    /// 蓝牙节点上顺手把地址抠出来查电量。任意线程可调用（只读、无状态）。
    /// </summary>
    internal static AudioLinkage Inspect(string pnpInstanceId, Dictionary<string, int> batteryByAddress)
    {
        if (string.IsNullOrWhiteSpace(pnpInstanceId)) return new AudioLinkage(AudioLink.Unknown, null);

        var devnode = LocateDevNode(pnpInstanceId);
        for (var hop = 0; devnode != 0 && hop <= MaxParentHops; hop++)
        {
            var id = DeviceIdOf(devnode);

            if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
            {
                var address = ExtractAddress(id);
                var percent = address is not null && batteryByAddress.TryGetValue(address, out var found)
                    ? found
                    : (int?)null;
                return new AudioLinkage(AudioLink.Bluetooth, percent);
            }

            if (id.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
            {
                return new AudioLinkage(AudioLink.Usb, null);
            }

            if (id.StartsWith("INTELAUDIO", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("HDAUDIO", StringComparison.OrdinalIgnoreCase))
            {
                return new AudioLinkage(AudioLink.Onboard, null);
            }

            if (CM_Get_Parent(out var parent, devnode, 0) != 0) break;
            devnode = parent;
        }

        return new AudioLinkage(AudioLink.Unknown, null);
    }

    /// <summary>
    /// 扫一遍所有蓝牙设备节点，返回「蓝牙地址 → 电量」。
    /// 同一台设备的多个节点共享一个地址，电量只挂在其中一个（Hands-Free / HID）上，其余读不到会被跳过。
    /// 任意线程可调用（只读、无状态）。
    /// </summary>
    internal static Dictionary<string, int> ReadBatteryByAddress()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in ListBluetoothDeviceIds())
        {
            var address = ExtractAddress(id);
            if (address is null || map.ContainsKey(address)) continue;

            var devnode = LocateDevNode(id);
            if (devnode == 0) continue;

            if (TryReadPercent(devnode, out var percent)) map[address] = percent;
        }

        return map;
    }

    /// <summary>
    /// 从设备实例 ID 里抠出蓝牙地址。只看最后一段（实例段）：
    /// <c>6&amp;19502778&amp;0&amp;80BF21CA057B_C00000000</c> → <c>80BF21CA057B</c>；
    /// <c>7&amp;210be92e&amp;0&amp;0029</c> 这种没有地址的实例段 → <c>null</c>。
    ///
    /// <para>
    /// 注意 <c>SWD\MMDEVAPI\{…}.{GUID}</c> 这类 ID 的 GUID 里天然含 12 位十六进制片段，
    /// 也会被抠出来（实测 <c>{60F6D318-…-8E7C9DBC1830}</c> 会得到 <c>8E7C9DBC1830</c>）。
    /// 调用方靠 <c>BTH*</c> 前缀把它挡在门外，所以这不构成误判 —— 但别拿这个函数当通用解析器用。
    /// </para>
    /// </summary>
    internal static string? ExtractAddress(string deviceInstanceId)
    {
        if (string.IsNullOrWhiteSpace(deviceInstanceId)) return null;

        var lastSlash = deviceInstanceId.LastIndexOf('\\');
        var tail = lastSlash >= 0 ? deviceInstanceId[(lastSlash + 1)..] : deviceInstanceId;

        var match = AddressPattern.Match(tail);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    // ---- CfgMgr32 ----

    private static List<string> ListBluetoothDeviceIds()
    {
        var result = new List<string>();

        if (CM_Get_Device_ID_List_SizeW(out var length, null, 0) != 0 || length == 0) return result;

        var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
        try
        {
            if (CM_Get_Device_ID_ListW(null, buffer, length, 0) != 0) return result;

            // 这是「一串字符串 + 双 null 结尾」，必须按已知长度读：
            // PtrToStringUni(ptr) 会在第一个 '\0' 处截断，只能拿到第一个设备（踩过）。
            var text = Marshal.PtrToStringUni(buffer, (int)length) ?? "";
            foreach (var id in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) result.Add(id);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static uint LocateDevNode(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var devnode, instanceId, 0) == 0) return devnode;
        // 0x1 = CM_LOCATE_DEVNODE_NORMAL，对某些已移除的节点更宽容
        return CM_Locate_DevNodeW(out devnode, instanceId, 1) == 0 ? devnode : 0;
    }

    private static string DeviceIdOf(uint devnode)
    {
        const int capacity = 1024;
        var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            return CM_Get_Device_IDW(devnode, buffer, capacity, 0) == 0
                ? Marshal.PtrToStringUni(buffer) ?? ""
                : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadPercent(uint devnode, out int percent)
    {
        percent = 0;

        var bytes = ReadProperty(devnode, BluetoothPropertySet, BatteryPercentPid, out var type);
        if (bytes is null || bytes.Length == 0) return false;

        var value = type switch
        {
            0x02 when bytes.Length >= 1 => (int)(sbyte)bytes[0],           // DEVPROP_TYPE_INT8
            0x03 when bytes.Length >= 1 => bytes[0],                       // DEVPROP_TYPE_UINT8 ← 实测就是它
            0x04 when bytes.Length >= 2 => BitConverter.ToInt16(bytes, 0),
            0x05 when bytes.Length >= 2 => BitConverter.ToUInt16(bytes, 0),
            0x06 when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0),
            0x07 when bytes.Length >= 4 => (int)BitConverter.ToUInt32(bytes, 0),
            0x12 => int.TryParse(Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim(), out var parsed) ? parsed : -1,
            _ => -1,
        };

        // 越界一律当「读不到」：宁可界面上不显示，也不把脏值糊上去
        if (value is < 0 or > 100) return false;

        percent = value;
        return true;
    }

    private static byte[]? ReadProperty(uint devnode, Guid propertySet, uint pid, out uint type)
    {
        type = 0;
        var key = new DEVPROPKEY { fmtid = propertySet, pid = pid };
        uint size = 0;

        // 先用 NULL 缓冲探大小：系统正常会返回 CR_BUFFER_SMALL 并在 size 里填上所需字节数，
        // 这不是失败（踩过：把它当失败会让每次读都返回 null）。
        var code = CM_Get_DevNode_PropertyW(devnode, ref key, out type, IntPtr.Zero, ref size, 0);
        if ((code != 0 && code != CrBufferSmall) || size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (CM_Get_DevNode_PropertyW(devnode, ref key, out type, buffer, ref size, 0) != 0) return null;

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, IntPtr buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devnode, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devnode, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devnode, IntPtr buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_PropertyW(uint devnode, ref DEVPROPKEY propertyKey,
        out uint propertyType, IntPtr propertyBuffer, ref uint propertyBufferSize, uint flags);
}
