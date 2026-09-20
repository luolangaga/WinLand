namespace WinIsland.Core;

public interface IIslandPlugin
{
    Task InitializeAsync(IPluginContext context);
    Task ShutdownAsync();
}
