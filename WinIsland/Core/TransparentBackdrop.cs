using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace WinIsland.Core;

/// <summary>
/// 完全透明的窗口背景（参考 WinUIEx TransparentTintBackdrop 的做法）：
/// 1. 把透明色 CompositionColorBrush 设为窗口的 SystemBackdrop；
/// 2. 配合 Win32.EnableWindowAlpha 的 DwmEnableBlurBehindWindow(空区域) 让 DWM 按 alpha 合成。
/// </summary>
public sealed partial class TransparentBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
{
    private Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _brush = CompositorProvider.Compositor.CreateColorBrush(
            Windows.UI.Color.FromArgb(0, 0, 0, 0));
        connectedTarget.SystemBackdrop = _brush;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // 目标断开后可能收到 null，跳过默认配置刷新
        if (target != null) base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
    }

    protected override void OnTargetDisconnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}

/// <summary>
/// 提供系统合成器（Windows.UI.Composition.Compositor）。
/// SystemBackdrop 属性要求 Windows.UI.Composition.CompositionBrush，
/// 需要一个带 Windows.System.DispatcherQueue 的线程来创建独立 Compositor。
/// </summary>
internal static partial class CompositorProvider
{
    private static Windows.UI.Composition.Compositor? _compositor;
    private static object? _controller; // 防止 DispatcherQueueController 被回收

    public static Windows.UI.Composition.Compositor Compositor
        => _compositor ??= CreateCompositor();

    /// <summary>
    /// 确保当前线程有一个 Windows.System.DispatcherQueue。
    /// SystemBackdrop 与 Mica/Acrylic 控制器都要求它存在（当前线程通常没有）。
    /// </summary>
    public static void EnsureDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;

        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,     // DQTYPE_THREAD_CURRENT
            apartmentType = 2,  // DQTAT_COM_STA
        };
        CreateDispatcherQueueController(options, out var controller);
        _controller = controller; // 防止 DispatcherQueueController 被回收
    }

    private static Windows.UI.Composition.Compositor CreateCompositor()
    {
        EnsureDispatcherQueue();
        return new Windows.UI.Composition.Compositor();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    // COM 接口编排需要经典 DllImport（LibraryImport 不支持 IUnknown 编排）
    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] out object dispatcherQueueController);
}
