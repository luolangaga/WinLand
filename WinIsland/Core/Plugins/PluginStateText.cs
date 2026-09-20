namespace WinIsland.Core.Plugins;

public static class PluginStateText
{
    public static string Describe(PluginState state) => state switch
    {
        PluginState.Discovered => "已发现",
        PluginState.Loaded => "已加载",
        PluginState.Active => "运行中",
        PluginState.Disabled => "已禁用",
        PluginState.Faulted => "错误",
        PluginState.Unloading => "卸载中",
        _ => state.ToString(),
    };
}
