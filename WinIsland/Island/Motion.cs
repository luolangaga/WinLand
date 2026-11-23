using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace WinIsland.Island;

/// <summary>Host motion policy. No SDK dependency or change to plugin contracts.</summary>
internal static class Motion
{
    private static readonly UISettings Preferences = new();
    internal static bool Enabled => Preferences.AnimationsEnabled;
    internal static TimeSpan Duration(double milliseconds) => TimeSpan.FromMilliseconds(Enabled ? milliseconds : 1);

    internal static Visual Visual(UIElement element) => ElementCompositionPreview.GetElementVisual(element);
    internal static CompositionEasingFunction Ease(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0.2f, 1));

    // StartingValue is sampled by the compositor, including an interrupted animation's current value.
    // Reading Visual.Scale/Opacity in C# would only read the base value, causing a visible restart.
    internal static void Scalar(CompositionObject visual, string property, float target, double milliseconds)
    {
        using var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = Duration(milliseconds);
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, target, Ease(visual.Compositor));
        visual.StartAnimation(property, animation);
    }

    internal static void Vector(Visual visual, string property, Vector3 target, double milliseconds,
        string startingExpression = "this.StartingValue", Vector3? adjustment = null)
    {
        if (property == "Translation" && visual.Properties.TryGetVector3(property, out _) != CompositionGetValueStatus.Succeeded)
            visual.Properties.InsertVector3(property, Vector3.Zero);
        using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = Duration(milliseconds);
        if (adjustment is { } value) animation.SetVector3Parameter("adjustment", value);
        animation.InsertExpressionKeyFrame(0, startingExpression);
        animation.InsertKeyFrame(1, target, Ease(visual.Compositor));
        visual.StartAnimation(property, animation);
    }

    internal static void Reveal(UIElement element, double milliseconds = 220, float distance = 8)
    {
        var visual = Visual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.Properties.InsertVector3("Translation", new Vector3(0, Enabled ? distance : 0, 0));
        visual.Opacity = Enabled ? 0 : 1;
        Vector(visual, "Translation", Vector3.Zero, milliseconds);
        Scalar(visual, "Opacity", 1, milliseconds);
    }
}

/// <summary>A single compositor clock keeps a progress fill and its handle exactly aligned.</summary>
internal sealed class ProgressMotion
{
    private readonly FrameworkElement _fill;
    private readonly CompositionPropertySet _state;
    private double _width = -1;
    private double _ratio = -1;

    internal ProgressMotion(FrameworkElement fill, FrameworkElement knob)
    {
        _fill = fill;
        var visual = Motion.Visual(fill);
        var compositor = visual.Compositor;
        _state = compositor.CreatePropertySet();
        _state.InsertScalar("Ratio", 0);
        _state.InsertScalar("Width", 0);
        _state.InsertScalar("KnobWidth", (float)knob.Width);
        var clip = compositor.CreateInsetClip();
        visual.Clip = clip;
        using var fillExpression = compositor.CreateExpressionAnimation("state.Width * (1 - state.Ratio)");
        fillExpression.SetReferenceParameter("state", _state);
        clip.StartAnimation("RightInset", fillExpression);

        ElementCompositionPreview.SetIsTranslationEnabled(knob, true);
        var handle = Motion.Visual(knob);
        handle.Properties.InsertVector3("Translation", Vector3.Zero);
        using var handleExpression = compositor.CreateExpressionAnimation(
            "Vector3(Clamp(state.Width * state.Ratio - state.KnobWidth / 2, 0, Max(0, state.Width - state.KnobWidth)), 0, 0)");
        handleExpression.SetReferenceParameter("state", _state);
        handle.StartAnimation("Translation", handleExpression);
    }

    internal void Set(double ratio, double width, double milliseconds)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        if (Math.Abs(width - _width) > 0.01)
        {
            _width = width;
            _fill.Width = width;
            _state.InsertScalar("Width", (float)width);
        }
        // Repeated polling must not restart an animation towards an unchanged endpoint.
        if (Math.Abs(ratio - _ratio) < 0.000001) return;
        _ratio = ratio;
        Motion.Scalar(_state, "Ratio", (float)ratio, milliseconds);
    }
}

/// <summary>
/// One frame-aligned progress clock for layout morphs that cannot use render transforms (HWND shape).
/// Retargets from the displayed progress and unsubscribes when idle or unloaded.
/// </summary>
internal sealed class MorphProgress(Action<double> apply)
{
    private readonly Stopwatch _clock = new();
    private double _from, _target, _milliseconds;
    private bool _running;
    internal double Value { get; private set; }

    internal void To(double target, TimeSpan duration)
    {
        Stop();
        _from = Value;
        _target = target;
        _milliseconds = Math.Max(1, duration.TotalMilliseconds * Math.Max(0.25, Math.Abs(target - Value)));
        if (!Motion.Enabled || Math.Abs(target - Value) < 0.001)
        {
            Value = target;
            apply(Value);
            return;
        }
        _running = true;
        _clock.Restart();
        CompositionTarget.Rendering += Render;
    }

    internal void Stop()
    {
        if (!_running) return;
        CompositionTarget.Rendering -= Render;
        _clock.Stop();
        _running = false;
    }

    internal void Complete()
    {
        Stop();
        Value = _target;
        apply(Value);
    }

    private void Render(object? sender, object e)
    {
        double t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _milliseconds, 0, 1);
        Value = _from + (_target - _from) * (1 - Math.Pow(1 - t, 3));
        apply(Value);
        if (t >= 1) Stop();
    }
}
