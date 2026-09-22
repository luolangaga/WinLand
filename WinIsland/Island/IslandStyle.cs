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
}
