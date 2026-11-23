using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using WinIsland.Core;
using WinIsland.Island;
using System.Numerics;

namespace WinIsland.Modules.Battery;

public sealed partial class BatteryIslandView : UserControl, IMorphView
{
    private readonly MorphProgress _morph;
    private bool _isExpanded;

    internal BatteryViewModel ViewModel { get; }

    UIElement IMorphView.View => this;

    internal BatteryIslandView(BatteryViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();
        _morph = new MorphProgress(ApplyMorph);
        ProgressTrack.SizeChanged += (_, _) => UpdateProgressBar();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        BatteryRing.SizeChanged += (_, _) => SyncRingSize();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateProgressArc();
        UpdateProgressBar();
        UpdateArcColor();
        UpdateCenterIcon();
        SyncGlow();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _morph.Complete();
        StopGlow();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateProgressArc();
        UpdateProgressBar();
        UpdateArcColor();
        UpdateCenterIcon();
        SyncGlow();
        Motion.Scalar(Motion.Visual(ChargingLabel), "Opacity", ViewModel.IsCharging && !_isExpanded ? 1 : 0, 180);
    }

    private void UpdateProgressArc()
    {
        if (FgRing == null) return;
        FgRing.Value = Math.Clamp(ViewModel.Percent, 0, 100);
    }

    /// <summary>
    /// 原生 ProgressRing 不跟随容器拉伸（默认固定 32×32），而岛体这颗环的外框会在
    /// 26↔48 之间做尺寸 morph —— 展开/收起动画期间 SizeChanged 是逐帧触发的，这里跟着同步。
    /// </summary>
    private void SyncRingSize()
    {
        if (FgRing == null || BatteryRing == null) return;
        double size = BatteryRing.ActualWidth;
        if (size > 0)
        {
            FgRing.Width = size;
            FgRing.Height = size;
        }
    }

    private void UpdateProgressBar()
    {
        if (ProgressBar == null || ProgressTrack == null) return;
        if (ProgressTrack.ActualWidth <= 0) return;
        ProgressBar.Width = ProgressTrack.ActualWidth;
        Motion.Vector(Motion.Visual(ProgressBar), "Scale", new Vector3(Math.Clamp(ViewModel.Percent / 100f, 0, 1), 1, 1), 240);
    }

    private void UpdateArcColor()
    {
        if (FgRing == null || ProgressBar == null) return;
        var c = ViewModel.BatteryColor;
        FgRing.Foreground = c;
        ProgressBar.Fill = c;
    }

    private void UpdateCenterIcon()
    {
        if (CenterIcon == null) return;
        CenterIcon.Glyph = ViewModel.BatteryGlyph;
    }

    public void AnimateToExpanded(TimeSpan duration) => SetExpanded(true, duration);
    public void AnimateToCompact(TimeSpan duration) => SetExpanded(false, duration);

    private void SetExpanded(bool expanded, TimeSpan duration)
    {
        _isExpanded = expanded;
        PercentText.Style = (Style)Resources[expanded ? "PercentExpandedStyle" : "PercentCompactStyle"];
        PercentText.FontWeight = expanded ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        Motion.Scalar(Motion.Visual(ChargingLabel), "Opacity", !expanded && ViewModel.IsCharging ? 1 : 0, 180);
        _morph.To(expanded ? 1 : 0, duration);
        SyncGlow();
    }

    private void ApplyMorph(double p)
    {
        RootGrid.Padding = new Thickness(8 + 12 * p, 14 * p, 14 + 6 * p, 10 * p);
        MainGrid.ColumnSpacing = 10 + 4 * p;
        BatteryRing.Width = BatteryRing.Height = 26 + 22 * p;
        CenterIcon.FontSize = 10 + 8 * p;
        PercentText.FontSize = 12 + 10 * p;
        PowerText.FontSize = 11 + 2 * p;
        ExpandedPanel.Height = 65 * p;
        ExpandedPanel.Opacity = p;
        GlowLayer.Opacity = ViewModel.IsCharging ? p : 0;
    }

    private void SyncGlow() => BackgroundScene.Configure(ViewModel.BackgroundMode,
        ViewModel.BackgroundIntensity, IsLoaded && _isExpanded && ViewModel.IsCharging && ViewModel.GlowEnabled, charging: true, watts: ViewModel.ChargePower);

    private void StopGlow() => BackgroundScene.Configure(ViewModel.BackgroundMode, ViewModel.BackgroundIntensity, false, charging: true);
}
