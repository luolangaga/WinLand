using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.Island;

namespace WinIsland.Core;

public sealed class IslandService : IDynamicIslandApi
{
    private readonly IslandWindow _island;
    private readonly SettingsService _settings;
    private readonly List<SettingsPageDescriptor> _pages = new();

    public event Action? SettingsPagesChanged;
    public event Action<string?>? SettingsOpenRequested;

    public IslandService(IslandWindow island, SettingsService settings)
    {
        _island = island;
        _settings = settings;
    }

    public IReadOnlyList<SettingsPageDescriptor> SettingsPages => _pages;
    public DispatcherQueue Dispatcher => _island.DispatcherQueue;
    public ISettingsStore Settings => _settings;

    public void SetLiveContent(string ownerId, IslandLiveContent? content)
        => RunOnUI(() => _island.SetLive(ownerId, content));

    public void SendMessage(IslandMessage message)
        => RunOnUI(() => _island.ShowMessage(message));

    public void ShowContent(UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null)
        => RunOnUI(() => _island.ShowTemporary(content, size, duration ?? TimeSpan.FromSeconds(5)));

    public void DismissTemporary()
        => RunOnUI(_island.DismissTemporary);

    public void AddSettingsPage(SettingsPageDescriptor page)
        => RunOnUI(() =>
        {
            _pages.RemoveAll(p => p.Id == page.Id);
            _pages.Add(page);
            SettingsPagesChanged?.Invoke();
        });

    public void OpenSettings(string? pageId = null)
        => RunOnUI(() => SettingsOpenRequested?.Invoke(pageId));

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
