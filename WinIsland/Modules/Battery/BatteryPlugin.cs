using Windows.Devices.Power;
using Windows.System.Power;
using WinIsland.Core;
using WinBattery = Windows.Devices.Power.Battery;

namespace WinIsland.Modules.Battery;

public sealed class BatteryPlugin : IslandPluginBase
{
    private BatteryViewModel _vm = null!;
    private BatterySpotlightView? _spotlightView;
    private IslandLiveContent _content = null!;
    private bool _enabled;
    private bool _wasPlugged;
    private bool _spotlightOpen;
    private WinBattery? _battery;

    public string StatusText { get; private set; } = "等待电池状态...";

    protected override Task OnInitializeAsync()
    {
        _enabled = Settings.Get("enabled", true);
        _vm = new BatteryViewModel();
        _content = BuildContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            "battery", Manifest.Name, "\uE857", () => new BatterySettingsPage(Context, this), 20));

        Context.OnSettingsChanged("enabled", () =>
        {
            _enabled = Settings.Get("enabled", true);
            RefreshState();
        });

        PowerManager.BatteryStatusChanged += OnPowerManagerChanged;
        PowerManager.RemainingChargePercentChanged += OnPowerManagerChanged;
        PowerManager.PowerSupplyStatusChanged += OnPowerManagerChanged;

        try
        {
            var aggregate = WinBattery.AggregateBattery;
            aggregate.ReportUpdated += OnBatteryReportUpdated;
            _battery = aggregate;
        }
        catch (Exception ex)
        {
            Log.Warn($"无法订阅电池报告：{ex.Message}");
        }

        RefreshState();
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        PowerManager.BatteryStatusChanged -= OnPowerManagerChanged;
        PowerManager.RemainingChargePercentChanged -= OnPowerManagerChanged;
        PowerManager.PowerSupplyStatusChanged -= OnPowerManagerChanged;

        if (_battery != null)
        {
            try
            {
                _battery.ReportUpdated -= OnBatteryReportUpdated;
            }
            catch
            {
            }

            _battery = null;
        }

        _spotlightOpen = false;
        SetContent(null);
        return Task.CompletedTask;
    }

    internal bool IsEnabled => _enabled;

    private IslandLiveContent BuildContent() => new()
    {
        Priority = 50,
        OwnerLabel = "充电监控",
        OwnerGlyph = "\uE857",
        OwnerAccent = Windows.UI.Color.FromArgb(255, 108, 203, 95),
        MorphView = new BatteryIslandView(_vm),
        CompactSize = new Windows.Foundation.Size(230, 40),
        ExpandedSize = new Windows.Foundation.Size(420, 150),
        OnTap = OpenSpotlight,
    };

    /// <summary>点击岛体 = 展开超级大卡片（大进度环 + 容量/健康/循环等高级信息）。</summary>
    private void OpenSpotlight()
    {
        _spotlightView ??= new BatterySpotlightView(_vm, Theme);
        _spotlightOpen = true;
        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlightView,
            Size = new Windows.Foundation.Size(700, 470),
            OnClosed = () => _spotlightOpen = false,
        });
    }

    private void CloseSpotlight()
    {
        if (!_spotlightOpen) return;
        _spotlightOpen = false;
        Context.Island.CloseSpotlight();
    }

    private void SetLive(bool show) => SetContent(show ? _content : null);

    private void OnPowerManagerChanged(object? sender, object e) => RunOnUI(RefreshState);

    private void OnBatteryReportUpdated(WinBattery sender, object args) => RunOnUI(UpdateChargePower);

    private void UpdateChargePower()
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
        catch
        {
        }

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

            UpdateChargePower();

            if (isPlugged && !_wasPlugged)
            {
                Context.Island.ShowMessage(new IslandMessage
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
                Context.Island.ShowMessage(new IslandMessage
                {
                    Title = "已断开充电器",
                    Text = $"当前电量 {percent}%",
                    Glyph = "\uE854",
                    Duration = TimeSpan.FromSeconds(3),
                });
            }

            _wasPlugged = isPlugged;

            // 拔掉充电器后大卡片没有意义了，主动收起（岛体本身也会跟着隐藏）
            if (!isPlugged) CloseSpotlight();

            SetLive(_enabled && isPlugged);
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
}
