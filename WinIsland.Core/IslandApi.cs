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
