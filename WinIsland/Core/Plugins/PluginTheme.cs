using WinIsland.Core;
using WinIsland.Island;

namespace WinIsland.Core.Plugins;

/// <summary>
/// 交给插件看的岛体明暗主题（<see cref="IIslandTheme"/>）。
/// 每个插件一份实例：变更要逐个插件守卫、逐个编组，插件在自己的回调里抛异常不该影响宿主或别的插件；
/// 作用域一旦撤销（停用/卸载）就彻底静默，停用后的插件不该再收到任何回调。
/// </summary>
internal sealed class PluginTheme : IIslandTheme
{
    private readonly PluginInstance _owner;
    private readonly PluginScope _scope;

    public PluginTheme(PluginInstance owner, PluginScope scope)
    {
        _owner = owner;
        _scope = scope;
        IsLight = IslandStyle.ResolveIsLight(owner.Settings);
    }

    public bool IsLight { get; private set; }

    public event Action? Changed;

    /// <summary>重新解析岛体生效主题（UI 线程调用）：值没变或作用域已撤销时什么都不做。</summary>
    internal void Apply()
    {
        bool isLight = IslandStyle.ResolveIsLight(_owner.Settings);
        if (isLight == IsLight || _scope.IsRevoked) return;

        IsLight = isLight;
        if (Changed is not { } handlers) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            _owner.InvokeGuarded("主题变更", (Action)handler);
        }
    }
}
