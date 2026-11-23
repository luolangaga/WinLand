using Windows.Graphics.Display;

namespace WinIsland.Core;

/// <summary>
/// 显示器解析层：把「岛该待在哪个屏幕」从散落各处的 <c>DisplayAreaFallback.Primary</c> 硬编码里收出来，
/// 统一按 <c>island.display</c> 设置（自动 = 跟随鼠标所在屏 / 指定某一台）解析，并在多屏热插拔时广播失效信号。
///
/// **枚举只用 <see cref="Win32.EnumerateMonitors"/>（<c>EnumDisplayMonitors</c>）。**
/// 不要用 <c>DisplayArea.FindAll()</c>：它在多屏机器上遍历会抛 <c>InvalidCastException</c>
/// （microsoft-ui-xaml#6454），旧实现把异常吞掉，于是所有多屏机器都被当成「只检测到一块显示器」，
/// 指定显示器与跟随鼠标全部静默失效 —— 设置页那句「当前只检测到一块显示器」就是它留下的。
/// 这里返回的几何（外框 / 工作区）全部是物理像素、虚拟桌面坐标，与 <c>AppWindow.MoveAndResize</c> 同一套。
/// </summary>
internal static class DisplayResolver
{
    /// <summary>设置键：岛锚定到哪块显示器。值为 <see cref="DisplayAuto"/> 或某块屏的 <c>\\.\DISPLAYn</c>。</summary>
    public const string SettingKey = "island.display";

    /// <summary>自动：跟随鼠标光标所在的显示器（默认）。</summary>
    public const string DisplayAuto = "auto";

    /// <summary>
    /// 显示器配置失效信号。多屏热插拔（拔/接外接屏、换分辨率、改显示器排列、切投影模式）时触发，
    /// **在任意线程**上 —— 订阅方自行编组到 UI 线程。
    /// </summary>
    public static event Action? DisplaysInvalidated;

    private static bool _subscribed;

    /// <summary>
    /// 测试注入：非 null 时代替 <see cref="Win32.EnumerateMonitors"/> 的枚举结果。
    /// 只在 <c>--reliability-tests</c> 里设置（真实双屏机器无法在 CI / 开发机上复现），
    /// 生产路径永远是 null。注入的是**物理像素**、虚拟桌面坐标 —— 与真实枚举同一套单位，
    /// 所以岛窗口按它算出的落点与真实副屏完全一致。
    /// </summary>
    internal static MonitorInfo[]? TestMonitors { get; set; }

    /// <summary>当前生效的显示器列表（唯一的枚举入口）。</summary>
    private static MonitorInfo[] Enumerate() => TestMonitors ?? Win32.EnumerateMonitors();

    /// <summary>
    /// 多屏热插拔时系统会广播 <c>DisplayInformation.DisplayContentsInvalidated</c>。
    /// WinUI 没有暴露等价事件，所以只能订阅这个静态事件。
    /// 该事件在**任意线程**触发，订阅方自行编组到 UI 线程。
    ///
    /// 这条事件在某些环境不会来（远程桌面、驱动更新、部分虚拟机），
    /// 所以调用方仍然要靠看门狗按外框定期核对（见 <c>IslandWindow.HealDisplayDrift</c>）。
    /// </summary>
    public static void EnsureInvalidationWatcher()
    {
        if (_subscribed) return;
        _subscribed = true;
        try
        {
            DisplayInformation.DisplayContentsInvalidated += (_, _) => NotifyInvalidated();
        }
        catch
        {
            // 取不到 DisplayInformation（极老系统 / 无显示器会话）时静默降级：只是没有热插拔重定位
        }
    }

    private static void NotifyInvalidated()
    {
        var handler = DisplaysInvalidated;
        if (handler == null) return;
        try
        {
            handler();
        }
        catch
        {
            // 订阅方在别的线程抛异常不能反过来污染系统事件
        }
    }

    /// <summary>所有显示器的描述（主屏排最前，其余按排列坐标从左到右、从上到下 —— 与设置页卡片同序）。</summary>
    public static MonitorInfo[] List() => Enumerate();

    /// <summary>
    /// 解析岛应该待在哪块显示器。
    /// <paramref name="setting"/> 为 <c>auto</c>/空时跟随鼠标光标所在屏；否则按 <c>\\.\DISPLAYn</c> 匹配，
    /// 匹配不到（那块屏被拔了/换插口了）静默退回自动模式 —— 不报错，用户下次打开设置页会看到「自动」被选中。
    /// </summary>
    /// <param name="fallbackRect">
    /// 鼠标屏解析不出来时的兜底（通常传岛窗口当前矩形）：绝不用「永远主屏」，
    /// 那在多屏机器上等于把跟随鼠标/指定屏全部废掉。
    /// </param>
    public static MonitorInfo Resolve(string? setting, Win32.RECT fallbackRect)
    {
        var monitors = Enumerate();
        if (monitors.Length == 0)
        {
            // 枚举彻底失败（没有桌面/远程会话）：退回一个与兜底矩形同位置的假记录，
            // 调用方拿到的是"目标屏就是现在这块"，不会把窗口搬到屏幕外。
            int width = Math.Max(1, fallbackRect.Right - fallbackRect.Left);
            int height = Math.Max(1, fallbackRect.Bottom - fallbackRect.Top);
            return new MonitorInfo(string.Empty, true, fallbackRect.Left, fallbackRect.Top,
                width, height, fallbackRect.Left, fallbackRect.Top, width, height);
        }

        bool auto = string.IsNullOrWhiteSpace(setting)
                    || string.Equals(setting, DisplayAuto, StringComparison.OrdinalIgnoreCase);
        Win32.GetCursorPos(out var cursor);
        var current = MonitorLayout.BestFit(monitors, fallbackRect.Left, fallbackRect.Top,
            fallbackRect.Right - fallbackRect.Left, fallbackRect.Bottom - fallbackRect.Top);

        return MonitorLayout.Pick(monitors, auto ? null : setting, cursor.X, cursor.Y, current)
               ?? MonitorLayout.Primary(monitors)
               ?? monitors[0];
    }

    /// <summary>一块矩形所在显示器（重叠最多者优先，完全不相交时取最近）。</summary>
    public static MonitorInfo? MonitorOf(Windows.Graphics.RectInt32 rect)
        => MonitorLayout.BestFit(Enumerate(), rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>一块矩形所在显示器（Win32 矩形版本）。</summary>
    public static MonitorInfo? MonitorOf(Win32.RECT rect)
        => MonitorLayout.BestFit(Enumerate(), rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top);
}
