using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinIsland.Island;

namespace WinIsland.Core;

public sealed class IslandService
{
    private readonly IslandWindow _island;
    private readonly SettingsService _settings;
    private readonly SpotlightHost _spotlight;
    private readonly List<SettingsPageDescriptor> _pages = new();
    // 投放目标台账：宿主内置的先注册（Order 900+），插件注册的按 Order 排在前面
    private readonly List<(string Owner, IslandDropTarget Target)> _dropTargets = new();

    public IslandService(IslandWindow island, SettingsService settings, IPluginLogger log)
    {
        _island = island;
        _settings = settings;
        _spotlight = new SpotlightHost(island, log);
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

    public void OpenSpotlight(string ownerId, IslandSpotlight spotlight)
        => RunOnUI(() => _spotlight.Show(ownerId, spotlight));

    public void CloseSpotlight(string ownerId)
        => RunOnUI(() => _spotlight.Close(ownerId));

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

    /// <summary>
    /// 注册一个投放目标。同一个 (ownerId, target.Id) 重复注册是覆盖语义；
    /// 台账一变就把整份快照推给岛，岛在投放会话中会就地重建卡片。
    /// </summary>
    public void AddDropTarget(string ownerId, IslandDropTarget target)
        => RunOnUI(() =>
        {
            _dropTargets.RemoveAll(t => t.Owner == ownerId && t.Target.Id == target.Id);
            _dropTargets.Add((ownerId, target));
            _dropTargets.Sort((a, b) => a.Target.Order.CompareTo(b.Target.Order));
            _island.SetDropTargets(_dropTargets.ToArray());
        });

    public void RemoveDropTarget(string ownerId, string targetId)
        => RunOnUI(() =>
        {
            if (_dropTargets.RemoveAll(t => t.Owner == ownerId && t.Target.Id == targetId) > 0)
            {
                _island.SetDropTargets(_dropTargets.ToArray());
            }
        });

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
