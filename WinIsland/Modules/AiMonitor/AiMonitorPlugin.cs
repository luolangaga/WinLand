using Microsoft.UI.Dispatching;
using WinIsland.Core;
using WinIsland.Core.Plugins;

namespace WinIsland.Modules.AiMonitor;

public sealed class AiMonitorPlugin : IslandPluginBase
{
    private const int DefaultPriority = 80;

    private AiMonitorViewModel _vm = null!;
    private IslandLiveContent _content = null!;
    private AiMonitorHttpListener? _httpListener;
    private DispatcherQueueTimer? _hideTimer;
    private bool _enabled;

    public string StatusText { get; private set; } = "等待 AI 任务...";

    protected override Task OnInitializeAsync()
    {
        _enabled = Settings.Get("enabled", true);
        _vm = new AiMonitorViewModel();
        _content = BuildContent(Settings.Get("priority", DefaultPriority));

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            "aimonitor", Manifest.Name, "\uE965", () => new AiMonitorSettingsPage(Context, this), 30));

        _hideTimer = Context.Dispatcher.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => SetLive(false);
        Context.Register(new ActionDisposable(() => _hideTimer?.Stop()));

        Context.OnSettingsChanged("enabled", () =>
        {
            _enabled = Settings.Get("enabled", true);
            if (!_enabled)
            {
                SetLive(false);
                StopHttpListener();
            }
            else
            {
                StartHttpListener();
            }
        });

        Context.OnSettingsChanged("priority", () =>
        {
            _content = BuildContent(Settings.Get("priority", DefaultPriority));
            UpdateContent(_content);
        });

        if (_enabled)
        {
            StartHttpListener();
        }

        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        SetLive(false);
        StopHttpListener();
        _hideTimer = null;
        return Task.CompletedTask;
    }

    internal bool IsEnabled => _enabled;

    internal bool IsRunning => _vm.IsRunning;

    internal string CurrentTask => _vm.TaskName;

    internal string CurrentDetail => _vm.Detail;

    internal void SetLive(bool show) => SetContent(show ? _content : null);

    internal void ScheduleHide() => RunOnUI(() =>
    {
        if (_hideTimer == null) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    });

    private IslandLiveContent BuildContent(int priority) => new()
    {
        Priority = priority,
        OwnerLabel = "AI 监控",
        OwnerGlyph = "\uE965",
        OwnerAccent = Windows.UI.Color.FromArgb(255, 108, 203, 95),
        MorphView = new AiMonitorIslandView(_vm),
        CompactSize = new Windows.Foundation.Size(230, 40),
        ExpandedSize = new Windows.Foundation.Size(420, 150),
    };

    private void StartHttpListener()
    {
        if (_httpListener != null) return;
        try
        {
            _httpListener = new AiMonitorHttpListener(_vm, Context, this);
            _httpListener.Start();
            StatusText = "监控中 · 端口 47321";
        }
        catch (Exception ex)
        {
            StatusText = $"HTTP 监听失败: {ex.Message}";
            Log.Error("启动 HTTP 监听失败", ex);
        }
    }

    private void StopHttpListener()
    {
        _httpListener?.Dispose();
        _httpListener = null;
    }
}
