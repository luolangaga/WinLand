using Microsoft.UI.Xaml;
using WinIsland.Core;

namespace WinIsland.Core.Plugins;

public sealed class ScopedIslandSurface : IIslandSurface
{
    private readonly PluginInstance _owner;
    private readonly IslandService _island;
    private readonly PluginScope _scope;
    private bool _contentRevokerRegistered;
    private bool _temporaryRevokerRegistered;

    public ScopedIslandSurface(PluginInstance owner, IslandService island, PluginScope scope)
    {
        _owner = owner;
        _island = island;
        _scope = scope;
    }

    public void SetContent(IslandLiveContent? content)
    {
        if (!_owner.TryEnter("SetContent"))
        {
            return;
        }

        if (content == null)
        {
            _island.SetLiveContent(_owner.Info.Id, null);
            return;
        }

        if (!_contentRevokerRegistered)
        {
            _contentRevokerRegistered = true;
            _scope.Register(new ActionDisposable(() => _island.SetLiveContent(_owner.Info.Id, null)));
        }

        _island.SetLiveContent(_owner.Info.Id, Guard(content));
    }

    public void ShowMessage(IslandMessage message)
    {
        if (!_owner.TryEnter("ShowMessage"))
        {
            return;
        }

        RegisterTemporaryRevoker();
        _island.SendMessage(_owner.Info.Id, message);
    }

    public void Show(UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null)
    {
        if (!_owner.TryEnter("Show"))
        {
            return;
        }

        RegisterTemporaryRevoker();
        _island.ShowContent(_owner.Info.Id, content, size, duration ?? TimeSpan.FromSeconds(5));
    }

    public void DismissTemporary()
    {
        if (!_owner.TryEnter("DismissTemporary"))
        {
            return;
        }

        _island.DismissTemporary(_owner.Info.Id);
    }

    public void AddSettingsPage(SettingsPageDescriptor page)
    {
        if (!_owner.TryEnter("AddSettingsPage"))
        {
            return;
        }

        var scopedId = ScopedPageId(page.Id);
        var descriptor = new SettingsPageDescriptor(
            scopedId,
            page.Title,
            page.Glyph,
            () => _owner.GuardPageFactory(page),
            page.Order);

        _island.AddSettingsPage(descriptor);
        _scope.Register(new ActionDisposable(() => _island.RemoveSettingsPage(scopedId)));
    }

    public void RemoveSettingsPage(string pageId)
    {
        if (!_owner.TryEnter("RemoveSettingsPage"))
        {
            return;
        }

        _island.RemoveSettingsPage(ScopedPageId(pageId));
    }

    private void RegisterTemporaryRevoker()
    {
        if (_temporaryRevokerRegistered)
        {
            return;
        }

        _temporaryRevokerRegistered = true;
        _scope.Register(new ActionDisposable(() => _island.DismissTemporary(_owner.Info.Id)));
    }

    private string ScopedPageId(string pageId) => $"{_owner.Info.Id}/{pageId}";

    private IslandLiveContent Guard(IslandLiveContent content) => new()
    {
        Priority = content.Priority,
        OwnerLabel = content.OwnerLabel,
        OwnerGlyph = content.OwnerGlyph,
        OwnerAccent = content.OwnerAccent,
        MorphView = content.MorphView,
        CompactContent = content.CompactContent,
        ExpandedContent = content.ExpandedContent,
        CompactSize = content.CompactSize,
        ExpandedSize = content.ExpandedSize,
        OnTap = content.OnTap == null ? null : () => _owner.InvokeGuarded("点击回调", content.OnTap),
    };
}
