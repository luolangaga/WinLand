using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinIsland.Core;

namespace WinIsland.Settings;

public sealed partial class GeneralSettingsPage : UserControl
{
    private readonly ISettingsStore _settings;
    private bool _loading = true;

    public GeneralSettingsPage(ISettingsStore settings)
    {
        _settings = settings;
        InitializeComponent();

        VisibleToggle.IsOn = _settings.Get("island.visible", true);
        HideIdleToggle.IsOn = _settings.Get("island.hideWhenIdle", false);
        OffsetSlider.Value = _settings.Get("island.topOffset", 6.0);
        _loading = false;
    }

    private void VisibleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("island.visible", VisibleToggle.IsOn);
    }

    private void HideIdleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("island.hideWhenIdle", HideIdleToggle.IsOn);
    }

    private void OffsetSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("island.topOffset", OffsetSlider.Value);
    }
}
