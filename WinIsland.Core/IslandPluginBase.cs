namespace WinIsland.Core;

public abstract class IslandPluginBase : IIslandPlugin
{
    private IPluginContext? _context;
    private IslandLiveContent? _shownContent;
    private bool _contentShown;

    protected IPluginContext Context => _context ?? throw new InvalidOperationException("Plugin is not initialized.");
    protected PluginManifest Manifest => Context.Manifest;
    protected IPluginLogger Log => Context.Log;
    protected ISettingsStore Settings => Context.Settings;
    protected string PluginDirectory => Context.PluginDirectory;
    protected Version HostVersion => Context.HostVersion;

    public async Task InitializeAsync(IPluginContext context)
    {
        _context = context;
        await OnInitializeAsync();
    }

    public async Task ShutdownAsync()
    {
        try
        {
            await OnShutdownAsync();
        }
        finally
        {
            _shownContent = null;
            _contentShown = false;
        }
    }

    protected virtual Task OnInitializeAsync() => Task.CompletedTask;

    protected virtual Task OnShutdownAsync() => Task.CompletedTask;

    protected void SetContent(IslandLiveContent? content)
    {
        if (content == null)
        {
            if (!_contentShown) return;
            _contentShown = false;
            _shownContent = null;
            Context.Island.SetContent(null);
            return;
        }

        if (_contentShown && ReferenceEquals(_shownContent, content)) return;

        _contentShown = true;
        _shownContent = content;
        Context.Island.SetContent(content);
    }

    protected void UpdateContent(IslandLiveContent content)
    {
        if (!_contentShown) return;
        _shownContent = content;
        Context.Island.SetContent(content);
    }

    protected void RunOnUI(Action action) => Context.RunOnUI(action);
}
