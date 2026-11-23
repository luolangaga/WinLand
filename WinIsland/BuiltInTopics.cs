namespace WinIsland;

/// <summary>
/// 宿主与内置插件约定的总线主题。放在宿主而不是 SDK 里：这些是**内置插件**的约定，
/// 外部插件要用就自己声明主题（总线本身不校验主题名）。
/// </summary>
internal static class BuiltInTopics
{
    /// <summary>全局快捷键「播放 / 暂停」：内置的「正在播放」订阅它。</summary>
    public const string MediaPlayPause = "host.media.playpause";
}
