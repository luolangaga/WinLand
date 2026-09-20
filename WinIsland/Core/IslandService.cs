using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.Island;

namespace WinIsland.Core;

public sealed class IslandService
{
    private readonly IslandWindow _island;
    private readonly SettingsService _settings;
    private readonly List<SettingsPageDescriptor> _pages = new();

    public IslandService(IslandWindow island, SettingsService settings)
    {
        _island = island;
        _settings = settings;
    }

    public event Action? SettingsPagesChanged;

    public event Action<string?>? SettingsOpenRequested;

    public IReadOnlyList<SettingsPageDescriptor> SettingsPages => _pages;

    public SettingsService Settings => _settings;

    public DispatcherQueue Dispatcher => _island.DispatcherQueue;

    public void SetLiveContent(string ownerId, IslandLiveContent? content)
        => RunOnUI(() => _island.SetLive(ownerId, content));

    public void SendMessage(IslandMessage message)
        => RunOnUI(() => _island.ShowMessage(message));

    public void SendMessage(string ownerId, IslandMessage message)
        => RunOnUI(() => _island.ShowMessage(message, ownerId));

    public void ShowContent(UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null)
        => RunOnUI(() => _island.ShowTemporary(content, size, duration ?? TimeSpan.FromSeconds(5)));

    public void ShowContent(string ownerId, UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null)
        => RunOnUI(() => _island.ShowTemporary(content, size, duration ?? TimeSpan.FromSeconds(5), ownerId));

    public void DismissTemporary()
        => RunOnUI(() => _island.DismissTemporary());

    public void DismissTemporary(string ownerId)
        => RunOnUI(() => _island.DismissTemporary(ownerId));

    public void AddSettingsPage(SettingsPageDescriptor page)
        => RunOnUI(() =>
        {
            _pages.RemoveAll(p => p.Id == page.Id);
            _pages.Add(page);
            SortPages();
            SettingsPagesChanged?.Invoke();
        });

    public void RemoveSettingsPage(string pageId)
        => RunOnUI(() =>
        {
            if (_pages.RemoveAll(p => p.Id == pageId) > 0)
            {
                SettingsPagesChanged?.Invoke();
            }
        });

    public void OpenSettings(string? pageId = null)
        => RunOnUI(() => SettingsOpenRequested?.Invoke(pageId));

    private void SortPages()
    {
        var sorted = _pages.OrderBy(p => p.Order).ToList();
        _pages.Clear();
        _pages.AddRange(sorted);
    }

    private void RunOnUI(Action action)
    {
        if (Dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            Dispatcher.TryEnqueue(() => action());
        }
    }
}
