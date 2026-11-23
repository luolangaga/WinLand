using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System.Numerics;
using Windows.Foundation;
using WinIsland.Core;
using WinIsland.Island;

namespace WinIsland.Modules.Media;

public sealed partial class MediaSpotlightView : UserControl
{
    private const double LinePitch = 64;
    private readonly LyricsService? _lyrics;
    private readonly AudioLevelMonitor? _levels;
    private readonly IIslandTheme _theme;
    private readonly List<TextBlock> _lines = [];
    private DispatcherQueueTimer? _tick;
    private CancellationTokenSource? _lyricsCts;
    private string? _lyricsKey;
    private IReadOnlyList<LyricLine>? _lyricLines;
    private int _lineIndex = -1;
    private bool _manualLyrics, _waiting, _dragging;
    private readonly ProgressMotion _progressMotion;
    private readonly System.Diagnostics.Stopwatch _timelineClock = new();

    internal MediaSpotlightView(MediaViewModel viewModel, LyricsService? lyrics, AudioLevelMonitor? levels, IIslandTheme theme)
    {
        ViewModel = viewModel; _lyrics = lyrics; _levels = levels; _theme = theme;
        InitializeComponent();
        _progressMotion = new ProgressMotion(ProgressFill, ProgressKnob);
        ElementCompositionPreview.SetIsTranslationEnabled(LyricsStack, true);
        Motion.Visual(LyricsStack).Properties.InsertVector3("Translation", Vector3.Zero);
        LyricsViewport.SizeChanged += (_, e) =>
        {
            LyricsViewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            if (!_manualLyrics && _lineIndex >= 0) MoveToLine(_lineIndex, false);
        };
        ProgressArea.SizeChanged += (_, _) => UpdateProgressVisual();
        Loaded += (_, _) => { _theme.Changed += OnThemeChanged; ViewModel.PropertyChanged += OnViewModelChanged; OnThemeChanged(); StartTick(); };
        Unloaded += (_, _) => { _theme.Changed -= OnThemeChanged; ViewModel.PropertyChanged -= OnViewModelChanged; OnHostClosed(); };
    }

    public MediaViewModel ViewModel { get; }
    private Brush LyricBrush => new SolidColorBrush(_theme.IsLight
        ? Windows.UI.Color.FromArgb(255, 28, 28, 30) : Microsoft.UI.Colors.White);
    private void OnThemeChanged() { foreach (var line in _lines) line.Foreground = LyricBrush; }
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaViewModel.HasProgress)) ConfigureLyricsMode();
        if (e.PropertyName == nameof(MediaViewModel.LyricsEnabled)) _lyricsKey = null;
    }

    public void OnHostClosed()
    {
        _tick?.Stop(); _timelineClock.Stop(); CancelLyrics(); _lyricsKey = null;
        StopWaiting();
        Motion.Visual(LyricsStack).StopAnimation("Translation");
        foreach (var line in _lines) { Motion.Visual(line).StopAnimation("Scale"); Motion.Visual(line).StopAnimation("Opacity"); }
        BackgroundScene.Configure(ViewModel.BackgroundMode, ViewModel.BackgroundIntensity, false);
    }

    private void StartTick()
    {
        if (_tick == null)
        {
            _tick = DispatcherQueue.CreateTimer(); _tick.Interval = TimeSpan.FromMilliseconds(33);
            _tick.Tick += (_, _) => OnTick();
        }
        _timelineClock.Restart(); ViewModel.RefreshTimeline?.Invoke(); _tick.Start(); OnTick();
    }
    private void OnTick()
    {
        if (ViewModel.IsPlaying && _timelineClock.ElapsedMilliseconds >= 250)
        { ViewModel.RefreshTimeline?.Invoke(); _timelineClock.Restart(); }
        UpdateProgressVisual(); UpdateLyrics();
        BackgroundScene.Configure(ViewModel.BackgroundMode, ViewModel.BackgroundIntensity, ViewModel.IsPlaying && ViewModel.GlowEnabled);
        #if DEBUG
        if (_previewAudio is { } sample) BackgroundScene.SetAudio(sample.X, sample.Y, sample.Z);
        else
#endif
            BackgroundScene.SetAudio(_levels?.Bass ?? 0, _levels?.Mid ?? 0, _levels?.Treble ?? 0);
    }

    private async void UpdateLyrics()
    {
        if (!ViewModel.LyricsEnabled)
        {
            if (_lyricLines != null) ResetLyrics();
            ShowFallback("歌词已关闭"); return;
        }
        var key = $"{ViewModel.Title}\u0001{ViewModel.Artist}";
        if (key != _lyricsKey)
        {
            _lyricsKey = key; ResetLyrics();
            if (string.IsNullOrWhiteSpace(ViewModel.Title)) { ShowFallback("暂无播放内容"); return; }
            if (_lyrics == null) { ShowFallback("这应该不是歌曲。"); return; }
            ShowFallback("正在寻找歌词", true);
            _lyricsCts = new CancellationTokenSource(); var token = _lyricsCts.Token;
            try
            {
                var lines = await _lyrics.FetchAsync(ViewModel.Title, ViewModel.Artist, ViewModel.Duration, token);
                if (token.IsCancellationRequested || key != _lyricsKey || !IsLoaded) return;
                if (lines is { Count: > 0 }) BuildLyrics(lines); else ShowFallback("这应该不是歌曲。");
            }
            catch (OperationCanceledException) { return; }
            catch { if (!token.IsCancellationRequested && key == _lyricsKey && IsLoaded) ShowFallback("这应该不是歌曲。"); }
        }
        if (_lyricLines is not { Count: > 0 }) return;
        if (_manualLyrics) return;
        int index = FindLine(_lyricLines, ViewModel.Position);
        if (index < 0) { ShowFallback("前奏", true); return; }
        ShowLyrics();
        if (index != _lineIndex) MoveToLine(index);
    }

    private static int FindLine(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int index = -1;
        for (int i = 0; i < lines.Count && lines[i].Time <= position; i++) index = i;
        return index;
    }
    private void BuildLyrics(IReadOnlyList<LyricLine> lines)
    {
        _lyricLines = lines; _lineIndex = -1;
        _lines.Clear(); LyricsStack.Children.Clear();
        foreach (var line in lines)
        {
            var block = new TextBlock { Text = line.Text, Height = LinePitch, FontSize = 21,
                FontFamily = new FontFamily("Segoe UI Variable Display"), FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                Foreground = LyricBrush, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            _lines.Add(block); LyricsStack.Children.Add(block);
        }
        ConfigureLyricsMode();
        if (!_manualLyrics)
        {
            var index = FindLine(lines, ViewModel.Position);
            if (index < 0) ShowFallback("前奏", true); else { ShowLyrics(); MoveToLine(index, false); }
        }
    }

    private void ConfigureLyricsMode()
    {
        bool manual = !ViewModel.HasProgress || _lyricLines?.All(l => l.Time < TimeSpan.Zero) == true;
        bool changed = manual != _manualLyrics;
        _manualLyrics = manual;
        LyricsHint.Text = !ViewModel.HasProgress ? "此媒体不支持播放进度，歌词可自由滚动。" : "此歌词没有时间轴，可自由滚动。";
        LyricsHint.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        LyricsScroller.VerticalScrollMode = manual ? ScrollMode.Enabled : ScrollMode.Disabled;
        LyricsScroller.VerticalScrollBarVisibility = manual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        LyricsScroller.IsTabStop = manual;
        if (_lyricLines == null) return;
        if (changed) { LyricsScroller.ChangeView(null, 0, null, true); _lineIndex = -1; }
        if (manual)
        {
            var pose = Motion.Visual(LyricsStack); pose.StopAnimation("Translation"); pose.Properties.InsertVector3("Translation", Vector3.Zero);
            foreach (var line in _lines)
            {
                var visual = Motion.Visual(line); visual.StopAnimation("Opacity"); visual.StopAnimation("Scale");
                visual.Opacity = 1; visual.Scale = Vector3.One; line.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
            ShowLyrics();
        }
    }

    private void MoveToLine(int index, bool animate = true)
    {
        int previous = _lineIndex; _lineIndex = index;
        // Only nearby rows need compositor animations, even for very long songs.
        for (int i = 0; i < _lines.Count; i++)
        {
            int distance = Math.Abs(i - index);
            var visual = Motion.Visual(_lines[i]);
            float opacity = distance switch { 0 => 1, 1 => .48f, 2 => .22f, 3 => .08f, _ => 0 };
            float scale = distance == 0 ? 1.035f : .96f;
            _lines[i].FontWeight = distance == 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            if (distance > 3 && (previous < 0 || Math.Abs(i - previous) > 3)) { visual.Opacity = 0; continue; }
            if (animate && previous >= 0)
            { Motion.Scalar(visual, "Opacity", opacity, 440); Motion.Vector(visual, "Scale", new Vector3(scale, scale, 1), 540); }
            else { visual.StopAnimation("Opacity"); visual.StopAnimation("Scale"); visual.Opacity = opacity; visual.Scale = new Vector3(scale, scale, 1); }
        }
        float target = (float)((LyricsViewport.ActualHeight - LinePitch) / 2 - index * LinePitch);
        var stack = Motion.Visual(LyricsStack);
        if (animate && previous >= 0 && Motion.Enabled)
        {
            using var scroll = stack.Compositor.CreateVector3KeyFrameAnimation();
            scroll.Duration = TimeSpan.FromMilliseconds(680);
            scroll.InsertExpressionKeyFrame(0, "this.StartingValue");
            scroll.InsertKeyFrame(1, new Vector3(0, target, 0), stack.Compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, 1), new Vector2(.3f, 1)));
            stack.StartAnimation("Translation", scroll);
        }
        else { stack.StopAnimation("Translation"); stack.Properties.InsertVector3("Translation", new Vector3(0, target, 0)); }
    }
    private void ResetLyrics()
    {
        CancelLyrics(); _lyricLines = null; _lineIndex = -1; _lines.Clear(); LyricsStack.Children.Clear();
        var pose = Motion.Visual(LyricsStack); pose.StopAnimation("Translation"); pose.Properties.InsertVector3("Translation", Vector3.Zero);
        ConfigureLyricsMode();
    }
    private void CancelLyrics() { _lyricsCts?.Cancel(); _lyricsCts?.Dispose(); _lyricsCts = null; }
    private void ShowLyrics() { StopWaiting(); LyricsViewport.Visibility = Visibility.Visible; LyricsFallback.Visibility = Visibility.Collapsed; }
    private void ShowFallback(string message, bool waiting = false)
    {
        LyricsStatus.Text = message; LyricsFallback.Visibility = Visibility.Visible; LyricsViewport.Visibility = Visibility.Collapsed;
        WaitingDots.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        if (waiting && !_waiting) StartWaiting(); else if (!waiting) StopWaiting();
    }
    private void StartWaiting()
    {
        _waiting = true;
        var dots = new[] { WaitDot0, WaitDot1, WaitDot2 };
        for (int i = 0; i < dots.Length; i++)
        {
            var visual = Motion.Visual(dots[i]);
            if (!Motion.Enabled) { visual.Opacity = .6f; continue; }
            using var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
            pulse.Duration = TimeSpan.FromMilliseconds(1200); pulse.DelayTime = TimeSpan.FromMilliseconds(i * 160);
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            pulse.InsertKeyFrame(0, .18f); pulse.InsertKeyFrame(.4f, 1, Motion.Ease(visual.Compositor)); pulse.InsertKeyFrame(1, .18f, Motion.Ease(visual.Compositor));
            visual.StartAnimation("Opacity", pulse);
        }
    }
    private void StopWaiting()
    {
        if (!_waiting) return;
        _waiting = false;
        foreach (var dot in new[] { WaitDot0, WaitDot1, WaitDot2 }) { Motion.Visual(dot).StopAnimation("Opacity"); Motion.Visual(dot).Opacity = .6f; }
    }
#if DEBUG
    private Vector3? _previewAudio;
    internal void PreviewAudio(float bass, float mid, float treble) => _previewAudio = new Vector3(bass, mid, treble);
    internal void PreviewLyrics(IReadOnlyList<LyricLine>? lines)
    {
        _lyricsKey = $"{ViewModel.Title}\u0001{ViewModel.Artist}";
        ResetLyrics();
        if (lines is { Count: > 0 }) BuildLyrics(lines); else ShowFallback("这应该不是歌曲。");
    }
#endif

    // ---- 进度与拖动 ----

    private void UpdateProgressVisual()
    {
        if (_dragging) return;

        double width = ProgressArea.ActualWidth;
        if (width <= 0) return;

        ApplyRatio(ViewModel.Progress, width);
        ProgressKnob.Visibility = ViewModel.HasProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyRatio(double ratio, double width) =>
        _progressMotion.Set(ratio, width, _dragging ? 1 : 100);


    private void ProgressArea_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasProgress) return;

        _dragging = true;
        ProgressArea.CapturePointer(e.Pointer);
        UpdateDrag(ProgressArea.ActualWidth > 0 ? e.GetCurrentPoint(ProgressArea).Position.X : 0);
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

    // ---- 播控 ----

    private void Previous_Click(object sender, RoutedEventArgs e) => ViewModel.SkipPrevious?.Invoke();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePlayPause?.Invoke();

    private void Next_Click(object sender, RoutedEventArgs e) => ViewModel.SkipNext?.Invoke();

    private void PrevSession_Click(object sender, RoutedEventArgs e) => ViewModel.SwitchToPrevSession?.Invoke();

    private void NextSession_Click(object sender, RoutedEventArgs e) => ViewModel.SwitchToNextSession?.Invoke();
}
