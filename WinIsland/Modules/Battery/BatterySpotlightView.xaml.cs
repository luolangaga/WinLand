using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinIsland.Core;
using WinIsland.Island;

namespace WinIsland.Modules.Battery;

/// <summary>
/// 电池的「超级展开」聚光卡：左侧大进度环，右侧高级信息（充放电功率、容量、健康度、
/// 循环次数、电压、预计时间）。数据每秒重新采样一次，进度环用原生 ProgressRing（确定模式），
/// 数值过渡与弧长都由控件自己负责；
/// 生命周期跟着 Loaded/Unloaded 走（宿主收起卡片时内容被卸下 → 停表）。
/// </summary>
public sealed partial class BatterySpotlightView : UserControl
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private Windows.UI.Color ChargingColor => _theme.IsLight
        ? Windows.UI.Color.FromArgb(255, 0x23, 0x78, 0x32) : Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F);
    private Windows.UI.Color WarningColor => _theme.IsLight
        ? Windows.UI.Color.FromArgb(255, 0x9C, 0x5D, 0) : Windows.UI.Color.FromArgb(255, 0xF5, 0xA6, 0x23);
    private Windows.UI.Color DangerColor => _theme.IsLight
        ? Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C) : Windows.UI.Color.FromArgb(255, 0xE8, 0x4D, 0x3D);

    private DispatcherQueueTimer? _tick;
    private BatteryPowerInfo _info;
    private int _sampledPercent = -1;
    private readonly IIslandTheme _theme;

    internal BatterySpotlightView(BatteryViewModel viewModel, IIslandTheme theme)
    {
        ViewModel = viewModel;
        _theme = theme;
        InitializeComponent();
        _theme.Changed += () => { if (IsLoaded) UpdateValues(); };

        ViewModel.PropertyChanged += (_, _) => { if (IsLoaded) SyncBackground(); };
        Loaded += (_, _) =>
        {
            SyncBackground();
            StartTick();
            OnTick();
        };
        Unloaded += (_, _) => StopTick();
    }

    public BatteryViewModel ViewModel { get; }

    /// <summary>
    /// 数值的中性色（读不到数据时的 "—"）：跟着岛体主题走 ——
    /// 浅色聚光卡上再用白字就等于没写。明暗两档与宿主的主文字同一档。
    /// </summary>
    private Brush NeutralText => new SolidColorBrush(_theme.IsLight
        ? Windows.UI.Color.FromArgb(255, 28, 28, 30)
        : Microsoft.UI.Colors.White);

    private void StartTick()
    {
        if (_tick == null)
        {
            _tick = DispatcherQueue.CreateTimer();
            _tick.Interval = SampleInterval;
            _tick.IsRepeating = true;
            _tick.Tick += (_, _) => OnTick();
        }

        _sampledPercent = -1;      // 第一拍立刻采样
        _tick.Start();
    }

    private void StopTick() { _tick?.Stop(); BackgroundScene.Configure(ViewModel.BackgroundMode, ViewModel.BackgroundIntensity, false, charging: true); }

    private void SyncBackground() => BackgroundScene.Configure(ViewModel.BackgroundMode, ViewModel.BackgroundIntensity,
        IsLoaded && ViewModel.IsCharging && ViewModel.GlowEnabled, charging: true, watts: ViewModel.ChargePower);

    private void OnTick()
    {
        _info = BatteryProbe.Read();
        UpdateValues();
    }

    private void UpdateValues()
    {
        var info = _info;
        int percent = Math.Clamp(ViewModel.Percent, 0, 100);

        if (percent != _sampledPercent)
        {
            _sampledPercent = percent;
            FgRing.Foreground = ViewModel.BatteryColor;
            FgRing.Value = percent;
        }

        // 状态胶囊：充电中 / 已接通电源 / 放电中
        if (info.Charging)
        {
            SetPill("正在充电", ChargingColor);
        }
        else if (info.Discharging)
        {
            SetPill("电池供电中", WarningColor);
        }
        else
        {
            SetPill("已接通电源", ChargingColor);
        }

        // 环下面那行大字：还有多久充满 / 还能用多久
        if (percent >= 100 && !info.Discharging)
        {
            EstimateText.Text = "已充满";
        }
        else if (info.UntilFullTime is { } estimateFull)
        {
            EstimateText.Text = $"预计 {BatteryFormat.Duration(estimateFull)}后充满";
        }
        else if (info.RemainingTime is { } estimateRemaining)
        {
            EstimateText.Text = $"剩余可用约 {BatteryFormat.Duration(estimateRemaining)}";
        }
        else
        {
            EstimateText.Text = info.Charging ? "正在充电" : "—";
        }

        if (info.Charging && info.ChargeRateMw is > 0)
        {
            ValuePowerLabel.Text = "充电功率";
            ValuePowerText.Text = BatteryFormat.Power(info.ChargeRateMw.Value);
            ValuePowerText.Foreground = new SolidColorBrush(ChargingColor);
        }
        else if (info.Discharging && info.DischargeRateMw is > 0)
        {
            ValuePowerLabel.Text = "放电功率";
            ValuePowerText.Text = BatteryFormat.Power(info.DischargeRateMw.Value);
            ValuePowerText.Foreground = new SolidColorBrush(WarningColor);
        }
        else
        {
            ValuePowerLabel.Text = "充电功率";
            ValuePowerText.Text = "—";
            ValuePowerText.Foreground = NeutralText;
        }

        ValueRemainingText.Text = info.RemainingCapacityMwh is { } remaining
            ? $"{BatteryFormat.Energy(remaining)}（{percent}%）"
            : $"{percent}%";
        ValueFullText.Text = info.FullChargeCapacityMwh is { } full ? BatteryFormat.Energy(full) : "—";
        ValueDesignText.Text = info.DesignedCapacityMwh is { } designed ? BatteryFormat.Energy(designed) : "—";

        if (info.HealthPercent is { } health)
        {
            ValueHealthText.Text = $"{health:F1}%";
            ValueHealthText.Foreground = new SolidColorBrush(
                health >= 80 ? ChargingColor : health >= 60 ? WarningColor : DangerColor);
        }
        else
        {
            ValueHealthText.Text = "—";
            ValueHealthText.Foreground = NeutralText;
        }

        ValueCycleText.Text = info.CycleCount is { } cycles ? $"{cycles} 次" : "—";

        if (info.VoltageMv is > 0)
        {
            ValueVoltageText.Text = info.VoltageMv.Value >= 1000
                ? $"{info.VoltageMv.Value / 1000.0:F2} V"
                : $"{info.VoltageMv.Value} mV";
        }
        else
        {
            ValueVoltageText.Text = "—";
        }

        if (info.UntilFullTime is { } untilFull)
        {
            ValueTimeLabel.Text = "预计充满";
            ValueTimeText.Text = BatteryFormat.Duration(untilFull);
        }
        else if (info.RemainingTime is { } remainingTime)
        {
            ValueTimeLabel.Text = "剩余可用";
            ValueTimeText.Text = BatteryFormat.Duration(remainingTime);
        }
        else
        {
            ValueTimeLabel.Text = info.Charging ? "预计充满" : "剩余可用";
            ValueTimeText.Text = "—";
        }
    }

    private void SetPill(string text, Windows.UI.Color color)
    {
        PillText.Text = text;
        PillText.Foreground = new SolidColorBrush(color);
        StatusPill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, color.R, color.G, color.B));
    }

}
