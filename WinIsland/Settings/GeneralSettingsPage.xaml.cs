using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinIsland.Core;
using WinIsland.Island;

namespace WinIsland.Settings;

public sealed partial class GeneralSettingsPage : UserControl
{
    private const string PositionKey = "island.position";
    private const string HorizontalKey = "island.horizontal";
    private const string TopOffsetKey = "island.topOffset";
    private const string BottomOffsetKey = "island.bottomOffset";
    private const string HorizontalOffsetKey = "island.horizontalOffset";

    private readonly ISettingsStore _settings;
    private bool _loading = true;

    public GeneralSettingsPage(ISettingsStore settings)
    {
        _settings = settings;
        InitializeComponent();

        var style = IslandStyle.ParseStyle(_settings.Get(IslandStyle.StyleKey, IslandStyle.AppleValue));
        var material = IslandStyle.ParseMaterial(_settings.Get(IslandStyle.MaterialKey, IslandStyle.AcrylicValue));
        StyleCombo.SelectedIndex = IslandStyle.IndexOf(style);
        MaterialCombo.SelectedIndex = IslandStyle.IndexOf(material);
        MaterialCombo.IsEnabled = style == IslandStyleKind.Fluent;

        VisibleToggle.IsOn = _settings.Get("island.visible", true);
        HideIdleToggle.IsOn = _settings.Get("island.hideWhenIdle", false);
        PositionCombo.SelectedIndex = string.Equals(_settings.Get(PositionKey, "top"), "bottom", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        HorizontalCombo.SelectedIndex = _settings.Get(HorizontalKey, "center") switch
        {
            "left" => 1,
            "right" => 2,
            _ => 0,
        };
        LoadOffsetControl();
        HorizontalOffsetSlider.Value = _settings.Get(HorizontalOffsetKey, 0.0);
        _loading = false;
    }

    /// <summary>偏移滑杆跟随位置模式：两套文案 + 两条设置键各自记忆。</summary>
    private void LoadOffsetControl()
    {
        bool wasLoading = _loading;
        _loading = true;
        bool bottom = PositionCombo.SelectedIndex == 1;
        OffsetTitle.Text = bottom ? "底部偏移" : "顶部偏移";
        OffsetHint.Text = bottom ? "灵动岛距离任务栏底部的距离 (px)" : "灵动岛距离屏幕顶部的距离 (px)";
        OffsetSlider.Value = _settings.Get(bottom ? BottomOffsetKey : TopOffsetKey, 6.0);
        _loading = wasLoading;
    }

    private void StyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StyleCombo.SelectedIndex < 0) return;

        var style = IslandStyle.Styles[StyleCombo.SelectedIndex];
        MaterialCombo.IsEnabled = style == IslandStyleKind.Fluent;
        if (_loading) return;
        _settings.Set(IslandStyle.StyleKey, style.ToValue());
    }

    private void MaterialCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MaterialCombo.SelectedIndex < 0 || _loading) return;
        _settings.Set(IslandStyle.MaterialKey, IslandStyle.Materials[MaterialCombo.SelectedIndex].ToValue());
    }

    private void PositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PositionCombo.SelectedIndex < 0) return;
        LoadOffsetControl();
        if (_loading) return;
        _settings.Set(PositionKey, PositionCombo.SelectedIndex == 1 ? "bottom" : "top");
    }

    private void HorizontalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HorizontalCombo.SelectedIndex < 0 || _loading) return;
        _settings.Set(HorizontalKey, HorizontalCombo.SelectedIndex switch
        {
            1 => "left",
            2 => "right",
            _ => "center",
        });
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
        _settings.Set(PositionCombo.SelectedIndex == 1 ? BottomOffsetKey : TopOffsetKey, OffsetSlider.Value);
    }

    private void HorizontalOffsetSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set(HorizontalOffsetKey, HorizontalOffsetSlider.Value);
    }
}
