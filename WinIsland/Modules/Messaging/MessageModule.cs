using WinIsland.Core;

namespace WinIsland.Modules.Messaging;

/// <summary>
/// 手动消息模块：在设置窗口提供一个「发送消息」页面，
/// 演示 SendMessage（文字消息）与 ShowContent（自定义 XAML 控件）两个接口。
/// </summary>
public sealed class MessageModule : IIslandModule
{
    public string Id => "messenger";
    public string DisplayName => "发送消息";

    public Task InitializeAsync(IDynamicIslandApi api)
    {
        api.AddSettingsPage(new SettingsPageDescriptor(
            "messenger", DisplayName, "\uE724", () => new MessageSettingsPage(api)));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}
