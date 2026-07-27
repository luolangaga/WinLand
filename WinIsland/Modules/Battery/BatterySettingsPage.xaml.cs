using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace WinIsland.Modules.Battery;

public sealed partial class BatterySettingsPage : UserControl
{
    private readonly IDynamicIslandApi _api;
    private readonly BatteryModule _module;
    private bool _loading = true;

    public BatterySettingsPage(IDynamicIslandApi api, BatteryModule module)
    {
        _api = api;
        _module = module;
        InitializeComponent();

        EnableToggle.IsOn = _api.Settings.Get("battery.enabled", true);
        StatusText.Text = _module.StatusText;
        _loading = false;
    }

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _api.Settings.Set("battery.enabled", EnableToggle.IsOn);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = _module.StatusText;
    }
}
