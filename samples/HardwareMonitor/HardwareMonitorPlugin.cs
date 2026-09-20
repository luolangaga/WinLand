using WinIsland.Core;

namespace HardwareMonitor;

public sealed class HardwareMonitorPlugin : IslandPluginBase
{
    private const int DefaultPriority = 95;
    private const int SampleIntervalMs = 1000;

    private HardwareSampler _sampler = null!;
    private HardwareMonitorView _view = null!;
    private IslandLiveContent _content = null!;
    private bool _enabled = true;
    private CancellationTokenSource? _cancellation;
    private CompactOptions _compactOptions = new(true, false, true, false);

    protected override Task OnInitializeAsync()
    {
        _sampler = new HardwareSampler(message => Log.Info(message));
        _sampler.Open(
            Settings.Get("device.cpu", 0),
            Settings.Get("device.gpu", 0),
            Settings.Get("device.net", 0));

        _view = new HardwareMonitorView();
        _compactOptions = ReadCompactOptions();
        _content = BuildContent();
        _enabled = Settings.Get("enabled", true);

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            "hardware", Manifest.Name, Manifest.IconGlyph ?? "\uE950", () => new HardwareSettingsPage(Context, this), 50));

        Context.Register(new ActionDisposable(StopSampling));
        Context.OnSettingsChanged("enabled", ApplyEnabled);
        Context.OnSettingsChanged("device.cpu", ApplyDeviceSelection);
        Context.OnSettingsChanged("device.gpu", ApplyDeviceSelection);
        Context.OnSettingsChanged("device.net", ApplyDeviceSelection);
        Context.OnSettingsChanged("compact.cpu", ApplyCompactOptions);
        Context.OnSettingsChanged("compact.gpu", ApplyCompactOptions);
        Context.OnSettingsChanged("compact.net", ApplyCompactOptions);
        Context.OnSettingsChanged("compact.fps", ApplyCompactOptions);

        StartSampling();
        SetContent(_enabled ? _content : null);
        Log.Info($"硬件监控已启动（采样 {SampleIntervalMs}ms，帧率数据源：{_sampler.FpsStatus}）");
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        StopSampling();
        _sampler.Dispose();
        SetContent(null);
        return Task.CompletedTask;
    }

    public CompactOptions CurrentCompactOptions => _compactOptions;

    public string DisplayName => Manifest.Name;

    public IReadOnlyList<string> CpuDevices => _sampler.CpuDevices;

    public IReadOnlyList<string> GpuDevices => _sampler.GpuDevices;

    public IReadOnlyList<string> NetworkDevices => _sampler.NetworkDevices;

    public string FpsStatus => _sampler.FpsStatus;

    public bool FpsRequiresAdmin => _sampler.FpsRequiresAdmin;

    public int GetDeviceIndex(string kind) => Settings.Get($"device.{kind}", 0);

    private IslandLiveContent BuildContent() => new()
    {
        Priority = Settings.Get("priority", DefaultPriority),
        OwnerLabel = Manifest.Name,
        OwnerGlyph = Manifest.IconGlyph,
        OwnerAccent = Windows.UI.Color.FromArgb(255, 76, 194, 255),
        MorphView = _view,
        CompactSize = new Windows.Foundation.Size(250, 40),
        ExpandedSize = new Windows.Foundation.Size(420, 152),
    };

    private CompactOptions ReadCompactOptions() => new(
        Settings.Get("compact.cpu", true),
        Settings.Get("compact.gpu", false),
        Settings.Get("compact.net", true),
        Settings.Get("compact.fps", false));

    private void ApplyCompactOptions() => _compactOptions = ReadCompactOptions();

    /// <summary>是否在灵动岛上展示（关闭后插件照常运行，只是不占用小岛）。</summary>
    private void ApplyEnabled()
    {
        _enabled = Settings.Get("enabled", true);
        SetContent(_enabled ? _content : null);
    }

    private void ApplyDeviceSelection()
        => _sampler.Select(
            Settings.Get("device.cpu", 0),
            Settings.Get("device.gpu", 0),
            Settings.Get("device.net", 0));

    private void StartSampling()
    {
        if (_cancellation != null) return;

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = _sampler.Sample();
                    var options = _compactOptions;
                    Context.RunOnUI(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            _view.Apply(snapshot, options);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("硬件采样失败", ex);
                }

                try
                {
                    await Task.Delay(SampleIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private void StopSampling()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        if (cancellation == null) return;
        try { cancellation.Cancel(); } catch { }
        cancellation.Dispose();
    }
}
