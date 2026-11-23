using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace WinIsland.PluginViews;

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
    internal static void Scalar(Visual visual, string property, float target, double milliseconds)
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

