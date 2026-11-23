using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using WinIsland.Core;
using WinIsland.Core.Plugins;

namespace WinIsland.Island;

public sealed partial class IslandWindow
{
    #region 内容交接（换内容时的进出）

    // 旧内容先走、新内容稍后进，两段错开约 80ms：这样读起来是"岛换了口气"，
    // 而不是两张画面叠在一起 —— 全程只有一次可感知的切换。
    private static TimeSpan ContentOutDuration => Motion.Duration(100);
    private static TimeSpan ContentInDelay => Motion.Enabled ? TimeSpan.FromMilliseconds(55) : TimeSpan.Zero;
    private static TimeSpan ContentInDuration => Motion.Duration(190);
    private const double ContentOutSlide = -3;
    private const double ContentInSlide = 5;
    private const double ContentInScale = 0.985;

    /// <summary>
    /// 岛体内容交接：旧内容挪进 <see cref="ContentLeaving"/> 快速淡出并向上让位，
    /// 新内容稍后从下方淡入、微缩放回位。
    ///
    /// 旧内容是**按此刻的渲染尺寸钉住**的：它不参与新尺寸的布局 —— 否则插件视图会在收缩动画里
    /// 被一点点挤扁。全程只动组合级属性（Opacity / RenderTransform），不触发重排，与尺寸 morph 逐帧共存。
    ///
    /// **所有**岛体内容切换都必须走这里或 <see cref="SetContentImmediate"/>：交接动画在跑时若有人直接改
    /// <see cref="ContentHost"/>，内容层会停在半途的透明/位移上。
    /// </summary>
    private void SwapContent(UIElement? next, bool animate)
    {
        var current = ContentHost.Content;
        // Repeated SetLive updates often reuse the same tree. Keep its in-flight transition intact.
        if (ReferenceEquals(current, next)) return;
        double leavingOpacity = ContentHost.Opacity;
        double leavingY = _contentSlide.TranslateY;
        double leavingScale = _contentSlide.ScaleX;
        SettleContentSwap();
        if (!animate || current == null || ReferenceEquals(current, next) || !_shown)
        {
            ContentHost.Content = next;
            return;
        }

        ContentLeaving.Width = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        ContentLeaving.Height = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;

        // 同一个元素不能同时挂在两个 ContentPresenter 下：先断开再交接。
        // 基础值一律停在"已就位"状态（新内容不透明、两层无位移），动画用显式 From/To ——
        // 万一条动画没跑起来，退化结果也只是"瞬间换内容"，而不是内容停在半路的透明/位移上。
        ContentHost.Content = null;
        ContentLeaving.Content = current;
        ContentHost.Content = next;

        int version = ++_contentSwapVersion;
        var sb = new Storyboard();
        sb.Children.Add(SlideAnim(ContentLeaving, "Opacity", leavingOpacity, 0, ContentOutDuration, TimeSpan.Zero));
        sb.Children.Add(SlideAnim(_leavingSlide, "TranslateY", leavingY, ContentOutSlide, ContentOutDuration, TimeSpan.Zero));
        sb.Children.Add(SlideAnim(_leavingSlide, "ScaleX", leavingScale, 1, ContentOutDuration, TimeSpan.Zero));
        sb.Children.Add(SlideAnim(_leavingSlide, "ScaleY", leavingScale, 1, ContentOutDuration, TimeSpan.Zero));
        sb.Children.Add(SlideAnim(ContentHost, "Opacity", 0, 1, ContentInDuration, ContentInDelay));
        sb.Children.Add(SlideAnim(_contentSlide, "TranslateY", ContentInSlide, 0, ContentInDuration, ContentInDelay));
        sb.Children.Add(SlideAnim(_contentSlide, "ScaleX", ContentInScale, 1, ContentInDuration, ContentInDelay));
        sb.Children.Add(SlideAnim(_contentSlide, "ScaleY", ContentInScale, 1, ContentInDuration, ContentInDelay));
        sb.Completed += (_, _) =>
        {
            if (version != _contentSwapVersion) return;
            _contentSwap = null;
            ClearLeaving();
        };
        _contentSwap = sb;
        sb.Begin();
    }

    /// <summary>不用交接动画地设置内容（树结构修正、聚光卡恢复、投放面板接管等路径）。</summary>
    private void SetContentImmediate(UIElement? content)
    {
        SettleContentSwap();
        ContentHost.Content = content;
    }

    /// <summary>立刻结束交接：停动画、清覆盖层、把两层复位。可随时调用（幂等）。</summary>
    private void SettleContentSwap()
    {
        _contentSwap?.Stop();
        _contentSwap = null;
        _contentSwapVersion++;
        ClearLeaving();
        ContentHost.Opacity = 1;
        ResetSlide(_contentSlide);
    }

    private void ClearLeaving()
    {
        ContentLeaving.Content = null;
        ContentLeaving.Opacity = 0;
        ResetSlide(_leavingSlide);
    }

    private static void ResetSlide(CompositeTransform transform)
    {
        transform.TranslateY = 0;
        transform.ScaleX = 1;
        transform.ScaleY = 1;
    }

    /// <summary>交接的单条动画：一律 EaseOut —— 交接要"落地"，回弹留给岛体自己的 morph。</summary>
    private static DoubleAnimation SlideAnim(
        DependencyObject target, string property, double from, double to, TimeSpan duration, TimeSpan delay)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    #endregion
}

