using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace WinIsland.Island;

/// <summary>Audio-driven music visuals and charging flow. All motion stays inside the painted card.</summary>
public sealed class AmbientBackdrop : UserControl
{
    private readonly Canvas _scene = new() { IsHitTestVisible = false };
    private readonly List<FrameworkElement> _parts = [];
    private readonly List<Ellipse> _beatRings = [];
    private CompositionPropertySet? _state;
    private string _mode = "off";
    private bool _active, _charging;
    private double _intensity = .35;
    private float _bass, _mid, _treble;
    private long _lastBeat;
    private int _ringIndex;
    public double TargetX { get; set; } = .90;
    public double TargetY { get; set; } = .24;

    public AmbientBackdrop()
    {
        IsHitTestVisible = false; Content = _scene;
        SizeChanged += (_, e) =>
        {
            _scene.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            SetSize();
            if (_active && _parts.Count == 0) Build();
        };
        Unloaded += (_, _) => Stop();
        ActualThemeChanged += (_, _) => { if (_active) Build(); UpdateOpacity(); };
    }

    internal static string ResolveMode(string mode, bool charging) => (mode, charging) switch
    {
        ("off", _) => "off",
        ("beam" or "circuit" or "chargepulse", true) => mode,
        ("spectrum" or "wave" or "bass", false) => mode,
        ("breathe", true) => "chargepulse", ("orbit", true) => "circuit",
        ("ripple" or "breathe", false) => "bass", ("orbit", false) => "wave",
        (_, true) => "beam", _ => "spectrum",
    };

    public void Configure(string mode, double intensity, bool active, bool charging = false, double watts = 0)
    {
        mode = ResolveMode(mode, charging);
        active &= IsLoaded && Motion.Enabled && mode != "off";
        bool rebuild = mode != _mode || charging != _charging || active != _active;
        bool changed = rebuild || Math.Abs(intensity - _intensity) > .001;
        _mode = mode; _charging = charging; _active = active; _intensity = Math.Clamp(intensity, .1, .8);
        if (rebuild) { if (active) Build(); else Stop(); }
        // Power adjusts the energy brightness, without changing a particle's position mid-flight.
        _state?.InsertScalar("Power", watts > 0 ? (float)Math.Clamp(watts / 65, .2, 1.5) : .45f);
        if (changed) UpdateOpacity();
    }

    public void SetAudio(float bass, float mid, float treble)
    {
        if (!_active || _charging || _state == null) return;
        bass = Math.Clamp(bass, 0, 1); mid = Math.Clamp(mid, 0, 1); treble = Math.Clamp(treble, 0, 1);
        SetBand("Bass", bass, ref _bass); SetBand("Mid", mid, ref _mid); SetBand("Treble", treble, ref _treble);
        if (_mode == "bass" && bass > .30f && bass - _previousBass > .085f && Environment.TickCount64 - _lastBeat > 210)
        {
            _lastBeat = Environment.TickCount64; FireBeat(bass);
        }
        _previousBass = bass;
    }
    private float _previousBass;
    private void SetBand(string name, float value, ref float previous)
    {
        if (Math.Abs(previous - value) < .003f) return;
        Motion.Scalar(_state!, name, value, value > previous ? 45 : 190);
        previous = value;
    }
    private void SetSize()
    {
        _state?.InsertScalar("Width", (float)ActualWidth); _state?.InsertScalar("Height", (float)ActualHeight);
        _state?.InsertScalar("TargetX", (float)TargetX); _state?.InsertScalar("TargetY", (float)TargetY);
    }
    private void UpdateOpacity() => Motion.Scalar(Motion.Visual(_scene), "Opacity",
        _active ? (float)(_intensity * (ActualTheme == ElementTheme.Light ? .8 : 1)) : 0, 240);
    private void StopParts()
    {
        foreach (var part in _parts)
        {
            var v = Motion.Visual(part);
            foreach (string property in new[] { "Scale", "Opacity", "Translation", "RotationAngleInDegrees" }) v.StopAnimation(property);
        }
        if (_state != null) foreach (string name in new[] { "Bass", "Mid", "Treble", "Clock" }) _state.StopAnimation(name);
    }
    private void Stop()
    {
        _active = false; StopParts();
        _bass = _mid = _treble = _previousBass = 0;
    }
    private void Build()
    {
        StopParts(); _parts.Clear(); _beatRings.Clear(); _scene.Children.Clear();
        _state?.Dispose();
        var compositor = Motion.Visual(_scene).Compositor;
        _state = compositor.CreatePropertySet();
        foreach (string name in new[] { "Bass", "Mid", "Treble", "Clock", "Power" }) _state.InsertScalar(name, 0);
        _bass = _mid = _treble = _previousBass = 0;
        SetSize();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_charging) BuildCharge(); else if (_mode == "bass") BuildBass(); else BuildAudioBars();
    }
    private Visual Add(FrameworkElement element)
    {
        _parts.Add(element); _scene.Children.Add(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = Motion.Visual(element); visual.Properties.InsertVector3("Translation", Vector3.Zero);
        return visual;
    }
    private void Bind(CompositionObject target, string property, string expression)
    {
        using var animation = target.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("s", _state!); target.StartAnimation(property, animation);
    }
    private Brush MusicBrush => new SolidColorBrush(ActualTheme == ElementTheme.Light
        ? Color.FromArgb(255, 0, 99, 177) : Color.FromArgb(255, 100, 196, 255));
    private Brush ChargeBrush => new SolidColorBrush(ActualTheme == ElementTheme.Light
        ? Color.FromArgb(255, 26, 117, 49) : Color.FromArgb(255, 108, 224, 139));

    private void BuildAudioBars()
    {
        const int count = 36;
        for (int i = 0; i < count; i++)
        {
            var bar = new Rectangle { Width = _mode == "wave" ? 3 : 5, Height = 100, RadiusX = 2.5, RadiusY = 2.5, Fill = MusicBrush };
            var v = Add(bar);
            v.CenterPoint = new Vector3(2.5f, _mode == "wave" ? 50 : 100, 0);
            float fraction = (i + 1f) / (count + 1f);
            float band = Math.Abs(fraction - .5f) * 2;
            // Three measured audio bands are interpolated; these are not invented FFT bins.
            float bass = Math.Max(0, 1 - band * 2), treble = Math.Max(0, band * 2 - 1), mid = 1 - bass - treble;
            string energy = $"(s.Bass*{F(bass)}+s.Mid*{F(mid)}+s.Treble*{F(treble)})";
            float contour = .72f + .28f * MathF.Sin(i * .7f);
            Bind(v, "Scale", $"Vector3(1,Max(0.025,{energy}*{F(contour)})*Min(s.Height*0.48,100)/100,1)");
            Bind(v, "Opacity", $"0.10+{energy}*0.78");
            Bind(v, "Translation", $"Vector3(s.Width*{F(fraction)}-2.5,s.Height*{(_mode == "wave" ? "0.52" : "0.88")}-{(_mode == "wave" ? "50" : "100")},0)");
        }
    }
    private void BuildBass()
    {
        for (int i = 0; i < 3; i++)
        {
            var ring = new Ellipse { Width = 96, Height = 96, Stroke = MusicBrush, StrokeThickness = 2 };
            var v = Add(ring); v.CenterPoint = new Vector3(48, 48, 0); v.Opacity = 0;
            Bind(v, "Translation", "Vector3(s.Width*0.25-48,s.Height*0.45-48,0)");
            _beatRings.Add(ring);
        }
        var core = new Ellipse { Width = 54, Height = 54, Stroke = MusicBrush, StrokeThickness = 3 };
        var pose = Add(core); pose.CenterPoint = new Vector3(27, 27, 0);
        Bind(pose, "Translation", "Vector3(s.Width*0.25-27,s.Height*0.45-27,0)");
        Bind(pose, "Scale", "Vector3(0.75+s.Bass*0.6,0.75+s.Bass*0.6,1)");
        Bind(pose, "Opacity", "0.08+s.Bass*0.85");
    }
    private void FireBeat(float strength)
    {
        if (_beatRings.Count == 0) return;
        var v = Motion.Visual(_beatRings[_ringIndex++ % _beatRings.Count]);
        v.StopAnimation("Scale"); v.StopAnimation("Opacity"); v.Scale = new Vector3(.6f, .6f, 1); v.Opacity = strength;
        Motion.Vector(v, "Scale", new Vector3(2.5f, 2.5f, 1), 760);
        Motion.Scalar(v, "Opacity", 0, 760);
    }
    private void BuildCharge()
    {
        // A recognisable laptop is the receiving end of every charging effect.
        var computer = new FontIcon { Glyph = "\uE7F4", FontSize = 28, Width = 36, Height = 36, Foreground = ChargeBrush };
        var device = Add(computer);
        Bind(device, "Translation", "Vector3(s.Width*s.TargetX-18,s.Height*s.TargetY-18,0)");
        device.Opacity = 1;
        using var clock = device.Compositor.CreateScalarKeyFrameAnimation();
        clock.Duration = TimeSpan.FromHours(24); clock.IterationBehavior = AnimationIterationBehavior.Forever;
        clock.InsertKeyFrame(0, 0); clock.InsertKeyFrame(1, 86400);
        _state!.StartAnimation("Clock", clock);
        int count = _mode == "circuit" ? 12 : _mode == "chargepulse" ? 5 : 9;
        for (int i = 0; i < count; i++)
        {
            var trail = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
            var color = ((SolidColorBrush)ChargeBrush).Color;
            trail.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0, color.R, color.G, color.B) });
            trail.GradientStops.Add(new GradientStop { Offset = .65, Color = color });
            trail.GradientStops.Add(new GradientStop { Offset = 1, Color = ActualTheme == ElementTheme.Light ? color : Color.FromArgb(255, 220, 255, 230) });
            var energy = new Rectangle { Width = _mode == "chargepulse" ? 5 : _mode == "beam" ? 30 : 15,
                Height = _mode == "beam" ? 3 : 2.5, RadiusX = 1.25, RadiusY = 1.25, Fill = trail };
            var v = Add(energy);
            string phase = $"(s.Clock*0.65+{F(i / (float)count)})";
            // Frac is continuous along each trajectory; opacity hides the wrap at the emitter.
            string p = $"({phase}-Floor({phase}))";
            float lane = (i % 3 - 1) * 12;
            string targetX = "s.Width*s.TargetX-18", targetY = "s.Height*s.TargetY";
            string sourceX = "Max(0,s.Width*s.TargetX-Min(s.Width*0.6,160))";
            if (_mode == "circuit")
                Bind(v, "Translation", $"Vector3({sourceX}+({targetX}-{sourceX})*{p},{targetY}+{F(lane)}*(1-Min(1,{p}*2)),0)");
            else
                Bind(v, "Translation", $"Vector3({sourceX}+({targetX}-{sourceX})*{p},{targetY}+{F(lane)}*(1-{p}),0)");
            Bind(v, "Opacity", $"Min(1,{p}*8)*Min(1,(1-{p})*8)*(0.5+Min(s.Power,1)*0.5)");
        }
        var impact = new Ellipse { Width = 44, Height = 44, Stroke = ChargeBrush, StrokeThickness = 1.5 };
        var halo = Add(impact); halo.CenterPoint = new Vector3(22, 22, 0);
        Bind(halo, "Translation", "Vector3(s.Width*s.TargetX-22,s.Height*s.TargetY-22,0)");
        Bind(halo, "Scale", "Vector3(1+0.12*Sin(s.Clock*6),1+0.12*Sin(s.Clock*6),1)");
        Bind(halo, "Opacity", "0.2+0.18*Sin(s.Clock*6)");
        var screen = new Rectangle { Width = 19, Height = 11, RadiusX = 2, RadiusY = 2, Fill = ChargeBrush };
        var screenPose = Add(screen);
        Bind(screenPose, "Translation", "Vector3(s.Width*s.TargetX-9.5,s.Height*s.TargetY-8,0)");
        Bind(screenPose, "Opacity", "0.14+0.10*(1+Sin(s.Clock*6))");
    }
    private static string F(float value) => value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
}
