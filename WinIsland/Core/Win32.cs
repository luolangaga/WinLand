using System.Runtime.InteropServices;

namespace WinIsland.Core;

/// <summary>灵动岛窗口所需的 Win32 互操作。</summary>
internal static partial class Win32
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint WS_POPUP = 0x8000_0000;
    public const uint WS_VISIBLE = 0x1000_0000;

    public const uint WS_EX_TOOLWINDOW = 0x0000_0080;
    public const uint WS_EX_NOACTIVATE = 0x0800_0000;
    public const uint WS_EX_TOPMOST = 0x0000_0008;
    // 可能导致白边的扩展样式位
    public const uint WS_EX_WINDOWEDGE = 0x0000_0100;
    public const uint WS_EX_CLIENTEDGE = 0x0000_0200;
    public const uint WS_EX_DLGMODALFRAME = 0x0000_0001;
    public const uint WS_EX_STATICEDGE = 0x0002_0000;

    public const int DWMWA_NCRENDERING_POLICY = 2;
    public const int DWMNCRP_DISABLED = 1;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWA_BORDER_COLOR = 34;
    /// <summary>DWMWA_COLOR_NONE：完全移除 DWM 绘制的窗口描边。</summary>
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    public static partial int DwmSetWindowAttributeUint(nint hwnd, int attr, ref uint attrValue, int attrSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public int fEnable;
        public nint hRgnBlur;
        public int fTransitionOnMaximized;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmEnableBlurBehindWindow(nint hwnd, ref DWM_BLURBEHIND blurBehind);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int x1, int y1, int x2, int y2);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int ellipseW, int ellipseH);

    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(nint hDest, nint hSrc1, nint hSrc2, int combineMode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint hObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PatBlt(nint hdc, int x, int y, int w, int h, uint rop);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint BLACKNESS = 0x00000042;
    private const int GCLP_HBRBACKGROUND = -10;

    [LibraryImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static partial nint SetClassLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    /// <summary>
    /// 去掉窗口类的背景画刷：否则窗口被擦除时会用类背景色（不透明白）填充，
    /// 凡是被窗口形状覆盖、而 XAML 内容没有画到的像素都会变成白块。
    /// </summary>
    public static void ClearWindowBackgroundBrush(nint hwnd)
        => SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND, nint.Zero);

    /// <summary>
    /// 把窗口客户区擦成「全 0」——BLACKNESS 会写 0（含 alpha=0），即完全透明像素。
    /// 用于父窗口表面：这样形状里没被 XAML 内容覆盖的区域是透明的，而不是白色。
    /// </summary>
    public static void ClearClientTransparent(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var rc)) return;

        var hdc = GetDC(hwnd);
        if (hdc == nint.Zero) return;
        PatBlt(hdc, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, BLACKNESS);
        ReleaseDC(hwnd, hdc);
    }

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PtInRegion(nint hRgn, int x, int y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    /// <summary>
    /// 设定窗口「形状」：区域之外的像素不属于这个窗口，既不绘制也不参与命中测试，
    /// 点击/触摸因此会真正落到下层窗口（跨进程有效）。
    /// 这是点击穿透唯一可靠的机制 —— WM_NCHITTEST 返回 HTTRANSPARENT 只对同线程窗口生效。
    /// 调用成功后系统接管区域所有权（不要 DeleteObject）；失败则就地释放。
    /// bRedraw 传 false：XAML 内容每帧自绘，不需要系统再额外触发整窗重绘。
    /// </summary>
    public static bool ApplyWindowShape(nint hwnd, nint hRgn, bool redraw = false)
    {
        bool ok = SetWindowRgn(hwnd, hRgn, redraw);
        if (!ok && hRgn != nint.Zero)
            DeleteObject(hRgn);
        return ok;
    }

    /// <summary>创建矩形 GDI 区域。需 DeleteObject 释放。</summary>
    public static nint CreateRectRegion(int x1, int y1, int x2, int y2) => CreateRectRgn(x1, y1, x2, y2);

    /// <summary>创建圆角矩形 GDI 区域（ellipseW/H 为两倍圆角半径，物理像素）。需 DeleteObject 释放。</summary>
    public static nint CreateRoundRectRegion(int x1, int y1, int x2, int y2, int ellipseW, int ellipseH)
        => CreateRoundRectRgn(x1, y1, x2, y2, Math.Max(0, ellipseW), Math.Max(0, ellipseH));

    /// <summary>释放 GDI 区域。</summary>
    public static void DeleteRegion(nint hRgn) => DeleteObject(hRgn);

    /// <summary>判断点是否在区域内。</summary>
    public static bool IsPointInRegion(nint hRgn, int x, int y) => PtInRegion(hRgn, x, y);

    /// <summary>原地合并：hDest = hDest OR hSrc2。</summary>
    public static void CombineRgnInPlace(nint hDest, nint hSrc2)
        => CombineRgn(hDest, hDest, hSrc2, RGN_OR);

    private const int RGN_OR = 2;

    /// <summary>
    /// 让 DWM 按 alpha 合成窗口表面（参考 WinUIEx）：
    /// DwmEnableBlurBehindWindow + 空模糊区域，配合透明 SystemBackdrop 实现真透明窗口。
    /// </summary>
    public static void EnableWindowAlpha(nint hwnd)
    {
        var margins = new MARGINS();
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var rgn = CreateRectRgn(-2, -2, -1, -1);
        var bb = new DWM_BLURBEHIND
        {
            dwFlags = 3, // DWM_BB_ENABLE | DWM_BB_BLURREGION
            fEnable = 1,
            hRgnBlur = rgn,
        };
        DwmEnableBlurBehindWindow(hwnd, ref bb);
        DeleteObject(rgn);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>MONITOR_DEFAULTTONEAREST</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// 按「显示器 id」（<c>DisplayArea.DisplayId.Value</c>）取 HMONITOR —— 用来把 WinRT 的
    /// <c>DisplayArea</c> 和 <c>DisplayInformation</c>（设备名/物理尺寸）对上。
    /// 取不到返回 0。
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static partial nint MonitorFromPointWrapped(POINT pt, uint dwFlags);

    /// <summary>
    /// WinRT 的 <c>DisplayArea.DisplayId</c> 与 Win32 的 HMONITOR 是两套 id，不能直接互换。
    /// 桥接办法：<c>DisplayArea.DisplayId.Value</c> 的高 32 位是适配器 id、低 32 位是目标 id，
    /// 而 <c>MonitorFromPoint</c> 在 Windows 10 1903+ 上会把这两个值当作
    /// 「DISPLAYCONFIG 路径」的 LUID/target id 来解析（历史兼容行为，即 MonitorFromDisplayId）。
    /// 因此把 DisplayId 原样塞进 POINT 的 X 分量再调用它。
    /// 换不到时返回 0，调用方自行降级。
    /// </summary>
    public static nint MonitorFromDisplayId(int displayId, uint flags)
        => MonitorFromPointWrapped(new POINT { X = displayId, Y = 0 }, flags);

    // ---- 显示器枚举：拿 \\.\DISPLAYn 名字与它在虚拟桌面里的排列位置 ----

    /// <summary>
    /// MONITORINFOEX。**必须带 <c>CharSet.Unicode</c>**：缺了它 <c>szDevice</c> 按 ANSI 排布，
    /// 结构体只有 72 字节（正确是 104），<c>GetMonitorInfo</c> 会直接失败并返回 0 块显示器。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const uint MONITORINFOF_PRIMARY = 0x0000_0001;

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint lprcMonitor, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    /// <summary>枚举回调的委托必须由调用方持有到枚举结束，否则会被 GC 回收。</summary>
    private static readonly MonitorEnumProc _monitorEnumProc = CollectMonitor;

    /// <summary>枚举结果的暂存缓冲：按线程隔离 —— 设置页、岛窗口、平台看门狗可能同时来问。</summary>
    [ThreadStatic]
    private static List<MonitorInfo>? _monitorBuffer;

    private static bool CollectMonitor(nint monitor, nint hdc, nint rect, nint data)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
        if (!GetMonitorInfo(monitor, ref info)) return true;

        (_monitorBuffer ??= new List<MonitorInfo>(4)).Add(new MonitorInfo(
            info.szDevice ?? string.Empty,
            (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
            info.rcMonitor.Left,
            info.rcMonitor.Top,
            info.rcMonitor.Right - info.rcMonitor.Left,
            info.rcMonitor.Bottom - info.rcMonitor.Top,
            info.rcWork.Left,
            info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left,
            info.rcWork.Bottom - info.rcWork.Top));
        return true;
    }

    /// <summary>
    /// 枚举接在桌面上的全部显示器（<c>EnumDisplayMonitors</c> + <c>GetMonitorInfo</c>）。
    ///
    /// 这是显示器的唯一真实来源，**不要用 <c>DisplayArea.FindAll()</c>** ——
    /// 它在多屏机器上遍历会抛 <c>InvalidCastException</c>（microsoft-ui-xaml#6454）：
    /// 旧实现把异常当"没有显示器"吞掉，于是所有多屏用户都看到「只检测到一块显示器」。
    /// <c>DisplayArea</c> 只能在已知某块屏时用于"这块屏的几何"（落点、DPI），不能用来枚举。
    /// 返回顺序：主屏最前，其余按排列坐标从左到右、从上到下。
    /// </summary>
    public static MonitorInfo[] EnumerateMonitors()
    {
        var buffer = _monitorBuffer ??= new List<MonitorInfo>(4);
        buffer.Clear();
        try
        {
            if (!EnumDisplayMonitors(nint.Zero, nint.Zero, _monitorEnumProc, nint.Zero))
                return Array.Empty<MonitorInfo>();
        }
        catch
        {
            return Array.Empty<MonitorInfo>();
        }

        return MonitorLayout.Sort(buffer);
    }

    /// <summary>离指定矩形最近（或重叠最多）的显示器。取不到时返回 null。</summary>
    public static MonitorInfo? MonitorFromRect(Win32.RECT rect)
        => MonitorLayout.BestFit(EnumerateMonitors(), rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top);

    /// <summary>包含指定点的显示器；点落在死区时取最近的一块。取不到时返回 null。</summary>
    public static MonitorInfo? MonitorAt(int x, int y)
    {
        var monitors = EnumerateMonitors();
        return MonitorLayout.At(monitors, x, y) ?? MonitorLayout.Nearest(monitors, x, y);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>
    /// 重新断言 DWM 无描边/无圆角属性。
    /// AppWindow.MoveAndResize 等操作可能重置这些属性，需在每次 resize 后调用。
    /// </summary>
    public static void RemoveDwmBorder(nint hwnd)
    {
        int pref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        uint none = DWMWA_COLOR_NONE;
        DwmSetWindowAttributeUint(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(uint));
        // 彻底禁用 DWM 非客户区渲染，防止任何残留描边
        int ncrp = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref ncrp, sizeof(int));
    }

    /// <summary>
    /// 将窗口设置为无边框弹出样式 + 工具窗口 + 默认不抢焦点。
    /// <paramref name="noActivate"/> 传 false 时保留可激活状态：只有「输入会话」（<c>InputSession</c>）
    /// 在窗口可见且正被输入时才这么调用，其余一律保持默认。
    /// </summary>
    public static void MakeIslandStyle(nint hwnd, bool noActivate = true)
    {
        SetWindowLongPtr(hwnd, GWL_STYLE, unchecked((nint)(WS_POPUP | WS_VISIBLE)));

        var ex = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        // 清除所有可能导致白边的扩展样式位
        ex &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        else ex &= ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)ex);

        // 去掉类背景画刷：窗口表面必须保持透明，任何默认擦除都会画成不透明白色
        ClearWindowBackgroundBrush(hwnd);

        // 样式改变后必须 SWP_FRAMECHANGED，并强制进入 TOPMOST 层
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // 开启 DWM alpha 合成，配合 TransparentBackdrop 实现真透明
        EnableWindowAlpha(hwnd);

        // 最后断言 DWM 无描边/无圆角（放在 SetWindowPos 之后，防止 SWP_FRAMECHANGED 重置）
        RemoveDwmBorder(hwnd);
    }

    /// <summary>
    /// 聚光卡的全屏覆盖窗样式：与岛体同款（无边框弹出 + 工具窗口 + 不抢焦点 + 置顶 + 真透明）。
    /// 三层白边防护与 alpha 合成对覆盖窗同样必要，所以共用同一份实现，只是名字点明用途。
    /// </summary>
    public static void MakeOverlayStyle(nint hwnd) => MakeIslandStyle(hwnd);

    // ---- 临时可激活（输入会话用）：默认 NOACTIVATE 不变，只在用户点到文本输入控件时短暂放开 ----

    /// <summary>
    /// 只翻转 WS_EX_NOACTIVATE 位（输入会话进出时调用）。位本身不是边框样式，改完立即生效，
    /// 所以刻意不做 SWP_FRAMECHANGED —— 那会重置窗口形状，而输入会话恰恰发生在窗口正可见的时候。
    /// </summary>
    public static void SetActivatable(nint hwnd, bool activatable)
    {
        var ex = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (activatable) ex &= ~WS_EX_NOACTIVATE;
        else ex |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)ex);
    }

    /// <summary>窗口当前是不是前台窗口。</summary>
    public static bool IsForeground(nint hwnd) => GetForegroundWindow() == hwnd;

    /// <summary>
    /// 把窗口抢到前台（调用前先 <see cref="SetActivatable"/> 清掉 NOACTIVATE）。
    /// 首选直接 SetForegroundWindow —— 刚收到点击的进程满足「最后输入事件」规则，一般直接放行；
    /// 被前台锁挡住时退回 AttachThreadInput 到当前前台线程的标准手法，用完立刻断开。
    /// 返回是否真的成为前台窗口。
    /// </summary>
    public static bool TryBringToForeground(nint hwnd)
    {
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd)
        {
            SetFocus(hwnd);
            return true;
        }

        var fg = GetForegroundWindow();
        uint fgThread = fg != nint.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint thread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != thread && AttachThreadInput(thread, fgThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thread, fgThread, false);
        }

        return GetForegroundWindow() == hwnd;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    // ---- Esc 热键（聚光卡用）：覆盖窗是 WS_EX_NOACTIVATE，拿不到键盘焦点，只能注册系统热键 ----

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_ESCAPE = 0x1B;
    private const int EscapeHotKeyId = 0x5750;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    // 静态持有委托，防止被 GC 回收；同时只有一个覆盖窗，单个处理器足够
    private static readonly SubclassProc _hotKeyProc = HotKeyProc;
    private static Action? _hotKeyHandler;

    /// <summary>
    /// 给窗口注册 Esc 热键并挂上 WM_HOTKEY 处理。返回 false 表示注册失败
    /// （例如热键已被别的程序占用）——调用方无需补救，点击卡片外区域仍然能关闭。
    /// </summary>
    public static bool InstallEscapeHotKey(nint hwnd, Action onPressed)
    {
        if (!RegisterHotKey(hwnd, EscapeHotKeyId, MOD_NOREPEAT, VK_ESCAPE))
        {
            return false;
        }

        _hotKeyHandler = onPressed;
        SetWindowSubclass(hwnd, _hotKeyProc, 0x15AE, 0);
        return true;
    }

    /// <summary>注销 Esc 热键并摘掉处理器（顺序与 <see cref="InstallEscapeHotKey"/> 相反）。</summary>
    public static void UninstallEscapeHotKey(nint hwnd)
    {
        UnregisterHotKey(hwnd, EscapeHotKeyId);
        RemoveWindowSubclass(hwnd, _hotKeyProc, 0x15AE);
        _hotKeyHandler = null;
    }

    private static nint HotKeyProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (uMsg == WM_HOTKEY && (int)wParam == EscapeHotKeyId)
        {
            try
            {
                _hotKeyHandler?.Invoke();
            }
            catch
            {
                // 热键线程/回调里绝不能抛出去
            }
            return 0;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// 只在窗口真的被压住时重新置顶，返回压住它的窗口描述（null = 没被压住、什么都没做）。
    /// 判定「被压住」的唯一标准是：岛体矩形上方存在任意可见且与其相交的窗口 ——
    /// 任务栏、开始菜单、搜索浮层都是置顶窗口，谁最后断言谁在上层。
    /// 只检查「任务栏是否在我上面」是不够的（那些浮层不是任务栏，同样会把岛盖掉）。
    /// WS_EX_TOPMOST 位本身不会清除，所以不能只看自己的样式位。
    /// </summary>
    public static string? EnsureTopmost(nint hwnd, RECT islandOnScreen)
    {
        if (FindCoverer(hwnd, islandOnScreen) is not { } coverer) return null;

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        return coverer;
    }

    /// <summary>岛体上方第一个「可见且与其相交」的窗口（类名 + 矩形）；没有则返回 null。</summary>
    private static string? FindCoverer(nint hwnd, RECT island)
    {
        for (var cur = GetWindow(hwnd, GW_HWNDPREV); cur != nint.Zero; cur = GetWindow(cur, GW_HWNDPREV))
        {
            if (!IsWindowVisible(cur)) continue;
            if (!GetWindowRect(cur, out var rect)) continue;

            // 与岛体不相交的窗口（例如各种 1x1 置顶辅助窗口）不算被压住
            if (MinOverlap(rect, island) <= 0) continue;

            var name = new char[64];
            int len = GetClassName(cur, name, name.Length);
            string cls = len > 0 ? new string(name, 0, len) : "(未知类)";

            // 系统拖拽缩略图（OLE 拖放循环创建的、跟随光标的那一小块）不算「压住岛」：
            // 它必须待在光标附近的最上层，把岛顶到它上面会让缩略图被岛盖住 ——
            // 用户正对着岛拖文件时尤其明显（文件投放期间每 120ms 就会触发一次重升）
            if (string.Equals(cls, "SysDragImage", StringComparison.Ordinal)) continue;

            // 输入法候选窗/输入宿主也不算：岛上有输入框时候选窗必然与岛相交，
            // 把岛顶到候选窗上面会让用户看不到候选词（中文输入直接没法用）
            if (IsInputOverlayWindow(cur, cls)) continue;

            return $"{cls} {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
        }
        return null;
    }

    // ---- 输入法/输入宿主窗口豁免（置顶自愈不得把它们顶下去）----

    /// <summary>输入法候选窗与输入宿主窗口的进程映像名（忽略大小写比较）。</summary>
    private static readonly string[] InputOverlayProcesses =
    {
        "textinputhost.exe",   // Win11 候选窗 / 表情面板宿主
        "chsime.exe",          // 微软拼音（旧）
        "jpnime.exe",          // 日语 IME
        "korime.exe",          // 韩语 IME
    };

    /// <summary>
    /// 是否是输入法候选窗/输入宿主窗口：类名以 IME 开头（IME / MSCTFIME UI / IMECandidateUI…），
    /// 或进程映像名命中输入宿主列表。判定是启发式 —— 中文输入实测有问题时按日志里记录的
    /// 类名/进程名扩充这里。
    /// </summary>
    private static bool IsInputOverlayWindow(nint hwnd, string cls)
    {
        if (cls.StartsWith("IME", StringComparison.OrdinalIgnoreCase)) return true;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == nint.Zero) return false;
        try
        {
            var name = new char[260];
            uint len = (uint)name.Length;
            if (!QueryFullProcessImageName(h, 0, name, ref len)) return false;
            string file = new(name, 0, (int)len);
            return InputOverlayProcesses.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    // char[] 数组参数 LibraryImport 源生成器不处理（同 GetClassName），只能用经典 DllImport
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags,
        [Out] char[] lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    // ---- 前台窗口/焦点变化监听：任务栏/开始菜单/搜索抬层时立刻重升岛（轮询只是兜底），
    //      同时也是触控展开「点了别处」的兜底信号 ----

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    private delegate void WinEventProc(nint hook, uint eventId, nint hwnd, int idObject, int idChild, uint threadId, uint time);

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    // 钩子委托必须存活（静态字段），否则会被 GC 回收导致回调丢失
    private static readonly WinEventProc _foregroundProc = OnForegroundEvent;
    private static readonly WinEventProc _focusProc = OnFocusEvent;
    private static Action? _foregroundHandler;

    /// <summary>
    /// 安装「别的窗口被激活」监听：前台窗口变化（点任务栏/开始菜单/别的应用）与焦点变化。
    /// 焦点那一路是触控的兜底信号 —— 触摸点在原生处理 WM_POINTER 的应用（浏览器、WinUI/UWP 应用、
    /// 岛自己的设置窗）里不会产生鼠标消息，低级鼠标钩子看不到，但焦点一定会动。
    /// WINEVENT_OUTOFCONTEXT 要求安装线程自己跑消息泵，所以这里起一个后台线程专门 GetMessage。
    /// 回调在钩子线程上执行，调用方负责切回 UI 线程。只安装一次。用途见 <see cref="EnsureTopmost"/>。
    /// </summary>
    public static void InstallForegroundWatcher(Action onForegroundChanged)
    {
        if (_foregroundHandler != null) return;
        _foregroundHandler = onForegroundChanged;

        var thread = new Thread(() =>
        {
            SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nint.Zero, _foregroundProc, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            // 焦点监听不跳过自己的进程：点自己的设置窗同样算「点了别的地方」
            SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, nint.Zero, _focusProc, 0, 0,
                WINEVENT_OUTOFCONTEXT);

            while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        })
        {
            IsBackground = true,
            Name = "WinIsland.ForegroundWatch",
        };
        thread.Start();
    }

    private static void OnForegroundEvent(nint hook, uint eventId, nint hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        try
        {
            _foregroundHandler?.Invoke();
        }
        catch
        {
            // 钩子线程上绝不能抛异常
        }
    }

    private static void OnFocusEvent(nint hook, uint eventId, nint hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        try
        {
            _foregroundHandler?.Invoke();
        }
        catch
        {
            // 钩子线程上绝不能抛异常
        }
    }

    // ---- 全局按下监听：触控展开后「点岛外收起」的唯一信号源 ----
    //
    // 岛窗是 WS_EX_NOACTIVATE，又被窗口形状裁成岛体本身：岛外的一切点击/触摸都到不了这里。
    // 而触摸没有悬停语义（手指抬起就是 PointerExited），展开后只能由「别处的下一次按下」来收。
    // 低级鼠标钩子在安装它的线程上回调，所以要单独一个消息泵线程 —— 钩子回调要求线程处于
    // 消息等待状态，而 UI 线程被插件卡住时不该连带拖住系统级输入。触摸在系统层会被提升成
    // 鼠标按下，钩子同样收得到。只在触控展开期间安装，其余时间系统输入零开销。

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const uint PM_NOREMOVE = 0x0000;
    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;
    /// <summary>监听线程回头确认「是否仍需要监听」的间隔；不监听时它就退出并卸掉钩子。</summary>
    private const uint MouseWatchPollMs = 500;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG msg, nint hWnd, uint min, uint max, uint removeMsg);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(uint count, nint handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll,
        uint milliseconds, uint wakeMask);

    // 钩子委托必须由静态字段持有，否则会被 GC 回收导致回调丢失
    private static readonly LowLevelMouseProc _lowLevelMouseProc = OnLowLevelMouse;
    private static readonly object _mouseWatcherGate = new();
    private static Action<POINT>? _mouseDownHandler;
    private static Thread? _mouseWatchThread;

    /// <summary>
    /// 开始派发全局按下（左/右/中/X 键）。<paramref name="onMouseDown"/> 在监听线程上被调用，
    /// 只做记录或切线程，绝不要在里面碰 UI。重复调用只替换处理器。
    /// </summary>
    public static void InstallMouseDownWatcher(Action<POINT> onMouseDown)
    {
        lock (_mouseWatcherGate)
        {
            _mouseDownHandler = onMouseDown;
            if (_mouseWatchThread != null) return;
            StartMouseWatchThread();
        }
    }

    /// <summary>停止派发全局按下：监听线程下一轮自行退出并卸掉钩子。</summary>
    public static void UninstallMouseDownWatcher()
    {
        lock (_mouseWatcherGate)
        {
            _mouseDownHandler = null;
        }
    }

    private static void StartMouseWatchThread()
    {
        _mouseWatchThread = new Thread(MouseWatchLoop)
        {
            IsBackground = true,
            Name = "WinIsland.MouseWatch",
        };
        _mouseWatchThread.Start();
    }

    private static void MouseWatchLoop()
    {
        // 线程先拥有消息队列 —— 钩子回调要靠「线程处于消息等待」才会被投递进来
        PeekMessage(out _, nint.Zero, 0, 0, PM_NOREMOVE);
        nint hook = SetWindowsHookEx(WH_MOUSE_LL, _lowLevelMouseProc, nint.Zero, 0);

        // MsgWaitForMultipleObjects 而非 GetMessage：既能立刻收到钩子回调，又能定期回头
        // 看处理器是否已被摘掉（摘掉就退出并卸钩子，不留长期存在的全局钩子）。
        while (IsMouseWatchWanted())
        {
            MsgWaitForMultipleObjects(0, nint.Zero, false, MouseWatchPollMs, QS_ALLINPUT);
            while (PeekMessage(out var msg, nint.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        if (hook != nint.Zero) UnhookWindowsHookEx(hook);

        lock (_mouseWatcherGate)
        {
            // 退出窗口期内又重新需要监听：补一个线程，否则这次安装会静默失效
            _mouseWatchThread = null;
            if (_mouseDownHandler != null) StartMouseWatchThread();
        }
    }

    private static bool IsMouseWatchWanted()
    {
        lock (_mouseWatcherGate) return _mouseDownHandler != null;
    }

    private static nint OnLowLevelMouse(int nCode, nint wParam, nint lParam)
    {
        // 回调在监听线程上：只取坐标后立刻返回（系统对钩子回调耗时有超时限制）
        if (nCode >= 0 && _mouseDownHandler is { } handler)
        {
            int msg = (int)wParam;
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                try
                {
                    handler(Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt);
                }
                catch
                {
                    // 钩子回调里绝不能把异常抛出去
                }
            }
        }

        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    // ---- 任务栏条带：底部模式下把岛嵌进任务栏条带 ----

    /// <summary>自动隐藏/全屏收起时任务栏滑出屏幕，但仍留 ~2px 缝隙，所以「在屏幕上」必须做真实交叠测试。</summary>
    public const int SliverPx = 8;

    /// <summary>主任务栏条带：停靠矩形（物理像素）+ 是否真的显示在屏幕上 + 托盘区左边界（0 = 没量到）。</summary>
    public readonly record struct TaskbarStrip(RECT Docked, bool Visible, int TrayLeft);

    private const uint ABM_GETTASKBARPOS = 5;
    private const uint GW_HWNDPREV = 3;

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    /// <summary>窗口在屏幕上的矩形（物理像素）。取不到返回 false 并把 <paramref name="rect"/> 清零。</summary>
    public static bool TryGetWindowRect(nint hWnd, out RECT rect)
    {
        if (hWnd != nint.Zero && GetWindowRect(hWnd, out rect)) return true;
        rect = default;
        return false;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hWnd, uint uCmd);

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [LibraryImport("shell32.dll")]
    private static partial nint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>主任务栏条带。<paramref name="display"/> 为主显示器外框（物理像素，用于判断任务栏是否真的在屏幕上）。
    /// 停靠矩形取 ABM_GETTASKBARPOS —— 它在任务栏自动隐藏时仍报告停靠位置，正是要嵌入的位置；
    /// 可见性另算：自动隐藏/全屏收起时任务栏被移出屏幕，只留一道缝隙。
    /// 取不到（非 explorer 外壳、调用失败）返回 null，调用方退回屏幕外框。
    /// 多屏请改用 <see cref="GetTaskbarStripFor(RECT)"/> —— 每个屏有自己的任务栏窗口。</summary>
    public static TaskbarStrip? GetTaskbarStrip(RECT display)
        => GetTaskbarStripFor(display);

    /// <summary>
    /// 指定显示器上的任务栏条带（物理像素）。<paramref name="display"/> 是目标显示器的外框。
    ///
    /// 多屏的关键：主屏任务栏的窗口类是 <c>Shell_TrayWnd</c>，而每个**副屏**各有自己的任务栏窗口，
    /// 类是 <c>Shell_SecondaryTrayWnd</c>（每个屏一个，没有标题）。原来的实现只 FindWindow 主任务栏，
    /// 于是副屏上要么量到主屏任务栏的位置、要么什么也量不到 —— 副屏的岛因此永远不会嵌进任务栏。
    ///
    /// 匹配方式：枚举所有任务栏窗口（主 + 全部副屏），取**停靠矩形与目标显示器外框重叠最多**的那个。
    /// 只按矩形匹配、不按顺序/编号匹配 —— 显示器编号与任务栏创建顺序没有保证。
    /// 一块屏都没有匹配上（该屏禁用了任务栏、非 explorer 外壳）时返回 null。
    /// </summary>
    public static TaskbarStrip? GetTaskbarStripFor(RECT display)
    {
        var tray = FindTaskbarFor(display);
        if (tray == nint.Zero) return null;
        if (!GetWindowRect(tray, out var live)) return null;

        bool visible = IsWindowVisible(tray) && MinOverlap(live, display) >= SliverPx;

        // 托盘区（TrayNotifyWnd）的左边界：靠右落点据此避开托盘/时钟，实测比估计可靠；量不到返回 0
        int trayLeft = 0;
        var notify = FindWindowEx(tray, nint.Zero, "TrayNotifyWnd", null);
        if (notify != nint.Zero && GetWindowRect(notify, out var notifyRect) && notifyRect.Right > notifyRect.Left)
            trayLeft = notifyRect.Left;

        // ABM_GETTASKBARPOS 只报告**主**任务栏的停靠位置，对副屏无效（它还停在主屏底部）。
        // 所以只有目标就是主任务栏时才用它（它能在自动隐藏时给出正确的停靠矩形）；
        // 副屏用窗口自身的实时矩形 —— 副屏任务栏不会被自动隐藏逻辑移出屏幕，实时矩形就是停靠矩形。
        RECT docked;
        if (tray == FindWindow("Shell_TrayWnd", null) && TryGetDockedTaskbarRect() is { } appbar)
            docked = appbar;
        else
            docked = live;

        return new TaskbarStrip(docked, visible, trayLeft);
    }

    /// <summary>
    /// 目标显示器上的任务栏窗口句柄（主屏 <c>Shell_TrayWnd</c>、副屏 <c>Shell_SecondaryTrayWnd</c>）。
    /// UIA 枚举任务栏元素时要用 —— 副屏任务栏是**另一个窗口**，拿主屏句柄去枚举只会看到主屏的按钮。
    /// 取不到返回 0。
    /// </summary>
    public static nint GetTaskbarWindow(RECT display) => FindTaskbarFor(display);

    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    /// <summary>按「与目标显示器外框重叠最多」挑选任务栏窗口。一块都没重叠时返回 0。</summary>
    private static nint FindTaskbarFor(RECT display)
    {
        nint best = nint.Zero;
        int bestOverlap = 0;

        // 主任务栏
        var primary = FindWindow(PrimaryTaskbarClass, null);
        Consider(primary);

        // 每块副屏一个：类名相同、没有标题，只能逐个枚举同类的顶层窗口
        nint cursor = nint.Zero;
        while (true)
        {
            cursor = FindWindowEx(nint.Zero, cursor, SecondaryTaskbarClass, null);
            if (cursor == nint.Zero) break;
            Consider(cursor);
        }

        return best;

        void Consider(nint hwnd)
        {
            if (hwnd == nint.Zero) return;
            if (!GetWindowRect(hwnd, out var rect)) return;

            int overlap = MinOverlap(rect, display);
            // 只认真正压在这块屏上的任务栏：自动隐藏时任务栏被移出屏幕，重叠会变成负数/极小，
            // 那种情况仍要认出来（岛据此判断"任务栏收起了"），所以用 0 做门槛而不是 SliverPx。
            if (overlap < 0) return;
            if (overlap <= bestOverlap && best != nint.Zero) return;

            bestOverlap = overlap;
            best = hwnd;
        }
    }

    /// <summary>两个矩形的较小重叠边（负数表示不重叠）。</summary>
    private static int MinOverlap(RECT a, RECT b)
        => Math.Min(
            Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left),
            Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

    private static RECT? TryGetDockedTaskbarRect()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == nint.Zero) return null;
        return data.rc;
    }

    // ---- 全屏应用检测（全屏时岛可隐藏 / 缩成贴边小色条）----

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int pquns);

    private const long WS_CAPTION_BITS = 0x00C00000;

    /// <summary>
    /// 前台窗口是否正在「全屏」占满给定显示器外框：独占全屏 / 演示模式（系统通知状态），
    /// 或没有标题栏、矩形覆盖整块屏的无边框全屏。带标题栏的最大化窗口（任务栏自动隐藏时矩形也会盖满屏）不算。
    /// 桌面、任务栏、本进程窗口（聚光卡覆盖窗）一律不算。
    /// </summary>
    public static bool IsFullscreenForeground(RECT display)
    {
        var fg = GetForegroundWindow();
        if (fg == nint.Zero || !IsWindowVisible(fg) || IsIconic(fg)) return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        var name = new char[64];
        int len = GetClassName(fg, name, name.Length);
        string cls = len > 0 ? new string(name, 0, len) : "";
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        if (!GetWindowRect(fg, out var r)) return false;
        bool covers = r.Left <= display.Left && r.Top <= display.Top
                      && r.Right >= display.Right && r.Bottom >= display.Bottom;

        if (covers)
        {
            long style = (long)GetWindowLongPtr(fg, GWL_STYLE);
            if ((style & WS_CAPTION_BITS) != WS_CAPTION_BITS) return true;
        }

        // 独占全屏 / 演示模式：窗口矩形不一定盖满，但必须与目标屏有重叠
        if (SHQueryUserNotificationState(out int state) == 0 && (state == 3 || state == 4))
            return MinOverlap(r, display) > 0;

        return false;
    }

    // ---- 样式守卫：AppWindow/presenter 会反复把 WS_DLGFRAME/WS_SYSMENU 写回 GWL_STYLE（白描边），
    // 子类化拦截 WM_STYLECHANGING，从根上过滤掉边框样式位 ----

    private const uint WM_STYLECHANGING = 0x007C;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCPAINT = 0x0085;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_SYSCOMMAND = 0x0112;
    /// <summary>SC_CLOSE 的低位掩码与值（系统菜单/Alt+F4 的关闭命令）。</summary>
    private const nint SC_CLOSE_MASK = 0xFFF0;
    private const nint SC_CLOSE = 0xF060;
    /// <summary>WS_CAPTION|WS_SYSMENU|WS_THICKFRAME|WS_MINIMIZEBOX|WS_MAXIMIZEBOX</summary>
    private const uint FrameStyleMask = 0x00CF0000;
    /// <summary>HTTRANSPARENT：让点击穿透到下层窗口。</summary>
    private const nint HTTRANSPARENT = -1;
    /// <summary>HTCLIENT：正常客户区点击。</summary>
    private const nint HTCLIENT = 1;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    // 静态持有委托，防止被 GC 回收
    private static readonly SubclassProc _styleGuardProc = StyleGuardProc;

    // 当前点击区域（由 IslandWindow 更新）。区域外返回 HTTRANSPARENT 穿透点击。
    private static nint _hitRgn;

    /// <summary>更新点击穿透区域：区域内正常响应，区域外穿透到下层窗口。
    /// 调用方无需释放 hRgn，由 SetHitRegion 接管所有权。
    /// 空句柄会被忽略（保留上一份有效区域）——区域为空时整块窗口都参与命中测试，
    /// 会让透明画布区域拦截点击。</summary>
    public static void SetHitRegion(nint hRgn)
    {
        if (hRgn == nint.Zero) return;

        var old = _hitRgn;
        _hitRgn = hRgn;
        if (old != nint.Zero && old != hRgn)
            DeleteObject(old);
    }

    private static unsafe nint StyleGuardProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        // 拦截 WM_NCCALCSIZE：返回 0 表示整个窗口都是客户区，彻底消除非客户区（白边来源）
        if (uMsg == WM_NCCALCSIZE && wParam == 1)
        {
            return 0;
        }
        // 拦截 WM_NCPAINT：不绘制任何非客户区内容
        if (uMsg == WM_NCPAINT)
        {
            return 0;
        }
        // 拦截 WM_ERASEBKGND：用「透明擦除」取代默认的类背景画刷（不透明白）。
        // 父窗口表面必须始终是 alpha=0 —— 否则窗口形状里任何 XAML 内容没覆盖到的像素
        // （动画中形状比内容早一帧、圆角外侧等）都会显示成白块/白闪。
        if (uMsg == WM_ERASEBKGND)
        {
            var hdc = wParam;
            if (hdc != nint.Zero && GetClientRect(hWnd, out var rc))
                PatBlt(hdc, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, BLACKNESS);
            return 1;
        }
        if (uMsg == WM_STYLECHANGING && wParam == GWL_STYLE && lParam != 0)
        {
            // STYLESTRUCT { uint styleOld; uint styleNew; }
            uint* styleNew = (uint*)(lParam + sizeof(uint));
            *styleNew &= ~FrameStyleMask;
            *styleNew |= WS_POPUP;
        }
        // 拦截 WM_SYSCOMMAND/SC_CLOSE：窗口在「输入会话」里会短暂可激活，Alt+F4 就会落到
        // 岛/聚光卡上 —— 它们既没有系统菜单也不该被键盘手势关掉（程序自己的关闭走 WM_CLOSE，
        // 不受这里影响）。
        if (uMsg == WM_SYSCOMMAND && (wParam & SC_CLOSE_MASK) == SC_CLOSE)
        {
            return 0;
        }
        // 拦截 WM_NCHITTEST：透明 padding 区域返回 HTTRANSPARENT 让点击穿透
        if (uMsg == WM_NCHITTEST)
        {
            var result = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            if (result == HTCLIENT)
            {
                int x = (short)(lParam & 0xFFFF);
                int y = (short)((lParam >> 16) & 0xFFFF);
                ScreenToClient(hWnd, ref x, ref y);
                var rgn = _hitRgn;
                if (rgn != nint.Zero && !PtInRegion(rgn, x, y))
                    return HTTRANSPARENT;
            }
            return result;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [LibraryImport("user32.dll")]
    private static partial nint ScreenToClient(nint hWnd, ref int x, ref int y);

    /// <summary>安装样式守卫，永久阻止任何代码给窗口加回边框样式。</summary>
    public static void InstallStyleGuard(nint hwnd) => SetWindowSubclass(hwnd, _styleGuardProc, 0x15AD, 0);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ExtractIconEx(string szFileName, int nIconIndex, nint[]? phiconLarge, nint[]? phiconSmall, uint nIcons);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    public static partial nint CopyImage(nint h, uint type, int cx, int cy, uint flags);

    public const uint IMAGE_BITMAP = 0;
    public const uint LR_COPYRETURNORG = 0x00000004;

    // ---- 启动期的原生提示框：单实例闸门跑在 XAML 之前，那会儿没有任何 XAML 对话框可用 ----

    private const uint MB_OK = 0x0000_0000;
    private const uint MB_ICONINFORMATION = 0x0000_0040;
    private const uint MB_SETFOREGROUND = 0x0001_0000;
    private const uint MB_TOPMOST = 0x0004_0000;

    /// <summary>ASFW_ANY：不限定进程，谁抢前台都放行。</summary>
    public const uint ASFW_ANY = 0xFFFF_FFFF;

    /// <summary>把「可以抢前台」的资格交给别的进程（调用方自己得能抢前台）。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint hWnd, string text, string caption, uint type);

    /// <summary>
    /// 原生模态提示框。<paramref name="text"/> 里用 \n 换行即可。
    /// 置顶显示：调用它的重复实例马上就会退出，这条提醒不能被随后弹出来的设置窗口盖住。
    /// </summary>
    public static void ShowStartupMessage(string text, string caption)
        => MessageBox(
            nint.Zero,
            text.Replace("\n", "\r\n"),
            caption,
            MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST);
}
