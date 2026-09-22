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

    private static readonly string[] HorizontalValues = { "center", "left", "right" };

    private readonly ISettingsStore _settings;
    private readonly Action? _openOnboarding;
    private bool _loading = true;
    /// <summary>当前实际生效的自启动模式（卡片回滚与状态文案都用它）。</summary>
    private AutoStartMode _autoStartApplied = AutoStartMode.Off;
    private bool _autoStartBusy;

    private ChoiceCard[] _positionCards = Array.Empty<ChoiceCard>();
    private ChoiceCard[] _horizontalCards = Array.Empty<ChoiceCard>();
    private ChoiceCard[] _styleCards = Array.Empty<ChoiceCard>();
    private ChoiceCard[] _materialCards = Array.Empty<ChoiceCard>();
    private ChoiceCard[] _autoStartCards = Array.Empty<ChoiceCard>();

    public GeneralSettingsPage(ISettingsStore settings, Action? openOnboarding = null)
    {
        _settings = settings;
        _openOnboarding = openOnboarding;
        InitializeComponent();

        BuildChoiceCards();

        VisibleToggle.IsOn = _settings.Get("island.visible", true);
        HideIdleToggle.IsOn = _settings.Get("island.hideWhenIdle", false);

        LoadOffsetControl();
        HorizontalOffsetSlider.Value = _settings.Get(HorizontalOffsetKey, 0.0);

        _autoStartApplied = AutoStartService.Current(
            AutoStartService.Parse(_settings.Get(AutoStartService.SettingKey, "off")));
        Select(_autoStartCards, (int)_autoStartApplied);
        UpdateAutoStartStatus(null);

        _loading = false;
    }

    // ---- 选择卡片 ----

    private void BuildChoiceCards()
    {
        var style = IslandStyle.ParseStyle(_settings.Get(IslandStyle.StyleKey, IslandStyle.AppleValue));
        var material = IslandStyle.ParseMaterial(_settings.Get(IslandStyle.MaterialKey, IslandStyle.AcrylicValue));
        var horizontal = _settings.Get(HorizontalKey, "center");
        bool bottom = IsBottom();

        _styleCards = new[]
        {
            ChoiceCard.Build("style.apple", ChoiceCard.StylePreview(IslandStyleKind.Apple),
                "Apple 灵动岛", "纯黑胶囊，经典灵动岛", () => PickStyle(IslandStyleKind.Apple)),
            ChoiceCard.Build("style.fluent", ChoiceCard.StylePreview(IslandStyleKind.Fluent),
                "Windows Fluent", "系统材质 + 更小圆角", () => PickStyle(IslandStyleKind.Fluent)),
        };
        ChoiceCard.FillRow(StyleCards, _styleCards);
        Select(_styleCards, IslandStyle.IndexOf(style));

        _materialCards = new[]
        {
            ChoiceCard.Build("material.acrylic", ChoiceCard.MaterialPreview(IslandMaterialKind.Acrylic),
                "Desktop Acrylic", "磨砂玻璃，透出背后的内容", () => PickMaterial(IslandMaterialKind.Acrylic)),
            ChoiceCard.Build("material.mica", ChoiceCard.MaterialPreview(IslandMaterialKind.Mica),
                "Mica", "取壁纸色调，更内敛不透明", () => PickMaterial(IslandMaterialKind.Mica)),
        };
        ChoiceCard.FillRow(MaterialCards, _materialCards);
        Select(_materialCards, IslandStyle.IndexOf(material));
        SetMaterialCardsEnabled(style == IslandStyleKind.Fluent);

        _positionCards = new[]
        {
            ChoiceCard.Build("position.top", ChoiceCard.ScreenDiagram(bottom: false, HorizontalIndex(horizontal)),
                "屏幕顶部", "贴在工作区顶部，悬停展开", () => PickPosition(false)),
            ChoiceCard.Build("position.bottom", ChoiceCard.ScreenDiagram(bottom: true, HorizontalIndex(horizontal)),
                "屏幕底部（嵌入任务栏）", "嵌进任务栏，展开向岛体上方生长", () => PickPosition(true)),
        };
        ChoiceCard.FillRow(PositionCards, _positionCards);
        Select(_positionCards, bottom ? 1 : 0);

        RebuildHorizontalCards(horizontal);

        _autoStartCards = new[]
        {
            ChoiceCard.Build("autostart.off", null, "关闭自启动", "需要时手动打开", () => PickAutoStart(AutoStartMode.Off)),
            ChoiceCard.Build("autostart.user", null, "普通权限自启动", "登录后直接启动，不弹 UAC", () => PickAutoStart(AutoStartMode.User)),
            ChoiceCard.Build("autostart.admin", null, "管理员权限自启动", "登录后以管理员身份静默启动（需一次 UAC 授权）", () => PickAutoStart(AutoStartMode.Admin)),
        };
        ChoiceCard.FillRow(AutoStartCards, _autoStartCards);
    }

    private void PickPosition(bool bottom)
    {
        if (_loading) return;
        _settings.Set(PositionKey, bottom ? "bottom" : "top");
        Select(_positionCards, bottom ? 1 : 0);
        RebuildHorizontalCards(_settings.Get(HorizontalKey, "center"));
        LoadOffsetControl();
    }

    private void PickHorizontal(string value)
    {
        if (_loading) return;
        _settings.Set(HorizontalKey, value);
        Select(_horizontalCards, Math.Max(0, Array.IndexOf(HorizontalValues, value)));
    }

    private void PickStyle(IslandStyleKind style)
    {
        if (_loading) return;
        _settings.Set(IslandStyle.StyleKey, style.ToValue());
        Select(_styleCards, IslandStyle.IndexOf(style));
        SetMaterialCardsEnabled(style == IslandStyleKind.Fluent);
    }

    private void PickMaterial(IslandMaterialKind material)
    {
        if (_loading) return;
        _settings.Set(IslandStyle.MaterialKey, material.ToValue());
        Select(_materialCards, IslandStyle.IndexOf(material));
    }

    private async void PickAutoStart(AutoStartMode mode)
    {
        if (_loading || _autoStartBusy) return;
        if (mode == _autoStartApplied)
        {
            Select(_autoStartCards, (int)_autoStartApplied);
            UpdateAutoStartStatus(null);
            return;
        }

        // 管理员模式会弹 UAC 并等待系统命令：必须放到后台线程，绝不能在 UI 线程上同步等待，
        // 否则整个应用（含岛与所有窗口）会在用户响应 UAC 之前被冻结
        _autoStartBusy = true;
        SetAutoStartCardsEnabled(false);
        UpdateAutoStartStatus(null, busy: true);

        var (success, error) = await AutoStartService.TryApplyAsync(mode);

        _autoStartBusy = false;
        SetAutoStartCardsEnabled(true);

        if (!success)
        {
            // 失败：选中态退回实际生效值，别让界面说谎
            Select(_autoStartCards, (int)_autoStartApplied);
            UpdateAutoStartStatus(error);
            return;
        }

        _autoStartApplied = mode;
        _settings.Set(AutoStartService.SettingKey, AutoStartService.ToValue(mode));
        Select(_autoStartCards, (int)mode);
        UpdateAutoStartStatus(null);
    }

    /// <summary>水平对齐示意图随位置模式重画：岛出现在顶部还是任务栏条里，跟随当前选择。</summary>
    private void RebuildHorizontalCards(string selected)
    {
        bool bottom = IsBottom();
        _horizontalCards = HorizontalValues
            .Select((value, index) => ChoiceCard.Build(
                $"horizontal.{value}",
                ChoiceCard.ScreenDiagram(bottom, index),
                value switch { "left" => "靠左", "right" => "靠右", _ => "居中" },
                null,
                () => PickHorizontal(value)))
            .ToArray();
        ChoiceCard.FillRow(HorizontalCards, _horizontalCards);
        Select(_horizontalCards, Math.Max(0, Array.IndexOf(HorizontalValues, selected)));
    }

    private void SetMaterialCardsEnabled(bool enabled)
    {
        foreach (var card in _materialCards) card.IsEnabled = enabled;
    }

    private void SetAutoStartCardsEnabled(bool enabled)
    {
        foreach (var card in _autoStartCards) card.IsEnabled = enabled;
    }

    private static void Select(ChoiceCard[] cards, int index)
    {
        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].IsSelected = i == index;
        }
    }

    private static int HorizontalIndex(string value) => value switch
    {
        "left" => 1,
        "right" => 2,
        _ => 0,
    };

    private bool IsBottom()
        => string.Equals(_settings.Get(PositionKey, "top"), "bottom", StringComparison.OrdinalIgnoreCase);

    // ---- 自启动状态 ----

    private void UpdateAutoStartStatus(string? error, bool busy = false)
    {
        if (busy)
        {
            AutoStartStatus.Text = "正在等待管理员授权，请在 UAC 弹窗里确认…";
            return;
        }

        if (!string.IsNullOrEmpty(error))
        {
            AutoStartStatus.Text = $"设置失败：{error}";
            return;
        }

        AutoStartStatus.Text = _autoStartApplied switch
        {
            AutoStartMode.User => $"已开启：登录后自动启动（{AutoStartService.ExecutablePath}）",
            AutoStartMode.Admin => "已开启：登录后以管理员权限自动启动（计划任务 WinIsland，开机静默提权，不再弹 UAC）",
            _ => "当前未开启",
        };
    }

    // ---- 偏移滑杆 ----

    /// <summary>偏移滑杆跟随位置模式：两套文案 + 两条设置键各自记忆。</summary>
    private void LoadOffsetControl()
    {
        bool wasLoading = _loading;
        _loading = true;
        bool bottom = IsBottom();
        OffsetTitle.Text = bottom ? "底部偏移" : "顶部偏移";
        OffsetHint.Text = bottom ? "灵动岛距离任务栏底部的距离 (px)" : "灵动岛距离屏幕顶部的距离 (px)";
        OffsetSlider.Value = _settings.Get(bottom ? BottomOffsetKey : TopOffsetKey, 6.0);
        _loading = wasLoading;
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
        _settings.Set(IsBottom() ? BottomOffsetKey : TopOffsetKey, OffsetSlider.Value);
    }

    private void HorizontalOffsetSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set(HorizontalOffsetKey, HorizontalOffsetSlider.Value);
    }

    private void RunOnboarding_Click(object sender, RoutedEventArgs e) => _openOnboarding?.Invoke();
}
