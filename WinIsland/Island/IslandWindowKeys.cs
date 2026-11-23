namespace WinIsland.Island;

/// <summary>
/// 活动记账 key 的纯逻辑（不含任何 WinUI 依赖，可直接被回归测试引用）。
///
/// 岛体用「一个字符串 key」区分每一条常驻内容：
///  · 单实例插件（2.5 及以前的全部插件）key 就是插件 id —— 与改动前逐字一致；
///  · 多实例插件是 <c>id + U+001F + 实例名</c>。
///
/// 分隔符取 U+001F（单元分隔符）：插件 id 由清单校验限定为小写字母/数字/连字符，
/// 实例名由插件自定但不可能含它，所以两种 key 永远不会撞车。
/// </summary>
internal static class IslandWindowKeys
{
    internal const char Separator = '\u001f';

    /// <summary>登记时用的 key。空/缺省的实例名等价于单实例那条。</summary>
    internal static string LiveKey(string owner, string? instanceId)
        => string.IsNullOrEmpty(instanceId) ? owner : owner + Separator + instanceId;

    /// <summary>
    /// 从 key 里还原插件 id。宿主级的 <c>plugin.&lt;id&gt;.priority</c> 这类设置键必须用它 ——
    /// 不能带实例后缀，否则多实例插件会读不到自己的优先级覆盖。
    /// </summary>
    internal static string OwnerOfKey(string key)
    {
        int index = key.IndexOf(Separator);
        return index < 0 ? key : key[..index];
    }
}
