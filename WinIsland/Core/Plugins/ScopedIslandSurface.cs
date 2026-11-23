using Microsoft.UI.Xaml;
using WinIsland.Core;

namespace WinIsland.Core.Plugins;

public sealed class ScopedIslandSurface : IIslandSurface
{
    private readonly PluginInstance _owner;
    private readonly IslandService _island;
    private readonly PluginScope _scope;
    private readonly HashSet<string> _dropTargetIds = new(StringComparer.Ordinal);
    // 这个插件挂过的实例 id（含 null 那条单实例内容）：停用时逐条撤回。
    // 必须逐条撤：SetLive(instance, null) 只清一条，换不出"全部清掉"的语义。
    private readonly HashSet<string?> _contentInstances = new(StringComparer.Ordinal);
    private bool _contentRevokerRegistered;
    private bool _temporaryRevokerRegistered;
    private bool _spotlightRevokerRegistered;

    public ScopedIslandSurface(PluginInstance owner, IslandService island, PluginScope scope)
    {
        _owner = owner;
        _island = island;
        _scope = scope;
    }

    public void SetContent(IslandLiveContent? content)
        => _owner.RunScoped(_scope, "SetContent", () => SetContentCore(null, content));

    public void SetContent(string instanceId, IslandLiveContent? content)
        => _owner.RunScoped(_scope, "SetContent", () => SetContentCore(instanceId, content));

    public void RemoveContent(string instanceId)
        => _owner.RunScoped(_scope, "RemoveContent", () => SetContentCore(instanceId, null));

    private void SetContentCore(string? instanceId, IslandLiveContent? content)
    {
        if (!_owner.TryEnter(_scope, "SetContent"))
        {
            return;
        }

        if (content == null)
        {
            _island.SetLiveContent(_owner.Info.Id, instanceId, null);
            _contentInstances.Remove(instanceId);
            return;
        }

        _contentInstances.Add(instanceId);

        if (!_contentRevokerRegistered)
        {
            _contentRevokerRegistered = true;
            _scope.Register(new ActionDisposable(() =>
            {
                // 停用/卸载：这个插件挂过的每一条内容都撤掉（包括它后来再也没更新过的那些实例）
                foreach (var id in _contentInstances.ToArray())
                {
                    _island.SetLiveContent(_owner.Info.Id, id, null);
                }
                _contentInstances.Clear();
            }));
        }

        _island.SetLiveContent(_owner.Info.Id, instanceId, Guard(content));
    }

    public void ShowMessage(IslandMessage message)
        => _owner.RunScoped(_scope, "ShowMessage", () => ShowMessageCore(message));

    private void ShowMessageCore(IslandMessage message)
    {
        if (!_owner.TryEnter(_scope, "ShowMessage"))
        {
            return;
        }

        RegisterTemporaryRevoker();
        _island.SendMessage(_owner.Info.Id, message);
    }

    public void Show(UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null)
        => _owner.RunScoped(_scope, "Show", () => ShowCore(content, size, duration));

    private void ShowCore(UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null)
    {
        if (!_owner.TryEnter(_scope, "Show"))
        {
            return;
        }

        RegisterTemporaryRevoker();
        _island.ShowContent(_owner.Info.Id, content, size, duration ?? TimeSpan.FromSeconds(5));
    }

    public void DismissTemporary()
        => _owner.RunScoped(_scope, "DismissTemporary", () => DismissTemporaryCore());

    private void DismissTemporaryCore()
    {
        if (!_owner.TryEnter(_scope, "DismissTemporary"))
        {
            return;
        }

        _island.DismissTemporary(_owner.Info.Id);
    }

    public void AddSettingsPage(SettingsPageDescriptor page)
        => _owner.RunScoped(_scope, "AddSettingsPage", () => AddSettingsPageCore(page));

    private void AddSettingsPageCore(SettingsPageDescriptor page)
    {
        if (!_owner.TryEnter(_scope, "AddSettingsPage"))
        {
            return;
        }

        var scopedId = ScopedPageId(page.Id);
        var descriptor = new SettingsPageDescriptor(
            scopedId,
            page.Title,
            page.Glyph,
            () => _owner.TryEnter(_scope, "设置页工厂")
                ? _owner.GuardPageFactory(page) : new Microsoft.UI.Xaml.Controls.TextBlock { Text = "插件已停用。" },
            page.Order);

        _island.AddSettingsPage(descriptor);
        _scope.Register(new ActionDisposable(() => _island.RemoveSettingsPage(scopedId)));
    }

    public void RemoveSettingsPage(string pageId)
        => _owner.RunScoped(_scope, "RemoveSettingsPage", () => RemoveSettingsPageCore(pageId));

    private void RemoveSettingsPageCore(string pageId)
    {
        if (!_owner.TryEnter(_scope, "RemoveSettingsPage"))
        {
            return;
        }

        _island.RemoveSettingsPage(ScopedPageId(pageId));
    }

    public void AddDropTarget(IslandDropTarget target)
        => _owner.RunScoped(_scope, "AddDropTarget", () => AddDropTargetCore(target));

    private void AddDropTargetCore(IslandDropTarget target)
    {
        if (!_owner.TryEnter(_scope, "AddDropTarget"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.Id))
        {
            return;
        }

        // 停用/卸载时撤回：同一个 Id 只挂一个撤回器（重复注册同 Id 是覆盖语义，不需要再挂）
        if (_dropTargetIds.Add(target.Id))
        {
            var id = target.Id;
            _scope.Register(new ActionDisposable(() => _island.RemoveDropTarget(_owner.Info.Id, id)));
        }

        _island.AddDropTarget(_owner.Info.Id, Guard(target));
    }

    public void RemoveDropTarget(string targetId)
        => _owner.RunScoped(_scope, "RemoveDropTarget", () => RemoveDropTargetCore(targetId));

    private void RemoveDropTargetCore(string targetId)
    {
        if (!_owner.TryEnter(_scope, "RemoveDropTarget") || string.IsNullOrWhiteSpace(targetId))
        {
            return;
        }

        _dropTargetIds.Remove(targetId);
        _island.RemoveDropTarget(_owner.Info.Id, targetId);
    }

    public void OpenSpotlight(IslandSpotlight spotlight)
        => _owner.RunScoped(_scope, "OpenSpotlight", () => OpenSpotlightCore(spotlight));

    private void OpenSpotlightCore(IslandSpotlight spotlight)
    {
        if (!_owner.TryEnter(_scope, "OpenSpotlight"))
        {
            return;
        }

        if (spotlight?.Content is null)
        {
            return;
        }

        if (!_spotlightRevokerRegistered)
        {
            _spotlightRevokerRegistered = true;
            _scope.Register(new ActionDisposable(() => _island.CloseSpotlight(_owner.Info.Id)));
        }

        _island.OpenSpotlight(_owner.Info.Id, new IslandSpotlight
        {
            Content = spotlight.Content,
            Size = spotlight.Size,
            OnClosed = spotlight.OnClosed == null
                ? null
                : () => _owner.InvokeGuarded("聚光卡关闭回调", spotlight.OnClosed),
        });
    }

    public void CloseSpotlight()
        => _owner.RunScoped(_scope, "CloseSpotlight", () => CloseSpotlightCore());

    private void CloseSpotlightCore()
    {
        if (!_owner.TryEnter(_scope, "CloseSpotlight"))
        {
            return;
        }

        _island.CloseSpotlight(_owner.Info.Id);
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

    /// <summary>
    /// 复制一份常驻内容：插件之后改自己那份对象不再影响宿主，回调统一包进守卫。
    /// 用 <c>with</c> 整体复制（SDK 里是 record）——之后新增属性会自动带上，不会再像手抄字段那样漏。
    /// </summary>
    private IslandLiveContent Guard(IslandLiveContent content) => content with
    {
        OnTap = content.OnTap == null ? null : () => _owner.RunScoped(_scope, "点击回调", content.OnTap),
    };

    /// <summary>
    /// 复制一份投放目标：插件之后改自己那份对象不再影响宿主，
    /// 回调统一包进异步守卫（投放动作可能 await），扩展名列表复制一份（避免插件改了它以后宿主读到半截）。
    /// 复制必须走 <c>with</c> 整体拷贝：**只能覆盖 <c>Handler</c> 与 <c>Extensions</c>** ——
    /// 曾经按字段手抄漏掉了 <see cref="IslandDropTarget.Kinds"/>，插件声明的 Text / Image
    /// 被静默降级成默认的 Files（表现与声明相反），别改回逐字段构造。
    /// </summary>
    private IslandDropTarget Guard(IslandDropTarget target) => target with
    {
        Extensions = target.Extensions?.ToArray(),
        Handler = ctx => _owner.InvokeScopedAsync(_scope, "投放回调", () => target.Handler(ctx)),
    };
}
