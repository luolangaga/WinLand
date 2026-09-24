using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinIsland.Core;

namespace WinIsland.Modules.Media;

/// <summary>
/// 音乐的「超级展开」聚光卡：大封面 + 逐行高亮滚动的歌词流 + 可拖动进度与播控。
/// 这是独立于岛视图的第二棵可视树（给覆盖窗用），生命周期跟着 Loaded/Unloaded 走 ——
/// 宿主收起卡片时会把内容卸下，这里随之停表、取消歌词请求。
/// </summary>
public sealed partial class MediaSpotlightView : UserControl
{
    /// <summary>每行歌词占的高度（固定值：长栈滚动靠平移，行高必须恒定）。</summary>
    private const double LinePitch = 52;
    /// <summary>当前行两侧各显示几行（共 5 行可见）。</summary>
    private const int VisibleRadius = 2;
    private static readonly double[] EmphasisByDistance = { 1.0, 0.5, 0.26 };

    private readonly LyricsService? _lyrics;
    private readonly AudioLevelMonitor? _levels;
    private readonly IIslandTheme _theme;
    private readonly List<TextBlock> _lines = new();
    private readonly Rectangle[] _bars;

    private DispatcherQueueTimer? _tick;
    private CancellationTokenSource? _lyricsCts;
    private string? _lyricsKey;
    private IReadOnlyList<LyricLine>? _lyricLines;
    private int _lineIndex = -1;
    private double _shiftY;
    private Storyboard? _scrollStoryboard;
    private bool _dragging;

    internal MediaSpotlightView(MediaViewModel viewModel, LyricsService? lyrics, AudioLevelMonitor? levels, IIslandTheme theme)
    {
        ViewModel = viewModel;
        _lyrics = lyrics;
        _levels = levels;
        _theme = theme;
        InitializeComponent();

        // 歌词行是代码建的，颜色烘在画刷里：主题一变就重刷一遍。
        // 视图活得跟插件一样久，主题对象也是，所以这里不需要退订。
        _theme.Changed += OnThemeChanged;

        _bars = new[] { EqBar0, EqBar1, EqBar2, EqBar3, EqBar4 };

        LyricsViewport.SizeChanged += (_, e) =>
        {
            LyricsViewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            UpdateProgressVisual();
        };

        Loaded += (_, _) => StartTick();
        Unloaded += (_, _) => StopTick();
    }

    public MediaViewModel ViewModel { get; }

    /// <summary>
    /// 歌词行的中性色：跟着岛体主题走 —— 浅色卡片（Fluent + 浅色系统）上再用白字就等于没写。
    /// 明暗两档与宿主的次级文字同一档。
    /// </summary>
    private Brush LyricBrush => new SolidColorBrush(_theme.IsLight
        ? Windows.UI.Color.FromArgb(255, 28, 28, 30)
        : Microsoft.UI.Colors.White);

    private void OnThemeChanged()
    {
        foreach (var line in _lines)
        {
            line.Foreground = LyricBrush;
        }
    }

    /// <summary>宿主收起卡片时调用：停表并取消还没回来的歌词请求。</summary>
    public void OnHostClosed()
    {
        StopTick();
        CancelLyrics();
        // 关闭时可能正好打断了取词请求：清掉键值，下次打开重新取
        _lyricsKey = null;
    }

    // ---- 定时刷新：进度 / 歌词行 / 音浪 ----

    private void StartTick()
    {
        if (_tick == null)
        {
            _tick = DispatcherQueue.CreateTimer();
            _tick.Interval = TimeSpan.FromMilliseconds(90);
            _tick.IsRepeating = true;
            _tick.Tick += (_, _) => OnTick();
        }

        _tick.Start();
    }

    private void StopTick() => _tick?.Stop();

    private void OnTick()
    {
        UpdateProgressVisual();
        UpdateLyrics();
        UpdateEqualizer();
    }

    // ---- 歌词 ----

    private async void UpdateLyrics()
    {
        if (!ViewModel.LyricsEnabled)
        {
            ResetLyrics();
            ShowFallback(_lyrics == null ? "未找到歌词" : "歌词已关闭");
            return;
        }

        var key = $"{ViewModel.Title}\u0001{ViewModel.Artist}";
        if (key != _lyricsKey)
        {
            _lyricsKey = key;
            ResetLyrics();

            if (string.IsNullOrWhiteSpace(ViewModel.Title))
            {
                ShowFallback("暂无播放内容");
                return;
            }

            ShowFallback(_lyrics == null ? "未找到歌词" : "正在获取歌词…");
            if (_lyrics == null) return;

            _lyricsCts = new CancellationTokenSource();
            var token = _lyricsCts.Token;
            try
            {
                var lines = await _lyrics.FetchAsync(ViewModel.Title, ViewModel.Artist, ViewModel.Duration, token);
                if (token.IsCancellationRequested || key != _lyricsKey) return;

                if (lines is { Count: > 0 })
                {
                    BuildLyrics(lines);
                }
                else
                {
                    ShowFallback("未找到歌词");
                }
            }
            catch
            {
                ShowFallback("未找到歌词");
            }
        }

        if (_lyricLines is not { Count: > 0 }) return;

        var index = FindLine(_lyricLines, ViewModel.Position);
        if (index != _lineIndex) MoveToLine(index);
    }

    private static int FindLine(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int index = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time > position) break;
            index = i;
        }

        return index;
    }

    private void BuildLyrics(IReadOnlyList<LyricLine> lines)
    {
        _lyricLines = lines;
        _lineIndex = -1;
        _shiftY = 0;
        _scrollStoryboard?.Stop();
        _scrollStoryboard = null;
        LyricsShift.Y = 0;

        _lines.Clear();
        LyricsStack.Children.Clear();
        foreach (var line in lines)
        {
            var block = new TextBlock
            {
                Text = line.Text,
                Height = LinePitch,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                Foreground = LyricBrush,
                MaxLines = 2,
                Opacity = 0,
                TextAlignment = TextAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _lines.Add(block);
            LyricsStack.Children.Add(block);
        }

        LyricsViewport.Visibility = Visibility.Visible;
        LyricsFallback.Visibility = Visibility.Collapsed;
        MoveToLine(FindLine(lines, ViewModel.Position), animate: false);
    }

    private void MoveToLine(int index, bool animate = true)
    {
        int previous = _lineIndex;
        _lineIndex = index;

        for (int i = 0; i < _lines.Count; i++)
        {
            int distance = Math.Abs(i - index);
            if (distance > VisibleRadius)
            {
                if (_lines[i].Opacity > 0) _lines[i].Opacity = 0;
                continue;
            }

            _lines[i].FontSize = distance == 0 ? 20 : 16;
            _lines[i].FontWeight = distance == 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            FadeTo(_lines[i], EmphasisByDistance[distance]);
        }

        // 当前行停在第 3 行（视口内半径 2），整条歌词栈靠平移滚动
        AnimateScroll(-(index - VisibleRadius) * LinePitch, animate && previous >= 0);
    }

    private void ResetLyrics()
    {
        CancelLyrics();
        _lyricLines = null;
        _lines.Clear();
        LyricsStack.Children.Clear();
        _lineIndex = -1;
        _shiftY = 0;
        _scrollStoryboard?.Stop();
        _scrollStoryboard = null;
        LyricsShift.Y = 0;
    }

    private void CancelLyrics()
    {
        _lyricsCts?.Cancel();
        _lyricsCts?.Dispose();
        _lyricsCts = null;
    }

    private void ShowFallback(string message)
    {
        LyricsFallback.Visibility = Visibility.Visible;
        LyricsViewport.Visibility = Visibility.Collapsed;
        LyricsStatus.Text = message;
    }

    private void AnimateScroll(double target, bool animate)
    {
        if (!animate)
        {
            _scrollStoryboard?.Stop();
            _scrollStoryboard = null;
            _shiftY = target;
            LyricsShift.Y = target;
            return;
        }

        var animation = new DoubleAnimation
        {
            From = _shiftY,
            To = target,
            Duration = new Duration(TimeSpan.FromSeconds(Math.Min(0.65, 0.22 + Math.Abs(target - _shiftY) / 420))),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, LyricsShift);
        Storyboard.SetTargetProperty(animation, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        _scrollStoryboard = storyboard;
        _shiftY = target;
        storyboard.Begin();
    }

    /// <summary>
    /// 歌词行淡到目标不透明度。先把基值写成目标值，动画用 FillBehavior.Stop ——
    /// 动画结束那刻基值正好接管，既没有跳变，也不会留下"动画保持"挡住后续赋值。
    /// </summary>
    private static void FadeTo(UIElement element, double target)
    {
        var from = element.Opacity;
        if (Math.Abs(from - target) < 0.001) return;

        element.Opacity = target;

        var animation = new DoubleAnimation
        {
            From = from,
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ---- 无歌词时的音浪 ----

    private void UpdateEqualizer()
    {
        if (LyricsFallback.Visibility != Visibility.Visible) return;

        bool playing = ViewModel.IsPlaying;
        float bass = playing ? _levels?.Bass ?? 0f : 0f;
        float mid = playing ? _levels?.Mid ?? 0f : 0f;
        float treble = playing ? _levels?.Treble ?? 0f : 0f;

        SetBar(EqBar0, 0.34f + bass * 1.7f);
        SetBar(EqBar1, 0.30f + mid * 1.6f);
        SetBar(EqBar2, 0.42f + Math.Max(mid, treble) * 1.5f);
        SetBar(EqBar3, 0.30f + mid * 1.6f);
        SetBar(EqBar4, 0.34f + treble * 1.9f);
    }

    private void SetBar(Rectangle bar, float level)
    {
        double target = Math.Clamp(8 + Math.Clamp(level, 0f, 1f) * 52, 8, 62);
        bar.Height += (target - bar.Height) * 0.35;     // 每帧向目标靠拢，看上去是连续的律动
    }

    // ---- 进度与拖动 ----

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
