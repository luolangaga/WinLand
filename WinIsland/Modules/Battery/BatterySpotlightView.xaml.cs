using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinIsland.Core;

namespace WinIsland.Modules.Battery;

/// <summary>
/// 电池的「超级展开」聚光卡：左侧大进度环，右侧高级信息（充放电功率、容量、健康度、
/// 循环次数、电压、预计时间）。数值每 40ms 平滑推进、每秒重新采样一次；
/// 生命周期跟着 Loaded/Unloaded 走（宿主收起卡片时内容被卸下 → 停表）。
/// </summary>
public sealed partial class BatterySpotlightView : UserControl
{
    private const int TickMs = 40;
    /// <summary>每 25 个 tick（约 1 秒）重新采一次数据。</summary>
    private const int SampleEveryTicks = 25;
    private const double RingEase = 0.16;

    private static readonly Windows.UI.Color ChargingColor = Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F);
    private static readonly Windows.UI.Color WarningColor = Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23);
    private static readonly Windows.UI.Color DangerColor = Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x4D, 0x3D);

    private DispatcherQueueTimer? _tick;
    private BatteryPowerInfo _info;
    private double _ringValue = 100;
    private double _ringTarget = 100;
    private int _ticks;
    private int _sampledPercent = -1;
    private readonly IIslandTheme _theme;

    internal BatterySpotlightView(BatteryViewModel viewModel, IIslandTheme theme)
    {
        ViewModel = viewModel;
        _theme = theme;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            StartTick();
            FadeIn();
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
            _tick.Interval = TimeSpan.FromMilliseconds(TickMs);
            _tick.IsRepeating = true;
            _tick.Tick += (_, _) => OnTick();
        }

        _sampledPercent = -1;
        _ticks = SampleEveryTicks;      // 第一拍立刻采样
        _tick.Start();
    }

    private void StopTick() => _tick?.Stop();

    private void OnTick()
    {
        if (++_ticks >= SampleEveryTicks)
        {
            _ticks = 0;
            _info = BatteryProbe.Read();
            UpdateValues();
        }

        UpdateRing();
    }

    private void UpdateRing()
    {
        _ringTarget = 100 - Math.Clamp(ViewModel.Percent, 0, 100);
        double delta = _ringTarget - _ringValue;
        if (Math.Abs(delta) < 0.02)
        {
            _ringValue = _ringTarget;
        }
        else
        {
            _ringValue += delta * RingEase;
        }

        FgRing.StrokeDashOffset = _ringValue;
    }

    private void UpdateValues()
    {
        var info = _info;
        int percent = Math.Clamp(ViewModel.Percent, 0, 100);

        if (percent != _sampledPercent)
        {
            _sampledPercent = percent;
            FgRing.Stroke = ViewModel.BatteryColor;
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

    /// <summary>信息区淡入：与卡片的飞入动画一起，读数据的那一拍不会显得突兀。</summary>
    private void FadeIn()
    {
        InfoPanel.Opacity = 1;

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(animation, InfoPanel);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
