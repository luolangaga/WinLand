using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeviceIsland;

/// <summary>
/// 设备动作 + 驱动器信息读取。
///
/// 「安全弹出」= 对卷句柄发一串 IOCTL：先 <c>FSCTL_LOCK_VOLUME</c> 锁卷（有文件被占用时这一步就会失败，
/// 正好是「设备正在使用中」的判据），再 <c>FSCTL_DISMOUNT_VOLUME</c> 卸载卷，然后取消
/// 「禁止移除介质」（<c>IOCTL_STORAGE_MEDIA_REMOVAL</c>），最后 <c>IOCTL_STORAGE_EJECT_MEDIA</c> 弹出。
/// 关闭句柄会自动释放卷锁，所以中途失败也不会把盘卡住。
/// </summary>
internal static class DeviceActions
{
    /// <summary>「打开」：磁盘开资源管理器；耳机开声音面板；手柄开游戏控制器面板。</summary>
    public static bool Open(DeviceInfo device, out string error)
    {
        error = "";
        try
        {
            string? file = device.Kind switch
            {
                DeviceKind.Drive => device.Target,
                DeviceKind.Audio => "mmsys.cpl",
                DeviceKind.Gamepad => "joy.cpl",
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(file))
            {
                error = "这个设备没有可打开的入口";
                return false;
            }

            Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>「安全弹出」：只对磁盘有效。返回 false 时 <paramref name="error"/> 是给人看的原因。</summary>
    public static bool Eject(DeviceInfo device, out string error)
    {
        error = "";

        if (device.Kind != DeviceKind.Drive || string.IsNullOrWhiteSpace(device.Target))
        {
            error = "只有磁盘可以安全弹出";
            return false;
        }

        var volume = @"\\.\" + device.Target.TrimEnd('\\');   // "E:\" → "\\.\E:"
        using var handle = NativeMethods.CreateFile(
            volume,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = $"打不开卷 {device.Target}（错误 {Marshal.GetLastWin32Error()}）";
            return false;
        }

        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_LOCK_VOLUME,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            error = "设备正在使用中（有文件被打开），先关掉再试";
            return false;
        }

        NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_DISMOUNT_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        var allowRemoval = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(allowRemoval, 0);   // PREVENT_MEDIA_REMOVAL.PreventMediaRemoval = FALSE
            NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_MEDIA_REMOVAL,
                allowRemoval, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(allowRemoval);
        }

        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_EJECT_MEDIA,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            error = "设备不支持弹出指令（这类盘可以直接拔）";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 读驱动器的总线类型和厂商/产品串（用于判断是不是 USB 外接盘，以及在没有卷标时命名）。
    /// 失败时返回 BusType = -1。
    /// </summary>
    public static (int BusType, string Vendor, string Product) QueryStorage(string driveRoot)
    {
        var volume = @"\\.\" + driveRoot.TrimEnd('\\');
        using var handle = NativeMethods.CreateFile(volume, 0,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid) return (-1, "", "");

        var query = new NativeMethods.STORAGE_PROPERTY_QUERY
        {
            PropertyId = NativeMethods.StorageDeviceProperty,
            QueryType = NativeMethods.PropertyStandardQuery,
        };

        var inSize = Marshal.SizeOf<NativeMethods.STORAGE_PROPERTY_QUERY>();
        var inBuffer = Marshal.AllocHGlobal(inSize);
        const int outSize = 1024;
        var outBuffer = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(query, inBuffer, false);

            if (!NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                    inBuffer, (uint)inSize, outBuffer, outSize, out _, IntPtr.Zero))
            {
                return (-1, "", "");
            }

            var descriptor = Marshal.PtrToStructure<NativeMethods.STORAGE_DEVICE_DESCRIPTOR>(outBuffer);
            return (descriptor.BusType,
                ReadAnsi(outBuffer, descriptor.VendorIdOffset),
                ReadAnsi(outBuffer, descriptor.ProductIdOffset));
        }
        catch
        {
            return (-1, "", "");
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    private static string ReadAnsi(IntPtr basePtr, int offset)
        => offset <= 0 ? "" : (Marshal.PtrToStringAnsi(IntPtr.Add(basePtr, offset)) ?? "").Trim();
}
