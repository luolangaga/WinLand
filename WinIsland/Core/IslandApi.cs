using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinIsland.Core;

/// <summary>
/// 灵动岛对外核心接口。模块 (IIslandModule) 通过它与灵动岛交互。
/// 主程序拥有全部尺寸/圆角/悬浮/临时消息控制权，插件只提供内容。
/// </summary>
public interface IDynamicIslandApi
{
    /// <summary>UI 线程调度器（所有 UIElement 必须在此线程创建）。</summary>
    DispatcherQueue Dispatcher { get; }

    /// <summary>持久化设置存储（JSON，位于 %LocalAppData%\WinIsland）。</summary>
    ISettingsStore Settings { get; }

    /// <summary>
    /// 注册/取消常驻内容。主程序自动处理悬浮展开/收起、尺寸动画、圆角。
    /// 传 null 取消注册。多个模块共存时后设置者优先。
    /// </summary>
    void SetLiveContent(string ownerId, IslandLiveContent? content);

    /// <summary>向灵动岛发送一条临时文字消息（自动展开，超时后恢复常驻内容）。</summary>
    void SendMessage(IslandMessage message);

    /// <summary>在灵动岛上临时展示任意 XAML 控件（超时后恢复常驻内容）。</summary>
    void ShowContent(UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null);

    /// <summary>立即收起并清除当前临时内容。</summary>
    void DismissTemporary();

    /// <summary>向设置窗口注册一个自定义设置页面。</summary>
    void AddSettingsPage(SettingsPageDescriptor page);

    /// <summary>打开设置窗口（可指定页面 Id）。</summary>
    void OpenSettings(string? pageId = null);
}

/// <summary>简单的键值设置存储。</summary>
public interface ISettingsStore
{
    T Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);
    /// <summary>某个键被修改后触发（参数为键名，UI 线程回调）。</summary>
    event Action<string>? Changed;
}

/// <summary>灵动岛功能模块。实现它并在 App 中注册即可扩展灵动岛。</summary>
public interface IIslandModule
{
    string Id { get; }
    string DisplayName { get; }
    Task InitializeAsync(IDynamicIslandApi api);
    Task ShutdownAsync();
}

/// <summary>
/// 可变形视图接口：由插件的内容视图实现。
/// 主程序在展开/收起时调用，与岛体尺寸动画同步执行，
/// 让内部元素流畅变形（而非交叉淡切两个视图）。
/// </summary>
public interface IMorphView
{
    /// <summary>要显示的 UI 元素（即实现此接口的控件自身）。</summary>
    UIElement View { get; }

    /// <summary>动画到展开态（与岛体放大同步）。</summary>
    void AnimateToExpanded(TimeSpan duration);

    /// <summary>动画到紧凑态（与岛体缩小同步）。</summary>
    void AnimateToCompact(TimeSpan duration);
}

/// <summary>常驻在灵动岛上的内容。</summary>
public sealed class IslandLiveContent
{
    /// <summary>
    /// 变形视图：若设置，主程序使用单视图模式，
    /// 在紧凑↔展开之间调用 IMorphView 方法让元素流畅变形。
    /// 设置后 CompactContent/ExpandedContent 被忽略。
    /// </summary>
    public IMorphView? MorphView { get; init; }

    /// <summary>紧凑（胶囊）状态下显示的控件（MorphView 模式下忽略）。</summary>
    public UIElement? CompactContent { get; init; }

    /// <summary>悬停展开后显示的控件（MorphView 模式下忽略，为空则不展开）。</summary>
    public UIElement? ExpandedContent { get; init; }

    /// <summary>紧凑状态逻辑尺寸。</summary>
    public Windows.Foundation.Size CompactSize { get; init; } = new(230, 40);

    /// <summary>展开状态逻辑尺寸。</summary>
    public Windows.Foundation.Size ExpandedSize { get; init; } = new(420, 150);
}

/// <summary>发送到灵动岛的文字消息。</summary>
public sealed class IslandMessage
{
    public required string Title { get; init; }
    public string? Text { get; init; }
    public string Glyph { get; init; } = "\uE8BD";
    public Windows.UI.Color? AccentColor { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(4);
}

/// <summary>注册到设置窗口的页面描述。</summary>
public sealed class SettingsPageDescriptor
{
    public SettingsPageDescriptor(string id, string title, string glyph, Func<UIElement> factory)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
        Factory = factory;
    }

    public string Id { get; }
    public string Title { get; }
    public string Glyph { get; }
    public Func<UIElement> Factory { get; }
}
