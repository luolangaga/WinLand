using WinIsland.Core.Plugins;

namespace WinIsland.Core;

/// <summary>
/// 进程内插件事件总线。订阅/发布都按插件作用域记账：
/// 插件停用后宿主立刻退订它的全部订阅，派发时也再复核一次 —— 停用插件绝不会被回调。
/// </summary>
public sealed class PluginBus
{
    private readonly List<Entry> _subscribers = new();
    private readonly object _gate = new();

    private sealed class Entry
    {
        public required string Topic { get; init; }
        public required string PublisherId { get; init; }
        public required PluginScope Scope { get; init; }
        public required Action<IslandBusMessage> Handler { get; init; }
        public bool Removed { get; set; }
    }

    /// <summary>
    /// 给某个插件建一个总线视图：发布时带上它的 id，订阅时记在它的作用域里（作用域撤销即退订）。
    /// 发布者没有作用域（宿主自己）时传 null。
    /// </summary>
    public IPluginBus For(string pluginId, PluginScope? scope) => new ScopedBus(this, pluginId, scope);

    /// <summary>宿主自己发布（PublisherId 为空）。</summary>
    public void Publish(string topic, object? payload) => PublishCore(string.Empty, topic, payload);

    private void PublishCore(string publisherId, string topic, object? payload)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;

        var message = new IslandBusMessage { Topic = topic, PublisherId = publisherId, Payload = payload };

        Entry[] snapshot;
        lock (_gate)
        {
            snapshot = _subscribers.ToArray();
        }

        foreach (var entry in snapshot)
        {
            if (entry.Removed) continue;
            if (!string.Equals(entry.Topic, topic, StringComparison.Ordinal)) continue;
            if (entry.Scope.IsRevoked) continue;

            try
            {
                entry.Handler(message);
            }
            catch (Exception)
            {
                // 订阅者（或它的动作）出问题不能回传给发布者：总线是旁路，不是调用链
            }
        }
    }

    private IDisposable SubscribeCore(string publisherId, PluginScope? scope, string topic, Action<IslandBusMessage> handler)
    {
        var effectiveScope = scope ?? new PluginScope("host");
        var entry = new Entry
        {
            Topic = topic,
            PublisherId = publisherId,
            Scope = effectiveScope,
            Handler = handler,
        };

        lock (_gate) _subscribers.Add(entry);

        var subscription = new ActionDisposable(() =>
        {
            lock (_gate)
            {
                entry.Removed = true;
                _subscribers.Remove(entry);
            }
        });

        // 记进插件作用域：停用/卸载时自动退订，插件自己忘了 Dispose 也不会留下悬挂引用
        return scope != null ? scope.Register(subscription) : subscription;
    }

    private sealed class ScopedBus(PluginBus owner, string pluginId, PluginScope? scope) : IPluginBus
    {
        public void Publish(string topic, object? payload = null)
        {
            if (scope is { IsRevoked: true }) return;
            owner.PublishCore(pluginId, topic, payload);
        }

        public IDisposable Subscribe(string topic, Action<IslandBusMessage> handler)
        {
            if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentException("主题不能为空。", nameof(topic));
            ArgumentNullException.ThrowIfNull(handler);
            return owner.SubscribeCore(pluginId, scope, topic, handler);
        }
    }
}
