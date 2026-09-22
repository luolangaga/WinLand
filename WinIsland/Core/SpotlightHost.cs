using WinIsland.Island;

namespace WinIsland.Core;

/// <summary>
/// 聚光卡（超级展开）的宿主侧生命周期：谁在开、什么时候关、关掉之后岛体怎么回来。
/// 同一时刻只有一张卡片；别的插件再次请求时替换（旧 owner 会收到 OnClosed）。
/// 所有方法都在 UI 线程执行（由 <see cref="IslandService"/> 负责编组）。
/// </summary>
public sealed class SpotlightHost
{
    private readonly IslandWindow _island;
    private readonly IPluginLogger _log;
    private SpotlightWindow? _window;
    private (string Owner, IslandSpotlight Content)? _current;
    private bool _islandOccluded;

    public SpotlightHost(IslandWindow island, IPluginLogger log)
    {
        _island = island;
        _log = log;
        _island.Closed += (_, _) => CloseAll();
    }

    public string? CurrentOwner => _current?.Owner;

    /// <summary>打开（或替换成）聚光卡：卡片先在主岛位置出现，随后岛体整块淡出。</summary>
    public void Show(string ownerId, IslandSpotlight spotlight)
    {
        if (string.IsNullOrEmpty(ownerId) || spotlight?.Content is null) return;

        bool sameOwner = _current is { } existing && string.Equals(existing.Owner, ownerId, StringComparison.Ordinal);
        if (_current is { } previous && !sameOwner)
        {
            _log.Debug($"聚光卡被插件「{ownerId}」接管，先收起「{previous.Owner}」的卡片。");
            NotifyClosed(previous.Owner, previous.Content);
        }

        _current = (ownerId, spotlight);

        // 起点/落点 = 主岛当前的屏幕矩形与渲染尺寸（岛体隐藏期间同样可算）
        var origin = _island.MainIslandScreenRect();
        var originSize = _island.MainIslandSizeDip();

        _window ??= CreateWindow();
        _window.ShowOverlay(origin, originSize, spotlight, _island.CurrentStyleKind, _island.IsMaterialApplied);

        if (!_islandOccluded)
        {
            _islandOccluded = true;
            _island.SetSpotlightOccluded(true);
        }
    }

    private SpotlightWindow CreateWindow()
    {
        var window = new SpotlightWindow(_log);
        window.Dismissed += OnDismissed;
        return window;
    }

    /// <summary>关闭当前聚光卡；<paramref name="ownerId"/> 不为 null 时只认自己的卡片。</summary>
    public void Close(string? ownerId = null)
    {
        if (_current is not { } current) return;
        if (ownerId != null && !string.Equals(ownerId, current.Owner, StringComparison.Ordinal)) return;

        _window?.CloseOverlay();
    }

    /// <summary>不做动画立刻收场（应用退出路径）。</summary>
    public void CloseAll() => _window?.ForceClose();

    /// <summary>收起动画结束、窗口已隐藏：把岛体放回来，并把这个卡片的 owner 通知到位。</summary>
    internal void OnDismissed()
    {
        if (_islandOccluded)
        {
            _islandOccluded = false;
            _island.SetSpotlightOccluded(false);
        }

        if (_current is { } current)
        {
            _current = null;
            NotifyClosed(current.Owner, current.Content);
        }
    }

    private void NotifyClosed(string ownerId, IslandSpotlight content)
    {
        if (content.OnClosed is not { } callback) return;

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            // ScopedIslandSurface 已经包了一层，这里再兜一次：宿主流程绝不因插件回调中断
            _log.Error($"插件「{ownerId}」的聚光卡关闭回调抛异常（已忽略）", ex);
        }
    }
}
