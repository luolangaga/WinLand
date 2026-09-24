using System.Runtime.InteropServices;

namespace DeviceIsland;

/// <summary>
/// 设备变更广播窗口：一个「只收消息」的隐藏窗口（parent = HWND_MESSAGE），
/// 注册卷 / USB 设备接口通知后，系统里插拔存储设备时就会收到 <c>WM_DEVICECHANGE</c>。
///
/// 两条设计取舍：
///   1. 跑在**自己线程自己的消息循环**里（GetMessage/DispatchMessage），不依赖宿主 UI 线程的泵，
///      也不占用 UI 线程 —— 宿主停用时把窗口关掉、线程退出。
///   2. 它只是「立即重扫」的**触发器**，不是唯一判据：驱动器列表本身靠扫描得到，
///      所以哪怕这个窗口建不起来（极端环境），插件依然能靠轮询工作。
///
/// 构造函数**不阻塞**：建窗口在后台线程上做，插件初始化不会被它拖住。
/// </summary>
internal sealed class DeviceNotifyWindow : IDisposable
{
    // 卷接口 / USB 设备接口，插拔 U 盘、移动硬盘都会触发
    private static readonly Guid GuidDevInterfaceVolume = new("53F5630D-B6BF-11D0-94F2-00A0C91EFB8B");
    private static readonly Guid GuidDevInterfaceUsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    private readonly DeviceLog _log;
    private readonly Thread _thread;
    private readonly string _className = "DeviceIslandNotify_" + Guid.NewGuid().ToString("N");

    private NativeMethods.WndProcDelegate? _wndProc;   // 必须存字段，否则委托被 GC 后回调地址失效
    private IntPtr _hwnd;
    private volatile bool _stopping;

    /// <summary>收到设备变更广播（在通知线程上触发，订阅方需自行编组回 UI 线程）。</summary>
    public event Action? DeviceChanged;

    public DeviceNotifyWindow(DeviceLog log)
    {
        _log = log;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DeviceIsland.DeviceNotify",
        };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _wndProc = WndProc;
            var windowClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = _className,
            };

            if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
            {
                _log($"设备变更窗口注册失败（错误 {Marshal.GetLastWin32Error()}），改为仅轮询", true);
                return;
            }

            _hwnd = NativeMethods.CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0,
                (IntPtr)NativeMethods.HWND_MESSAGE, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _log($"设备变更窗口创建失败（错误 {Marshal.GetLastWin32Error()}），改为仅轮询", true);
                return;
            }

            RegisterInterface(GuidDevInterfaceVolume);
            RegisterInterface(GuidDevInterfaceUsbDevice);
            _log("设备变更广播窗口已就绪（插拔即时响应）", false);

            while (!_stopping && NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _log($"设备变更监听异常（改为仅轮询）：{ex.Message}", true);
        }
    }

    private void RegisterInterface(Guid guid)
    {
        var size = Marshal.SizeOf<NativeMethods.DEV_BROADCAST_DEVICEINTERFACE>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var filter = new NativeMethods.DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = size,
                dbcc_devicetype = NativeMethods.DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_classguid = guid,
            };
            Marshal.StructureToPtr(filter, buffer, false);
            NativeMethods.RegisterDeviceNotification(_hwnd, buffer, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_DEVICECHANGE && !_stopping)
        {
            var evt = wParam.ToInt32();
            if (evt is NativeMethods.DBT_DEVICEARRIVAL or NativeMethods.DBT_DEVICEREMOVECOMPLETE)
            {
                try
                {
                    DeviceChanged?.Invoke();
                }
                catch
                {
                    // 订阅方自己兜异常，这里绝不让它冒到消息循环
                }
            }
        }
        else if (msg == NativeMethods.WM_CLOSE)
        {
            NativeMethods.DestroyWindow(hWnd);
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _stopping = true;

        try
        {
            // 让通知线程自己收尾（WndProc 里 DestroyWindow + PostQuitMessage）：
            // WM_QUIT 只能投给自己线程的消息队列，不能在这里直接 PostQuitMessage
            var hwnd = _hwnd;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // 关窗失败无所谓：通知线程是后台线程，进程退出时自然结束
        }
    }
}
