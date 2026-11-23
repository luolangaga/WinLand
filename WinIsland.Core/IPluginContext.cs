using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinIsland.Core;

public interface IPluginContext
{
    PluginManifest Manifest { get; }
    string PluginDirectory { get; }
    Version HostVersion { get; }
    DispatcherQueue Dispatcher { get; }
    IPluginLogger Log { get; }
    ISettingsStore Settings { get; }
    IIslandSurface Island { get; }

    /// <summary>岛体当前的明暗主题（Fluent 跟随系统，Apple 恒为深色）。见 <see cref="IIslandTheme"/>。</summary>
    IIslandTheme Theme { get; }

    IDisposable Register(IDisposable disposable);
    IDisposable OnSettingsChanged(string key, Action handler);
    IDisposable CreateTimer(TimeSpan interval, bool repeat, Action tick);
    void RunOnUI(Action action);
    Task RunOnUIAsync(Func<Task> action);
}

public interface IIslandSurface
{
    void SetContent(IslandLiveContent? content);
    void ShowMessage(IslandMessage message);
    void Show(UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null);
    void DismissTemporary();
    void AddSettingsPage(SettingsPageDescriptor page);
    void RemoveSettingsPage(string pageId);
    void AddDropTarget(IslandDropTarget target);
    void RemoveDropTarget(string targetId);
    void OpenSpotlight(IslandSpotlight spotlight);
    void CloseSpotlight();

    /// <summary>
    /// 多实例内容（宿主 2.6）：同一个插件可以同时挂多条常驻内容，各自参与优先级排序、各占一张队列卡片。
    /// 典型用法是两个城市各一张天气卡、两块 GPU 各一张监控卡。
    ///
    /// <paramref name="instanceId"/> 只在插件内部唯一即可（例如 <c>city-beijing</c>）。
    /// 默认实现直接转发到单实例版本，所以**不传实例 id 的老代码行为完全不变**；
    /// 认不出这条能力的宿主或测试替身照常工作。
    /// </summary>
    void SetContent(string instanceId, IslandLiveContent? content) => SetContent(content);

    /// <summary>撤掉某一条实例内容（等价于 <c>SetContent(instanceId, null)</c>）。</summary>
    void RemoveContent(string instanceId) => SetContent(instanceId, null);
}

public interface IPluginLogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string message, Exception exception);
}
