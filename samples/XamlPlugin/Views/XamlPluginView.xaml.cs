using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace XamlPlugin.Views;

public sealed partial class XamlPluginView : UserControl, IMorphView
{
    private const double CompactSize = 12;
    private const double ExpandedSize = 44;

    private readonly DispatcherQueueTimer _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;

    public XamlPluginView()
    {
        PluginXaml.Load(this);

        _morphTimer = DispatcherQueue.CreateTimer();
        _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
        _morphTimer.IsRepeating = true;
        _morphTimer.Tick += (_, _) => OnMorphTick();
    }

    public UIElement View => this;

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    private void StartMorph(double target, TimeSpan duration)
    {
        _morphFrom = CurrentProgress;
        _morphTarget = target;
        _morphDuration = duration;
        _morphStart = DateTimeOffset.UtcNow;
        _morphTimer.Start();
    }

    private double CurrentProgress { get; set; }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        Apply(_morphFrom + (_morphTarget - _morphFrom) * eased);
        if (t >= 1)
        {
            Apply(_morphTarget);
            _morphTimer.Stop();
        }
    }

    private void Apply(double progress)
    {
        CurrentProgress = progress;
        var size = CompactSize + (ExpandedSize - CompactSize) * progress;
        Dot.Width = size;
        Dot.Height = size;
        Details.Height = ExpandedSize * progress;
        Details.Opacity = progress;
    }
}
