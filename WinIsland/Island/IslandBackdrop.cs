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

    /// <summary>挂上指定材质（会替换已挂的材质）。系统不支持时返回 false 且保持现状。</summary>
    public bool TryApply(IslandMaterialKind material)
    {
        if (!IsSupported(material)) return false;

        CompositorProvider.EnsureDispatcherQueue();
        _configuration ??= new SystemBackdropConfiguration
        {
            Theme = SystemBackdropTheme.Dark,
            IsInputActive = true,
        };

        var target = _window.As<ICompositionSupportsSystemBackdrop>();
        if (material == IslandMaterialKind.Mica)
        {
            var controller = new MicaController { Kind = MicaKind.Base };
            controller.AddSystemBackdropTarget(target);
            controller.SetSystemBackdropConfiguration(_configuration);
            Replace(acrylic: null, mica: controller);
        }
        else
        {
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
