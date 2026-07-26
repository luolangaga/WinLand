using WinIsland.Core;
using WinIsland.Settings;

namespace WinIsland.Modules;

/// <summary>
/// 内置通用模块：注册灵动岛外观与行为的设置页面。
/// 与其他模块一样通过 AddSettingsPage 注册，SettingsWindow 不再硬编码任何页面。
/// </summary>
public sealed class GeneralModule : IIslandModule
{
    public string Id => "general";
    public string DisplayName => "通用";

    private IDynamicIslandApi _api = null!;

    public Task InitializeAsync(IDynamicIslandApi api)
    {
        _api = api;
        _api.AddSettingsPage(new SettingsPageDescriptor(
            "general", DisplayName, "\uE713", () => new GeneralSettingsPage(api.Settings)));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}
