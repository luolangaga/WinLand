using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinIsland.Core;

/// <summary>
/// 任务栏嵌入 hook 的宿主侧：注入、健康检查、失败自动回滚。
///
/// 原生侧（WinIsland.TaskbarHook.dll，需要 Windows SDK 才能编译）负责接管任务栏 XAML 树、
/// 把岛插进 explorer 自己的布局里，从而让任务栏图标真正「让位」。这里只负责生命周期与安全，
/// 所有判定都「宁可不注入、绝不留在半改状态」：
///   · 注入前门禁：系统版本、DLL 是否存在、explorer 是否找到；
///   · 注入后 3 秒内必须等到 Ready（成功接管）或心跳，否则视为失败并回退；
///   · 注入后短时间内 explorer 重启过（很可能是我们的锅）→ 永久标记失败，不再自动重试；
///   · 任何失败都只回退到现有浮层方案 —— 岛照旧能用，绝不会出现「任务栏被改坏」。
///
/// 协议（全部是 Local\ 命名对象，宿主创建、hook 打开；不需要管理员权限）：
///   Ready       hook 成功接管任务栏后 SetEvent，宿主据此区分「已接管」与「注入了但不支持」；
///   Heartbeat   hook 每 500ms SetEvent，宿主 Wait(0) 后 Reset，用于探活；
///   Shutdown    宿主 SetEvent，hook 收到后立刻移除已插入元素并自行 detach。
/// </summary>
internal sealed partial class TaskbarHookHost : IDisposable
{
    public enum HookState
    {
        /// <summary>用户没开启（或已关闭）。</summary>
        Off,
        /// <summary>hook 组件不存在 / 系统不满足前提 —— 根本不会注入。</summary>
        Unavailable,
        /// <summary>正在注入 / 等 hook 报 Ready。</summary>
        Injecting,
        /// <summary>已接管且心跳正常。</summary>
        Active,
        /// <summary>注入了但当前系统不支持（hook 主动 fail-closed 退出），已回退。</summary>
        Unsupported,
        /// <summary>注入失败或心跳中断，已回退，且不再自动重试。</summary>
        Failed,
    }

    public const string EnabledKey = "island.taskbarHook";
    public const string FailedKey = "island.taskbarHook.failed";
    public const string UnsupportedKey = "island.taskbarHook.unsupported";
    public const string StatusKey = "island.taskbarHook.status";

    private const string DllName = "WinIsland.TaskbarHook.dll";
    private const string ReadyEventName = @"Local\WinIsland.TaskbarHook.Ready";
    private const string UnsupportedEventName = @"Local\WinIsland.TaskbarHook.Unsupported";
    private const string HeartbeatEventName = @"Local\WinIsland.TaskbarHook.Heartbeat";
    private const string ShutdownEventName = @"Local\WinIsland.TaskbarHook.Shutdown";
    /// <summary>hook 侧自己写的日志（explorer 进程内），出错时把最后一行带回给用户看。</summary>
    private static readonly string HookLogPath =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "WinIsland", "logs", "taskbar-hook.log");

    /// <summary>hook 注入后多久没 Ready/心跳就判定失败（含注入前的旧实例等待，故留宽一点）。</summary>
    private const int ActivateTimeoutMs = 6000;
    /// <summary>心跳超过这个间隔没来就认为 hook 卡死/进程重启。</summary>
    private const int HeartbeatTimeoutMs = 1500;
    /// <summary>注入后这个时间内 explorer 挂掉，就认定是注入的锅，永久停用。</summary>
    private const int SuspectCrashWindowMs = 15000;
    /// <summary>explorer 正常重启后的最大自动重注入次数。</summary>
    private const int MaxReinjects = 3;
    /// <summary>最低支持的 Windows 版本（Windows 11 21H2）。</summary>
    private const int MinWindowsBuild = 22000;

    private readonly SettingsService _settings;
    private readonly IPluginLogger _log;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private nint _readyEvent;
    private nint _unsupportedEvent;
    private nint _heartbeatEvent;
    private nint _shutdownEvent;
    private uint _explorerPid;
    private long _stateChangedAt;
    private long _lastHeartbeatAt;
    private int _reinjectCount;

    public TaskbarHookHost(SettingsService settings, IPluginLogger log, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _settings = settings;
        _log = log;
        _dispatcher = dispatcher;
        _readyEvent = Native.CreateEventW(nint.Zero, true, false, ReadyEventName);
        _unsupportedEvent = Native.CreateEventW(nint.Zero, true, false, UnsupportedEventName);
        _heartbeatEvent = Native.CreateEventW(nint.Zero, true, false, HeartbeatEventName);
        _shutdownEvent = Native.CreateEventW(nint.Zero, true, false, ShutdownEventName);
    }

    public HookState State { get; private set; } = HookState.Off;

    /// <summary>hook 是否真的在接管任务栏（此时浮层不该再往任务栏里画）。</summary>
    public bool IsActive => State == HookState.Active;

    public event Action? Changed;

    /// <summary>由宿主定时器（约 1 秒一次）调用：驱动注入、探活与回滚。</summary>
    public void Tick()
    {
        // 这个方法是 WinUI 定时器回调，异常越过边界会变成 stowed exception 直接崩掉进程
        // （AGENTS.md：每个从宿主进入的回调都必须守卫）
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            _log.Error("任务栏 hook 看门狗异常（已忽略，回退浮层方案）", ex);
            try
            {
                RequestDetach();
                _settings.Set(FailedKey, true);
                SetState(HookState.Failed, $"看门狗异常，已回退浮层：{ex.Message}");
            }
            catch
            {
                // 连回退都失败就什么都不做，绝不能把异常抛回 WinUI
            }
        }
    }

    private void TickCore()
    {
        if (!_settings.Get(EnabledKey, false))
        {
            if (State != HookState.Off) SetState(HookState.Off, "未开启（当前使用浮层方案）。");
            return;
        }

        // 心跳要在所有状态下一律消费：hook 用「连续多次没人消费心跳」判断宿主是否还活着，
        // 若某个状态（例如已上报不支持）就不再消费，hook 会误以为宿主已死而自行卸载。
        if (Consume(ref _heartbeatEvent)) _lastHeartbeatAt = Environment.TickCount64;

        if (_settings.Get(FailedKey, false))
        {
            SetState(HookState.Failed, $"上次注入失败，已停用自动注入（关掉再打开可重试）。{HookLogTail()}");
            return;
        }

        if (_settings.Get(UnsupportedKey, "") is { Length: > 0 } unsupportedReason)
        {
            MarkUnsupported(unsupportedReason, persist: false);
            return;
        }

        switch (State)
        {
            case HookState.Off:
            case HookState.Unavailable:
                if (_stateChangedAt == 0 || State == HookState.Unavailable)
                    TryInject();
                break;

            case HookState.Injecting:
                // hook 明确说「不支持」时必须先判 —— 它仍然会心跳，不能把心跳当接管成功
                if (Consume(ref _unsupportedEvent))
                {
                    MarkUnsupported(HookLogTail(), persist: true);
                    break;
                }

                if (WaitForReady())
                {
                    _lastHeartbeatAt = Environment.TickCount64;
                    SetState(HookState.Active, "已接管任务栏（任务栏图标会为岛让位）。");
                }
                else if (Elapsed(_stateChangedAt) > ActivateTimeoutMs)
                {
                    // 注入进去了却既不 Ready 也没上报：DLL 卡住
                    RequestDetach();
                    MarkFailed($"注入后 {ActivateTimeoutMs}ms 内没有任何回应");
                }
                break;

            case HookState.Unsupported:
            case HookState.Failed:
                break;

            case HookState.Active:
                if (Environment.TickCount64 - _lastHeartbeatAt > HeartbeatTimeoutMs)
                    HandleHeartbeatLost();
                break;
        }
    }

    /// <summary>用户关闭开关 / 退出应用时调用：请求 hook 干净卸载。</summary>
    public void Disable()
    {
        RequestDetach();
        SetState(HookState.Off, "已关闭（当前使用浮层方案）。");
    }

    public void Dispose()
    {
        RequestDetach();
        Native.CloseHandle(_readyEvent);
        Native.CloseHandle(_heartbeatEvent);
        Native.CloseHandle(_shutdownEvent);
        _readyEvent = _heartbeatEvent = _shutdownEvent = nint.Zero;
    }

    #region 注入与回滚

    private void TryInject()
    {
        if (WindowsBuild < MinWindowsBuild)
        {
            SetState(HookState.Unavailable, "仅支持 Windows 11。");
            return;
        }

        string dll = Path.Combine(AppContext.BaseDirectory, DllName);
        if (!File.Exists(dll))
        {
            // 原生侧还没编出来（需要 Windows SDK）—— 静默停在「未安装」，不报错刷屏
            SetState(HookState.Unavailable, $"hook 组件未安装（缺少 {DllName}）。");
            return;
        }

        var trayHwnd = Win32.GetTaskbarWindow();
        if (trayHwnd == nint.Zero)
        {
            SetState(HookState.Unavailable, "找不到任务栏窗口，暂不注入。");
            return;
        }

        Native.GetWindowThreadProcessId(trayHwnd, out uint pid);
        if (pid == 0)
        {
            SetState(HookState.Unavailable, "无法定位 explorer 进程。");
            return;
        }

        if (_explorerPid != pid)
        {
            _explorerPid = pid;
            _reinjectCount = 0;
        }

        SetState(HookState.Injecting, "正在注入 hook…");

        // 注入含 WaitForSingleObject，不能在 UI 线程上做；而且注入前要先请旧实例退场并等它卸载，
        // 所以整段放后台线程。任何结果都切回 UI 线程再改状态（状态与设置写入只允许 UI 线程）。
        Task.Run(() =>
        {
            // 1) 请上一个实例退场，并等它 FreeLibraryAndExitThread 走完
            RequestDetach();
            Thread.Sleep(400);

            // 2) 复位所有协议事件 —— Shutdown 是手动重置事件，不复位的话新 hook 会立刻
            //    看到「已关闭」并当场卸载（第一次实测就是这么失败的：心跳一次就消失）
            Native.ResetEvent(_readyEvent);
            Native.ResetEvent(_unsupportedEvent);
            Native.ResetEvent(_heartbeatEvent);
            Native.ResetEvent(_shutdownEvent);

            // 3) 注入
            bool ok = InjectDll(pid, dll, out string error);
            _dispatcher.TryEnqueue(() => FinishInject(ok, error, dll, pid));
        });
    }

    private void FinishInject(bool ok, string error, string dll, uint pid)
    {
        if (State != HookState.Injecting) return;   // 期间被关掉/已回退就忽略

        if (!ok)
        {
            MarkFailed(error);
            return;
        }

        _stateChangedAt = Environment.TickCount64;
        _log.Info($"任务栏 hook 已注入 explorer (pid {pid})：{dll}");
    }

    private void HandleHeartbeatLost()
    {
        Native.GetWindowThreadProcessId(Win32.GetTaskbarWindow(), out uint pid);
        bool explorerRestarted = pid != 0 && pid != _explorerPid;

        if (explorerRestarted && Elapsed(_stateChangedAt) < SuspectCrashWindowMs)
        {
            // 刚注入完 explorer 就重启了：基本可以认定与我们有关 —— 永久停用，绝不再试
            MarkFailed($"注入后 {Elapsed(_stateChangedAt) / 1000}s 内 explorer 重启（疑似注入导致的崩溃）");
            return;
        }

        if (explorerRestarted && _reinjectCount < MaxReinjects)
        {
            _reinjectCount++;
            _explorerPid = pid;
            _log.Warn($"explorer 已重启，第 {_reinjectCount} 次尝试重新注入任务栏 hook。");
            SetState(HookState.Off, "explorer 重启，正在重新注入…");
            return;
        }

        // 同一个 explorer 进程但心跳断了：hook 卡死或自卸载，回退浮层
        RequestDetach();
        MarkFailed("hook 心跳中断");
    }

    private void MarkFailed(string reason)
    {
        _settings.Set(FailedKey, true);
        _log.Warn($"任务栏 hook 已回退到浮层方案：{reason}。");
        SetState(HookState.Failed, $"注入失败，已回退浮层：{reason}。");
    }

    /// <summary>hook 明确上报「当前系统/版本不支持接管」：回退浮层、请它卸载，并按需持久化以免反复重试。</summary>
    private void MarkUnsupported(string reason, bool persist)
    {
        if (persist)
        {
            _settings.Set(UnsupportedKey, reason);
            _log.Warn($"任务栏 hook 上报不支持：{reason}");
            RequestDetach();   // 它什么也做不了，请它立刻卸载，explorer 里不留东西
        }
        SetState(HookState.Unsupported, $"已注入，但当前系统/版本不支持接管（仍用浮层）：{reason}");
    }

    /// <summary>hook 侧日志的最后一行 —— 它跑在 explorer 里，出问题的原因只能从那里带回来。</summary>
    private static string HookLogTail()
    {
        try
        {
            if (!File.Exists(HookLogPath)) return "（hook 未写日志）";
            string? last = null;
            foreach (string line in File.ReadLines(HookLogPath)) last = line;
            return last is { Length: > 0 } ? last : "（hook 日志为空）";
        }
        catch
        {
            return "（读 hook 日志失败）";
        }
    }

    private void RequestDetach()
    {
        if (_shutdownEvent != nint.Zero) Native.SetEvent(_shutdownEvent);
    }

    /// <summary>Ready 事件是否已置位；没置位但已经有心跳也算握手成功（hook 先起心跳再接管）。</summary>
    private bool WaitForReady()
    {
        if (_readyEvent != nint.Zero && Native.WaitForSingleObject(_readyEvent, 0) == Native.WAIT_OBJECT_0)
            return true;

        return Consume(ref _heartbeatEvent);
    }

    private static bool Consume(ref nint handle)
    {
        if (handle == nint.Zero) return false;
        if (Native.WaitForSingleObject(handle, 0) != Native.WAIT_OBJECT_0) return false;
        Native.ResetEvent(handle);
        return true;
    }

    private void SetState(HookState state, string status)
    {
        if (State == state && _settings.Get(StatusKey, "") == status) return;
        State = state;
        _stateChangedAt = Environment.TickCount64;
        _settings.Set(StatusKey, status);   // 设置页直接读这条展示状态
        Changed?.Invoke();
    }

    private static long Elapsed(long since) => Environment.TickCount64 - since;

    /// <summary>系统版本门禁：hook 依赖 Win11 的任务栏 XAML 结构。</summary>
    private static int WindowsBuild => System.Environment.OSVersion.Version.Build;

    #endregion

    #region Win32：远程注入

    private static bool InjectDll(uint pid, string dllPath, out string error)
    {
        error = "";
        nint process = Native.OpenProcess(Native.PROCESS_CREATE_THREAD | Native.PROCESS_QUERY_INFORMATION
                                          | Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_WRITE, false, pid);
        if (process == nint.Zero)
        {
            error = $"OpenProcess 失败（错误码 {Marshal.GetLastWin32Error()}）";
            return false;
        }

        nint remote = nint.Zero;
        nint thread = nint.Zero;
        try
        {
            byte[] path = Encoding.Unicode.GetBytes(dllPath + "\0");
            remote = Native.VirtualAllocEx(process, nint.Zero, (nuint)path.Length, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (remote == nint.Zero)
            {
                error = $"VirtualAllocEx 失败（错误码 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            if (!Native.WriteProcessMemory(process, remote, path, (nuint)path.Length, out _))
            {
                error = $"WriteProcessMemory 失败（错误码 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            nint loadLibrary = Native.GetProcAddress(Native.GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            if (loadLibrary == nint.Zero)
            {
                error = "找不到 LoadLibraryW";
                return false;
            }

            thread = Native.CreateRemoteThread(process, nint.Zero, 0, loadLibrary, remote, 0, out _);
            if (thread == nint.Zero)
            {
                error = $"CreateRemoteThread 失败（错误码 {Marshal.GetLastWin32Error()}，可能被安全软件拦截）";
                return false;
            }

            if (Native.WaitForSingleObject(thread, 5000) != Native.WAIT_OBJECT_0)
            {
                error = "等待远程 LoadLibrary 超时";
                return false;
            }

            // 远程线程的返回值就是 LoadLibrary 的 HMODULE：0 表示 DLL 加载失败
            if (!Native.GetExitCodeThread(thread, out nint module) || module == nint.Zero)
            {
                error = "DLL 在 explorer 中加载失败（位数不符 / 依赖缺失 / 被安全软件拦截）";
                return false;
            }

            return true;
        }
        finally
        {
            if (thread != nint.Zero) Native.CloseHandle(thread);
            if (remote != nint.Zero) Native.VirtualFreeEx(process, remote, 0, Native.MEM_RELEASE);
            Native.CloseHandle(process);
        }
    }

    private static partial class Native
    {
        public const uint PROCESS_CREATE_THREAD = 0x0002;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint PAGE_READWRITE = 0x04;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 258;

        [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
        public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [LibraryImport("kernel32.dll", EntryPoint = "VirtualAllocEx", SetLastError = true)]
        public static partial nint VirtualAllocEx(nint process, nint address, nuint size, uint type, uint protect);

        [LibraryImport("kernel32.dll", EntryPoint = "VirtualFreeEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool VirtualFreeEx(nint process, nint address, nuint size, uint type);

        [LibraryImport("kernel32.dll", EntryPoint = "WriteProcessMemory", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateRemoteThread", SetLastError = true)]
        public static partial nint CreateRemoteThread(nint process, nint attributes, nuint stackSize, nint start, nint parameter, uint flags, out uint threadId);

        [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
        public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

        [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeThread", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetExitCodeThread(nint thread, out nint exitCode);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint GetModuleHandle(string moduleName);

        [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint GetProcAddress(nint module, string name);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
            [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

        [LibraryImport("kernel32.dll", EntryPoint = "SetEvent", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetEvent(nint handle);

        [LibraryImport("kernel32.dll", EntryPoint = "ResetEvent", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ResetEvent(nint handle);

        [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint handle);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    }

    #endregion
}
