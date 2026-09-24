using Microsoft.UI.Xaml;

namespace WinIsland.Core;

public interface ISettingsStore
{
    T Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);
    event Action<string>? Changed;
}

public interface IMorphView
{
    UIElement View { get; }
    void AnimateToExpanded(TimeSpan duration);
    void AnimateToCompact(TimeSpan duration);
}

public sealed class IslandLiveContent
{
    public int Priority { get; init; }
    public string? OwnerLabel { get; init; }
    public string? OwnerGlyph { get; init; }
    public Windows.UI.Color? OwnerAccent { get; init; }
    public IMorphView? MorphView { get; init; }
    public UIElement? CompactContent { get; init; }
    public UIElement? ExpandedContent { get; init; }
    public Action? OnTap { get; init; }
    public Windows.Foundation.Size CompactSize { get; init; } = new(230, 40);
    public Windows.Foundation.Size ExpandedSize { get; init; } = new(420, 150);
}

/// <summary>
/// 「超级展开」聚光卡：插件自己决定何时打开，由宿主在一个独立的覆盖窗口里
/// 从岛体位置带倾角飞入、居中放大展示。尺寸与内容完全由插件决定；
/// 关闭（点击卡片外区域 / Esc / 插件停用 / 被其他插件替换）由宿主负责并回调 <see cref="OnClosed"/>。
/// </summary>
public sealed class IslandSpotlight
{
    /// <summary>
    /// 卡片内容。必须是**独立于岛视图**的可视树 —— 每个窗口一棵树，同一个 UIElement 不能同时挂在两个窗口里。
    /// </summary>
    public required UIElement Content { get; init; }

    /// <summary>卡片期望尺寸（DIP）。宿主会按显示器工作区夹取并居中，视图请用自适应布局。</summary>
    public Windows.Foundation.Size Size { get; init; } = new(640, 440);

    /// <summary>关闭回调：任何关闭路径都会触发一次（宿主已做异常保护，抛异常只记日志）。</summary>
    public Action? OnClosed { get; init; }
}

public sealed class IslandMessage
{
    public required string Title { get; init; }
    public string? Text { get; init; }
    public string Glyph { get; init; } = "\uE8BD";
    public Windows.UI.Color? AccentColor { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(4);
}

/// <summary>拖入载荷的类型。一次拖拽只会命中其中一种，可以按位组合表示"接受哪几种"。</summary>
[Flags]
public enum IslandDropKind
{
    /// <summary>读不出来 / 不支持的类型（也会在载荷还没读完时作为占位值）。</summary>
    None = 0,

    /// <summary>文件或文件夹（资源管理器里拖过来的那种）。</summary>
    Files = 1,

    /// <summary>文本（编辑器、浏览器里选中一段拖过来；网址链接也走这里）。</summary>
    Text = 2,

    /// <summary>图片（浏览器里拖图片、截图工具里拖图）。</summary>
    Image = 4,

    /// <summary>以上全部。</summary>
    All = Files | Text | Image,
}

/// <summary>
/// 文件投放目标：把文件/文件夹拖到岛上时，岛会展开成一排投放卡片，
/// 拖到某张卡片上松手就执行它。展示、滚动与命中全部由宿主接管，插件只负责拿到路径后干活。
/// 需要宿主 2.2.0 及以上（清单里用 <c>min_host_version: "2.2.0"</c> 做门槛）。
/// </summary>
public sealed class IslandDropTarget
{
    /// <summary>插件内唯一的标识；重复注册同一个 Id 会覆盖前一个。</summary>
    public required string Id { get; init; }

    /// <summary>卡片标题，例如「加入播放列表」。</summary>
    public required string Title { get; init; }

    /// <summary>卡片图标字形（Segoe Fluent Icons / Segoe MDL2 Assets）。</summary>
    public string Glyph { get; init; } = "\uE8B7";

    /// <summary>卡片副标题，可为空。</summary>
    public string? Hint { get; init; }

    /// <summary>卡片悬停高亮用的强调色；不填用中性白。</summary>
    public Windows.UI.Color? AccentColor { get; init; }

    /// <summary>排序（升序）。宿主内置的动作用 900+，插件默认 0 会排在它们前面。</summary>
    public int Order { get; init; }

    /// <summary>
    /// 接受哪些载荷类型，默认只收文件（<see cref="IslandDropKind.Files"/>）。
    /// 可以组合，例如 <c>IslandDropKind.Files | IslandDropKind.Image</c>；
    /// 载荷类型不匹配时卡片变暗且无法投放。
    /// </summary>
    public IslandDropKind Kinds { get; init; } = IslandDropKind.Files;

    /// <summary>
    /// 文件扩展名白名单（含点，例如 ".mp3"；忽略大小写），**只对 <see cref="IslandDropKind.Files"/> 生效**。
    /// null 或空表示接受任何文件；非空时载荷里只要有一项匹配就接受这张卡片，全不匹配时卡片变暗且无法投放。
    /// </summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>
    /// 松手时执行。返回的文案由宿主弹一条临时消息反馈给用户（返回 null 表示不提示）。
    /// 在 UI 线程被调用；异常由宿主隔离，只记日志、不会崩宿主。
    /// </summary>
    public required Func<IslandDropContext, Task<string?>> Handler { get; init; }
}

/// <summary>一次投放的载荷。一次拖拽只会是文件、文本、图片中的一种。</summary>
public sealed class IslandDropContext
{
    /// <summary>载荷类型（与目标声明的 <see cref="IslandDropTarget.Kinds"/> 匹配的那一种）。</summary>
    public IslandDropKind Kind { get; init; } = IslandDropKind.Files;

    /// <summary>拖进来的文件/文件夹的完整路径（<see cref="IslandDropKind.Files"/> 时有值）。</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

    /// <summary>对应的文件名（含扩展名），与 <see cref="Paths"/> 一一对应，便于直接展示。</summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();

    /// <summary>文本内容（<see cref="IslandDropKind.Text"/> 时有值；网址链接也在这里）。</summary>
    public string? Text { get; init; }

    /// <summary>图片的原始字节（<see cref="IslandDropKind.Image"/> 时有值，保留源格式，通常是 PNG / JPEG）。</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>这批载荷有多少项：文件是路径条数，文本/图片算 1。</summary>
    public int ItemCount => Kind == IslandDropKind.Files ? Paths.Count : 1;

    /// <summary>是否只有一项（文件只拖了一个，或载荷是文本/图片）。</summary>
    public bool IsSingle => ItemCount == 1;
}

public sealed class SettingsPageDescriptor
{
    public SettingsPageDescriptor(string id, string title, string glyph, Func<UIElement> factory, int order = 0)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
        Factory = factory;
        Order = order;
    }

    public string Id { get; }
    public string Title { get; }
    public string Glyph { get; }
    public Func<UIElement> Factory { get; }
    public int Order { get; }
}
