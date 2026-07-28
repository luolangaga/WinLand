using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinIsland.Core;

namespace WinIsland.Modules.AiMonitor;

public sealed partial class AiMonitorIslandView : UserControl, IMorphView
{
    private Storyboard? _morphStoryboard;
    private bool _isExpanded;

    private DispatcherQueueTimer? _glowTimer;
    private DispatcherQueueTimer? _dotTimer;
    private DispatcherQueueTimer? _pulseTimer;
    private DispatcherQueueTimer? _scanTimer;
    private DispatcherQueueTimer? _arcTimer;

    private float _glowAmbient;
    private float _uiGlow1, _uiGlow2, _uiGlow3;
    private int _dotTick;
    private float _pulsePhase;
    private int _pulseIdx;
    private float _scanY;
    private int _arcTick;

    internal AiMonitorViewModel ViewModel { get; }

    UIElement IMorphView.View => this;

    internal AiMonitorIslandView(AiMonitorViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _glowTimer = DispatcherQueue.CreateTimer();
        _glowTimer.Interval = TimeSpan.FromMilliseconds(16);
        _glowTimer.Tick += (_, _) => OnGlowTick();

        _dotTimer = DispatcherQueue.CreateTimer();
        _dotTimer.Interval = TimeSpan.FromMilliseconds(280);
        _dotTimer.Tick += (_, _) => OnDotTick();

        _pulseTimer = DispatcherQueue.CreateTimer();
        _pulseTimer.Interval = TimeSpan.FromMilliseconds(16);
        _pulseTimer.Tick += (_, _) => OnPulseTick();

        _scanTimer = DispatcherQueue.CreateTimer();
        _scanTimer.Interval = TimeSpan.FromMilliseconds(16);
        _scanTimer.Tick += (_, _) => OnScanTick();

        _arcTimer = DispatcherQueue.CreateTimer();
        _arcTimer.Interval = TimeSpan.FromMilliseconds(30);
        _arcTimer.Tick += (_, _) => OnArcTick();

        SyncDotSpinner();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _glowTimer?.Stop();
        _dotTimer?.Stop();
        _pulseTimer?.Stop();
        _scanTimer?.Stop();
        _arcTimer?.Stop();
        _morphStoryboard?.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiMonitorViewModel.IsRunning))
        {
            SyncDotSpinner();
            SyncGlow();
            SyncPulse();
            SyncScan();
            SyncArc();
        }
    }

    #region Dot spinner (compact + running)

    private void SyncDotSpinner()
    {
        if (_dotTimer == null) return;
        if (ViewModel.IsRunning && !_isExpanded)
        {
            if (!_dotTimer.IsRunning) _dotTimer.Start();
        }
        else
        {
            _dotTimer.Stop();
            Dot1.Opacity = 0.3;
            Dot2.Opacity = 0.6;
            Dot3.Opacity = 1.0;
        }
    }

    private void OnDotTick()
    {
        _dotTick++;
        double t = _dotTick * 0.15;
        Dot1.Opacity = 0.25 + 0.75 * Math.Max(0, Math.Sin(t));
        Dot2.Opacity = 0.25 + 0.75 * Math.Max(0, Math.Sin(t - 1.0));
        Dot3.Opacity = 0.25 + 0.75 * Math.Max(0, Math.Sin(t - 2.0));
    }

    #endregion

    #region Pulse ring (compact + running)

    private void SyncPulse()
    {
        if (_pulseTimer == null) return;
        bool shouldPulse = ViewModel.IsRunning && !_isExpanded;
        if (shouldPulse && !_pulseTimer.IsRunning)
        {
            _pulsePhase = 0;
            _pulseIdx = 0;
            PulseRing.Opacity = 0;
            _pulseTimer.Start();
        }
        else if (!shouldPulse && _pulseTimer.IsRunning)
        {
            _pulseTimer.Stop();
            PulseRing.Opacity = 0;
        }
    }

    private void OnPulseTick()
    {
        _pulsePhase += 0.025f;
        if (_pulsePhase > 1.0f)
        {
            _pulsePhase = 0;
            _pulseIdx = (_pulseIdx + 1) % 2;
        }

        float p = _pulsePhase;
        float scale = 1.0f + p * 0.8f;
        float alpha = (1.0f - p) * 0.6f;

        PulseRingScale.ScaleX = PulseRingScale.ScaleY = scale;
        PulseRing.Opacity = alpha;
    }

    #endregion

    #region Scan line (expanded + running)

    private void SyncScan()
    {
        if (_scanTimer == null) return;
        bool shouldScan = ViewModel.IsRunning && _isExpanded;
        if (shouldScan && !_scanTimer.IsRunning)
        {
            _scanY = 0;
            ScanLine.Opacity = 0;
            _scanTimer.Start();
        }
        else if (!shouldScan && _scanTimer.IsRunning)
        {
            _scanTimer.Stop();
            ScanLine.Opacity = 0;
        }
    }

    private void OnScanTick()
    {
        float h = (float)ActualHeight;
        if (h <= 0) return;

        _scanY += 0.8f;
        if (_scanY > h + 20)
            _scanY = -20;

        ScanLineTranslate.Y = _scanY;
        float dist = Math.Abs(_scanY - h * 0.5f);
        float centerFade = Math.Max(0, 1.0f - dist / (h * 0.5f));
        ScanLine.Opacity = centerFade * 0.7f;
    }

    #endregion

    #region Arc spinner (expanded + running)

    private void SyncArc()
    {
        if (_arcTimer == null) return;
        bool shouldArc = ViewModel.IsRunning && _isExpanded;
        if (shouldArc && !_arcTimer.IsRunning)
        {
            ArcSpinner.Opacity = 1;
            _arcTimer.Start();
        }
        else if (!shouldArc && _arcTimer.IsRunning)
        {
            _arcTimer.Stop();
            ArcSpinner.Opacity = 0;
        }
    }

    private void OnArcTick()
    {
        _arcTick++;
        ArcSpinnerRotate.Angle = _arcTick * 4.5;
        ArcSpinner.StrokeDashOffset = -_arcTick * 0.5;
    }

    #endregion

    #region Aurora glow (expanded + running)

    private void SyncGlow()
    {
        if (_glowTimer == null) return;
        bool shouldGlow = _isExpanded && ViewModel.IsRunning;
        if (shouldGlow && !_glowTimer.IsRunning)
            _glowTimer.Start();
        else if (!shouldGlow && _glowTimer.IsRunning)
            _glowTimer.Stop();
    }

    private void OnGlowTick()
    {
        _glowAmbient += 0.025f;

        float g1 = 0.15f * ((float)Math.Sin(_glowAmbient) * 0.5f + 0.5f);
        float g2 = 0.13f * ((float)Math.Sin(_glowAmbient * 0.7f + 1.2f) * 0.5f + 0.5f);
        float g3 = 0.11f * ((float)Math.Sin(_glowAmbient * 1.3f + 2.5f) * 0.5f + 0.5f);

        _uiGlow1 = _uiGlow1 * 0.92f + g1 * 0.08f;
        _uiGlow2 = _uiGlow2 * 0.90f + g2 * 0.10f;
        _uiGlow3 = _uiGlow3 * 0.88f + g3 * 0.12f;

        float b = Math.Clamp(_uiGlow1, 0f, 1f);
        float m = Math.Clamp(_uiGlow2, 0f, 1f);
        float c = Math.Clamp(_uiGlow3, 0f, 1f);

        Glow1Scale.ScaleX = Glow1Scale.ScaleY = 0.40 + b * 1.8;
        GlowOrb1.Opacity = 0.30 + b * 0.55;

        Glow2Scale.ScaleX = Glow2Scale.ScaleY = 0.35 + m * 1.5;
        GlowOrb2.Opacity = 0.25 + m * 0.50;

        Glow3Scale.ScaleX = Glow3Scale.ScaleY = 0.30 + c * 1.2;
        GlowOrb3.Opacity = 0.20 + c * 0.45;
    }

    #endregion

    public void AnimateToExpanded(TimeSpan duration)
    {
        _morphStoryboard?.Stop();
        _isExpanded = true;
        SyncGlow();
        SyncDotSpinner();
        SyncPulse();
        SyncScan();
        SyncArc();

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var midTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);
        var halfDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4);

        sb.Children.Add(DiscreteKeyFrameAnim(RootGrid, "Padding",
            new Thickness(16, 10, 16, 8), midTime));
        sb.Children.Add(Anim(MainGrid, "ColumnSpacing", MainGrid.ColumnSpacing, 14, duration, easing));

        sb.Children.Add(Anim(IconBox, "Width", 26, 48, duration, easing));
        sb.Children.Add(Anim(IconBox, "Height", 26, 48, duration, easing));
        sb.Children.Add(DiscreteKeyFrameAnim(IconBg, "CornerRadius", new CornerRadius(14), midTime));
        sb.Children.Add(Anim(StatusIcon, "FontSize", 12, 18, duration, easing));
        sb.Children.Add(Anim(TaskNameText, "FontSize", 12, 15, duration, easing));

        sb.Children.Add(Anim(DetailText, "Height", 0, 18, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.2)));
        sb.Children.Add(Anim(DetailText, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.2)));

        sb.Children.Add(Anim(Spinner, "Opacity", Spinner.Opacity, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ArcSpinner, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.25)));

        sb.Children.Add(Anim(ProgressRow, "Height", 0, 20, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        sb.Children.Add(Anim(ProgressRow, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        sb.Children.Add(Anim(ExpandedPanel, "Height", 0, 60, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        sb.Children.Add(Anim(ExpandedPanel, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        if (ViewModel.IsRunning)
        {
            sb.Children.Add(Anim(GlowLayer, "Opacity", 0, 1,
                TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5),
                new CubicEase { EasingMode = EasingMode.EaseOut },
                beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        }

        sb.Completed += (_, _) =>
        {
            TaskNameText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            TaskNameText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            if (ViewModel.IsRunning)
                GlowLayer.Opacity = 1;
            SyncGlow();
            SyncScan();
            SyncArc();
        };

        _morphStoryboard = sb;
        sb.Begin();
    }

    public void AnimateToCompact(TimeSpan duration)
    {
        _morphStoryboard?.Stop();
        _isExpanded = false;
        _glowTimer?.Stop();
        _scanTimer?.Stop();
        _arcTimer?.Stop();

        ScanLine.Opacity = 0;
        ArcSpinner.Opacity = 0;

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var midTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);
        var thirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3);

        sb.Children.Add(DiscreteKeyFrameAnim(RootGrid, "Padding",
            new Thickness(8, 0, 14, 0), midTime));
        sb.Children.Add(Anim(MainGrid, "ColumnSpacing", MainGrid.ColumnSpacing, 10, duration, easing));

        sb.Children.Add(Anim(IconBox, "Width",
            IconBox.ActualWidth > 0 ? IconBox.ActualWidth : 48, 26, duration, easing));
        sb.Children.Add(Anim(IconBox, "Height",
            IconBox.ActualHeight > 0 ? IconBox.ActualHeight : 48, 26, duration, easing));
        sb.Children.Add(DiscreteKeyFrameAnim(IconBg, "CornerRadius", new CornerRadius(7), midTime));
        sb.Children.Add(Anim(StatusIcon, "FontSize",
            StatusIcon.FontSize > 0 ? StatusIcon.FontSize : 18, 12, duration, easing));
        sb.Children.Add(Anim(TaskNameText, "FontSize",
            TaskNameText.FontSize > 0 ? TaskNameText.FontSize : 15, 12, duration, easing));

        sb.Children.Add(Anim(DetailText, "Height",
            DetailText.ActualHeight > 0 ? DetailText.ActualHeight : 18, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(DetailText, "Opacity",
            DetailText.Opacity > 0 ? DetailText.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ArcSpinner, "Opacity",
            ArcSpinner.Opacity > 0 ? ArcSpinner.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ProgressRow, "Height",
            ProgressRow.ActualHeight > 0 ? ProgressRow.ActualHeight : 20, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(ProgressRow, "Opacity",
            ProgressRow.Opacity > 0 ? ProgressRow.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ExpandedPanel, "Height",
            ExpandedPanel.ActualHeight > 0 ? ExpandedPanel.ActualHeight : 60, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(ExpandedPanel, "Opacity",
            ExpandedPanel.Opacity > 0 ? ExpandedPanel.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        var twoThirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);
        sb.Children.Add(Anim(Spinner, "Opacity", Spinner.Opacity >= 0 ? Spinner.Opacity : 0, 1, twoThirdDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        sb.Children.Add(Anim(GlowLayer, "Opacity",
            GlowLayer.Opacity > 0 ? GlowLayer.Opacity : 1, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Completed += (_, _) =>
        {
            TaskNameText.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            TaskNameText.Foreground = new SolidColorBrush(
                Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
            SyncDotSpinner();
            SyncPulse();
        };

        _morphStoryboard = sb;
        sb.Begin();
    }

    #region Animation helpers

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

    #endregion
}
