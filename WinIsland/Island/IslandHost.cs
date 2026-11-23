using WinIsland.Core;

namespace WinIsland.Island;

/// <summary>
/// 岛体需要回问主机的少数几件事。岛窗口刻意不认识 <c>App</c>（诊断预览、回归测试都会单独建岛），
/// 所以这些能力走一个小接口：
///  · 勿扰/暂歇状态（消息该不该弹、可见性怎么算）；
///  · 消息历史落盘（右键菜单与设置页要能看到「刚才那条是什么」）；
///  · 当前活动内容的主操作（右键菜单里的「取消当前活动」）。
/// 没挂宿主时（预览/测试）一律按最保守的语义降级：不静默、不记历史。
/// </summary>
internal interface IslandHost
{
    /// <summary>勿扰进行中：临时消息不弹（但照样记录），岛体不因新活动自动现身。</summary>
    bool IsQuiet { get; }

    /// <summary>记一条消息到历史（宿主消息与插件消息都走这里）。</summary>
    void RecordMessage(IslandMessage message, string? owner);

    /// <summary>设置页请求（右键菜单「打开设置」用）。</summary>
    void OpenSettings(string? pageId);

    /// <summary>退出应用（右键菜单）。</summary>
    void RequestExit();
}
