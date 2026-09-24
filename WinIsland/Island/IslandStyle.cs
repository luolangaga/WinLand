using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinIsland.Island;

/// <summary>岛屿外观风格。</summary>
public enum IslandStyleKind
{
    /// <summary>Apple 灵动岛：不透明纯黑胶囊。</summary>
    Apple,

    /// <summary>Windows Fluent：元素级系统材质 + 小圆角 + 1px 描边。</summary>
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
    private static readonly Color FluentSurface = Color.FromArgb(64, 0, 0, 0);
    private static readonly Color FluentSolidSurface = Color.FromArgb(230, 32, 32, 32);
    private static readonly Color FluentStroke = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color AppleIdleDot = Color.FromArgb(255, 46, 46, 50);
    private static readonly Color FluentIdleDot = Color.FromArgb(115, 255, 255, 255);
    private static readonly Color AppleSpotlightSurface = Color.FromArgb(245, 12, 12, 16);
    private static readonly Color FluentSpotlightSurface = Color.FromArgb(232, 38, 38, 42);
    private static readonly Color FluentSpotlightSolidSurface = Color.FromArgb(235, 26, 26, 30);
    private static readonly Color SpotlightStroke = Color.FromArgb(26, 255, 255, 255);

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
    /// 岛体底衬：Apple 不透明纯黑；Fluent 有系统材质时是半透明深色薄纱（压在材质之上保证白字对比度），
    /// 材质不可用时退回接近不透明的深灰，文字依旧可读。
    /// </summary>
    public static SolidColorBrush CreateSurfaceFill(IslandStyleKind style, bool materialApplied)
    {
        if (style == IslandStyleKind.Apple) return new SolidColorBrush(AppleSurface);
        return new SolidColorBrush(materialApplied ? FluentSurface : FluentSolidSurface);
    }

    /// <summary>岛体描边：仅 Fluent 使用 1px 浅色描边。</summary>
    public static SolidColorBrush? CreateStroke(IslandStyleKind style)
        => style == IslandStyleKind.Fluent ? new SolidColorBrush(FluentStroke) : null;

    /// <summary>空闲小点颜色：纯黑胶囊上用深灰（现状），系统材质上用半透明白。</summary>
    public static Color IdleDotColor(IslandStyleKind style)
        => style == IslandStyleKind.Fluent ? FluentIdleDot : AppleIdleDot;

    /// <summary>聚光卡圆角。</summary>
    public static double ResolveSpotlightRadius(IslandStyleKind style)
        => style == IslandStyleKind.Fluent ? FluentSpotlightRadius : AppleSpotlightRadius;

    /// <summary>
    /// 聚光卡底衬：Apple 近不透明纯黑（延续灵动岛的黑胶囊身份），
    /// Fluent 半透明炭灰（能看到被遮罩压暗的桌面，材质可用时更透一些保证"系统材质感"）。
    /// 覆盖窗是独立的全屏透明窗，这里刻意不用元素级 Acrylic —— 那需要窗口自己的材质背衬，
    /// 在「透明窗 + 遮罩」的组合里会退化成不可控的色块。
    /// </summary>
    public static SolidColorBrush CreateSpotlightFill(IslandStyleKind style, bool materialApplied)
    {
        if (style == IslandStyleKind.Apple) return new SolidColorBrush(AppleSpotlightSurface);
        return new SolidColorBrush(materialApplied ? FluentSpotlightSurface : FluentSpotlightSolidSurface);
    }

    /// <summary>聚光卡描边：两种风格都用 1px 浅色描边，把卡片从暗化遮罩里"抠"出来。</summary>
    public static SolidColorBrush CreateSpotlightStroke(IslandStyleKind style)
        => new(SpotlightStroke);

    // ---- 临时消息（岛体内部的反馈条，不参与点击形状，但圆角与配色必须和岛体同一套规则）----

    /// <summary>
    /// 临时内容高过这个值就按"卡片"取圆角，而不是按"胶囊"。
    /// 一行消息 40 高、圆角取高度一半是颗胶囊；两行正文的高卡再用高度一半就成了跑道形，不像卡片。
    /// </summary>
    public const double MessageCardHeight = 44;

    private const double AppleMessageChipRadius = 8;
    private const double FluentMessageChipRadius = 6;
    private static readonly Color AppleMessageChipFill = Color.FromArgb(255, 46, 46, 50);   // 与空闲点同色：没有强调色时的中性芯片
    private static readonly Color FluentMessageChipFill = Color.FromArgb(30, 255, 255, 255);
    private static readonly Color MessageChipStroke = Color.FromArgb(38, 255, 255, 255);
    private static readonly Color MessageTitleColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color MessageTextColor = Color.FromArgb(152, 255, 255, 255);

    /// <summary>消息图标芯片圆角。</summary>
    public static double ResolveMessageChipRadius(IslandStyleKind style, double chipSize)
        => Math.Min(style == IslandStyleKind.Fluent ? FluentMessageChipRadius : AppleMessageChipRadius, chipSize / 2);

    /// <summary>
    /// 消息图标芯片底色：插件给了强调色就用它（消息的身份色），没给就用中性芯片 ——
    /// 宿主自己的消息（启动提示、更新提示、投放反馈）因此不会平白冒出一个蓝色圆点。
    /// </summary>
    public static SolidColorBrush CreateMessageChipFill(IslandStyleKind style, Color? accent)
        => accent is { } color
            ? new SolidColorBrush(color)
            : new SolidColorBrush(style == IslandStyleKind.Fluent ? FluentMessageChipFill : AppleMessageChipFill);

    /// <summary>消息图标芯片描边：仅 Fluent，和岛体描边、投放卡片描边同一档。</summary>
    public static SolidColorBrush? CreateMessageChipStroke(IslandStyleKind style)
        => style == IslandStyleKind.Fluent ? new SolidColorBrush(MessageChipStroke) : null;

    /// <summary>消息标题色：近纯白（两种底衬上都要顶得住）。</summary>
    public static SolidColorBrush CreateMessageTitleBrush() => new(MessageTitleColor);

    /// <summary>消息正文色：六成白，比标题低一档，两行排在一起才有主次。</summary>
    public static SolidColorBrush CreateMessageTextBrush() => new(MessageTextColor);

    // ---- 文件投放卡片（岛体内部的小卡片，不参与点击形状，但圆角与配色必须和岛体同一套规则）----

    private const double AppleDropTileRadius = 16;
    private const double FluentDropTileRadius = 8;
    private static readonly Color AppleDropTileFill = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color FluentDropTileFill = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color DropTileStroke = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color DropTileNeutralHighlight = Color.FromArgb(51, 255, 255, 255);

    /// <summary>投放卡片圆角。</summary>
    public static double ResolveDropTileRadius(IslandStyleKind style, double tileHeight)
        => Math.Min(style == IslandStyleKind.Fluent ? FluentDropTileRadius : AppleDropTileRadius, tileHeight / 2);

    /// <summary>投放卡片底色：黑胶囊上用一档很浅的白（Apple），材质上稍亮一点（Fluent）。</summary>
    public static SolidColorBrush CreateDropTileFill(IslandStyleKind style, bool materialApplied)
        => new(style == IslandStyleKind.Apple ? AppleDropTileFill : FluentDropTileFill);

    /// <summary>投放卡片描边：仅 Fluent 使用，和岛体描边同一档。</summary>
    public static SolidColorBrush? CreateDropTileStroke(IslandStyleKind style)
        => style == IslandStyleKind.Fluent ? new SolidColorBrush(DropTileStroke) : null;

    /// <summary>投放卡片高亮层：有强调色就用强调色（保留 40% 透出底色），没有就用中性白 20%。</summary>
    public static SolidColorBrush CreateDropTileHighlight(IslandStyleKind style, Color? accent)
    {
        if (accent is not { } color) return new SolidColorBrush(DropTileNeutralHighlight);
        return new SolidColorBrush(Color.FromArgb(102, color.R, color.G, color.B));
    }
}
