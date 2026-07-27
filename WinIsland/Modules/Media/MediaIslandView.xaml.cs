using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using WinIsland.Core;

namespace WinIsland.Modules.Media;

/// <summary>
/// 统一变形视图：单个 UserControl 同时承载紧凑态与展开态的所有元素。
/// 实现 IMorphView，由主程序在展开/收起时调用，与岛体尺寸动画同步。
/// </summary>
public sealed partial class MediaIslandView : UserControl, IMorphView
{
    private readonly Storyboard _barsStoryboard = new();
    private bool _barsRunning;
    private Storyboard? _morphStoryboard;

    internal MediaViewModel ViewModel { get; }

    UIElement IMorphView.View => this;

    public MediaIslandView(MediaViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();

        BuildBarsAnimation();
        Loaded += (_, _) => SyncBars();
        Unloaded += (_, _) => StopBars();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaViewModel.IsPlaying)) SyncBars();
        };
    }

    public void AnimateToExpanded(TimeSpan duration)
    {
        _morphStoryboard?.Stop();

         // 立即设置非动画属性
    RootGrid.Padding = new Thickness(20, 16, 20, 12);
    MainGrid.ColumnSpacing = 14;
    Cover.CornerRadius = new CornerRadius(14);
    
    Title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        Title.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        StopBars();

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };

        // 封面放大
        sb.Children.Add(Anim(Cover, "Width", 26, 72, duration, easing));
        sb.Children.Add(Anim(Cover, "Height", 26, 72, duration, easing));
        sb.Children.Add(Anim(PlaceholderIcon, "FontSize", 12, 28, duration, easing));

        // 标题字号放大
        sb.Children.Add(Anim(Title, "FontSize", 12, 15, duration, easing));

        // 艺术家：高度 + 透明度
        var halfDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4);
        sb.Children.Add(Anim(Artist, "Height", 0, 20, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.2)));
        sb.Children.Add(Anim(Artist, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.2)));

        // 音频条淡出
        sb.Children.Add(Anim(Bars, "Opacity", 1, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        // 播控：高度 + 透明度
        sb.Children.Add(Anim(Controls, "Height", 0, 48, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        sb.Children.Add(Anim(Controls, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        _morphStoryboard = sb;
        sb.Begin();
    }

    public void AnimateToCompact(TimeSpan duration)
    {
        _morphStoryboard?.Stop();

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };

        // 封面缩小
        sb.Children.Add(Anim(Cover, "Width", Cover.ActualWidth > 0 ? Cover.ActualWidth : 72, 26, duration, easing));
        sb.Children.Add(Anim(Cover, "Height", Cover.ActualHeight > 0 ? Cover.ActualHeight : 72, 26, duration, easing));
        sb.Children.Add(Anim(PlaceholderIcon, "FontSize", PlaceholderIcon.FontSize > 0 ? PlaceholderIcon.FontSize : 28, 12, duration, easing));

        // 标题字号缩小
        sb.Children.Add(Anim(Title, "FontSize", Title.FontSize > 0 ? Title.FontSize : 15, 12, duration, easing));

        // 艺术家：高度 + 透明度（先收）
        var thirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3);
        sb.Children.Add(Anim(Artist, "Height", Artist.ActualHeight > 0 ? Artist.ActualHeight : 20, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(Artist, "Opacity", Artist.Opacity > 0 ? Artist.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        // 播控：高度 + 透明度（先收）
        sb.Children.Add(Anim(Controls, "Height", Controls.ActualHeight > 0 ? Controls.ActualHeight : 48, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(Controls, "Opacity", Controls.Opacity > 0 ? Controls.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        // 音频条淡入（后出）
        var twoThirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);
        sb.Children.Add(Anim(Bars, "Opacity", Bars.Opacity >= 0 ? Bars.Opacity : 0, 1, twoThirdDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        sb.Completed += (_, _) =>
        {
            // 动画完成后设置非动画属性
            RootGrid.Padding = new Thickness(8, 0, 14, 0);
            MainGrid.ColumnSpacing = 10;
            Cover.CornerRadius = new CornerRadius(7);
            Title.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            Title.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
            SyncBars();
        };

        _morphStoryboard = sb;
        sb.Begin();
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

    private void Previous_Click(object sender, RoutedEventArgs e) => ViewModel.SkipPrevious?.Invoke();
    private void PlayPause_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePlayPause?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => ViewModel.SkipNext?.Invoke();
}
