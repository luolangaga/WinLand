using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using WinIsland.Core;

namespace WinIsland.Modules.Battery;

public sealed partial class BatteryIslandView : UserControl, IMorphView
{
    private Storyboard? _morphStoryboard;
    private bool _isExpanded;
    private bool _entrancePlayed;

    private DispatcherQueueTimer? _glowTimer;
    private float _uiGlow;
    private float _glowAmbient;

    private DispatcherQueueTimer? _arcTimer;
    private readonly List<Polyline> _arcs = new();
    private readonly Random _rng = new();
    private int _arcTick;

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

        _arcTimer = DispatcherQueue.CreateTimer();
        _arcTimer.Interval = TimeSpan.FromMilliseconds(60);
        _arcTimer.Tick += (_, _) => OnArcTick();

        BuildArcs();

        UpdateProgressArc();
        UpdateProgressBar();
        UpdateArcColor();
        UpdateCenterIcon();

        if (!_entrancePlayed)
        {
            _entrancePlayed = true;
            PlayEntranceAnimation();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _glowTimer?.Stop();
        _arcTimer?.Stop();
        _morphStoryboard?.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BatteryViewModel.Percent):
                UpdateProgressArc();
                UpdateProgressBar();
                UpdateArcColor();
                break;
            case nameof(BatteryViewModel.IsCharging):
                UpdateArcColor();
                UpdateCenterIcon();
                if (ViewModel.IsCharging && !_isExpanded)
                    PlayChargingLabelAnimation();
                else
                    ChargingLabel.Opacity = 0;
                SyncGlow();
                SyncArcs();
                break;
            case nameof(BatteryViewModel.BatteryColor):
                UpdateArcColor();
                break;
            case nameof(BatteryViewModel.BatteryGlyph):
                UpdateCenterIcon();
                break;
        }
    }

    #region Electric arcs

    private void BuildArcs()
    {
        if (ArcCanvas == null) return;

        for (int i = 0; i < 6; i++)
        {
            var arc = new Polyline
            {
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0x50, 0x6C, 0xCB, 0x5F)),
                StrokeThickness = _rng.Next(1, 3),
                Opacity = 0,
                IsHitTestVisible = false,
            };
            _arcs.Add(arc);
            ArcCanvas.Children.Add(arc);
        }
    }

    private void SyncArcs()
    {
        if (_arcTimer == null) return;
        bool shouldArc = ViewModel.IsCharging;
        if (shouldArc && !_arcTimer.IsRunning)
        {
            ArcCanvas.Opacity = 1;
            _arcTimer.Start();
        }
        else if (!shouldArc && _arcTimer.IsRunning)
        {
            _arcTimer.Stop();
            ArcCanvas.Opacity = 0;
            foreach (var a in _arcs) a.Opacity = 0;
        }
    }

    private void OnArcTick()
    {
        _arcTick++;
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        for (int i = 0; i < _arcs.Count; i++)
        {
            var arc = _arcs[i];

            bool active = ((_arcTick + i * 7) % 10) < 4;
            if (!active)
            {
                arc.Opacity = 0;
                continue;
            }

            double startX = _rng.NextDouble() * w * 0.3;
            double startY = h * 0.2 + _rng.NextDouble() * h * 0.6;
            double endX = w * 0.7 + _rng.NextDouble() * w * 0.3;
            double endY = h * 0.2 + _rng.NextDouble() * h * 0.6;

            int segments = 3 + _rng.Next(3);
            var pts = new PointCollection { new(startX, startY) };

            for (int s = 1; s < segments; s++)
            {
                double t = (double)s / segments;
                double x = startX + (endX - startX) * t + (_rng.NextDouble() - 0.5) * 30;
                double y = startY + (endY - startY) * t + (_rng.NextDouble() - 0.5) * 20;
                pts.Add(new(x, y));
            }
            pts.Add(new(endX, endY));

            arc.Points = pts;
            arc.Opacity = 0.15 + _rng.NextDouble() * 0.35;
            arc.StrokeThickness = 0.8 + _rng.NextDouble() * 1.5;

            byte alpha = (byte)(0x30 + _rng.Next(0x40));
            arc.Stroke = new SolidColorBrush(
                Windows.UI.Color.FromArgb(alpha, 0x6C, 0xCB, 0x5F));
        }
    }

    #endregion

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
        if (ProgressTrack.ActualWidth <= 0) return;
        ProgressBar.Width = ViewModel.Percent / 100.0 * ProgressTrack.ActualWidth;
    }

    private void UpdateArcColor()
    {
        if (FgRing == null || ProgressBar == null) return;
        var c = ViewModel.BatteryColor;
        FgRing.Stroke = c;
        ProgressBar.Fill = c;
    }

    private void UpdateCenterIcon()
    {
        if (CenterIcon == null) return;
        CenterIcon.Glyph = ViewModel.BatteryGlyph;
    }

    private void PlayChargingLabelAnimation()
    {
        var sb = new Storyboard();

        sb.Children.Add(Anim(ChargingLabel, "Opacity", 0, 1,
            TimeSpan.FromMilliseconds(300),
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(200)));

        sb.Children.Add(Anim(ChargingLabelTranslate, "X", 8, 0,
            TimeSpan.FromMilliseconds(350),
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(200)));

        sb.Begin();
    }

    private void PlayEntranceAnimation()
    {
        var sb = new Storyboard();

        double circumference = 100;
        double targetOffset = circumference - (ViewModel.Percent / 100.0 * circumference);

        var arcEasing = new CubicEase { EasingMode = EasingMode.EaseOut };
        sb.Children.Add(Anim(FgRing, "StrokeDashOffset", circumference, targetOffset,
            TimeSpan.FromMilliseconds(600), arcEasing));

        sb.Children.Add(Anim(MainGrid, "Opacity", 0, 1,
            TimeSpan.FromMilliseconds(300),
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(200)));

        sb.Children.Add(Anim(BatteryRing, "Opacity", 0, 1,
            TimeSpan.FromMilliseconds(300),
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(100)));

        sb.Completed += (_, _) =>
        {
            SyncGlow();
            SyncArcs();
            if (ViewModel.IsCharging && !_isExpanded)
                PlayChargingLabelAnimation();
        };

        _morphStoryboard = sb;
        sb.Begin();
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
            new Thickness(20, 14, 20, 10), midTime));
        sb.Children.Add(Anim(MainGrid, "ColumnSpacing", MainGrid.ColumnSpacing, 14, duration, easing));

        sb.Children.Add(Anim(BatteryRing, "Width", 26, 48, duration, easing));
        sb.Children.Add(Anim(BatteryRing, "Height", 26, 48, duration, easing));
        sb.Children.Add(Anim(CenterIcon, "FontSize", 10, 18, duration, easing));
        sb.Children.Add(Anim(PercentText, "FontSize", 12, 15, duration, easing));

        var halfDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4);
        sb.Children.Add(Anim(PowerText, "FontSize", 11, 13, duration, easing));

        sb.Children.Add(Anim(ChargingLabel, "Opacity", ChargingLabel.Opacity, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ExpandedPanel, "Height", 0, 65, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        sb.Children.Add(Anim(ExpandedPanel, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        if (ViewModel.IsCharging)
        {
            sb.Children.Add(Anim(GlowLayer, "Opacity", 0, 1,
                TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5),
                new CubicEase { EasingMode = EasingMode.EaseOut },
                beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        }

        sb.Completed += (_, _) =>
        {
            PercentText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            // 展开态更亮一档：换 Style 而不是赋画刷，颜色才继续跟着主题走
            PercentText.Style = (Style)Resources["PercentExpandedStyle"];
            if (ViewModel.IsCharging)
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

        sb.Children.Add(Anim(BatteryRing, "Width",
            BatteryRing.ActualWidth > 0 ? BatteryRing.ActualWidth : 48, 26, duration, easing));
        sb.Children.Add(Anim(BatteryRing, "Height",
            BatteryRing.ActualHeight > 0 ? BatteryRing.ActualHeight : 48, 26, duration, easing));
        sb.Children.Add(Anim(CenterIcon, "FontSize",
            CenterIcon.FontSize > 0 ? CenterIcon.FontSize : 18, 10, duration, easing));
        sb.Children.Add(Anim(PercentText, "FontSize",
            PercentText.FontSize > 0 ? PercentText.FontSize : 15, 12, duration, easing));

        var thirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3);
        sb.Children.Add(Anim(PowerText, "FontSize",
            PowerText.FontSize > 0 ? PowerText.FontSize : 13, 11, duration, easing));

        sb.Children.Add(Anim(ExpandedPanel, "Height",
            ExpandedPanel.ActualHeight > 0 ? ExpandedPanel.ActualHeight : 65, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(ExpandedPanel, "Opacity",
            ExpandedPanel.Opacity > 0 ? ExpandedPanel.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(GlowLayer, "Opacity",
            GlowLayer.Opacity > 0 ? GlowLayer.Opacity : 1, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Completed += (_, _) =>
        {
            PercentText.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            PercentText.Style = (Style)Resources["PercentCompactStyle"];
            if (ViewModel.IsCharging)
                PlayChargingLabelAnimation();
        };

        _morphStoryboard = sb;
        sb.Begin();
    }

    #region Glow

    private void SyncGlow()
    {
        if (_glowTimer == null) return;
        bool shouldGlow = _isExpanded && ViewModel.IsCharging;
        if (shouldGlow && !_glowTimer.IsRunning)
            _glowTimer.Start();
        else if (!shouldGlow && _glowTimer.IsRunning)
            _glowTimer.Stop();
    }

    private void OnGlowTick()
    {
        _glowAmbient += 0.03f;
        float pulse = 0.15f * ((float)Math.Sin(_glowAmbient) * 0.5f + 0.5f);
        _uiGlow = _uiGlow * 0.92f + pulse * 0.08f;

        float g = Math.Clamp(_uiGlow, 0f, 1f);
        GlowOrbScale.ScaleX = GlowOrbScale.ScaleY = 0.6 + g * 1.5;
        GlowOrb.Opacity = 0.35 + g * 0.55;
    }

    #endregion

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
