using System.Net.NetworkInformation;

namespace HardwareMonitor;

public sealed record HardwareSnapshot(
    string CpuName,
    double CpuLoad,
    string GpuName,
    double GpuLoad,
    string NetName,
    double NetDown,
    double NetUp,
    string FpsProcess,
    double Fps,
    string FpsStatus,
    bool FpsRequiresAdmin);

public sealed class HardwareSampler : IDisposable
{
    private const int ProcessorCounter = 0;
    private const int GpuCounter = 1;

    private readonly Action<string> _log;
    private readonly FpsMeter _fps;
    private readonly PdhQuery _pdh = new(
        @"\Processor Information(*)\% Processor Time",
        @"\GPU Engine(*)\Utilization Percentage");
    private readonly List<NetworkInterface> _adapters;
    private readonly List<string> _cpuDevices = new();
    private readonly List<string> _gpuDevices = new();
    private readonly List<long> _gpuLuids = new();

    private int _cpuIndex;
    private int _gpuIndex;
    private int _networkIndex;
    private string? _lastNetworkId;
    private long _lastBytesIn;
    private long _lastBytesOut;
    private DateTime _lastNetworkSample;

    public HardwareSampler(Action<string> log)
    {
        _log = log;
        _fps = new FpsMeter(log);
        _adapters = SystemMetrics.ActiveAdapters();
        _gpuDevices.AddRange(SystemMetrics.GpuNames);
        _cpuDevices.Add("全部核心");
    }

    public IReadOnlyList<string> CpuDevices => _cpuDevices;

    public IReadOnlyList<string> GpuDevices => _gpuDevices;

    public IReadOnlyList<string> NetworkDevices => _adapters.Select(a => a.Name).ToArray();

    public string FpsStatus => _fps.Status;

    public bool FpsRequiresAdmin => _fps.RequiresAdmin;

    public void Open(int cpuIndex, int gpuIndex, int networkIndex)
    {
        _cpuIndex = cpuIndex;
        _gpuIndex = gpuIndex;
        _networkIndex = networkIndex;

        _pdh.Collect();
        Thread.Sleep(400);
        _pdh.Collect();

        RefreshGpuDevices();
        _fps.Start();

        _log($"数据源：CPU {SystemMetrics.CpuName}（{SystemMetrics.ProcessorGroupCount} 组）· "
             + $"GPU {(_gpuDevices.Count == 0 ? "未检测到" : string.Join(" / ", _gpuDevices))} · "
             + $"网卡 {_adapters.Count} 个 · 帧率：{_fps.Status}");
    }

    public void Select(int cpuIndex, int gpuIndex, int networkIndex)
    {
        _cpuIndex = cpuIndex;
        _gpuIndex = gpuIndex;
        if (networkIndex != _networkIndex)
        {
            _networkIndex = networkIndex;
            _lastNetworkId = null;
            _lastNetworkSample = default;
        }
    }

    public HardwareSnapshot Sample()
    {
        _pdh.Collect();

        var cpu = ReadCpu();
        var (gpuName, gpuLoad) = ReadGpu();
        var (down, up, netName) = SampleNetwork();
        var fps = _fps.ReadFps(out var fpsSource);

        return new HardwareSnapshot(
            SystemMetrics.CpuName,
            cpu,
            gpuName,
            gpuLoad,
            netName,
            down,
            up,
            fpsSource,
            fps,
            _fps.Status,
            _fps.RequiresAdmin);
    }

    public void Dispose()
    {
        _fps.Dispose();
        _pdh.Dispose();
    }

    private void RefreshGpuDevices()
    {
        _gpuLuids.Clear();
        foreach (var item in _pdh.Read(GpuCounter))
        {
            var luid = ParseLuid(item.Instance);
            if (luid != 0 && !_gpuLuids.Contains(luid))
            {
                _gpuLuids.Add(luid);
            }
        }

        if (_gpuDevices.Count == 0 && _gpuLuids.Count > 0)
        {
            for (var i = 0; i < _gpuLuids.Count; i++)
            {
                _gpuDevices.Add($"GPU {i + 1}");
            }
        }
    }

    private double ReadCpu()
    {
        try
        {
            var items = _pdh.Read(ProcessorCounter);
            if (items.Count == 0) return -1;

            var groups = items
                .Select(i => (Group: ParseGroup(i.Instance), i.Value))
                .Where(i => i.Group >= 0)
                .ToList();

            if (groups.Count == 0) return Math.Round(items.Average(i => i.Value), 0);

            var distinctGroups = groups.Select(g => g.Group).Distinct().OrderBy(g => g).ToList();
            if (distinctGroups.Count > 1)
            {
                for (var i = 0; i < distinctGroups.Count; i++)
                {
                    var label = $"CPU 组 {distinctGroups[i]}";
                    if (!_cpuDevices.Contains(label)) _cpuDevices.Add(label);
                }
            }

            if (_cpuIndex <= 0 || distinctGroups.Count <= 1)
            {
                return Math.Round(groups.Average(g => g.Value), 0);
            }

            var wanted = distinctGroups[Math.Min(_cpuIndex - 1, distinctGroups.Count - 1)];
            var values = groups.Where(g => g.Group == wanted).Select(g => g.Value).ToList();
            return values.Count == 0 ? -1 : Math.Round(values.Average(), 0);
        }
        catch (Exception ex)
        {
            _log($"读取 CPU 占用失败：{ex.Message}");
            return -1;
        }
    }

    private (string Name, double Load) ReadGpu()
    {
        try
        {
            var items = _pdh.Read(GpuCounter);
            if (items.Count == 0) return (GpuLabel(), -1);

            var target = _gpuIndex >= 0 && _gpuIndex < _gpuLuids.Count ? _gpuLuids[_gpuIndex] : (long?)null;
            var matched = target == null
                ? items
                : items.Where(i => ParseLuid(i.Instance) == target.Value).ToList();

            if (matched.Count == 0) matched = items;

            var busiest = matched.MaxBy(i => i.Value);
            return (GpuLabel(), Math.Round(Math.Clamp(busiest.Value, 0, 100), 0));
        }
        catch (Exception ex)
        {
            _log($"读取 GPU 占用失败：{ex.Message}");
            return ("读取失败", -1);
        }
    }

    private string GpuLabel()
    {
        if (_gpuDevices.Count == 0) return "未检测到 GPU";
        return _gpuDevices[Math.Clamp(_gpuIndex, 0, _gpuDevices.Count - 1)];
    }

    private (double Down, double Up, string Name) SampleNetwork()
    {
        try
        {
            if (_adapters.Count == 0) return (0, 0, "无网络设备");

            var adapter = _adapters[Math.Clamp(_networkIndex, 0, _adapters.Count - 1)];
            var stats = adapter.GetIPStatistics();
            var now = DateTime.UtcNow;

            if (_lastNetworkId != adapter.Id || _lastNetworkSample == default)
            {
                _lastNetworkId = adapter.Id;
                _lastNetworkSample = now;
                _lastBytesIn = stats.BytesReceived;
                _lastBytesOut = stats.BytesSent;
                return (0, 0, adapter.Name);
            }

            var elapsed = (now - _lastNetworkSample).TotalSeconds;
            if (elapsed < 0.2) return (0, 0, adapter.Name);

            var down = Math.Max(0, (stats.BytesReceived - _lastBytesIn) / elapsed);
            var up = Math.Max(0, (stats.BytesSent - _lastBytesOut) / elapsed);

            _lastNetworkSample = now;
            _lastBytesIn = stats.BytesReceived;
            _lastBytesOut = stats.BytesSent;

            return (down, up, adapter.Name);
        }
        catch (Exception ex)
        {
            _log($"读取网络统计失败：{ex.Message}");
            return (0, 0, "读取失败");
        }
    }

    private static int ParseGroup(string instance)
    {
        var comma = instance.IndexOf(',');
        var head = comma >= 0 ? instance[..comma] : instance;
        return int.TryParse(head, out var group) ? group : -1;
    }

    private static long ParseLuid(string instance)
    {
        var parts = instance.Split('_');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] != "luid") continue;

            long high = 0;
            long low = 0;
            if (parts[i + 1].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(parts[i + 1][2..], System.Globalization.NumberStyles.HexNumber, null, out high);
            }

            if (i + 2 < parts.Length && parts[i + 2].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(parts[i + 2][2..], System.Globalization.NumberStyles.HexNumber, null, out low);
            }

            return (high << 32) | (low & 0xFFFFFFFFL);
        }

        return 0;
    }
}
