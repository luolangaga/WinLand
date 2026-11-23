using AgentHub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;

namespace AgentHub;

internal static class HubCharts
{
    private static readonly Windows.UI.ViewManagement.UISettings Preferences = new();
    internal static bool Animate => Preferences.AnimationsEnabled;
    internal static string Short(long n) => n >= 1000000 ? $"{n / 1000000.0:F1}M" : n >= 1000 ? $"{n / 1000.0:F1}k" : n.ToString("N0");
    internal static void Reveal(UIElement element, int direction)
    {
        if (!Animate || direction == 0) return; // Data refresh is never an entrance animation.
        var v = ElementCompositionPreview.GetElementVisual(element); ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        using var opacity = v.Compositor.CreateScalarKeyFrameAnimation(); opacity.Duration = TimeSpan.FromMilliseconds(220); opacity.InsertKeyFrame(0, 0); opacity.InsertKeyFrame(1, 1); v.StartAnimation("Opacity", opacity);
        using var slide = v.Compositor.CreateVector3KeyFrameAnimation(); slide.Duration = TimeSpan.FromMilliseconds(240);
        slide.InsertKeyFrame(0, new Vector3(direction * 14, 0, 0)); slide.InsertKeyFrame(1, Vector3.Zero, v.Compositor.CreateCubicBezierEasingFunction(new(.2f, 0), new(.2f, 1))); v.StartAnimation("Translation", slide);
    }
    // Retarget from the displayed values, never zero. Loaded does not replay animation.
    private sealed class Transition
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
        private readonly System.Diagnostics.Stopwatch _clock = new();
        private readonly Action<double[]> _draw;
        private double[] _current = [], _from = [], _target = [];
        public Transition(FrameworkElement owner, Action<double[]> draw)
        {
            _draw = draw; _timer = owner.DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += (_, _) => { try { double t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / 350, 0, 1), eased = 1 - Math.Pow(1 - t, 3);
                for (int i = 0; i < _current.Length; i++) _current[i] = _from[i] + (_target[i] - _from[i]) * eased;
                _draw(_current); if (t >= 1) _timer.Stop(); } catch { _timer.Stop(); } };
            owner.Unloaded += (_, _) => { _timer.Stop(); _current = (double[])_target.Clone(); _draw(_current); };
        }
        public void To(params double[] target)
        {
            if (_target.SequenceEqual(target)) return;
            _timer.Stop(); _target = (double[])target.Clone();
            if (_current.Length != target.Length || !Animate) { _current = (double[])target.Clone(); _draw(_current); return; }
            _from = (double[])_current.Clone(); _clock.Restart(); _timer.Start();
        }
    }
    internal sealed class RingChart : Grid
    {
        private readonly HubPalette _palette;
        private readonly Microsoft.UI.Xaml.Shapes.Path _arc;
        private readonly TextBlock _label;
        private readonly Transition _transition;
        private readonly double _size;
        public RingChart(Quota q, HubPalette p, double size)
        {
            _palette = p; _size = size; Width = Height = size;
            Children.Add(new Ellipse { Stroke = p.Line, StrokeThickness = 4, Margin = new(3) });
            _arc = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }; Children.Add(_arc);
            var labels = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _label = p.Label("", 15); _label.HorizontalAlignment = HorizontalAlignment.Center; labels.Children.Add(_label);
            var caption = p.Label("已用", 8, true); caption.HorizontalAlignment = HorizontalAlignment.Center; labels.Children.Add(caption); Children.Add(labels);
            _transition = new(this, values => Draw(values[0])); Update(q);
        }
        public void Update(Quota q)
        {
            _arc.Stroke = q.UsedPercent >= 90 ? _palette.Coral : q.UsedPercent >= 70 ? _palette.Amber : _palette.Mint;
            _label.Text = $"{q.UsedPercent:F0}%";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, q.Agent + $" 已使用 {q.UsedPercent:F0}%");
            _transition.To(Math.Clamp(q.UsedPercent, 0, 100));
        }
        private void Draw(double value)
        {
            double percent = Math.Clamp(value, 0, 99.999), radius = _size / 2 - 5, c = _size / 2, angle = percent * Math.PI * 2 / 100;
            var figure = new PathFigure { StartPoint = new(c, c - radius) };
            figure.Segments.Add(new ArcSegment { Point = new(c + radius * Math.Sin(angle), c - radius * Math.Cos(angle)), Size = new(radius, radius), IsLargeArc = percent > 50, SweepDirection = SweepDirection.Clockwise });
            var geometry = new PathGeometry(); geometry.Figures.Add(figure); _arc.Data = geometry; _arc.Opacity = percent > .05 ? 1 : 0;
        }
    }
    internal sealed class HistogramChart : Grid
    {
        private readonly HubPalette _palette;
        private readonly Border[] _bars = new Border[24];
        private readonly Transition _transition;
        public HistogramChart(IEnumerable<UsageEvent> source, HubPalette p, double height)
        {
            _palette = p; Height = height; ColumnSpacing = 4;
            for (int i = 0; i < 24; i++) { ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); _bars[i] = new Border { CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Bottom }; SetColumn(_bars[i], i); Children.Add(_bars[i]); }
            _transition = new(this, values => { for (int i = 0; i < values.Length; i++) _bars[i].Height = Math.Max(3, values[i]); }); Update(source);
        }
        public void Update(IEnumerable<UsageEvent> source)
        {
            var events = source.ToArray(); var now = DateTimeOffset.UtcNow;
            var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            var counts = Enumerable.Range(0, 24).Select(i => events.Where(e => e.Time >= hour.AddHours(i - 23) && e.Time < hour.AddHours(i - 22)).Sum(e => e.Input + e.Output + e.Cache)).ToArray();
            long max = Math.Max(1, counts.Max());
            for (int i = 0; i < 24; i++) { _bars[i].Background = counts[i] > 0 ? _palette.Mint : _palette.Line; ToolTipService.SetToolTip(_bars[i], hour.AddHours(i - 23).ToLocalTime().ToString("HH:mm") + $" · {counts[i]:N0} tokens"); }
            _transition.To(counts.Select(n => Math.Max(3, n / (double)max * Height)).ToArray());
        }
    }
    internal sealed class ProgressChart : ProgressBar
    {
        private readonly Transition _transition;
        public ProgressChart(double value, Brush color, Brush track)
        { Maximum = 100; Height = 4; Foreground = color; Background = track; VerticalAlignment = VerticalAlignment.Center; _transition = new(this, values => Value = values[0]); Update(value); }
        public void Update(double value) => _transition.To(Math.Clamp(value, 0, 100));
    }
    internal static RingChart Ring(Quota q, HubPalette p, double size = 62) => new(q, p, size);
    internal static HistogramChart Histogram(IEnumerable<UsageEvent> source, HubPalette p, double height = 48) => new(source, p, height);
    internal static ProgressChart Progress(double value, Brush color, Brush track) => new(value, color, track);
}
