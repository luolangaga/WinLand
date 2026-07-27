using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinIsland.Core;

namespace WinIsland.Modules.Battery;

public sealed partial class BatteryIslandView : UserControl, IMorphView
{
    private Storyboard? _morphStoryboard;
    private bool _isExpanded;
    private DispatcherQueueTimer? _glowTimer;
    private float _glowPhase;

    internal BatteryViewModel ViewModel { get; }

    UIElement IMorphView.View => this;

    internal BatteryIslandView(BatteryViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _glowTimer = DispatcherQueue.CreateTimer();
        _glowTimer.Interval = TimeSpan.FromMilliseconds(16);
        _glowTimer.Tick += (_, _) => OnGlowTick();

        UpdateProgressArc();
        UpdateProgressBar();
        UpdateBatteryColor();
        UpdateBatteryGlyph();
        SyncGlow();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _glowTimer?.Stop();
        _morphStoryboard?.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BatteryViewModel.Percent):
                UpdateProgressArc();
                UpdateProgressBar();
                UpdateBatteryColor();
                break;
            case nameof(BatteryViewModel.IsCharging):
                UpdateBatteryGlyph();
                UpdateBatteryColor();
                SyncGlow();
                break;
            case nameof(BatteryViewModel.BatteryColor):
                UpdateBatteryColor();
                break;
            case nameof(BatteryViewModel.BatteryGlyph):
                UpdateBatteryGlyph();
                break;
        }
    }

    private void UpdateProgressArc()
    {
        if (FgRing == null) return;
        double circumference = 100;
        double offset = circumference - (ViewModel.Percent / 100.0 * circumference);
        FgRing.StrokeDashOffset = offset;
    }

    private void UpdateProgressBar()
    {
        if (ProgressBar == null || ProgressTrack == null) return;
        ProgressBar.Width = ViewModel.Percent / 100.0 * ProgressTrack.ActualWidth;
    }

    private void UpdateBatteryColor()
    {
        if (FgRing == null || ProgressBar == null) return;
        var color = ViewModel.BatteryColor;
        FgRing.Stroke = color;
        ProgressBar.Fill = color;
    }

    private void UpdateBatteryGlyph()
    {
        if (CenterIcon == null) return;
        CenterIcon.Glyph = ViewModel.BatteryGlyph;
    }

    public void AnimateToExpanded(TimeSpan duration)
    {
        _morphStoryboard?.Stop();
        _isExpanded = true;
        SyncGlow();

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var midTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);

        sb.Children.Add(DiscreteKeyFrameAnim(RootGrid, "Padding",
            new Thickness(20, 16, 20, 12), midTime));
        sb.Children.Add(Anim(MainGrid, "ColumnSpacing", MainGrid.ColumnSpacing, 14, duration, easing));

        sb.Children.Add(Anim(BatteryRing, "Width", 26, 72, duration, easing));
        sb.Children.Add(Anim(BatteryRing, "Height", 26, 72, duration, easing));
        sb.Children.Add(Anim(CenterIcon, "FontSize", 10, 28, duration, easing));
        sb.Children.Add(Anim(PercentText, "FontSize", 12, 15, duration, easing));

        var halfDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4);
        sb.Children.Add(Anim(PowerText, "Height", 0, 20, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.2)));
        sb.Children.Add(Anim(PowerText, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.2)));

        sb.Children.Add(Anim(BoltIcon, "Opacity", 1, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ExpandedPanel, "Height", 0, 80, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        sb.Children.Add(Anim(ExpandedPanel, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        if (ViewModel.GlowEnabled && ViewModel.IsCharging)
        {
            sb.Children.Add(Anim(GlowLayer, "Opacity", 0, 1,
                TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5),
                new CubicEase { EasingMode = EasingMode.EaseOut },
                beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        }

        sb.Completed += (_, _) =>
        {
            PercentText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            PercentText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            if (ViewModel.GlowEnabled && ViewModel.IsCharging)
                GlowLayer.Opacity = 1;
            SyncGlow();
            UpdateProgressBar();
        };

        _morphStoryboard = sb;
        sb.Begin();
    }

    public void AnimateToCompact(TimeSpan duration)
    {
        _morphStoryboard?.Stop();
        _isExpanded = false;
        _glowTimer?.Stop();

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var midTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);

        sb.Children.Add(DiscreteKeyFrameAnim(RootGrid, "Padding",
            new Thickness(8, 0, 14, 0), midTime));
        sb.Children.Add(Anim(MainGrid, "ColumnSpacing", MainGrid.ColumnSpacing, 10, duration, easing));

        sb.Children.Add(Anim(BatteryRing, "Width", BatteryRing.ActualWidth > 0 ? BatteryRing.ActualWidth : 72, 26, duration, easing));
        sb.Children.Add(Anim(BatteryRing, "Height", BatteryRing.ActualHeight > 0 ? BatteryRing.ActualHeight : 72, 26, duration, easing));
        sb.Children.Add(Anim(CenterIcon, "FontSize", CenterIcon.FontSize > 0 ? CenterIcon.FontSize : 28, 10, duration, easing));
        sb.Children.Add(Anim(PercentText, "FontSize", PercentText.FontSize > 0 ? PercentText.FontSize : 15, 12, duration, easing));

        var thirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3);
        sb.Children.Add(Anim(PowerText, "Height", PowerText.ActualHeight > 0 ? PowerText.ActualHeight : 20, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(PowerText, "Opacity", PowerText.Opacity > 0 ? PowerText.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ExpandedPanel, "Height", ExpandedPanel.ActualHeight > 0 ? ExpandedPanel.ActualHeight : 80, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(ExpandedPanel, "Opacity", ExpandedPanel.Opacity > 0 ? ExpandedPanel.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        var twoThirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);
        sb.Children.Add(Anim(BoltIcon, "Opacity", BoltIcon.Opacity >= 0 ? BoltIcon.Opacity : 0, 1, twoThirdDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        sb.Children.Add(Anim(GlowLayer, "Opacity",
            GlowLayer.Opacity > 0 ? GlowLayer.Opacity : 1, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Completed += (_, _) =>
        {
            PercentText.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            PercentText.Foreground = new SolidColorBrush(
                Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        };

        _morphStoryboard = sb;
        sb.Begin();
    }

    private void SyncGlow()
    {
        if (_glowTimer == null) return;
        bool shouldGlow = _isExpanded && ViewModel.IsCharging && ViewModel.GlowEnabled;
        if (shouldGlow && !_glowTimer.IsRunning)
            _glowTimer.Start();
        else if (!shouldGlow && _glowTimer.IsRunning)
            _glowTimer.Stop();
    }

    private void OnGlowTick()
    {
        _glowPhase += 0.03f;
        float pulse1 = 0.15f * ((float)Math.Sin(_glowPhase) * 0.5f + 0.5f);
        float pulse2 = 0.12f * ((float)Math.Sin(_glowPhase * 0.7f + 1.0f) * 0.5f + 0.5f);

        Glow1Scale.ScaleX = Glow1Scale.ScaleY = 0.50 + pulse1 * 1.5;
        Glow1.Opacity = 0.35 + pulse1 * 0.55;
        Glow2Scale.ScaleX = Glow2Scale.ScaleY = 0.45 + pulse2 * 1.2;
        Glow2.Opacity = 0.30 + pulse2 * 0.45;
    }

    private static DoubleAnimation Anim(
        DependencyObject target, string property,
        double from, double to, TimeSpan duration,
        EasingFunctionBase easing, TimeSpan? beginTime = null)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        if (beginTime.HasValue) anim.BeginTime = beginTime.Value;
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }

    private static ObjectAnimationUsingKeyFrames DiscreteKeyFrameAnim(
        DependencyObject target, string property, object value, TimeSpan keyTime)
    {
        var anim = new ObjectAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new DiscreteObjectKeyFrame
        {
            KeyTime = keyTime,
            Value = value,
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }
}
