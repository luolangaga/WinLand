using System.Runtime.InteropServices;
using WinIsland.Core;

namespace ClipboardIsland;

/// <summary>
/// 「点一条历史 → 直接粘进当前输入框」需要三件事：
///   1. 判断该粘到哪个窗口。岛窗和聚光卡都是 WS_EX_NOACTIVATE，点击不会抢焦点，
///      所以用户点历史条目的那一刻，前台窗口通常**就是**他正在打字的那个输入框；
///   2. 万一焦点不在目标上（用户中途点过别处），能靠 <see cref="LastExternal"/> 找回最近一次的外部窗口；
///   3. 用 SendInput 发一次 Ctrl+V。
///
/// 安全边界：只有能确认目标窗口时才按键。切不过去就宁可什么都不做 ——
/// 把 Ctrl+V 发到错误的窗口（比如已经打完字的聊天框）比不粘贴糟糕得多。
/// </summary>
internal static class PasteTarget
{
    private const int VkControl = 0x11;
    private const int VkV = 0x56;

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>宿主自己的进程号：用来把「本进程的窗口」排除出粘贴目标。</summary>
    private static readonly int SelfProcessId = Environment.ProcessId;
    private static readonly uint SelfProcessIdU = (uint)SelfProcessId;

    private static IntPtr _lastExternal;

    /// <summary>最近一次「不是本进程」的前台窗口。</summary>
    public static IntPtr LastExternal => _lastExternal;

    /// <summary>
    /// 采样当前前台窗口，只记下不是本进程的那些。由插件按秒调用，开销可忽略。
    /// </summary>
    public static void Sample()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (IsExternal(hwnd)) _lastExternal = hwnd;
        }
        catch
        {
            // 采样失败不影响主流程
        }
    }

    /// <summary>这次该粘到哪个窗口：优先当前前台，其次兜底窗口；都没有返回零。</summary>
    public static IntPtr Resolve()
    {
        var foreground = GetForegroundWindow();
        if (IsExternal(foreground)) return foreground;

        return IsExternal(_lastExternal) ? _lastExternal : IntPtr.Zero;
    }

    /// <summary>
    /// 把 Ctrl+V 送到用户当前正在输入的窗口。成功返回 true。
    /// 任何一步不确定都返回 false，让调用方只保留「已复制到剪贴板」这一半效果。
    /// </summary>
    public static async Task<bool> SendPasteAsync(IPluginLogger log)
    {
        var target = Resolve();
        if (target == IntPtr.Zero)
        {
            log.Warn("找不到可粘贴的目标窗口，这次只复制到剪贴板");
            return false;
        }

        if (IsIconic(target))
        {
            log.Warn("目标窗口已最小化，这次只复制到剪贴板");
            return false;
        }

        if (GetForegroundWindow() != target)
        {
            // 兜底路径：焦点真的不在目标窗口上，主动把它叫回来。叫不动就放弃
            if (!SetForegroundWindow(target))
            {
                log.Warn("无法把焦点切回目标窗口，这次只复制到剪贴板");
                return false;
            }

            for (var i = 0; i < 10 && GetForegroundWindow() != target; i++)
            {
                await Task.Delay(30).ConfigureAwait(true);
            }

            if (GetForegroundWindow() != target)
            {
                log.Warn("焦点没有切回目标窗口，这次只复制到剪贴板");
                return false;
            }
        }

        return SendCtrlV(log);
    }

    private static bool SendCtrlV(IPluginLogger log)
    {
        var inputs = new INPUT[4];

        inputs[0].type = InputKeyboard;
        inputs[0].U.ki.wVk = VkControl;

        inputs[1].type = InputKeyboard;
        inputs[1].U.ki.wVk = VkV;

        inputs[2].type = InputKeyboard;
        inputs[2].U.ki.wVk = VkV;
        inputs[2].U.ki.dwFlags = KeyEventKeyUp;

        inputs[3].type = InputKeyboard;
        inputs[3].U.ki.wVk = VkControl;
        inputs[3].U.ki.dwFlags = KeyEventKeyUp;

        var sent = SendInput(4, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 4) return true;

        // 最常见的原因是目标窗口以管理员身份运行：UIPI 会拦下低完整性进程注入的按键
        log.Warn($"发送 Ctrl+V 失败（内核接受了 {sent}/4 个事件，错误码 {Marshal.GetLastWin32Error()}）");
        return false;
    }

    private static bool IsExternal(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid != 0 && pid != SelfProcessIdU;
    }

    // ---------------------------------------------------------------- Win32 声明

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // 显式布局的联合体：x64 下整个 INPUT 必须正好 40 字节，少一个分支 SendInput 直接拒绝（0x80070057）
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInputStructure);
}
