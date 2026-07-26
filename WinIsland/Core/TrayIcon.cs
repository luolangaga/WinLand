using System.Runtime.InteropServices;

namespace WinIsland.Core;

/// <summary>托盘菜单项。</summary>
public sealed class TrayMenuItem
{
    public TrayMenuItem(string text, Action? action = null, bool isChecked = false, bool isSeparator = false)
    {
        Text = text;
        Action = action;
        IsChecked = isChecked;
        IsSeparator = isSeparator;
    }

    public static TrayMenuItem Separator { get; } = new("", null, false, true);

    public string Text { get; }
    public Action? Action { get; }
    public bool IsChecked { get; }
    public bool IsSeparator { get; }
}

/// <summary>
/// 基于 Shell_NotifyIcon 的托盘图标（原生实现，右键弹出 Win32 菜单）。
/// 必须在 UI 线程创建（依赖该线程的消息循环）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1; // WM_APP + 1
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;

    private const int NIM_ADD = 0x0;
    private const int NIM_DELETE = 0x2;
    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;

    private const int MF_STRING = 0x0;
    private const int MF_SEPARATOR = 0x800;
    private const int MF_CHECKED = 0x8;
    private const int MF_GRAYED = 0x1;

    private const int TPM_RETURNCMD = 0x100;
    private const int TPM_RIGHTBUTTON = 0x2;

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly WndProc _wndProc; // 防止委托被 GC
    private readonly nint _hwnd;
    private readonly nint _hIcon;
    private readonly string _className;
    private bool _disposed;

    /// <summary>每次弹出菜单时调用，返回菜单项列表。</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    /// <summary>左键双击托盘图标。</summary>
    public Action? DoubleClicked { get; set; }

    public TrayIcon(string tooltip, string iconPath)
    {
        _className = "WinIslandTray_" + Environment.ProcessId;
        _wndProc = WndProcImpl;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, _className, "WinIslandTrayWnd", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        _hIcon = File.Exists(iconPath)
            ? LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 0, 0, 0x10 /*LR_LOADFROMFILE*/ | 0x40 /*LR_DEFAULTSIZE*/)
            : IntPtr.Zero;

        var nid = CreateNid();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_APP_TRAY;
        nid.hIcon = _hIcon;
        nid.szTip = tooltip;
        Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    private NOTIFYICONDATA CreateNid() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
    };

    private nint WndProcImpl(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            switch ((int)lParam)
            {
                case WM_RBUTTONUP:
                    ShowMenu();
                    return 0;
                case WM_LBUTTONDBLCLK:
                    DoubleClicked?.Invoke();
                    return 0;
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = MenuProvider?.Invoke();
        if (items == null || items.Count == 0) return;

        var hMenu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.IsSeparator)
                {
                    AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, null);
                }
                else
                {
                    var flags = MF_STRING;
                    if (item.IsChecked) flags |= MF_CHECKED;
                    if (item.Action == null) flags |= MF_GRAYED;
                    AppendMenu(hMenu, flags, i + 1, item.Text);
                }
            }

            // 让菜单能在点击别处时正确消失
            SetForegroundWindow(_hwnd);
            GetCursorPos(out var pt);
            int cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            if (cmd >= 1 && cmd <= items.Count)
            {
                items[cmd - 1].Action?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var nid = CreateNid();
        Shell_NotifyIcon(NIM_DELETE, ref nid);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        DestroyWindow(_hwnd);
        UnregisterClass(_className, GetModuleHandle(null));
    }

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint hInst, string name, int type, int cx, int cy, int fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, int uFlags, nint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, int uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(nint hMenu, int flags, int x, int y, nint hwnd, nint lptpm);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    #endregion
}
