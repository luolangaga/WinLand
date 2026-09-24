using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinIsland.Core;

namespace WinIsland.Modules.Media;

public sealed partial class MediaSettingsPage : UserControl
{
    private readonly ISettingsStore _settings;
    private readonly MediaPlugin _plugin;
    private bool _loading = true;

    public MediaSettingsPage(IPluginContext context, MediaPlugin plugin)
    {
        _settings = context.Settings;
        _plugin = plugin;
        InitializeComponent();

        EnableToggle.IsOn = _settings.Get("enabled", true);
        GlowToggle.IsOn = _settings.Get("glow", true);
        LyricsToggle.IsOn = _settings.Get("lyrics", true);
        PrioritySlider.Value = _settings.Get("priority", 100);
        StatusText.Text = _plugin.StatusText;
        _loading = false;
    }

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("enabled", EnableToggle.IsOn);
    }

    private void GlowToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("glow", GlowToggle.IsOn);
    }

    private void LyricsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("lyrics", LyricsToggle.IsOn);
    }

    private void PrioritySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("priority", (int)PrioritySlider.Value);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await _plugin.RefreshNowAsync();
        StatusText.Text = _plugin.StatusText;
    }
}
