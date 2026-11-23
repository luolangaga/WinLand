using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinIsland.Core;
using WinIsland.Island;
using System.Numerics;

namespace WinIsland.Modules.Media;

/// <summary>
/// 统一变形视图：单个 UserControl 同时承载紧凑态与展开态的所有元素。
/// 实现 IMorphView，由主程序在展开/收起时调用，与岛体尺寸动画同步。
/// 背景读取真实音频三频段，组合表达式负责帧间插值，不重排布局。
/// </summary>
public sealed partial class MediaIslandView : UserControl, IMorphView
{
    private readonly MorphProgress _morph;
    private bool _isExpanded;

    private readonly AudioLevelMonitor? _levelMonitor;
    private readonly DispatcherQueueTimer _audioTimer;
    private readonly DispatcherQueueTimer _progressTimer;

    internal MediaViewModel ViewModel { get; }

    UIElement IMorphView.View => this;

    internal MediaIslandView(MediaViewModel vm, AudioLevelMonitor? levelMonitor)
    {
        ViewModel = vm;
        _levelMonitor = levelMonitor;
        InitializeComponent();
        _progressMotion = new ProgressMotion(ProgressFill, ProgressKnob);
        BuildBarsAnimation();

        _morph = new MorphProgress(ApplyMorph);
        Loaded += (_, _) => { SyncBars(); SyncGlow(); SyncProgressTimerActive(); };
        Unloaded += (_, _) => { StopBars(); _audioTimer?.Stop(); _progressTimer?.Stop(); _morph.Complete(); };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaViewModel.IsPlaying)) SyncBars();
            if (e.PropertyName is nameof(MediaViewModel.IsPlaying) or nameof(MediaViewModel.GlowEnabled) or nameof(MediaViewModel.WaveEnabled) or nameof(MediaViewModel.BackgroundMode) or nameof(MediaViewModel.BackgroundIntensity))
            {
                SyncBars();
                SyncGlow();
                SyncProgressTimerActive();
            }
            if (e.PropertyName is nameof(MediaViewModel.Progress) or nameof(MediaViewModel.HasProgress))
                UpdateProgressVisual();
        };

        ProgressArea.SizeChanged += (_, _) => UpdateProgressVisual();

        _audioTimer = DispatcherQueue.CreateTimer();
        _audioTimer.Interval = TimeSpan.FromMilliseconds(50);
        _audioTimer.Tick += (_, _) => OnGlowTick();

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

    public void AnimateToExpanded(TimeSpan duration) => SetExpanded(true, duration);
    public void AnimateToCompact(TimeSpan duration) => SetExpanded(false, duration);

    private void SetExpanded(bool expanded, TimeSpan duration)
    {
        _isExpanded = expanded;
        Title.FontWeight = expanded ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        Title.Style = (Style)Resources[expanded ? "TitleExpandedStyle" : "TitleCompactStyle"];
        _morph.To(expanded ? 1 : 0, duration);
        SyncBars();
        SyncGlow();
        SyncProgressTimerActive();
    }

    private void ApplyMorph(double p)
    {
        // A single continuous progress replaces independent clocks and midpoint Padding jumps.
        RootGrid.Padding = new Thickness(8 + 8 * p, 10 * p, 14 + 2 * p, 8 * p);
        MainGrid.ColumnSpacing = 10 + 4 * p;
        Cover.Width = Cover.Height = 26 + 38 * p;
        CoverArt.ArtworkRadius = 7 + 7 * p;
        CoverArt.PlaceholderSize = 12 + 14 * p;
        Title.FontSize = 12 + 4 * p;
        Artist.Height = 18 * p;
        SourceAppRow.Height = 14 * p;
        ControlsRow.Height = 40 * p;
        ProgressRow.Height = 14 * p;
        Artist.Opacity = SourceAppRow.Opacity = ControlsRow.Opacity = ProgressRow.Opacity = p;
        Bars.Opacity = 1 - p;
        GlowLayer.Opacity = ViewModel.GlowEnabled ? p : 0;
    }

    #region 节奏光晕 — 读取 AudioLevelMonitor 实时频段能量

    private void OnGlowTick()
    {
        float bass = _levelMonitor?.Bass ?? 0, mid = _levelMonitor?.Mid ?? 0, treble = _levelMonitor?.Treble ?? 0;
#if DEBUG
        if (_previewBands is { } sample) { bass = sample.X; mid = sample.Y; treble = sample.Z; }
#endif
        BackgroundScene.SetAudio(bass, mid, treble);

    }
#if DEBUG
    private Vector3? _previewBands;
    internal void PreviewAudio(Vector3? bands) => _previewBands = bands;
#endif
    private void SyncGlow()
    {
        bool active = IsLoaded && _isExpanded && ViewModel.IsPlaying && ViewModel.GlowEnabled;
        BackgroundScene.Configure(ViewModel.BackgroundMode, ViewModel.BackgroundIntensity, active);
        if (IsLoaded && ViewModel.IsPlaying && Motion.Enabled &&
            _isExpanded && ViewModel.GlowEnabled && ViewModel.BackgroundMode != "off") _audioTimer.Start();
        else _audioTimer.Stop();
    }

    #endregion

    #region 音频条动画
    // Original 1.0 animation (0d9f1d7): animate height so rounded caps retain their shape.
    private readonly Storyboard _barsStoryboard = new();
    private bool _barsRunning;
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
            From = from, To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, bar);
        Storyboard.SetTargetProperty(anim, "Height");
        _barsStoryboard.Children.Add(anim);
    }
    private void StartPlaybackWave()
    {
        if (_barsRunning) return;
        _barsStoryboard.Begin();
        _barsRunning = true;
    }
    private void SyncBars()
    {
        if (IsLoaded && ViewModel.IsPlaying && Motion.Enabled && ViewModel.WaveEnabled && !_isExpanded)
            StartPlaybackWave();
        else StopBars();
    }
    private void StopBars()
    {
        if (!_barsRunning) return;
        _barsStoryboard.Stop();
        _barsRunning = false;
    }
    #endregion

    private void Previous_Click(object sender, RoutedEventArgs e) => ViewModel.SkipPrevious?.Invoke();
    private void PlayPause_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePlayPause?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => ViewModel.SkipNext?.Invoke();
    private void PrevSession_Click(object sender, RoutedEventArgs e) => ViewModel.SwitchToPrevSession?.Invoke();
    private void NextSession_Click(object sender, RoutedEventArgs e) => ViewModel.SwitchToNextSession?.Invoke();

    private void SyncProgressTimerActive()
    {
        if (IsLoaded && _isExpanded && ViewModel.IsPlaying && ViewModel.HasProgress)
            _progressTimer.Start();
        else
            _progressTimer.Stop();

        UpdateProgressVisual();
    }

    #region 进度条：可点击/拖动跳转

    private readonly ProgressMotion _progressMotion;
    private bool _dragging;

    private void UpdateProgressVisual()
    {
        if (_dragging) return;

        double width = ProgressArea.ActualWidth;
        if (width <= 0) return;

        ApplyRatio(ViewModel.Progress, width);
        ProgressKnob.Visibility = ViewModel.HasProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyRatio(double ratio, double width) =>
        _progressMotion.Set(ratio, width, _dragging ? 1 : 240);


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
