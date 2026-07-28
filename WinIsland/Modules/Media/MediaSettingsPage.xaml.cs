using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinIsland.Core;

namespace WinIsland.Modules.Media;

public sealed partial class MediaSettingsPage : UserControl
{
    private readonly IDynamicIslandApi _api;
    private readonly MediaModule _module;
    private bool _loading = true;

    public MediaSettingsPage(IDynamicIslandApi api, MediaModule module)
    {
        _api = api;
        _module = module;
        InitializeComponent();

        EnableToggle.IsOn = _api.Settings.Get("media.enabled", true);
        GlowToggle.IsOn = _api.Settings.Get("media.glow", true);
        PrioritySlider.Value = _api.Settings.Get("media.priority", 100);
        StatusText.Text = _module.StatusText;
        _loading = false;
    }

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _api.Settings.Set("media.enabled", EnableToggle.IsOn);
    }

    private void GlowToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _api.Settings.Set("media.glow", GlowToggle.IsOn);
    }

    private void PrioritySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _api.Settings.Set("media.priority", (int)PrioritySlider.Value);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = _module.StatusText;
    }
}
