// WinIsland.TaskbarHook.dll —— 任务栏嵌入 hook（阶段一：握手 + 只读能力探测，绝不修改任务栏）
//
// 宿主协议见 Core/TaskbarHookHost.cs。宿主先创建这些命名事件，hook 只打开：
//   Local\WinIsland.TaskbarHook.Ready        成功接管任务栏后 SetEvent
//   Local\WinIsland.TaskbarHook.Unsupported  当前系统/版本不支持时 SetEvent（宿主不再自动重试）
//   Local\WinIsland.TaskbarHook.Heartbeat    每 500ms SetEvent，宿主 Reset 后用它探活
//   Local\WinIsland.TaskbarHook.Shutdown     宿主 SetEvent 请求卸载；hook 收到后 FreeLibraryAndExitThread
//
// 安全约束（照参考项目的规则，任何一条都不能破）：
//   · 阶段一只做只读检查（模块/导出是否在），绝不动任务栏的可视化树；
//   · 所有失败都不抛异常、不留残留：报 Unsupported 后继续心跳，让宿主知道「活着但没接管」；
//   · DllMain 里不做重活（只起线程），所有工作在线程里做，绝不碰加载器锁；
//   · 不依赖外部运行时（静态 CRT），避免给 explorer 引入额外依赖。

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <string>

namespace {

constexpr wchar_t kReadyEvent[] = L"Local\\WinIsland.TaskbarHook.Ready";
constexpr wchar_t kUnsupportedEvent[] = L"Local\\WinIsland.TaskbarHook.Unsupported";
constexpr wchar_t kHeartbeatEvent[] = L"Local\\WinIsland.TaskbarHook.Heartbeat";
constexpr wchar_t kShutdownEvent[] = L"Local\\WinIsland.TaskbarHook.Shutdown";

constexpr DWORD kHeartbeatIntervalMs = 500;

HMODULE g_module = nullptr;
HANDLE g_ready = nullptr;
HANDLE g_unsupported = nullptr;
HANDLE g_heartbeat = nullptr;
HANDLE g_shutdown = nullptr;

/// 极简日志：%LOCALAPPDATA%\WinIsland\logs\taskbar-hook.log（每行带时间戳，立即 flush）。
/// explorer 里不做任何网络/账户类工作，只写这一行文本，方便宿主与用户查因。
void Log(const wchar_t* format, ...)
{
    wchar_t message[512] = {};
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(message, _TRUNCATE, format, args);
    va_end(args);

    wchar_t localAppData[MAX_PATH] = {};
    if (GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH) == 0) return;

    std::wstring directory = std::wstring(localAppData) + L"\\WinIsland\\logs";
    CreateDirectoryW((std::wstring(localAppData) + L"\\WinIsland").c_str(), nullptr);
    CreateDirectoryW(directory.c_str(), nullptr);

    SYSTEMTIME now = {};
    GetLocalTime(&now);
    wchar_t line[768] = {};
    _snwprintf_s(line, _TRUNCATE, L"%04u-%02u-%02u %02u:%02u:%02u.%03u [hook] %s\r\n",
                 now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, message);

    std::wstring path = directory + L"\\taskbar-hook.log";
    HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;

    int bytes = WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0, nullptr, nullptr);
    if (bytes > 1)
    {
        std::string utf8(static_cast<size_t>(bytes - 1), '\0');
        WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8.data(), bytes, nullptr, nullptr);
        DWORD written = 0;
        WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    }
    CloseHandle(file);
}

HANDLE OpenHostEvent(const wchar_t* name)
{
    return OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, name);
}

/// 阶段一的只读能力探测：确认这个 explorer 里 XAML 诊断入口是否存在。
/// 不做任何接管动作，只把结论写进日志并据此上报支持性。
bool ProbeXamlDiagnostics(std::wstring& notes)
{
    HMODULE xaml = GetModuleHandleW(L"Windows.UI.Xaml.dll");
    const wchar_t* moduleName = L"Windows.UI.Xaml.dll";
    if (xaml == nullptr)
    {
        xaml = GetModuleHandleW(L"Microsoft.UI.Xaml.dll");
        moduleName = L"Microsoft.UI.Xaml.dll";
    }

    if (xaml == nullptr)
    {
        notes = L"explorer 里没有加载 XAML 模块（任务栏可能不是 XAML 实现）";
        return false;
    }

    FARPROC entry = GetProcAddress(xaml, "InitializeXamlDiagnosticsEx");
    if (entry == nullptr)
    {
        notes = std::wstring(L"模块 ") + moduleName + L" 里没有 InitializeXamlDiagnosticsEx 导出";
        return false;
    }

    notes = std::wstring(L"已找到诊断入口 ") + moduleName + L"!InitializeXamlDiagnosticsEx"
            + L"（阶段一不接管，仅探测）";
    return true;
}

/// 工作线程：握手 → 心跳 → 等 Shutdown → 干净卸载。
/// 阶段一「不接管」= 上报 Unsupported（宿主据此回退浮层且不再自动重试）。
DWORD WINAPI WorkerProc(LPVOID)
{
    g_ready = OpenHostEvent(kReadyEvent);
    g_unsupported = OpenHostEvent(kUnsupportedEvent);
    g_heartbeat = OpenHostEvent(kHeartbeatEvent);
    g_shutdown = OpenHostEvent(kShutdownEvent);

    if (g_heartbeat == nullptr || g_shutdown == nullptr)
    {
        Log(L"宿主事件不存在（heartbeat=%p shutdown=%p），自行退出", g_heartbeat, g_shutdown);
        FreeLibraryAndExitThread(g_module, 0);
    }

    Log(L"已注入 explorer (pid %lu)，开始握手", GetCurrentProcessId());

    std::wstring notes;
    bool capable = ProbeXamlDiagnostics(notes);
    Log(L"能力探测：%s", notes.c_str());

    if (capable)
    {
        Log(L"阶段一：探测通过，但接管逻辑尚未实现 —— 上报 Unsupported，保持心跳");
        if (g_unsupported != nullptr) SetEvent(g_unsupported);
    }
    else
    {
        Log(L"阶段一：环境不具备接管条件 —— 上报 Unsupported，保持心跳");
        if (g_unsupported != nullptr) SetEvent(g_unsupported);
    }

    DWORD staleBeats = 0;
    while (true)
    {
        // 宿主每消费一次心跳就 ResetEvent；连续多次发现没人消费 = 宿主进程已退出，
        // 此时必须自己卸载：explorer 里绝不留残留代码（参考项目同款规则）。
        if (WaitForSingleObject(g_heartbeat, 0) == WAIT_OBJECT_0)
        {
            if (++staleBeats >= 5)
            {
                Log(L"宿主已退出（心跳连续无人消费），自行卸载");
                break;
            }
        }
        else
        {
            staleBeats = 0;
        }

        if (g_heartbeat != nullptr) SetEvent(g_heartbeat);

        DWORD wait = WaitForSingleObject(g_shutdown, kHeartbeatIntervalMs);
        if (wait == WAIT_OBJECT_0)
        {
            Log(L"收到 Shutdown，移除一切并卸载 DLL");
            break;
        }
        if (wait == WAIT_FAILED)
        {
            Log(L"等待 Shutdown 失败（错误码 %lu），自行卸载", GetLastError());
            break;
        }
    }

    if (g_ready != nullptr) CloseHandle(g_ready);
    if (g_unsupported != nullptr) CloseHandle(g_unsupported);
    if (g_heartbeat != nullptr) CloseHandle(g_heartbeat);
    if (g_shutdown != nullptr) CloseHandle(g_shutdown);

    FreeLibraryAndExitThread(g_module, 0);
    return 0;
}

}  // namespace

/// 宿主/诊断用的版本号：接管协议每变一次就加一。
extern "C" __declspec(dllexport) DWORD WINAPI WinIslandHookProtocolVersion()
{
    return 1;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);

    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);

        // DllMain 里只起线程，绝不等待/加载/分配大块内存
        HANDLE thread = CreateThread(nullptr, 0, WorkerProc, nullptr, 0, nullptr);
        if (thread != nullptr) CloseHandle(thread);
    }

    return TRUE;
}
