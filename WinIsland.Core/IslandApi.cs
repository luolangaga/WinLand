using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinIsland.Core;

public interface ICallbackServer
{
    int Port { get; }
    string BaseUrl { get; }
    bool IsRunning { get; }
    void RegisterRoute(string path, Func<System.Text.Json.JsonElement, Task> handler);
    void UnregisterRoute(string path, Func<System.Text.Json.JsonElement, Task> handler);
}

public interface IDynamicIslandApi
{
    DispatcherQueue Dispatcher { get; }
    ISettingsStore Settings { get; }
    ICallbackServer Callbacks { get; }
    void SetLiveContent(string ownerId, IslandLiveContent? content);
    void SendMessage(IslandMessage message);
    void ShowContent(UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null);
    void DismissTemporary();
    void AddSettingsPage(SettingsPageDescriptor page);
    void OpenSettings(string? pageId = null);
}

public interface ISettingsStore
{
    T Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);
    event Action<string>? Changed;
}

public interface IIslandModule
{
    string Id { get; }
    string DisplayName { get; }
    Task InitializeAsync(IDynamicIslandApi api);
    Task ShutdownAsync();
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
    public Windows.Foundation.Size CompactSize { get; init; } = new(230, 40);
    public Windows.Foundation.Size ExpandedSize { get; init; } = new(420, 150);
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
