using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinIsland.Core;

namespace WinIsland.Modules.Media;

/// <summary>
/// 统一变形视图：单个 UserControl 同时承载紧凑态与展开态的所有元素。
/// 实现 IMorphView，由主程序在展开/收起时调用，与岛体尺寸动画同步。
/// 节奏光晕通过 AudioLevelMonitor 读取实时音频频段能量，每帧驱动光晕缩放/透明度。
/// </summary>
public sealed partial class MediaIslandView : UserControl, IMorphView
{
    private readonly Storyboard _barsStoryboard = new();
    private bool _barsRunning;
    private Storyboard? _morphStoryboard;
    private bool _isExpanded;

    private readonly AudioLevelMonitor? _levelMonitor;
    private readonly DispatcherQueueTimer _glowTimer;
    private readonly DispatcherQueueTimer _progressTimer;
    private float _uiBass, _uiMid, _uiTreble;

    internal MediaViewModel ViewModel { get; }

    UIElement IMorphView.View => this;

    internal MediaIslandView(MediaViewModel vm, AudioLevelMonitor? levelMonitor)
    {
        ViewModel = vm;
        _levelMonitor = levelMonitor;
        InitializeComponent();

        BuildBarsAnimation();
        Loaded += (_, _) => SyncBars();
        Unloaded += (_, _) => { StopBars(); _glowTimer?.Stop(); _progressTimer?.Stop(); };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaViewModel.IsPlaying)) SyncBars();
            if (e.PropertyName is nameof(MediaViewModel.IsPlaying) or nameof(MediaViewModel.GlowEnabled))
                SyncGlow();
            if (e.PropertyName is nameof(MediaViewModel.Progress) or nameof(MediaViewModel.HasProgress))
                UpdateProgressVisual();
        };

        ProgressArea.SizeChanged += (_, _) => UpdateProgressVisual();

        _glowTimer = DispatcherQueue.CreateTimer();
        _glowTimer.Interval = TimeSpan.FromMilliseconds(16);
        _glowTimer.Tick += (_, _) => OnGlowTick();

        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(250);
        _progressTimer.Tick += (_, _) => SyncProgressTimer();
    }

    private void SyncProgressTimer()
    {
        if (_isExpanded && ViewModel.IsPlaying && ViewModel.HasProgress)
        {
            ViewModel.RefreshTimeline?.Invoke();
        }
    }

    public void AnimateToExpanded(TimeSpan duration)
    {
        double currentSpacing = MainGrid.ColumnSpacing;
        _morphStoryboard?.Stop();
        _isExpanded = true;
        SyncGlow();

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var midTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);

        sb.Children.Add(DiscreteKeyFrameAnim(RootGrid, "Padding",
            new Thickness(16, 10, 16, 8), midTime));
        sb.Children.Add(Anim(MainGrid, "ColumnSpacing", currentSpacing, 14, duration, easing));
        sb.Children.Add(DiscreteKeyFrameAnim(Cover, "CornerRadius",
            new CornerRadius(14), midTime));

        StopBars();

        sb.Children.Add(Anim(Cover, "Width", 26, 72, duration, easing));
        sb.Children.Add(Anim(Cover, "Height", 26, 72, duration, easing));
        sb.Children.Add(Anim(PlaceholderIcon, "FontSize", 12, 28, duration, easing));
        sb.Children.Add(Anim(Title, "FontSize", 12, 15, duration, easing));

        var halfDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4);
        sb.Children.Add(Anim(Artist, "Height", 0, 18, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.2)));
        sb.Children.Add(Anim(Artist, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.2)));

        sb.Children.Add(Anim(SourceAppRow, "Height", 0, 14, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        sb.Children.Add(Anim(SourceAppRow, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        sb.Children.Add(Anim(Bars, "Opacity", 1, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ControlsRow, "Height", 0, 40, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        sb.Children.Add(Anim(ControlsRow, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        sb.Children.Add(Anim(ProgressRow, "Height", 0, 14, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4)));
        sb.Children.Add(Anim(ProgressRow, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4)));

        if (ViewModel.GlowEnabled)
        {
            sb.Children.Add(Anim(GlowLayer, "Opacity", 0, 1,
                TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5),
                new CubicEase { EasingMode = EasingMode.EaseOut },
                beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        }

        sb.Completed += (_, _) =>
        {
            Title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            // 展开态标题亮一档：换 Style 而不是赋画刷，颜色才继续跟着主题走
            Title.Style = (Style)Resources["TitleExpandedStyle"];
            if (ViewModel.GlowEnabled)
                GlowLayer.Opacity = 1;
            SyncGlow();
            SyncProgressTimerActive();
        };

        _morphStoryboard = sb;
        sb.Begin();
    }

    public void AnimateToCompact(TimeSpan duration)
    {
        double currentSpacing = MainGrid.ColumnSpacing;
        _morphStoryboard?.Stop();
        _isExpanded = false;
        _glowTimer.Stop();

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var midTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);

        sb.Children.Add(DiscreteKeyFrameAnim(RootGrid, "Padding",
            new Thickness(8, 0, 14, 0), midTime));
        sb.Children.Add(Anim(MainGrid, "ColumnSpacing", currentSpacing, 10, duration, easing));
        sb.Children.Add(DiscreteKeyFrameAnim(Cover, "CornerRadius",
            new CornerRadius(7), midTime));

        sb.Children.Add(Anim(Cover, "Width", Cover.ActualWidth > 0 ? Cover.ActualWidth : 72, 26, duration, easing));
        sb.Children.Add(Anim(Cover, "Height", Cover.ActualHeight > 0 ? Cover.ActualHeight : 72, 26, duration, easing));
        sb.Children.Add(Anim(PlaceholderIcon, "FontSize", PlaceholderIcon.FontSize > 0 ? PlaceholderIcon.FontSize : 28, 12, duration, easing));
        sb.Children.Add(Anim(Title, "FontSize", Title.FontSize > 0 ? Title.FontSize : 15, 12, duration, easing));

        var thirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3);
        sb.Children.Add(Anim(Artist, "Height", Artist.ActualHeight > 0 ? Artist.ActualHeight : 18, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(Artist, "Opacity", Artist.Opacity > 0 ? Artist.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(SourceAppRow, "Height", SourceAppRow.ActualHeight > 0 ? SourceAppRow.ActualHeight : 14, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(SourceAppRow, "Opacity", SourceAppRow.Opacity > 0 ? SourceAppRow.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ControlsRow, "Height", ControlsRow.ActualHeight > 0 ? ControlsRow.ActualHeight : 40, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(ControlsRow, "Opacity", ControlsRow.Opacity > 0 ? ControlsRow.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ProgressRow, "Height", ProgressRow.ActualHeight > 0 ? ProgressRow.ActualHeight : 14, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(ProgressRow, "Opacity", ProgressRow.Opacity > 0 ? ProgressRow.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        var twoThirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);
        sb.Children.Add(Anim(Bars, "Opacity", Bars.Opacity >= 0 ? Bars.Opacity : 0, 1, twoThirdDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        sb.Children.Add(Anim(GlowLayer, "Opacity",
            GlowLayer.Opacity > 0 ? GlowLayer.Opacity : 1, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Completed += (_, _) =>
        {
            Title.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            Title.Style = (Style)Resources["TitleCompactStyle"];
            SyncBars();
            _progressTimer.Stop();
        };

        _morphStoryboard = sb;
        sb.Begin();
    }

    #region 节奏光晕 — 读取 AudioLevelMonitor 实时频段能量

    private int _glowTick;
    private float _ambientPhase;

    private void OnGlowTick()
    {
        _glowTick++;

        if (_levelMonitor != null && _levelMonitor.IsRunning)
        {
            float bass = _levelMonitor.Bass;
            float mid = _levelMonitor.Mid;
            float treble = _levelMonitor.Treble;

            _uiBass += (bass - _uiBass) * 0.45f;
            _uiMid += (mid - _uiMid) * 0.40f;
            _uiTreble += (treble - _uiTreble) * 0.38f;
        }
        else
        {
            _ambientPhase += 0.03f;
            float pulse = 0.12f * ((float)Math.Sin(_ambientPhase) * 0.5f + 0.5f);
            float pulse2 = 0.10f * ((float)Math.Sin(_ambientPhase * 0.7f + 1.0f) * 0.5f + 0.5f);
            float pulse3 = 0.08f * ((float)Math.Sin(_ambientPhase * 1.3f + 2.0f) * 0.5f + 0.5f);

            _uiBass = _uiBass * 0.92f + pulse * 0.08f;
            _uiMid = _uiMid * 0.92f + pulse2 * 0.08f;
            _uiTreble = _uiTreble * 0.92f + pulse3 * 0.08f;
        }

        float b = Math.Clamp(_uiBass, 0f, 1f);
        float m = Math.Clamp(_uiMid, 0f, 1f);
        float t = Math.Clamp(_uiTreble, 0f, 1f);

        // 低频：大范围扩散 + 强亮度 punch（鼓点冲击感）
        Glow1Scale.ScaleX = Glow1Scale.ScaleY = 0.50 + b * 1.8;
        Glow1.Opacity = 0.40 + b * 0.60;

        // 中频：中等扩散 + 明亮闪烁
        Glow2Scale.ScaleX = Glow2Scale.ScaleY = 0.45 + m * 1.4;
        Glow2.Opacity = 0.35 + m * 0.55;

        // 高频：快速闪烁（镲片/齿音）
        Glow3Scale.ScaleX = Glow3Scale.ScaleY = 0.40 + t * 1.0;
        Glow3.Opacity = 0.30 + t * 0.50;
    }

    private void SyncGlow()
    {
        bool shouldGlow = _isExpanded && ViewModel.IsPlaying && ViewModel.GlowEnabled;
        AudioLog.Write($"SyncGlow: expanded={_isExpanded}, playing={ViewModel.IsPlaying}, glow={ViewModel.GlowEnabled}, shouldGlow={shouldGlow}, timerRunning={_glowTimer.IsRunning}, monitorRunning={_levelMonitor?.IsRunning}, monitorStatus={_levelMonitor?.Status}");
        if (shouldGlow && !_glowTimer.IsRunning)
            _glowTimer.Start();
        else if (!shouldGlow && _glowTimer.IsRunning)
            _glowTimer.Stop();
    }

    #endregion

    #region 辅助动画方法

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

    #region 音频条动画

    private void BuildBarsAnimation()
    {
        AddBarAnim(Bar1, 4, 13, 320);
        AddBarAnim(Bar2, 6, 15, 260);
        AddBarAnim(Bar3, 5, 11, 380);
    }

    private void AddBarAnim(Microsoft.UI.Xaml.Shapes.Rectangle bar, double from, double to, int ms)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, bar);
        Storyboard.SetTargetProperty(anim, "Height");
        _barsStoryboard.Children.Add(anim);
    }

    private void SyncBars()
    {
        if (ViewModel.IsPlaying && IsLoaded && Artist.Opacity < 0.1)
        {
            if (!_barsRunning)
            {
                _barsStoryboard.Begin();
                _barsRunning = true;
            }
        }
        else
        {
            StopBars();
        }
    }

    private void StopBars()
    {
        if (_barsRunning)
        {
            _barsStoryboard.Stop();
            _barsRunning = false;
        }
    }

    #endregion

    private void Previous_Click(object sender, RoutedEventArgs e) => ViewModel.SkipPrevious?.Invoke();
    private void PlayPause_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePlayPause?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => ViewModel.SkipNext?.Invoke();
    private void PrevSession_Click(object sender, RoutedEventArgs e) => ViewModel.SwitchToPrevSession?.Invoke();
    private void NextSession_Click(object sender, RoutedEventArgs e) => ViewModel.SwitchToNextSession?.Invoke();

    private void SyncProgressTimerActive()
    {
        if (_isExpanded && ViewModel.IsPlaying && ViewModel.HasProgress)
            _progressTimer.Start();
        else
            _progressTimer.Stop();

        UpdateProgressVisual();
    }

    #region 进度条：可点击/拖动跳转

    private bool _dragging;

    private void UpdateProgressVisual()
    {
        if (_dragging) return;

        double width = ProgressArea.ActualWidth;
        if (width <= 0) return;

        ApplyRatio(ViewModel.Progress, width);
        ProgressKnob.Visibility = ViewModel.HasProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyRatio(double ratio, double width)
    {
        double x = Math.Clamp(ratio, 0, 1) * width;
        ProgressFill.Width = x;
        double margin = Math.Clamp(x - ProgressKnob.Width / 2, 0, Math.Max(0, width - ProgressKnob.Width));
        ProgressKnob.Margin = new Thickness(margin, 0, 0, 0);
    }

    /// <summary>进度条自己消化点击：不让它冒泡成「点了岛体」，否则拖一下进度就弹出聚光卡。</summary>
    private void ProgressArea_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void ProgressArea_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasProgress) return;

        _dragging = true;
        ProgressArea.CapturePointer(e.Pointer);
        UpdateDrag(e.GetCurrentPoint(ProgressArea).Position.X);
        e.Handled = true;
    }

    private void ProgressArea_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        UpdateDrag(e.GetCurrentPoint(ProgressArea).Position.X);
        e.Handled = true;
    }

    private void ProgressArea_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        double ratio = UpdateDrag(e.GetCurrentPoint(ProgressArea).Position.X);
        _dragging = false;
        ProgressArea.ReleasePointerCapture(e.Pointer);

        var target = TimeSpan.FromTicks((long)(ViewModel.Duration.Ticks * ratio));
        ViewModel.SeekTo?.Invoke(target);
        PositionText.Text = MediaViewModel.FormatTime(target);
        e.Handled = true;
    }

    private void ProgressArea_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        PositionText.Text = ViewModel.PositionText;
    }

    private double UpdateDrag(double x)
    {
        double width = ProgressArea.ActualWidth;
        if (width <= 0) return 0;

        double ratio = Math.Clamp(x / width, 0, 1);
        ApplyRatio(ratio, width);
        PositionText.Text = MediaViewModel.FormatTime(TimeSpan.FromTicks((long)(ViewModel.Duration.Ticks * ratio)));
        ProgressKnob.Visibility = Visibility.Visible;
        return ratio;
    }

    #endregion
}
