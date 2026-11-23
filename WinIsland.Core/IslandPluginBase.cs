namespace WinIsland.Core;

public abstract class IslandPluginBase : IIslandPlugin
{
    private IPluginContext? _context;
    private IslandLiveContent? _shownContent;
    private bool _contentShown;
    // 多实例内容：实例 id → 最近一次登记的那份（用于去重与"当它还没显示时"的判定）
    private readonly Dictionary<string, IslandLiveContent> _instances = new(StringComparer.Ordinal);

    protected IPluginContext Context => _context ?? throw new InvalidOperationException("Plugin is not initialized.");
    protected PluginManifest Manifest => Context.Manifest;
    protected IPluginLogger Log => Context.Log;
    protected ISettingsStore Settings => Context.Settings;
    protected string PluginDirectory => Context.PluginDirectory;
    protected Version HostVersion => Context.HostVersion;

    /// <summary>岛体当前的明暗主题。XAML 视图里 <c>{ThemeResource ...}</c> 会自动跟着走，
    /// 代码里搭视图时才需要读它（写死的白色在浅色岛上就是白字压白底）。</summary>
    protected IIslandTheme Theme => Context.Theme;

    /// <summary>
    /// 插件间事件总线（宿主 2.6）。老宿主上没有这个成员，所以按能力取：
    /// <code>if (Context is IPluginBus bus) { ... }</code>
    /// 订阅会跟着作用域回收，插件停用即自动退订。
    /// </summary>
    protected IPluginBus? Bus => Context as IPluginBus;

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
            _instances.Clear();
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

    /// <summary>
    /// 多实例内容的登记/更新/撤掉（宿主 2.6）。同一个插件可以同时挂多条常驻内容
    /// （两个城市各一张天气卡、两块 GPU 各一张监控卡），每条各自参与优先级排序、各占一张队列卡片。
    ///
    /// <paramref name="instanceId"/> 只在插件内部唯一即可；<paramref name="content"/> 传 null 表示撤掉这一条。
    /// **不调用这个方法的老插件行为完全不变** —— 单实例路径仍然是 <see cref="SetContent(IslandLiveContent?)"/>。
    /// </summary>
    protected void SetContent(string instanceId, IslandLiveContent? content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        if (content == null)
        {
            if (_instances.Remove(instanceId)) Context.Island.RemoveContent(instanceId);
            return;
        }

        // 与单实例路径同样的去重：同一条内容反复刷新不该重复下发（否则每次都会重建队列卡片）
        if (_instances.TryGetValue(instanceId, out var previous) && ReferenceEquals(previous, content)) return;

        _instances[instanceId] = content;
        Context.Island.SetContent(instanceId, content);
    }

    /// <summary>更新一条已登记的多实例内容（没登记过时什么都不做，与 <see cref="UpdateContent(IslandLiveContent)"/> 同语义）。</summary>
    protected void UpdateContent(string instanceId, IslandLiveContent content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (!_instances.ContainsKey(instanceId)) return;
        SetContent(instanceId, content);
    }

    /// <summary>撤掉一条多实例内容（等价于 <c>SetContent(instanceId, null)</c>）。</summary>
    protected void RemoveContent(string instanceId) => SetContent(instanceId, null);

    protected void RunOnUI(Action action) => Context.RunOnUI(action);
}
