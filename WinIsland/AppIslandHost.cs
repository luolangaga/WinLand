using WinIsland.Core;
using WinIsland.Island;

namespace WinIsland;

/// <summary>
/// <see cref="IslandHost"/> 的主机实现：把「勿扰状态 / 消息历史 / 设置页 / 退出」这些
/// 岛窗口不该知道的东西转回 <see cref="App"/>。岛窗口只拿接口，诊断预览与回归测试不接它。
/// </summary>
internal sealed class AppIslandHost : IslandHost
{
    private readonly SettingsService _settings;
    private readonly MessageHistory _history;
    private readonly Action<string?> _openSettings;
    private readonly Action _exit;

    public AppIslandHost(SettingsService settings, MessageHistory history,
        Action<string?> openSettings, Action exit)
    {
        _settings = settings;
        _history = history;
        _openSettings = openSettings;
        _exit = exit;
    }

    public bool IsQuiet => QuietMode.IsActive(_settings);

    public void RecordMessage(IslandMessage message, string? owner)
        => _history.Add(message.Title, message.Text, owner, message.Glyph);

    public void OpenSettings(string? pageId) => _openSettings(pageId);

    public void RequestExit() => _exit();
}
