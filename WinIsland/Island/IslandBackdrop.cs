using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinIsland.Core;
using WinRT;

namespace WinIsland.Island;

/// <summary>
/// 窗口级系统材质（Desktop Acrylic / Mica）。
///
/// 不用内置的 MicaBackdrop / DesktopAcrylicBackdrop 的原因：它们把 SystemBackdropConfiguration
/// 的 IsInputActive 绑到窗口激活状态，而灵动岛窗口是 WS_EX_NOACTIVATE、永远不激活 —— 材质会被
/// 系统按「非激活」降级成纯色（深色主题下就是 #2C2C2C 那种灰）。这里直接使用控制器，自己提供配置
/// 并把 IsInputActive 固定为 true，才能拿到真正的材质外观。
///
/// 明暗主题同样由这份配置决定：材质是系统画的，浅色主题必须显式告诉它，否则浅色壁纸上会糊一层
/// 深色磨砂（那正是「没适配浅色/深色」的样子）。主题变化可以就地改配置，不必重建控制器。
/// </summary>
internal sealed class IslandBackdrop : IDisposable
{
    private readonly Window _window;
    private SystemBackdropConfiguration? _configuration;
    private DesktopAcrylicController? _acrylic;
    private MicaController? _mica;

    public IslandBackdrop(Window window) => _window = window;

    public static bool IsSupported(IslandMaterialKind material)
        => material == IslandMaterialKind.Mica
            ? MicaController.IsSupported()
            : DesktopAcrylicController.IsSupported();

    /// <summary>
    /// 挂上指定材质与明暗主题。系统不支持该材质时返回 false 且保持现状；
    /// 材质没变时只更新主题（主题切换不该重建控制器，那会闪一下）。
    /// </summary>
    public bool TryApply(IslandMaterialKind material, bool light)
    {
        if (!IsSupported(material)) return false;

        CompositorProvider.EnsureDispatcherQueue();
        _configuration ??= new SystemBackdropConfiguration { IsInputActive = true };
        _configuration.Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;

        var target = _window.As<ICompositionSupportsSystemBackdrop>();
        if (material == IslandMaterialKind.Mica)
        {
            if (_mica != null) return true;

            var controller = new MicaController { Kind = MicaKind.Base };
            controller.AddSystemBackdropTarget(target);
            controller.SetSystemBackdropConfiguration(_configuration);
            Replace(acrylic: null, mica: controller);
        }
        else
        {
            if (_acrylic != null) return true;

            var controller = new DesktopAcrylicController();
            controller.AddSystemBackdropTarget(target);
            controller.SetSystemBackdropConfiguration(_configuration);
            Replace(acrylic: controller, mica: null);
        }

        return true;
    }

    public void Clear() => Replace(acrylic: null, mica: null);

    public void Dispose()
    {
        Clear();
        _configuration = null;
    }

    /// <summary>新材质先挂上、旧材质后释放，切换过程中不会出现「两个都不在」的空档。</summary>
    private void Replace(DesktopAcrylicController? acrylic, MicaController? mica)
    {
        var oldAcrylic = _acrylic;
        var oldMica = _mica;
        _acrylic = acrylic;
        _mica = mica;
        oldAcrylic?.Dispose();
        oldMica?.Dispose();
    }
}
