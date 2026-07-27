using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace WinIsland.Modules.AcpMonitor;

public sealed partial class AcpMonitorSettingsPage : UserControl
{
    private readonly IDynamicIslandApi _api;
    private readonly AcpMonitorModule _module;
    private bool _loading;

    public AcpMonitorSettingsPage(IDynamicIslandApi api, AcpMonitorModule module)
    {
        _api = api;
        _module = module;
        InitializeComponent();

        _loading = true;
        EnabledToggle.IsOn = api.Settings.Get("acpmonitor.enabled", true);
        CallbackUrlBlock.Text = $"POST {module.CallbackBaseUrl}/agent/event";
        RefreshInstallStatus();
        _loading = false;
    }

    private void RefreshInstallStatus()
    {
        RefreshAgentStatus("opencode", OpenCodeStatus, InstallOpenCodeBtn);
        RefreshAgentStatus("claude", ClaudeStatus, InstallClaudeBtn);
        RefreshAgentStatus("codex", CodexStatus, InstallCodexBtn);
    }

    private static void RefreshAgentStatus(string agentId, TextBlock statusBlock, Button installBtn)
    {
        var installed = AcpMonitorModule.IsPluginInstalled(agentId);
        statusBlock.Text = installed ? "已安装 ✓" : "未安装";
        statusBlock.Foreground = installed
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGreen)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        installBtn.Content = installed ? "重新安装" : "安装";
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _api.Settings.Set("acpmonitor.enabled", EnabledToggle.IsOn);
    }

    private async void InstallOpenCode_Click(object sender, RoutedEventArgs e)
    {
        await InstallAgentAsync("opencode", InstallOpenCodeBtn);
    }

    private async void InstallClaude_Click(object sender, RoutedEventArgs e)
    {
        await InstallAgentAsync("claude", InstallClaudeBtn);
    }

    private async void InstallCodex_Click(object sender, RoutedEventArgs e)
    {
        await InstallAgentAsync("codex", InstallCodexBtn);
    }

    private async Task InstallAgentAsync(string agentId, Button btn)
    {
        btn.IsEnabled = false;
        btn.Content = "安装中...";
        try
        {
            await _module.InstallPluginAsync(agentId);
            RefreshInstallStatus();
        }
        catch (Exception ex)
        {
            btn.Content = "安装失败";
            StatusBlock.Text = $"安装失败: {ex.Message}";
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }
}
