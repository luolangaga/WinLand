using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace WinIsland.Modules.Media;

public sealed partial class MediaCompactView : UserControl
{
    private readonly Storyboard _barsStoryboard = new();
    private bool _barsRunning;

    internal MediaViewModel ViewModel { get; }

    public MediaCompactView(MediaViewModel vm)
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
        if (ViewModel.IsPlaying && IsLoaded)
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
}
