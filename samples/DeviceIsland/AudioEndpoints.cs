using System.Runtime.InteropServices;

namespace DeviceIsland;

/// <summary>
/// 一个音频输出端点的「身份」。比 WinRT 的 <c>DeviceInformation</c> 多出几样决定性信息：
/// <see cref="EnumeratorName"/>（挂在哪个枚举器/总线上）、<see cref="FormFactor"/>（物理形态是不是耳机），
/// 以及 <see cref="DevNodeId"/>（这个端点在 PnP 设备树里的实例 ID，用来往上找它的物理设备）。
/// </summary>
internal sealed record AudioEndpoint(
    string Id, string Name, string EnumeratorName, uint FormFactor, int State, string DevNodeId);

/// <summary>
/// 音频输出端点枚举 + 「是不是外接耳机」判定（Core Audio / MMDevice）。
///
/// <para>
/// 为什么不用 <c>DeviceInformation.CreateWatcher(DeviceClass.AudioRender)</c>：
/// 它只按「是不是活动的输出端点」筛，于是**虚拟声卡**（网易虚拟音频设备、Nahimic、VB-Audio…）
/// 和**板载扬声器**会被一起报出来 —— 这些根本不是用户插进来的东西。
/// </para>
///
/// <para>
/// 这里改成直接读每个端点的属性，三把尺子一起量：
/// <list type="number">
///   <item><c>PKEY_Device_EnumeratorName</c> —— 挂在哪个枚举器上：<c>ROOT</c>/<c>SWD</c> 是软件/虚拟设备，
///         <c>USB</c>/<c>BTHENUM</c> 是外接总线，<c>INTELAUDIO</c>/<c>HDAUDIO</c> 是板载声卡；</item>
///   <item><c>PKEY_AudioEndpoint_FormFactor</c> —— 物理形态：耳机头(3)/耳麦(5)/听筒(6) 才算耳机；</item>
///   <item><c>IMMDevice.GetState</c> —— 状态：只有 <c>ACTIVE</c> 才算「真的插着」，
///         3.5mm 耳机口在没插东西时正是 <c>UNPLUGGED</c>。</item>
/// </list>
/// </para>
///
/// 全部是系统自带 COM 接口，零第三方依赖；在非打包进程里可直接用（已实测）。
/// </summary>
internal static class AudioEndpoints
{
    /// <summary>DEVICE_STATE_ACTIVE：设备已启用且「在场」。</summary>
    internal const int StateActive = 0x1;

    private const int StateUnplugged = 0x8;
    private const int StateNotPresent = 0x4;
    private const int StateDisabled = 0x2;

    private const uint FormSpeakers = 1;
    private const uint FormHeadphones = 3;
    private const uint FormHeadset = 5;
    private const uint FormHandset = 6;

    private const int EDataFlowRender = 0;
    private const int DeviceStateAll = 0xF;

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    // PKEY_Device_* 都在同一个 fmtid 下
    private static readonly PROPERTYKEY PkeyFriendlyName = new()
    { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

    private static readonly PROPERTYKEY PkeyEnumeratorName = new()
    { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 24 };

    private static readonly PROPERTYKEY PkeyFormFactor = new()
    { fmtid = new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), pid = 0 };

    /// <summary>
    /// 端点 ID → 它在 PnP 设备树里的实例 ID。
    ///
    /// <para>
    /// <b>是构造出来的，不是读出来的。</b>实测这些端点的属性存储里取
    /// <c>PKEY_Device_InstanceId</c> 会拿到空值（<c>vt=0</c>），靠不住；
    /// 而 Windows 给每个音频端点建的软件设备节点是固定命名 —— <c>SWD\MMDEVAPI\</c> + 端点 ID ——
    /// 实测直接解析得到，沿它的父链就能走到真实硬件节点上（<see cref="AudioDeviceTree"/> 靠它认总线、找电量）。
    /// </para>
    /// </summary>
    private const string MmDevApiPrefix = "SWD\\MMDEVAPI\\";

    /// <summary>
    /// 枚举所有音频**输出**端点（含已拔出 / 已禁用 / 不存在的，便于排查），不做任何筛选。
    /// 调用方负责挑。COM 对象每次调用自建自销，可安全地在任意后台线程上调用。
    /// </summary>
    internal static List<AudioEndpoint> EnumerateAll()
    {
        var result = new List<AudioEndpoint>();

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator)!)!;

            if (enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateAll, out collection) != 0
                || collection is null)
            {
                return result;
            }

            if (collection.GetCount(out var count) != 0) return result;

            for (var i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) != 0 || device is null) continue;
                    if (device.GetId(out var id) != 0 || string.IsNullOrWhiteSpace(id)) continue;
                    device.GetState(out var state);

                    var name = "";
                    var bus = "";
                    var form = uint.MaxValue;

                    if (device.OpenPropertyStore(0 /*STGM_READ*/, out var store) == 0 && store is not null)
                    {
                        try
                        {
                            name = ReadString(store, PkeyFriendlyName);
                            bus = ReadString(store, PkeyEnumeratorName);
                            form = ReadUInt(store, PkeyFormFactor);
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(store);
                        }
                    }

                    result.Add(new AudioEndpoint(id, name, bus, form, state, MmDevApiPrefix + id));
                }
                finally
                {
                    if (device is not null) Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            if (collection is not null) Marshal.ReleaseComObject(collection);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }

        return result;
    }

    /// <summary>
    /// 是不是「用户插进来的外接耳机」。三条规则，按总线分：
    /// <list type="bullet">
    ///   <item><c>ROOT</c> / <c>SWD</c>（虚拟声卡、软件音频设备）→ <b>永远不算</b>，网易虚拟音频设备、Nahimic、VB-Audio 都在这里；</item>
    ///   <item><c>USB</c> / 蓝牙（<c>BTHENUM</c> / <c>BTHHFENUM</c> / <c>BTHLEENUM</c>）→ <b>算</b>。这些总线本身就是外接的，
    ///         而且不少 USB 耳机把形态报成 Speakers，若再按形态卡就会把真耳机漏掉；</item>
    ///   <item>板载声卡（<c>INTELAUDIO</c> / <c>HDAUDIO</c>）→ <b>只有耳机形态才算</b>，
    ///         于是板载扬声器口、SPDIF、HDMI 输出全被排除，只留 3.5mm 耳机口。</item>
    /// </list>
    /// 另外：状态必须是 <c>ACTIVE</c>。拔掉的蓝牙耳机、空着的耳机口（<c>UNPLUGGED</c>）、
    /// 被禁用的端点，都不该在岛上冒出来。
    /// </summary>
    internal static bool IsExternalHeadset(AudioEndpoint endpoint)
    {
        if (endpoint.State != StateActive) return false;

        return endpoint.EnumeratorName.ToUpperInvariant() switch
        {
            "ROOT" or "SWD" => false,

            "USB" or "BTHENUM" or "BTHHFENUM" or "BTHLEENUM" => true,

            "INTELAUDIO" or "HDAUDIO" => IsHeadShape(endpoint.FormFactor),

            // 不认识的枚举器：宁缺勿滥，不往岛上放
            _ => false,
        };
    }

    /// <summary>物理形态是不是「戴在耳朵上」的那类。</summary>
    internal static bool IsHeadShape(uint formFactor)
        => formFactor is FormHeadphones or FormHeadset or FormHandset;

    /// <summary>
    /// 过滤 + 去重。同一副蓝牙耳机常会**同时**以两条路径出现（<c>BTHENUM</c> 直连 + 板载 offload 的
    /// <c>INTELAUDIO</c>），两边都 ACTIVE 时按名字去重，免得岛上冒出两台一模一样的耳机。
    /// </summary>
    internal static List<AudioEndpoint> SelectExternalHeadsets(IEnumerable<AudioEndpoint> all)
    {
        var kept = new List<AudioEndpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in all)
        {
            if (!IsExternalHeadset(endpoint)) continue;

            var key = string.IsNullOrWhiteSpace(endpoint.Name) ? endpoint.Id : endpoint.Name;
            if (!seen.Add(key)) continue;

            kept.Add(endpoint);
        }

        return kept;
    }

    /// <summary>把端点按「为什么被跳过」分类，只用于日志，让人一眼看懂过滤对不对。</summary>
    internal static string ExplainSkip(AudioEndpoint endpoint)
    {
        if (IsExternalHeadset(endpoint)) return "命中";

        var reason = endpoint.EnumeratorName.ToUpperInvariant() switch
        {
            "ROOT" or "SWD" => "虚拟/软件音频设备",
            "USB" or "BTHENUM" or "BTHHFENUM" or "BTHLEENUM" => "外接音频",
            "INTELAUDIO" or "HDAUDIO" when IsHeadShape(endpoint.FormFactor) => "板载耳机口",
            "INTELAUDIO" or "HDAUDIO" when endpoint.FormFactor == FormSpeakers => "板载扬声器",
            "INTELAUDIO" or "HDAUDIO" => "板载其它输出",
            _ => "未知总线",
        };

        if (endpoint.State == StateActive) return reason;

        var state = endpoint.State switch
        {
            StateUnplugged => "未插入",
            StateNotPresent => "不存在",
            StateDisabled => "已禁用",
            _ => $"0x{endpoint.State:X}",
        };

        return $"{reason}·{state}";
    }

    /// <summary>连接方式的中文说法，给岛上的副标题用。</summary>
    internal static string DescribeBus(AudioEndpoint endpoint) => endpoint.EnumeratorName.ToUpperInvariant() switch
    {
        "USB" => "USB 音频",
        "BTHENUM" or "BTHHFENUM" or "BTHLEENUM" => "蓝牙音频",
        "INTELAUDIO" or "HDAUDIO" => "3.5mm 耳机口",
        _ => "音频输出",
    };

    /// <summary>
    /// 连接方式的中文说法 —— <b>优先用设备树判定</b>（<see cref="AudioLink"/>），
    /// 它比端点自报的枚举器准：实测蓝牙耳机的枚举器会报 <c>INTELAUDIO</c>，
    /// 只看枚举器会把蓝牙耳机说成「3.5mm 耳机口」。
    /// 设备树摸不出底细（<see cref="AudioLink.Unknown"/>）时才回退到枚举器。
    /// </summary>
    internal static string DescribeLink(AudioLink link, AudioEndpoint endpoint) => link switch
    {
        AudioLink.Bluetooth => "蓝牙音频",
        AudioLink.Usb => "USB 音频",
        // 走到板载总线上还留在列表里的，只可能是耳机口（扬声器口/HDMI/SPDIF 都被 IsExternalHeadset 挡掉了）
        AudioLink.Onboard => "3.5mm 耳机口",
        _ => DescribeBus(endpoint),
    };

    private static string ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var value) != 0) return "";
        try
        {
            const ushort vtLpwstr = 31;
            return value.vt == vtLpwstr && value.pointerValue != IntPtr.Zero
                ? Marshal.PtrToStringUni(value.pointerValue) ?? ""
                : "";
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static uint ReadUInt(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var value) != 0) return uint.MaxValue;
        try
        {
            const ushort vtUi4 = 19;
            return value.vt == vtUi4 ? value.uintValue : uint.MaxValue;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    // ---- Core Audio COM 声明 ----

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore? properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    /// <summary>
    /// 显式布局并补齐到 24 字节 —— x64 上原生 PROPVARIANT 就是这个大小，
    /// 托管结构比它短的话，系统写回来时会越界踩内存。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort reserved1;
        [FieldOffset(4)] public ushort reserved2;
        [FieldOffset(6)] public ushort reserved3;
        [FieldOffset(8)] public IntPtr pointerValue;
        [FieldOffset(8)] public uint uintValue;
        [FieldOffset(8)] public int intValue;
        [FieldOffset(16)] public long padding;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);
}
