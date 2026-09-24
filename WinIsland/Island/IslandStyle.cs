using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinIsland.Core;

namespace WinIsland.Island;

/// <summary>岛屿外观风格。</summary>
public enum IslandStyleKind
{
    /// <summary>Apple 灵动岛：不透明纯黑胶囊。明暗主题下都是黑的 —— 这是它的身份。</summary>
    Apple,

    /// <summary>Windows Fluent：元素级系统材质 + 小圆角 + 1px 描边，配色跟随系统明暗主题。</summary>
    Fluent,
}

/// <summary>Fluent 风格使用的系统材质。</summary>
public enum IslandMaterialKind
{
    /// <summary>Desktop Acrylic：实时模糊背后桌面/窗口的磨砂玻璃。</summary>
    Acrylic,

    /// <summary>Mica：采样壁纸色调的偏不透明材质（Windows 11 起支持，其余系统回退纯色）。</summary>
    Mica,
}

/// <summary>
/// 外观风格的唯一策略来源：圆角、材质、底衬、描边、空闲点颜色。
/// 圆角同时决定点击穿透的窗口形状（<see cref="IslandWindow.UpdateHitRegion"/> 会读取岛体的
/// CornerRadius），因此任何新增的圆角规则都必须经过这里，避免岛体与窗口形状不一致导致白块或吞点击。
///
/// 配色一律明暗成对：Fluent 跟随系统主题（<c>light = true</c> 取浅色那一套），
/// Apple 忽略 <c>light</c>（黑胶囊上没有"浅色"这回事）。任何新增的画刷都必须同时给出两套值 ——
/// 只写一套就等于把岛钉死在一种主题里，浅色用户会直接看到白字压白底。
/// </summary>
public static class IslandStyle
{
    public const string StyleKey = "island.style";
    public const string MaterialKey = "island.material";

    public const string AppleValue = "apple";
    public const string FluentValue = "fluent";
    public const string AcrylicValue = "acrylic";
    public const string MicaValue = "mica";

    /// <summary>设置页下拉的选项顺序，索引即 SelectedIndex。</summary>
    public static readonly IslandStyleKind[] Styles = { IslandStyleKind.Apple, IslandStyleKind.Fluent };

    /// <summary>设置页材质下拉的选项顺序，索引即 SelectedIndex。</summary>
    public static readonly IslandMaterialKind[] Materials = { IslandMaterialKind.Acrylic, IslandMaterialKind.Mica };

    // Apple：紧凑/空闲/临时消息取高度一半成胶囊，展开封顶 28
    private const double AppleExpandedRadiusCap = 28;
    private const double AppleQueueRadius = 17;
    // Fluent：外层 12 / 内层 8 的嵌套圆角
    private const double FluentCompactRadius = 8;
    private const double FluentExpandedRadius = 12;
    private const double FluentQueueRadius = 8;
    // 聚光卡（超级展开）：一张独立的大卡片，圆角比岛体舒展
    private const double AppleSpotlightRadius = 28;
    private const double FluentSpotlightRadius = 20;

    private static readonly Color AppleSurface = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color FluentSurfaceDark = Color.FromArgb(64, 0, 0, 0);
    /// <summary>
    /// 浅色主题**不压纱**（完全透明）：深色那层黑纱是为了保证白字对比度，
    /// 而浅色材质本来就淡，再压一层白纱就彻底看不出是材质了（深色文字在浅色材质上本来就够对比）。
    /// </summary>
    private static readonly Color FluentSurfaceLight = Color.FromArgb(0, 255, 255, 255);
    private static readonly Color FluentSolidSurfaceDark = Color.FromArgb(230, 32, 32, 32);
    private static readonly Color FluentSolidSurfaceLight = Color.FromArgb(230, 243, 243, 243);
    private static readonly Color FluentStrokeDark = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color FluentStrokeLight = Color.FromArgb(20, 0, 0, 0);
    private static readonly Color AppleIdleDot = Color.FromArgb(255, 46, 46, 50);
    private static readonly Color FluentIdleDotDark = Color.FromArgb(115, 255, 255, 255);
    private static readonly Color FluentIdleDotLight = Color.FromArgb(115, 0, 0, 0);
    private static readonly Color AppleSpotlightSurface = Color.FromArgb(245, 12, 12, 16);
    private static readonly Color FluentSpotlightSurfaceDark = Color.FromArgb(232, 38, 38, 42);
    private static readonly Color FluentSpotlightSurfaceLight = Color.FromArgb(232, 249, 249, 249);
    private static readonly Color FluentSpotlightSolidSurfaceDark = Color.FromArgb(235, 26, 26, 30);
    private static readonly Color FluentSpotlightSolidSurfaceLight = Color.FromArgb(235, 243, 243, 243);
    private static readonly Color SpotlightStrokeDark = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color SpotlightStrokeLight = Color.FromArgb(26, 0, 0, 0);

    public static IslandStyleKind ParseStyle(string? value)
        => string.Equals(value?.Trim(), FluentValue, StringComparison.OrdinalIgnoreCase)
            ? IslandStyleKind.Fluent
            : IslandStyleKind.Apple;

    public static IslandMaterialKind ParseMaterial(string? value)
        => string.Equals(value?.Trim(), MicaValue, StringComparison.OrdinalIgnoreCase)
            ? IslandMaterialKind.Mica
            : IslandMaterialKind.Acrylic;

    public static string ToValue(this IslandStyleKind style)
        => style == IslandStyleKind.Fluent ? FluentValue : AppleValue;

    public static string ToValue(this IslandMaterialKind material)
        => material == IslandMaterialKind.Mica ? MicaValue : AcrylicValue;

    public static int IndexOf(IslandStyleKind style) => Array.IndexOf(Styles, style);

    public static int IndexOf(IslandMaterialKind material) => Array.IndexOf(Materials, material);

    private static Color Pick(bool light, Color dark, Color lightColor) => light ? lightColor : dark;

    /// <summary>
    /// 该用浅色配色吗：只有 Fluent 跟随系统主题，Apple 的黑胶囊在浅色系统下也是黑的。
    /// 岛体、聚光卡、设置页预览都读这一个判断，别在别处再写一遍。
    /// </summary>
    public static bool IsLightChrome(IslandStyleKind style, bool light)
        => style == IslandStyleKind.Fluent && light;

    /// <summary>当前生效的岛体明暗（设置里的外观风格 + 系统主题）：岛体与插件侧都从这里取。</summary>
    public static bool ResolveIsLight(ISettingsStore settings)
        => IsLightChrome(ParseStyle(settings.Get(StyleKey, AppleValue)), SystemTheme.IsLight);

    /// <summary>岛体圆角：<paramref name="height"/> 为岛体高度，<paramref name="expanded"/> 表示展开大岛。</summary>
    public static double ResolveRadius(IslandStyleKind style, double height, bool expanded)
    {
        if (style == IslandStyleKind.Fluent)
        {
            return Math.Min(expanded ? FluentExpandedRadius : FluentCompactRadius, height / 2);
        }

        return expanded ? Math.Min(AppleExpandedRadiusCap, height / 2) : height / 2;
    }

    /// <summary>队列卡片圆角。</summary>
    public static double ResolveQueueRadius(IslandStyleKind style, double cardHeight)
        => style == IslandStyleKind.Fluent
            ? Math.Min(FluentQueueRadius, cardHeight / 2)
            : AppleQueueRadius;

    /// <summary>
    /// 岛体底衬：Apple 不透明纯黑（明暗都一样）；Fluent 有系统材质时深色主题压一层黑纱
    /// （保证白字对比度），浅色主题不压纱、让材质原样透出来 —— 浅色材质本来就淡，
    /// 再叠白纱就成了平白板，那是"看不出材质"而不是"适配了浅色"。
    /// 材质不可用时退回接近不透明的纯色（深色 #202020 / 浅色 #F3F3F3），文字依旧可读。
    /// </summary>
    public static SolidColorBrush CreateSurfaceFill(IslandStyleKind style, bool materialApplied, bool light)
    {
        if (style == IslandStyleKind.Apple) return new SolidColorBrush(AppleSurface);
        if (materialApplied) return new SolidColorBrush(Pick(light, FluentSurfaceDark, FluentSurfaceLight));
        return new SolidColorBrush(Pick(light, FluentSolidSurfaceDark, FluentSolidSurfaceLight));
    }

    /// <summary>岛体描边：仅 Fluent 使用 1px 细描边（深色主题浅描边、浅色主题深描边）。</summary>
    public static SolidColorBrush? CreateStroke(IslandStyleKind style, bool light)
        => style == IslandStyleKind.Fluent
            ? new SolidColorBrush(Pick(light, FluentStrokeDark, FluentStrokeLight))
            : null;

    /// <summary>空闲小点颜色：纯黑胶囊上用深灰（现状），系统材质上用半透明的白（深色）/ 黑（浅色）。</summary>
    public static Color IdleDotColor(IslandStyleKind style, bool light)
        => style == IslandStyleKind.Fluent
            ? Pick(light, FluentIdleDotDark, FluentIdleDotLight)
            : AppleIdleDot;

    /// <summary>聚光卡圆角。</summary>
    public static double ResolveSpotlightRadius(IslandStyleKind style)
        => style == IslandStyleKind.Fluent ? FluentSpotlightRadius : AppleSpotlightRadius;

    /// <summary>
    /// 聚光卡底衬：Apple 近不透明纯黑（延续灵动岛的黑胶囊身份），
    /// Fluent 近不透明的炭灰（深色）/ 近白（浅色）—— 遮罩始终是压暗的黑，浅色卡片因此照样从遮罩里"浮"出来。
    /// 覆盖窗是独立的全屏透明窗，这里刻意不用元素级 Acrylic —— 那需要窗口自己的材质背衬，
    /// 在「透明窗 + 遮罩」的组合里会退化成不可控的色块。
    /// </summary>
    public static SolidColorBrush CreateSpotlightFill(IslandStyleKind style, bool materialApplied, bool light)
    {
        if (style == IslandStyleKind.Apple) return new SolidColorBrush(AppleSpotlightSurface);
        return new SolidColorBrush(materialApplied
            ? Pick(light, FluentSpotlightSurfaceDark, FluentSpotlightSurfaceLight)
            : Pick(light, FluentSpotlightSolidSurfaceDark, FluentSpotlightSolidSurfaceLight));
    }

    /// <summary>聚光卡描边：两种风格都用 1px 细描边，把卡片从暗化遮罩里"抠"出来。</summary>
    public static SolidColorBrush CreateSpotlightStroke(IslandStyleKind style, bool light)
        => new(Pick(light, SpotlightStrokeDark, SpotlightStrokeLight));

    // ---- 临时消息（岛体内部的反馈条，不参与点击形状，但圆角与配色必须和岛体同一套规则）----

    /// <summary>
    /// 临时内容高过这个值就按"卡片"取圆角，而不是按"胶囊"。
    /// 一行消息 40 高、圆角取高度一半是颗胶囊；两行正文的高卡再用高度一半就成了跑道形，不像卡片。
    /// </summary>
    public const double MessageCardHeight = 44;

    private const double AppleMessageChipRadius = 8;
    private const double FluentMessageChipRadius = 6;
    private static readonly Color AppleMessageChipFill = Color.FromArgb(255, 46, 46, 50);   // 与空闲点同色：没有强调色时的中性芯片
    private static readonly Color FluentMessageChipFillDark = Color.FromArgb(30, 255, 255, 255);
    private static readonly Color FluentMessageChipFillLight = Color.FromArgb(30, 0, 0, 0);
    private static readonly Color MessageChipStrokeDark = Color.FromArgb(38, 255, 255, 255);
    private static readonly Color MessageChipStrokeLight = Color.FromArgb(38, 0, 0, 0);
    private static readonly Color MessageTitleDark = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color MessageTitleLight = Color.FromArgb(255, 28, 28, 30);
    private static readonly Color MessageTextDark = Color.FromArgb(152, 255, 255, 255);
    private static readonly Color MessageTextLight = Color.FromArgb(152, 0, 0, 0);

    /// <summary>消息图标芯片圆角。</summary>
    public static double ResolveMessageChipRadius(IslandStyleKind style, double chipSize)
        => Math.Min(style == IslandStyleKind.Fluent ? FluentMessageChipRadius : AppleMessageChipRadius, chipSize / 2);

    /// <summary>
    /// 消息图标芯片底色：插件给了强调色就用它（消息的身份色），没给就用中性芯片 ——
    /// 宿主自己的消息（启动提示、更新提示、投放反馈）因此不会平白冒出一个蓝色圆点。
    /// </summary>
    public static SolidColorBrush CreateMessageChipFill(IslandStyleKind style, Color? accent, bool light)
    {
        if (accent is { } color) return new SolidColorBrush(color);
        return new SolidColorBrush(style == IslandStyleKind.Fluent
            ? Pick(light, FluentMessageChipFillDark, FluentMessageChipFillLight)
            : AppleMessageChipFill);
    }

    /// <summary>消息图标芯片描边：仅 Fluent，和岛体描边、投放卡片描边同一档。</summary>
    public static SolidColorBrush? CreateMessageChipStroke(IslandStyleKind style, bool light)
        => style == IslandStyleKind.Fluent
            ? new SolidColorBrush(Pick(light, MessageChipStrokeDark, MessageChipStrokeLight))
            : null;

    /// <summary>
    /// 消息图标芯片里的图标色：插件给了强调色就用白（强调色都是饱和色，白图标才亮得起来），
    /// 中性芯片则跟着主题走 —— 浅色主题下芯片是浅底，再用白图标就什么都看不见了。
    /// </summary>
    public static Color MessageChipGlyphColor(Color? accent, bool light)
        => accent is not null ? MessageTitleDark : Pick(light, MessageTitleDark, MessageTitleLight);

    /// <summary>消息标题色：深色主题近纯白 / 浅色主题近纯黑，两种底衬上都要顶得住。</summary>
    public static SolidColorBrush CreateMessageTitleBrush(bool light)
        => new(Pick(light, MessageTitleDark, MessageTitleLight));

    /// <summary>消息正文色：比标题低一档（六成），两行排在一起才有主次。</summary>
    public static SolidColorBrush CreateMessageTextBrush(bool light)
        => new(Pick(light, MessageTextDark, MessageTextLight));

    // ---- 文件投放卡片（岛体内部的小卡片，不参与点击形状，但圆角与配色必须和岛体同一套规则）----

    private const double AppleDropTileRadius = 16;
    private const double FluentDropTileRadius = 8;
    private static readonly Color AppleDropTileFill = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color FluentDropTileFillDark = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color FluentDropTileFillLight = Color.FromArgb(26, 0, 0, 0);
    private static readonly Color DropTileStrokeDark = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color DropTileStrokeLight = Color.FromArgb(20, 0, 0, 0);
    private static readonly Color DropTileNeutralHighlightDark = Color.FromArgb(51, 255, 255, 255);
    private static readonly Color DropTileNeutralHighlightLight = Color.FromArgb(51, 0, 0, 0);

    /// <summary>投放卡片圆角。</summary>
    public static double ResolveDropTileRadius(IslandStyleKind style, double tileHeight)
        => Math.Min(style == IslandStyleKind.Fluent ? FluentDropTileRadius : AppleDropTileRadius, tileHeight / 2);

    /// <summary>投放卡片底色：黑胶囊上用一档很浅的白（Apple），材质上稍亮（深色）/ 稍暗（浅色）一档。</summary>
    public static SolidColorBrush CreateDropTileFill(IslandStyleKind style, bool materialApplied, bool light)
        => new(style == IslandStyleKind.Apple
            ? AppleDropTileFill
            : Pick(light, FluentDropTileFillDark, FluentDropTileFillLight));

    /// <summary>投放卡片描边：仅 Fluent 使用，和岛体描边同一档。</summary>
    public static SolidColorBrush? CreateDropTileStroke(IslandStyleKind style, bool light)
        => style == IslandStyleKind.Fluent
            ? new SolidColorBrush(Pick(light, DropTileStrokeDark, DropTileStrokeLight))
            : null;

    /// <summary>投放卡片高亮层：有强调色就用强调色（保留 40% 透出底色），没有就用中性白（深色）/ 中性黑（浅色）20%。</summary>
    public static SolidColorBrush CreateDropTileHighlight(IslandStyleKind style, Color? accent, bool light)
    {
        if (accent is not { } color) return new SolidColorBrush(Pick(light, DropTileNeutralHighlightDark, DropTileNeutralHighlightLight));
        return new SolidColorBrush(Color.FromArgb(102, color.R, color.G, color.B));
    }

    // ---- 岛体内部的次级文字（投放面板的摘要与卡片标签）----

    private static readonly Color PanelTextPrimaryDark = Color.FromArgb(240, 255, 255, 255);
    private static readonly Color PanelTextPrimaryLight = Color.FromArgb(240, 28, 28, 30);
    private static readonly Color PanelTextSecondaryDark = Color.FromArgb(215, 255, 255, 255);
    private static readonly Color PanelTextSecondaryLight = Color.FromArgb(215, 28, 28, 30);
    private static readonly Color PanelTextHintDark = Color.FromArgb(140, 255, 255, 255);
    private static readonly Color PanelTextHintLight = Color.FromArgb(140, 0, 0, 0);

    /// <summary>投放面板的标题文字（摘要标题、卡片标签）。</summary>
    public static SolidColorBrush CreatePanelTextBrush(bool light)
        => new(Pick(light, PanelTextPrimaryDark, PanelTextPrimaryLight));

    /// <summary>投放面板的次级文字（卡片图标）。</summary>
    public static SolidColorBrush CreatePanelSecondaryBrush(bool light)
        => new(Pick(light, PanelTextSecondaryDark, PanelTextSecondaryLight));

    /// <summary>投放面板的提示文字（摘要副标题）。</summary>
    public static SolidColorBrush CreatePanelHintBrush(bool light)
        => new(Pick(light, PanelTextHintDark, PanelTextHintLight));

    private static readonly Color PanelFadeDark = Color.FromArgb(230, 0, 0, 0);
    private static readonly Color PanelFadeLight = Color.FromArgb(230, 255, 255, 255);

    /// <summary>
    /// 投放面板左右渐隐的起点色（远端取同色透明的 alpha 0）。必须和面板底色同色相：
    /// 黑胶囊上是黑（现状），浅色材质上是白 —— 浅色面板边上压一道黑渐变会读成"脏边"。
    /// </summary>
    public static Color PanelEdgeFadeColor(bool light) => Pick(light, PanelFadeDark, PanelFadeLight);
}
