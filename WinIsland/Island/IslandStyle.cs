using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
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

    /// <summary>
    /// Apple 胶囊是不是也用窗口材质（液态玻璃）。默认关 ——
    /// 关着的时候 Apple 就是它本来的样子：不透明纯黑胶囊（这是它的身份）。
    /// 开了之后 Apple 保留胶囊形状与圆角，但内部变成半透明黑纱，
    /// 透过它能看到窗口级 Desktop Acrylic 实时模糊出来的背后内容。
    /// </summary>
    public const string AppleGlassKey = "island.appleGlass";

    /// <summary>
    /// Apple 玻璃模式当前是否生效（由 <c>IslandWindow.ApplyBackdrop</c> 在应用外观前写入）。
    /// 做成静态是为了不给下面每一个取色方法都加一个 <c>appleGlass</c> 参数 ——
    /// 整个进程只有一个岛窗口，这个值在一次 ApplyStyle 期间是稳定的。
    /// </summary>
    public static bool AppleGlassEnabled { get; set; }

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

    /// <summary>
    /// Apple 玻璃胶囊的黑纱。取 **66%（高级磨砂黑）**：
    /// 极透（12%）会把白字对比度冲掉、看着像块塑料片；
    /// 不透（100%）就是原来的纯黑胶囊、材质白挂。
    /// 66% 是"有分量的磨砂"—— 背后内容隐约可见（磨砂感），白字依旧顶得住（可读性）。
    /// </summary>
    private static readonly Color AppleGlassSurface = Color.FromArgb(168, 0, 0, 0);

    /// <summary>Apple 玻璃胶囊在浅色系统下的黑纱：浅色材质上不压纱，文字用深色即可。</summary>
    private static readonly Color AppleGlassSurfaceLight = Color.FromArgb(0, 0, 0, 0);

    /// <summary>Apple 玻璃胶囊的亮边：对角渐变用（左上最亮 → 右下消失）。</summary>
    private static readonly Color AppleGlassRimBrightDark = Color.FromArgb(90, 255, 255, 255);
    private static readonly Color AppleGlassRimDimDark = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color AppleGlassRimBrightLight = Color.FromArgb(70, 0, 0, 0);
    private static readonly Color AppleGlassRimDimLight = Color.FromArgb(18, 0, 0, 0);
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

    /// <summary>Apple 玻璃模式下的聚光卡底衬：比原来透一档（245 → 205）。</summary>
    private static readonly Color AppleSpotlightGlassSurface = Color.FromArgb(205, 12, 12, 16);
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
    /// <summary>
    /// 材质色调：把岛体那层「纱」的颜色**交给窗口材质本身**，而不是靠 Border 的底衬涂。
    ///
    /// 为什么必须这样：点击形状比岛体渲染范围外扩 1 DIP（<c>IslandWindow.ShapeSlackDip</c>），
    /// 材质铺满整个形状，而 Border 底衬只盖到实际尺寸 —— 外围那一圈只有材质、没有纱，
    /// 在深色主题下读成「没铺满的一圈亮边」（AGENTS.md 把这条记为已知代价）。
    /// 颜色交给材质后整块形状颜色完全一致，这一圈自然消失，而且不必动 ShapeSlack（圆角锯齿修复不受影响）。
    /// </summary>
    public static (Color Tint, float TintOpacity, float Luminosity) ResolveMaterialTint(
        IslandStyleKind style, bool light)
    {
        if (style == IslandStyleKind.Apple)
        {
            // Apple 哑光黑胶囊：颜色照旧由不透明底衬负责，材质不需要色调
            return (Color.FromArgb(255, 0, 0, 0), 0f, 0f);
        }

        // 浅色主题不压纱（压了就把材质冲成平白板）；深色主题压同样的 25% 黑，保证白字对比度
        return light
            ? (Color.FromArgb(255, 0, 0, 0), 0f, 0f)
            : (Color.FromArgb(255, 0, 0, 0), FluentSurfaceDark.A / 255f, 0f);
    }

    public static SolidColorBrush CreateSurfaceFill(IslandStyleKind style, bool materialApplied, bool light)
    {
        if (style == IslandStyleKind.Apple)
        {
            // 玻璃模式的颜色已经做进材质本身了（见 IslandBackdrop.ApplyAcrylicTint）——
            // 这里必须留**全透明**，否则底衬只盖到实际尺寸、外围那 1 DIP 又会露出一圈没铺满
            if (materialApplied && AppleGlassEnabled)
            {
                return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            }

            return new SolidColorBrush(AppleSurface);
        }

        // 有材质时底衬留**全透明** —— 那层纱已经由材质色调负责（见 ResolveMaterialTint），
        // 在这里再涂一层只盖到实际尺寸的纱，就会重新长出那一圈亮边。
        if (materialApplied) return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        return new SolidColorBrush(Pick(light, FluentSolidSurfaceDark, FluentSolidSurfaceLight));
    }

    /// <summary>
    /// 岛体描边：Fluent 用 1px 细描边；Apple 玻璃模式用**对角渐变**的亮边。
    ///
    /// 玻璃感的关键就在这里：均匀一圈亮边看着像描边框，
    /// 而「左上最亮 → 右下几乎消失」才是光从左上打过来的真实样子
    /// （Windows 7 Aero、iOS 26 都是这么处理的）。
    /// </summary>
    public static Brush? CreateStroke(IslandStyleKind style, bool materialApplied, bool light)
    {
        if (style == IslandStyleKind.Apple)
        {
            // 用户要的是「纯色 + 像水一样透明」：不要任何描边。
            // 一圈亮边（尤其对角渐变的）在纯色玻璃上只会读成"不协调的框"。
            return null;
        }

        return new SolidColorBrush(Pick(light, FluentStrokeDark, FluentStrokeLight));
    }

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
        if (style == IslandStyleKind.Apple)
        {
            // 玻璃胶囊模式下卡片也留一点透明度（覆盖窗没有自己的材质，
            // 所以这里透出来的是暗化遮罩 —— 只有轻微差别，但和岛体是一套观感）
            return new SolidColorBrush(materialApplied ? AppleSpotlightGlassSurface : AppleSpotlightSurface);
        }

        return new SolidColorBrush(materialApplied
            ? Pick(light, FluentSpotlightSurfaceDark, FluentSpotlightSurfaceLight)
            : Pick(light, FluentSpotlightSolidSurfaceDark, FluentSpotlightSolidSurfaceLight));
    }

    /// <summary>聚光卡描边：两种风格都用 1px 细描边，把卡片从暗化遮罩里"抠"出来。</summary>
    public static SolidColorBrush CreateSpotlightStroke(IslandStyleKind style, bool light)
        => new(Pick(light, SpotlightStrokeDark, SpotlightStrokeLight));

    // ---- Fluent 岛体上缘的 1px 高光（玻璃反光）----

    private static readonly Color TopHighlightDark = Color.FromArgb(28, 255, 255, 255);
    private static readonly Color TopHighlightLight = Color.FromArgb(72, 255, 255, 255);

    /// <summary>
    /// Fluent 岛体/卡片上缘的 1px 渐变高光（两端渐隐，圆角处自然收掉，不需要额外裁剪）。
    /// Apple 不加：它的身份是哑光纯黑胶囊，一道反光会把它读成"玻璃片"。
    /// </summary>
    public static LinearGradientBrush? CreateTopHighlight(IslandStyleKind style, bool light)
    {
        // Apple 的目标是「纯色、像水一样透明」—— 一道受光边只会让它看起来是两截。
        // 只有 Fluent（作者原生的材质风格）才用这道高光。
        if (style != IslandStyleKind.Fluent) return null;

        var color = Pick(light, TopHighlightDark, TopHighlightLight);
        var fade = Color.FromArgb((byte)(color.A * 0.45), color.R, color.G, color.B);
        var clear = Color.FromArgb(0, color.R, color.G, color.B);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        brush.GradientStops.Add(new GradientStop { Color = clear, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = fade, Offset = 0.14 });
        brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = fade, Offset = 0.86 });
        brush.GradientStops.Add(new GradientStop { Color = clear, Offset = 1 });
        return brush;
    }

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
