namespace WinIsland.Core;

/// <summary>
/// 插件间事件总线（宿主 2.6）。插件拿 <see cref="IPluginContext"/> 自行转型即可：
/// <code>
/// if (Context is IPluginBus bus) using var sub = bus.Subscribe("media.track", OnTrack);
/// </code>
/// 做成独立接口而不是往 <see cref="IPluginContext"/> 里加成员，是为了让不认这条能力的
/// 旧宿主与测试替身继续原样工作（插件只当消费者，不实现这些接口）。
///
/// 语义：进程内、同步派发、按订阅顺序；发布者与订阅者都可能已被停用 —— 宿主在派发前按
/// 各自的作用域复核，被撤销的订阅静默丢弃，订阅回调里的异常只记日志、绝不回传到发布者。
/// </summary>
public interface IPluginBus
{
    /// <summary>
    /// 广播一条消息。没有订阅者时是廉价操作。
    /// <paramref name="payload"/> 由发布者与订阅者自行约定类型；跨程序集传递时建议用小 record / 基础类型。
    /// </summary>
    void Publish(string topic, object? payload = null);

    /// <summary>
    /// 订阅一个主题。返回的 <see cref="IDisposable"/> 释放即退订；
    /// 插件停用/卸载时宿主也会自动退订（不需要手动释放，但建议用完就放）。
    /// </summary>
    IDisposable Subscribe(string topic, Action<IslandBusMessage> handler);
}

/// <summary>总线上的一条消息。</summary>
public sealed class IslandBusMessage
{
    /// <summary>主题（约定用点分小写，例如 <c>media.track</c>）。</summary>
    public required string Topic { get; init; }

    /// <summary>发布者的插件 id；宿主自己发布时为空字符串。</summary>
    public string PublisherId { get; init; } = string.Empty;

    /// <summary>载荷（类型由主题约定）。</summary>
    public object? Payload { get; init; }

    /// <summary>发布时刻。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
