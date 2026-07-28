using WinIsland.Core;

namespace WinIsland.Modules.AiMonitor;

public sealed class AiMonitorModule : IIslandModule
{
    public string Id => "aimonitor";
    public string DisplayName => "AI 任务监控";

    private IDynamicIslandApi _api = null!;
    private AiMonitorViewModel _vm = null!;
    private IslandLiveContent _content = null!;
    private AiMonitorHttpListener? _httpListener;
    private bool _enabled;
    private bool _liveShown;

    public string StatusText { get; private set; } = "等待 AI 任务...";

    public async Task InitializeAsync(IDynamicIslandApi api)
    {
        _api = api;
        _enabled = api.Settings.Get("aimonitor.enabled", true);

        _vm = new AiMonitorViewModel();

        var islandView = new AiMonitorIslandView(_vm);

        _content = new IslandLiveContent
        {
            Priority = api.Settings.Get("aimonitor.priority", 80),
            OwnerLabel = "AI 监控",
            OwnerGlyph = "\uE965",
            OwnerAccent = Windows.UI.Color.FromArgb(255, 108, 203, 95),
            MorphView = islandView,
            CompactSize = new Windows.Foundation.Size(230, 40),
            ExpandedSize = new Windows.Foundation.Size(420, 150),
        };

        api.AddSettingsPage(new SettingsPageDescriptor(
            "aimonitor", DisplayName, "\uE965", () => new AiMonitorSettingsPage(api, this)));

        api.Settings.Changed += key =>
        {
            if (key == "aimonitor.enabled")
            {
                _enabled = _api.Settings.Get("aimonitor.enabled", true);
                if (!_enabled)
                {
                    SetLive(false);
                    _httpListener?.Dispose();
                    _httpListener = null;
                }
                else
                {
                    StartHttpListener();
                }
            }
            else if (key == "aimonitor.priority")
            {
                var newPriority = _api.Settings.Get("aimonitor.priority", 80);
                _content = new IslandLiveContent
                {
                    Priority = newPriority,
                    OwnerLabel = _content.OwnerLabel,
                    OwnerGlyph = _content.OwnerGlyph,
                    OwnerAccent = _content.OwnerAccent,
                    MorphView = _content.MorphView,
                    CompactContent = _content.CompactContent,
                    ExpandedContent = _content.ExpandedContent,
                    OnTap = _content.OnTap,
                    CompactSize = _content.CompactSize,
                    ExpandedSize = _content.ExpandedSize,
                };
                if (_liveShown) _api.SetLiveContent(Id, _content);
            }
        };

        if (_enabled)
            StartHttpListener();

        await Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        SetLive(false);
        _httpListener?.Dispose();
        _httpListener = null;
        return Task.CompletedTask;
    }

    private void StartHttpListener()
    {
        if (_httpListener != null) return;
        try
        {
            _httpListener = new AiMonitorHttpListener(_vm, _api, this);
            _httpListener.Start();
            StatusText = "监控中 · 端口 47321";
        }
        catch (Exception ex)
        {
            StatusText = $"HTTP 监听失败: {ex.Message}";
        }
    }

    internal void SetLive(bool show)
    {
        if (show == _liveShown) return;
        _liveShown = show;
        _api.SetLiveContent(Id, show ? _content : null);
    }

    internal bool IsEnabled => _enabled;
    internal bool IsRunning => _vm.IsRunning;
    internal string CurrentTask => _vm.TaskName;
    internal string CurrentDetail => _vm.Detail;
}
