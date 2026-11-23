using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace WinIsland.Settings;

internal sealed class BackgroundEffectSettings : StackPanel
{
    internal BackgroundEffectSettings(ISettingsStore settings, bool includeToggle, bool charging = false)
    {
        Spacing = 12;
        Children.Add(new TextBlock { Text = "背景动效", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        Children.Add(new TextBlock { Text = charging ? "能量射流注入电脑、沿电路流动或脉冲充能；仅充电时播放，可随时关闭。" : "音柱、声波和低音冲击跟随实际音频能量；暂停或静音时收回，可随时关闭。", TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush });
        if (includeToggle)
        {
            var enabled = new ToggleSwitch { Header = "启用背景动画", IsOn = settings.Get("glow", true) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(enabled, "BackgroundEnabled");
            enabled.Toggled += (_, _) => settings.Set("glow", enabled.IsOn);
            Children.Add(enabled);
        }
        string[] values = charging ? ["beam", "circuit", "chargepulse", "off"] : ["spectrum", "wave", "bass", "off"];
        string[] names = charging ? ["能量射流 · 注入电脑", "电路传输 · 汇聚充能", "脉冲充能 · 连续补给", "关闭"] : ["三频音柱 · 节奏律动", "律动声波 · 随乐起伏", "低音冲击 · 鼓点扩散", "关闭"];
        var mode = new ComboBox { Header = "动画风格", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var name in names) mode.Items.Add(name);
        int index = Array.IndexOf(values, WinIsland.Island.AmbientBackdrop.ResolveMode(settings.Get("background", charging ? "beam" : "spectrum"), charging));
        mode.SelectedIndex = index < 0 ? 0 : index;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(mode, "BackgroundStyle");
        mode.SelectionChanged += (_, _) => { if (mode.SelectedIndex >= 0) settings.Set("background", values[mode.SelectedIndex]); };
        Children.Add(mode);
        var intensity = new Slider { Header = "效果强度", Minimum = 10, Maximum = 80, StepFrequency = 5,
            Value = settings.Get("backgroundIntensity", charging ? 50 : 35) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(intensity, "BackgroundIntensity");
        intensity.ValueChanged += (_, _) => settings.Set("backgroundIntensity", (int)intensity.Value);
        Children.Add(intensity);
    }
}
