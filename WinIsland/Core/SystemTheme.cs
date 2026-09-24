using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace WinIsland.Core;

/// <summary>
/// 系统明暗主题（设置 → 个性化 → 颜色 → 「默认应用模式」，注册表 AppsUseLightTheme）。
///
/// 为什么不直接用 <c>Application.RequestedTheme</c> / <c>ActualTheme</c>：WinUI 3 的应用主题
/// 在启动时定一次就不再重解析，系统在进程存活期间换主题（手动切换、自动深色模式到点）它不跟着变，
/// 而岛是常驻程序 —— 换主题必须当场生效，不能等重启。
/// 这里用 <c>UISettings.ColorValuesChanged</c> 只当"系统颜色可能变了"的信号（它也会为别的颜色变化触发），
/// 收到后重新读一次注册表，值真的变了才通知订阅者；通知一律编组到 UI 线程，订阅者可以直接改 UI。
/// </summary>
internal static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightValueName = "AppsUseLightTheme";

    // 必须由字段持有：UISettings 一旦被回收，ColorValuesChanged 就再也不会来了
    private static readonly Windows.UI.ViewManagement.UISettings Settings = new();

    private static DispatcherQueue? _uiDispatcher;
    private static bool _started;

    /// <summary>当前是否浅色主题（读不到注册表值时按 Windows 默认的浅色算）。</summary>
    public static bool IsLight { get; private set; } = ReadIsLight();

    /// <summary>系统明暗主题变化（已在 UI 线程发出；只在真的变了时触发）。</summary>
    public static event Action? Changed;

    /// <summary>开始监听。必须在 UI 线程调用一次（<see cref="App"/> 启动时）。</summary>
    public static void Start(DispatcherQueue uiDispatcher)
    {
        if (_started) return;
        _started = true;
        _uiDispatcher = uiDispatcher;
        Settings.ColorValuesChanged += (_, _) => Reload();
    }

    private static void Reload()
    {
        bool light = ReadIsLight();
        if (light == IsLight) return;

        IsLight = light;
        var dispatcher = _uiDispatcher;
        if (dispatcher == null)
        {
            Changed?.Invoke();
            return;
        }

        if (dispatcher.HasThreadAccess) Changed?.Invoke();
        else dispatcher.TryEnqueue(() => Changed?.Invoke());
    }

    private static bool ReadIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(LightValueName) is not int value || value != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
