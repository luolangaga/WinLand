using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace WinIsland.Modules.Battery;

public sealed partial class BatterySettingsPage : UserControl
{
    private readonly ISettingsStore _settings;
    private readonly BatteryPlugin _plugin;
    private bool _loading = true;

    public BatterySettingsPage(IPluginContext context, BatteryPlugin plugin)
    {
        _settings = context.Settings;
        _plugin = plugin;
        InitializeComponent();

        EnableToggle.IsOn = _settings.Get("enabled", true);
        StatusText.Text = _plugin.StatusText;
        _loading = false;
    }

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("enabled", EnableToggle.IsOn);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = _plugin.StatusText;
    }
}
