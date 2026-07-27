using Windows.System.Power;
using WinIsland.Core;
using WinBattery = Windows.Devices.Power.Battery;

namespace WinIsland.Modules.Battery;

public sealed class BatteryModule : IIslandModule
{
    public string Id => "battery";
    public string DisplayName => "充电监控";

    private IDynamicIslandApi _api = null!;
    private BatteryViewModel _vm = null!;
    private IslandLiveContent _content = null!;
    private bool _enabled;
    private bool _liveShown;
    private bool _wasPlugged;
    private WinBattery? _battery;

    public string StatusText { get; private set; } = "等待电池状态...";

    public async Task InitializeAsync(IDynamicIslandApi api)
    {
        _api = api;
        _enabled = api.Settings.Get("battery.enabled", true);

        _vm = new BatteryViewModel();

        var islandView = new BatteryIslandView(_vm);

        _content = new IslandLiveContent
        {
            Priority = 50,
            OwnerLabel = "充电监控",
            OwnerGlyph = "\uE857",
            OwnerAccent = Windows.UI.Color.FromArgb(255, 108, 203, 95),
            MorphView = islandView,
            CompactSize = new Windows.Foundation.Size(230, 40),
            ExpandedSize = new Windows.Foundation.Size(420, 150),
        };

        api.AddSettingsPage(new SettingsPageDescriptor(
            "battery", DisplayName, "\uE857", () => new BatterySettingsPage(api, this)));

        api.Settings.Changed += key =>
        {
            if (key == "battery.enabled")
            {
                _enabled = _api.Settings.Get("battery.enabled", true);
                RunOnUI(RefreshState);
            }
        };

        PowerManager.BatteryStatusChanged += (_, _) => RunOnUI(RefreshState);
        PowerManager.RemainingChargePercentChanged += (_, _) => RunOnUI(RefreshState);
        PowerManager.PowerSupplyStatusChanged += (_, _) => RunOnUI(RefreshState);

        try
        {
            var aggBattery = WinBattery.AggregateBattery;
            aggBattery.ReportUpdated += (_, _) => RunOnUI(OnBatteryReportUpdated);
            _battery = aggBattery;
        }
        catch { }

        RefreshState();

        await Task.CompletedTask;
    }

    public Task ShutdownAsync()
    {
        SetLive(false);
        return Task.CompletedTask;
    }

    private void OnBatteryReportUpdated()
    {
        if (_battery == null) return;

        try
        {
            var report = _battery.GetReport();
            var chargeRate = report.ChargeRateInMilliwatts;

            if (chargeRate.HasValue && chargeRate.Value > 0)
            {
                _vm.ChargePower = chargeRate.Value / 1000.0;
            }
        }
        catch { }

        UpdateStatusText();
    }

    private void RefreshState()
    {
        try
        {
            var percent = PowerManager.RemainingChargePercent;
            var status = PowerManager.BatteryStatus;
            var powerSource = PowerManager.PowerSupplyStatus;
            var isOnAc = powerSource == PowerSupplyStatus.Adequate;
            var isCharging = status == BatteryStatus.Charging;
            var isPlugged = isCharging || (status == BatteryStatus.Idle && isOnAc);

            _vm.Percent = percent;
            _vm.IsCharging = isPlugged;

            OnBatteryReportUpdated();

            if (isPlugged && !_wasPlugged)
            {
                _api.SendMessage(new IslandMessage
                {
                    Title = "已连接充电器",
                    Text = $"当前电量 {percent}%",
                    Glyph = "\uE859",
                    AccentColor = Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F),
                    Duration = TimeSpan.FromSeconds(3),
                });
            }
            else if (!isPlugged && _wasPlugged)
            {
                _api.SendMessage(new IslandMessage
                {
                    Title = "已断开充电器",
                    Text = $"当前电量 {percent}%",
                    Glyph = "\uE854",
                    Duration = TimeSpan.FromSeconds(3),
                });
            }

            _wasPlugged = isPlugged;

            if (_enabled && isPlugged)
                SetLive(true);
            else
                SetLive(false);
        }
        catch
        {
            StatusText = "无法读取电池状态";
        }
    }

    private void UpdateStatusText()
    {
        var percent = _vm.Percent;
        var isPlugged = _vm.IsCharging;
        var power = _vm.ChargePower;
        var status = PowerManager.BatteryStatus;

        if (isPlugged)
        {
            if (status == BatteryStatus.Charging)
            {
                _vm.StatusText = power > 0 ? $"充电中 · {power:F1} W" : "充电中";
                StatusText = power > 0 ? $"充电中 {percent}% · {power:F1} W" : $"充电中 {percent}%";
            }
            else
            {
                _vm.StatusText = percent >= 100 ? "已充满" : $"接通电源 · {percent}%";
                StatusText = percent >= 100 ? $"已充满 {percent}%" : $"接通电源 · {percent}%";
            }
        }
        else
        {
            _vm.StatusText = $"电池 {percent}%";
            StatusText = $"未充电 · 电量 {percent}%";
        }
    }

    private void SetLive(bool show)
    {
        if (show == _liveShown) return;
        _liveShown = show;
        _api.SetLiveContent(Id, show ? _content : null);
    }

    private void RunOnUI(Action action)
    {
        if (_api.Dispatcher.HasThreadAccess)
            action();
        else
            _api.Dispatcher.TryEnqueue(() => action());
    }
}
