using WinIsland.PluginViews;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace XamlPlugin.Views;

public sealed partial class XamlPluginView : UserControl, IMorphView
{
    private const double CompactSize = 12;
    private const double ExpandedSize = 44;

    private readonly MorphProgress _morph;

    public XamlPluginView()
    {
        PluginXaml.Load(this);

        _morph = new MorphProgress(Apply);
        Unloaded += (_, _) => _morph.Complete();
    }

    public UIElement View => this;

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    private void StartMorph(double target, TimeSpan duration) => _morph.To(target, duration);

    private void Apply(double progress)
    {
        var size = CompactSize + (ExpandedSize - CompactSize) * progress;
        Dot.Width = size;
        Dot.Height = size;
        Details.Height = ExpandedSize * progress;
        Details.Opacity = progress;
    }
}
