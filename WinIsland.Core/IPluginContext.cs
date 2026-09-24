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
}

public interface IPluginLogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string message, Exception exception);
}
