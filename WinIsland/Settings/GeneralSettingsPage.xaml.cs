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
    private const string HoverExpandKey = "island.hoverExpand";
    private const string HoverDelayKey = "island.hoverDelay";
    private const string BounceKey = "island.bounce";
    private const string DisplayKey = "island.display";
    private const string RadiusKey = "island.radius";
    private const string OpacityKey = "island.opacity";

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
    private ChoiceCard[] _fullscreenCards = Array.Empty<ChoiceCard>();
    private ChoiceCard[] _displayCards = Array.Empty<ChoiceCard>();
    /// <summary>显示器卡片的顺序键（index 0 固定是 auto，其余与枚举结果一一对应）。</summary>
    private string[] _displayKeys = Array.Empty<string>();

    public GeneralSettingsPage(ISettingsStore settings, Action? openOnboarding = null)
    {
        _settings = settings;
        _openOnboarding = openOnboarding;
        InitializeComponent();

        BuildChoiceCards();
        IdleEdgeToggle.IsOn = _settings.Get("island.idleEdgeHide", false);

        VisibleToggle.IsOn = _settings.Get("island.visible", true);
        HideIdleToggle.IsOn = _settings.Get("island.hideWhenIdle", false);
        ActivityCountToggle.IsOn = _settings.Get("island.showActivityCount", true);
        DropToggle.IsOn = _settings.Get("island.dropEnabled", true);
        HoverExpandToggle.IsOn = _settings.Get(HoverExpandKey, true);
        HoverDelaySlider.Value = _settings.Get(HoverDelayKey, 0.0);
        HoverDelaySlider.IsEnabled = HoverExpandToggle.IsOn;
        BounceToggle.IsOn = _settings.Get(BounceKey, true);

        ScaleIslandSlider.Value = _settings.Get("island.scale.island", 100);
        ScaleIdleSlider.Value = _settings.Get("island.scale.idle", 100);
        ScaleStripSlider.Value = _settings.Get("island.scale.strip", 100);

        RadiusSlider.Value = _settings.Get(RadiusKey, 100.0);
        OpacitySlider.Value = _settings.Get(OpacityKey, 100.0);

        LoadOffsetControl();
        HorizontalOffsetSlider.Value = _settings.Get(HorizontalOffsetKey, 0.0);

        QuietCombo.SelectedIndex = 1;      // 默认 30 分钟
        RefreshQuietState();
        RefreshPlacementState();

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

        string fsMode = _settings.Get("island.fullscreen", "strip");
        _fullscreenCards = new[]
        {
            ChoiceCard.Build("fullscreen.strip", null, "显示小色条", "隐藏岛体，屏幕边缘留一条色条，鼠标靠近即展开", () => PickFullscreen("strip")),
            ChoiceCard.Build("fullscreen.hide", null, "完全隐藏", "全屏期间岛体与色条都不显示", () => PickFullscreen("hide")),
            ChoiceCard.Build("fullscreen.normal", null, "正常显示", "不避让全屏，岛体照常置顶显示", () => PickFullscreen("normal")),
        };
        ChoiceCard.FillRow(FullscreenCards, _fullscreenCards);
        Select(_fullscreenCards, fsMode switch { "hide" => 1, "normal" => 2, _ => 0 });

        RebuildDisplayCards();

        // 设置页开着时插/拔显示器：卡片列表要跟着变，否则用户会看到一块已经不存在的屏
        DisplayResolver.DisplaysInvalidated += OnDisplaysInvalidated;
        Unloaded += (_, _) => DisplayResolver.DisplaysInvalidated -= OnDisplaysInvalidated;
    }

    private void OnDisplaysInvalidated()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RebuildDisplayCards);
            return;
        }

        RebuildDisplayCards();
    }

    /// <summary>可靠性测试用：显示器卡片所在的网格（生产代码不读它）。</summary>
    internal Grid DisplayCardsForTest => DisplayCards;

    /// <summary>
    /// 显示器卡片：第一张固定是「自动（跟随鼠标）」，后面是这台机器实际枚举到的每一块屏。
    /// 实时枚举而不是读缓存 —— 设置页打开时用户刚插上的屏应该立刻出现；
    /// 显示器数量变了会在系统事件后重建一次，见下面的 DisplaysInvalidated 订阅。
    /// </summary>
    private void RebuildDisplayCards()
    {
        var displays = DisplayResolver.List();
        var selected = _settings.Get<string?>(DisplayKey, null);

        var cards = new List<ChoiceCard>
        {
            ChoiceCard.Build(
                "display.auto",
                ChoiceCard.AutoDisplayDiagram(),
                "自动",
                "跟随鼠标光标所在的那块屏",
                () => PickDisplay(null)),
        };

        var keys = new List<string> { DisplayResolver.DisplayAuto };

        // 单显示器机器不画"多屏"示意图，免得凭空多出一块不存在的屏
        int diagramCount = Math.Clamp(displays.Length, 1, 2);
        int primaryIndex = Array.FindIndex(displays, d => d.IsPrimary);

        for (int i = 0; i < displays.Length; i++)
        {
            var d = displays[i];
            int diagramSlot = displays.Length == 1 ? 0 : (i == 0 ? 0 : 1);
            cards.Add(ChoiceCard.Build(
                $"display.{i}",
                ChoiceCard.DisplayDiagram(diagramCount, diagramSlot, primaryIndex),
                d.Label,
                d.IsPrimary ? "系统主显示器" : "扩展显示器",
                () => PickDisplay(d.Key)));
            keys.Add(d.Key);
        }

        // 被指定的那块屏拔掉了：卡片列表里已经没有它，但设置里还留着旧 id ——
        // 界面要诚实反映"现在实际跟的是哪块屏"，所以按「设置值匹配不上任何卡片」时高亮自动。
        int index = 0;
        if (!string.IsNullOrWhiteSpace(selected) &&
            !string.Equals(selected, DisplayResolver.DisplayAuto, StringComparison.OrdinalIgnoreCase))
        {
            int hit = keys.FindIndex(k => string.Equals(k, selected, StringComparison.OrdinalIgnoreCase));
            index = hit >= 0 ? hit : 0;
        }

        _displayCards = cards.ToArray();
        _displayKeys = keys.ToArray();
        // 一行最多 3 张：屏多的时候折行，别把标题压成三条字
        ChoiceCard.FillGrid(DisplayCards, perRow: 3, _displayCards);
        Select(_displayCards, index);

        DisplayHint.Text = displays.Length <= 1
            ? "岛锚定在哪块屏幕。当前只检测到一块显示器"
            : $"岛锚定在哪块屏幕：跟随鼠标光标，或固定某一台（当前检测到 {displays.Length} 块）。多屏热插拔时会自动重新定位";
    }

    /// <summary>写入 island.display；<paramref name="key"/> 为 null 表示「自动」。</summary>
    private void PickDisplay(string? key)
    {
        if (_loading) return;
        _settings.Set(DisplayKey, key ?? DisplayResolver.DisplayAuto);
        Select(_displayCards, Math.Max(0, Array.IndexOf(_displayKeys, key ?? DisplayResolver.DisplayAuto)));
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
    private void IdleEdgeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _settings.Set("island.idleEdgeHide", IdleEdgeToggle.IsOn);
    }

    private void LoadOffsetControl()
    {
        bool wasLoading = _loading;
        _loading = true;
        bool bottom = IsBottom();
        OffsetTitle.Text = bottom ? "底部偏移" : "顶部偏移";
        OffsetHint.Text = bottom ? "距离任务栏底部的距离 (DIP)，负值可向外移动" : "距离工作区顶部的距离 (DIP)，负值可嵌入顶部任务栏";
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

    private void DropToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("island.dropEnabled", DropToggle.IsOn);
    }

    private void ActivityCountToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("island.showActivityCount", ActivityCountToggle.IsOn);
    }

    private void HoverExpandToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set(HoverExpandKey, HoverExpandToggle.IsOn);
        HoverDelaySlider.IsEnabled = HoverExpandToggle.IsOn;
    }

    private void HoverDelaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set(HoverDelayKey, HoverDelaySlider.Value);
    }

    private void BounceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set(BounceKey, BounceToggle.IsOn);
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

    private void PickFullscreen(string mode)
    {
        if (_loading) return;
        Select(_fullscreenCards, mode switch { "hide" => 1, "normal" => 2, _ => 0 });
        _settings.Set("island.fullscreen", mode);
    }

    private void ScaleSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        string? key = ReferenceEquals(sender, ScaleIslandSlider) ? "island.scale.island"
                    : ReferenceEquals(sender, ScaleIdleSlider) ? "island.scale.idle"
                    : ReferenceEquals(sender, ScaleStripSlider) ? "island.scale.strip" : null;
        if (key != null) _settings.Set(key, (int)Math.Round(((Slider)sender).Value));
    }

    private void RadiusSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set(RadiusKey, Math.Round(RadiusSlider.Value));
    }

    private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set(OpacityKey, Math.Round(OpacitySlider.Value));
    }

    private void RunOnboarding_Click(object sender, RoutedEventArgs e) => _openOnboarding?.Invoke();

    // ---- 勿扰 ----

    /// <summary>静默时长选项：与 <see cref="QuietMode.SnoozeMinutes"/> 一一对应。</summary>
    private static readonly int[] QuietMinutes = { 15, 30, 60, 120 };

    private void RefreshQuietState()
    {
        bool active = QuietMode.IsActive(_settings);
        QuietButton.Content = active ? "结束静默" : "开始静默";
        QuietHint.Text = active
            ? $"正在静默，还剩 {QuietMode.DescribeRemaining(_settings)}：消息只记进历史、不弹出来"
            : "静默期间不弹临时消息、也不因新活动自动现身；消息照样记进历史";
        QuietCombo.IsEnabled = !active;
    }

    private void QuietCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // 选中项本身不落盘（时长只在点「开始静默」那一刻才决定），这里只是保持界面一致
        RefreshQuietState();
    }

    private void QuietButton_Click(object sender, RoutedEventArgs e)
    {
        if (QuietMode.IsActive(_settings))
        {
            QuietMode.End(_settings);
        }
        else
        {
            int index = Math.Clamp(QuietCombo.SelectedIndex, 0, QuietMinutes.Length - 1);
            QuietMode.Snooze(_settings, TimeSpan.FromMinutes(QuietMinutes[index]));
        }

        RefreshQuietState();
    }

    // ---- 手动位置 ----

    private void RefreshPlacementState()
    {
        bool manual = _settings.Get<double?>("island.dragX", null) is not null;
        ResetPlacementButton.IsEnabled = manual;
        DragPlacementHint.Text = manual
            ? "当前是手动位置。按住 Alt 再拖岛体（或按住中键拖动）可以重新摆放"
            : "按住 Alt 再拖岛体（或按住中键拖动）可以把它摆到任意位置；改了位置/对齐设置会自动回到自动落位";
    }

    private void ResetPlacement_Click(object sender, RoutedEventArgs e)
    {
        // 岛体自己会在设置变化时重算落点；这里删两个键就够了
        if (_settings is SettingsService service)
        {
            service.Remove("island.dragX");
            service.Remove("island.dragY");
        }
        else
        {
            _settings.Set<double?>("island.dragX", null);
            _settings.Set<double?>("island.dragY", null);
        }

        RefreshPlacementState();
    }
}
