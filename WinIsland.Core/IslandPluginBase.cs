namespace WinIsland.Core;

public abstract class IslandPluginBase : IIslandModule, IPluginManifest
{
    private IDynamicIslandApi? _api;
    private IPluginContext? _ctx;

    protected IDynamicIslandApi Api => _api ?? throw new InvalidOperationException("Plugin not initialized.");
    protected IPluginContext Context => _ctx ?? throw new InvalidOperationException("Plugin not initialized.");
    protected IPluginLogger Log => Context.Logger;
    protected ISettingsStore Settings => Api.Settings;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual string? Description => null;
    public virtual string? Author => null;
    public virtual string? Version => null;
    public virtual string? IconGlyph => null;

    public virtual Task InitializeAsync(IDynamicIslandApi api)
    {
        _api = api;
        return Task.CompletedTask;
    }

    public virtual Task ShutdownAsync() => Task.CompletedTask;

    internal void SetContext(IPluginContext context) => _ctx = context;
}
