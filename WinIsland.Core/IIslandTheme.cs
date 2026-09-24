namespace WinIsland.Core;

/// <summary>
/// 岛体当前的明暗主题。Fluent 外观跟随系统（设置 → 个性化 → 颜色 → 「默认应用模式」），
/// Apple 外观恒为深色 —— 不透明黑胶囊上没有"浅色"这回事。
///
/// 用 XAML 搭的视图通常什么都不用做：岛体根节点的主题一变，视图里所有
/// <c>{ThemeResource ...}</c>（以及没写 Foreground 的默认前景色）就跟着换成另一套。
/// 这个接口是给**在代码里搭视图**的插件用的 —— 代码里取不到"当前元素主题"，
/// 写死的白色到了浅色岛上就是白字压白底。
/// </summary>
public interface IIslandTheme
{
    /// <summary>岛体当前是否浅色。</summary>
    bool IsLight { get; }

    /// <summary>
    /// 主题变化（在 UI 线程触发）。代码里搭的视图要在这里按新主题换掉自己烘死的画刷：
    /// 重建一份视图、或者只改那几个 SolidColorBrush 的 Color 都行。
    /// </summary>
    event Action? Changed;
}
