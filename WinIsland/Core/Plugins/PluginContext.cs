using WinIsland.Core;

namespace WinIsland.Core.Plugins;

internal sealed class PluginContext : IPluginContext
{
    private readonly PluginInstance _owner;
    private readonly PluginScope _scope;
    private readonly PluginManifest _manifest;
    private readonly string _directory;
    private readonly ScopedSettings _settings;
    private readonly ScopedIslandSurface _island;
    private readonly PluginTheme _theme;

    public PluginContext(PluginInstance owner, PluginScope scope, PluginManifest manifest, string directory, PluginTheme theme)
    {
        _owner = owner;
        _scope = scope;
        _manifest = manifest;
        _directory = directory;
        _theme = theme;
        _settings = new ScopedSettings(owner.Settings, manifest.Id, scope, owner.RunOnUI);
        _island = new ScopedIslandSurface(owner, owner.Island, scope);
    }

    public PluginManifest Manifest => _manifest;

    public string PluginDirectory => _directory;

    public Version HostVersion => IslandSdk.HostVersion;

    public Microsoft.UI.Dispatching.DispatcherQueue Dispatcher => _owner.Dispatcher;

    public IPluginLogger Log => _owner.Log;

    public ISettingsStore Settings => _settings;

    public IIslandSurface Island => _island;

    public IIslandTheme Theme => _theme;

    public IDisposable Register(IDisposable disposable) => _scope.Register(disposable);

    public IDisposable OnSettingsChanged(string key, Action handler)
    {
        void OnChanged(string changedKey)
        {
            if (!string.Equals(changedKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _owner.InvokeGuarded($"设置变更（{key}）", handler);
        }

        _settings.Changed += OnChanged;
        return _scope.Register(new ActionDisposable(() => _settings.Changed -= OnChanged));
    }

    public IDisposable CreateTimer(TimeSpan interval, bool repeat, Action tick)
    {
        if (!_owner.Dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException("CreateTimer 必须在 UI 线程调用（例如在 InitializeAsync 内或通过 Context.RunOnUI 编组后调用）。");
        }

        var timer = _owner.Dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeat;
        Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object> handler
            = (_, _) => _owner.InvokeGuarded("定时器", tick);
        timer.Tick += handler;
        timer.Start();
        return _scope.Register(new ActionDisposable(() =>
        {
            timer.Stop();
            timer.Tick -= handler;
        }));
    }

    public void RunOnUI(Action action) => _owner.RunOnUI(action);

    public Task RunOnUIAsync(Func<Task> action) => _owner.RunOnUIAsync(action);
}
