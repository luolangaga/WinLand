using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using WinIsland.Island;

namespace WinIsland.Modules.Media;

/// <summary>Hold the old artwork until edge-on; only the artwork rotates, never the host's painted chrome.</summary>
public sealed class AlbumArtView : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source),
        typeof(ImageSource), typeof(AlbumArtView), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty PlaceholderSizeProperty = DependencyProperty.Register(nameof(PlaceholderSize),
        typeof(double), typeof(AlbumArtView), new PropertyMetadata(26d,
            (owner, args) => ((AlbumArtView)owner)._placeholder.FontSize = (double)args.NewValue));
    public static readonly DependencyProperty ArtworkRadiusProperty = DependencyProperty.Register(nameof(ArtworkRadius),
        typeof(double), typeof(AlbumArtView), new PropertyMetadata(16d,
            (owner, args) => ((AlbumArtView)owner)._surface.CornerRadius = new CornerRadius((double)args.NewValue)));
    public static readonly DependencyProperty PlaceholderFillProperty = DependencyProperty.Register(nameof(PlaceholderFill),
        typeof(Brush), typeof(AlbumArtView), new PropertyMetadata(null));

    private readonly Grid _frame = new();
    private readonly Border _surface = new() { CornerRadius = new CornerRadius(16) };
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly FontIcon _placeholder = new() { Glyph = "\uE8D6", FontSize = 26 };
    private CompositionScopedBatch? _batch;
    private ImageSource? _displayed;
    private int _version;
#if DEBUG
    internal static double PreviewDurationScale { get; set; } = 1;
#endif
    private static double FlipDuration(double milliseconds)
    {
#if DEBUG
        return milliseconds * PreviewDurationScale;
#else
        return milliseconds;
#endif
    }

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double PlaceholderSize { get => (double)GetValue(PlaceholderSizeProperty); set => SetValue(PlaceholderSizeProperty, value); }
    public double ArtworkRadius { get => (double)GetValue(ArtworkRadiusProperty); set => SetValue(ArtworkRadiusProperty, value); }
    public Brush? PlaceholderFill { get => (Brush?)GetValue(PlaceholderFillProperty); set => SetValue(PlaceholderFillProperty, value); }

    public AlbumArtView()
    {
        IsHitTestVisible = false;
        _frame.Children.Add(_placeholder); _frame.Children.Add(_image);
        _surface.Child = _frame; Content = _surface;
        // Background belongs to the turning surface, never to a stationary outer plate.
        _surface.SetBinding(Border.BackgroundProperty, new Microsoft.UI.Xaml.Data.Binding
        { Source = this, Path = new PropertyPath(nameof(PlaceholderFill)), Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay });
        _frame.SizeChanged += (_, _) => UpdatePivot();
        Loaded += (_, _) => { UpdatePivot(); SetImmediate(); };
        Unloaded += (_, _) => SetImmediate();
        Display(null);
    }

    private static void SourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
        => ((AlbumArtView)owner).ChangeArtwork();

    private void UpdatePivot()
    {
        var pose = Motion.Visual(_surface);
        pose.CenterPoint = new Vector3((float)_frame.ActualWidth / 2, (float)_frame.ActualHeight / 2, 0);
        pose.RotationAxis = Vector3.UnitY;
        var perspective = Matrix4x4.Identity; perspective.M34 = -1f / 600;
        pose.TransformMatrix = perspective;
    }

    private void ChangeArtwork()
    {
        if (!IsLoaded || !Motion.Enabled || _displayed == null || Source == null) { SetImmediate(); return; }
        if (ReferenceEquals(Source, _displayed))
        {
            ++_version; _batch?.Dispose(); _batch = null;
            RotateTo(Motion.Visual(_surface), 0, 180, false);
            return;
        }
        int version = ++_version;
        _batch?.Dispose();
        var pose = Motion.Visual(_surface);
        UpdatePivot();
        var batch = pose.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batch = batch;
        batch.Completed += (_, _) =>
        {
            if (ReferenceEquals(_batch, batch)) _batch = null;
            batch.Dispose();
            if (version != _version || !IsLoaded) return;
            Display(Source);
            // +90 and -90 are both edge-on, so the texture changes while hidden.
            pose.StopAnimation("RotationAngleInDegrees");
            pose.RotationAngleInDegrees = -90;
            RotateTo(pose, 0, 180, false);
        };
        RotateTo(pose, 90, 180, true);
        batch.End();
    }

    private static void RotateTo(Visual pose, float angle, double milliseconds, bool departing)
    {
        using var animation = pose.Compositor.CreateScalarKeyFrameAnimation();
        using var easing = departing
            ? pose.Compositor.CreateCubicBezierEasingFunction(new Vector2(.55f, 0), Vector2.One)
            : pose.Compositor.CreateCubicBezierEasingFunction(Vector2.Zero, new Vector2(.45f, 1));
        animation.Duration = TimeSpan.FromMilliseconds(FlipDuration(milliseconds));
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, angle, easing);
        pose.StartAnimation("RotationAngleInDegrees", animation);
    }

    private void SetImmediate()
    {
        ++_version; _batch?.Dispose(); _batch = null;
        var pose = Motion.Visual(_surface);
        pose.StopAnimation("RotationAngleInDegrees"); pose.StopAnimation("Scale");
        pose.RotationAngleInDegrees = 0; pose.Scale = Vector3.One;
        Display(Source);
    }

    private void Display(ImageSource? source)
    {
        _displayed = source; _image.Source = source;
        _image.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
        _placeholder.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
    }
}
