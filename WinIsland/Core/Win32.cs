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

    /// <summary>将窗口设置为无边框弹出样式 + 工具窗口 + 不抢焦点。</summary>
    public static void MakeIslandStyle(nint hwnd)
    {
        SetWindowLongPtr(hwnd, GWL_STYLE, unchecked((nint)(WS_POPUP | WS_VISIBLE)));

        var ex = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        // 清除所有可能导致白边的扩展样式位
        ex &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
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
            return $"{cls} {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
        }
        return null;
    }

    // ---- 前台窗口变化监听：任务栏/开始菜单/搜索抬层时立刻重升岛（轮询只是兜底）----

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
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
    private static Action? _foregroundHandler;

    /// <summary>
    /// 安装「前台窗口变化」监听。WINEVENT_OUTOFCONTEXT 要求安装线程自己跑消息泵，
    /// 所以这里起一个后台线程专门 GetMessage。回调在钩子线程上执行，调用方负责切回 UI 线程。
    /// 只安装一次。用途见 <see cref="EnsureTopmost"/>。
    /// </summary>
    public static void InstallForegroundWatcher(Action onForegroundChanged)
    {
        if (_foregroundHandler != null) return;
        _foregroundHandler = onForegroundChanged;

        var thread = new Thread(() =>
        {
            SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nint.Zero, _foregroundProc, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

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

    /// <summary>
    /// 主任务栏条带。<paramref name="display"/> 为主显示器外框（物理像素，用于判断任务栏是否真的在屏幕上）。
    /// 停靠矩形取 ABM_GETTASKBARPOS —— 它在任务栏自动隐藏时仍报告停靠位置，正是要嵌入的位置；
    /// 可见性另算：自动隐藏/全屏收起时任务栏被移出屏幕，只留一道缝隙。
    /// 取不到（非 explorer 外壳、调用失败）返回 null，调用方退回屏幕外框。
    /// </summary>
    public static TaskbarStrip? GetTaskbarStrip(RECT display)
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == nint.Zero || !GetWindowRect(tray, out var live)) return null;

        bool visible = IsWindowVisible(tray) && MinOverlap(live, display) >= SliverPx;

        // 托盘区（TrayNotifyWnd）的左边界：靠右落点据此避开托盘/时钟，实测比估计可靠；量不到返回 0
        int trayLeft = 0;
        var notify = FindWindowEx(tray, nint.Zero, "TrayNotifyWnd", null);
        if (notify != nint.Zero && GetWindowRect(notify, out var notifyRect) && notifyRect.Right > notifyRect.Left)
            trayLeft = notifyRect.Left;

        return new TaskbarStrip(TryGetDockedTaskbarRect() ?? live, visible, trayLeft);
    }

    /// <summary>主任务栏窗口句柄（Shell_TrayWnd）；取不到返回 0。UIA 枚举任务栏元素时要用。</summary>
    public static nint GetTaskbarWindow() => FindWindow("Shell_TrayWnd", null);

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

    // ---- 样式守卫：AppWindow/presenter 会反复把 WS_DLGFRAME/WS_SYSMENU 写回 GWL_STYLE（白描边），
    // 子类化拦截 WM_STYLECHANGING，从根上过滤掉边框样式位 ----

    private const uint WM_STYLECHANGING = 0x007C;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCPAINT = 0x0085;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_ERASEBKGND = 0x0014;
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
}
