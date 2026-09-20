using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace WinIsland.Settings;

/// <summary>
/// 设置页宿主：<see cref="SettingsWindow"/> 里 Frame 的导航目标。
/// 滚动与内容列宽都由它统一负责，插件页只要返回自己的根元素：
/// 窄窗口铺满、宽窗口封顶并居中；横向滚动关闭（否则内容按无限宽测量，
/// 星号列会塌成内容宽，长文本还会把整页横向推走导致右侧被裁）。
/// </summary>
public sealed partial class SettingsPageHost : Page
{
    /// <summary>内容列最大宽度：更宽时不再拉伸，避免一行文字过长。</summary>
    private const double MaxContentWidth = 1000;

    private const double HorizontalPadding = 32;

    public SettingsPageHost()
    {
        InitializeComponent();

        // 显式定宽，不依赖 Stretch + MaxWidth 的居中表现
        Scroller.SizeChanged += (_, e) => SyncColumnWidth(e.NewSize.Width);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ContentColumn.Children.Clear();
        if (e.Parameter is UIElement page) ContentColumn.Children.Add(page);
    }

    private void SyncColumnWidth(double viewportWidth)
    {
        var available = viewportWidth - HorizontalPadding * 2;
        if (available <= 0) return;
        ContentColumn.Width = available > MaxContentWidth ? MaxContentWidth : available;
    }
}
