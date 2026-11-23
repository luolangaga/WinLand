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
    private double _radiusTarget = 17;
    private CompositeTransform? _sizeClock;
    private long _sizeClockToken;

    private void ReleaseSizeClock()
    {
        _sizeClock?.UnregisterPropertyChangedCallback(CompositeTransform.ScaleXProperty, _sizeClockToken);
        _sizeClock = null;
    }
    #region 尺寸动画

    /// <summary>
    /// 按岛体高度与展开状态设置圆角。圆角是点击穿透形状的唯一来源
    /// （<see cref="UpdateHitRegion"/> 读取岛体与队列卡片的 CornerRadius），
    /// 因此所有圆角规则只能来自 <see cref="IslandStyle.ResolveRadius"/>。
    /// </summary>
    private void ApplyCornerRadius(double height, bool expanded)
    {
        _radiusHeight = height;
        _radiusExpanded = expanded;
        _radiusTarget = IslandStyle.ResolveRadius(_style, height, expanded, _radiusScale);
        if (_sizeStoryboard == null) _mainSurface.SetRadius(_radiusTarget);
    }

    /// <summary>
    /// 临时内容（消息 / 插件自定义内容）的圆角档位：高过一条胶囊就按卡片算 ——
    /// 两行正文的高卡若还用 height/2，两端会圆成跑道形，不像卡片。
    /// </summary>
    private void ApplyTemporaryRadius(double height)
        => ApplyCornerRadius(height, expanded: height > IslandStyle.MessageCardHeight);

    private void AnimateIslandSize(Size islandTarget, TimeSpan duration, bool isTemporary = false)
    {
        var totalTarget = isTemporary ? islandTarget : ComputeTotalSize(_expanded);
        bool unchanged = islandTarget == _currentIsland && _sizeStoryboard == null;

        // 这里只做兜底：正常路径下画布已由内容集合变化时预留好。
        // 尺寸动画期间绝不 resize 窗口 —— 否则客户区宽度变化会让 DWM 用上一帧
        // （旧客户区坐标）合成新窗口矩形，出现偏移 Δw/2 的副本残影。
        EnsureCanvas();

        var previousIsland = _currentIsland;
        _currentIsland = islandTarget;
        _currentTotalSize = totalTarget;

        if (unchanged)
        {
            IslandRoot.Width = islandTarget.Width;
            IslandRoot.Height = islandTarget.Height;
            UpdateHitRegion();
            return;
        }

        var fromW = double.IsNaN(IslandRoot.ActualWidth) || IslandRoot.ActualWidth <= 0
            ? previousIsland.Width : IslandRoot.ActualWidth;
        var fromH = double.IsNaN(IslandRoot.ActualHeight) || IslandRoot.ActualHeight <= 0
            ? previousIsland.Height : IslandRoot.ActualHeight;
        double fromRadius = IslandRoot.CornerRadius.TopLeft;
        if (_sizeStoryboard != null)
        {
            _sizeStoryboard.Stop();
            ReleaseSizeClock();
            _sizeStoryboard = null;
            IslandRoot.Width = fromW;
            IslandRoot.Height = fromH;
            _mainSurface.SetRadius(fromRadius);
        }
        var version = ++_animVersion;

        var sb = new Storyboard();
        var easing = SizeEasing(isTemporary, fromH, islandTarget.Height);
        // Animate layout directly; a managed Width/Height callback adds another
        // layout pass during rendering. The observer only interpolates the radius.
        sb.Children.Add(MakeSizeAnim(IslandRoot, "Width", fromW, islandTarget.Width, duration, easing));
        sb.Children.Add(MakeSizeAnim(IslandRoot, "Height", fromH, islandTarget.Height, duration, easing));
        var frame = new CompositeTransform { ScaleX = 0 };
        _sizeClock = frame;
        _sizeClockToken = frame.RegisterPropertyChangedCallback(CompositeTransform.ScaleXProperty, (_, _) =>
        {
            double progress = frame.ScaleX;
            double width = fromW + (islandTarget.Width - fromW) * progress;
            double height = fromH + (islandTarget.Height - fromH) * progress;
            _mainSurface.SetRadius(Math.Clamp(fromRadius + (_radiusTarget - fromRadius) * progress,
                0, Math.Min(width, height) / 2));
        });
        sb.Children.Add(MakeSizeAnim(frame, nameof(CompositeTransform.ScaleX), 0, 1, duration, easing));

        sb.Completed += (_, _) =>
        {
            if (version != _animVersion) return;
            _sizeStoryboard = null;
            ReleaseSizeClock();
            IslandRoot.Width = islandTarget.Width;
            IslandRoot.Height = islandTarget.Height;
            _mainSurface.SetRadius(_radiusTarget);
            UpdateHitRegion();
        };
        _sizeStoryboard = sb;
        sb.Begin();
    }

    private Size _currentTotalSize = IdleBaseSize;

    /// <summary>
    /// 尺寸动画的缓动。岛体一贯是"回弹"（BackEase EaseOut）；临时内容例外 ——
    /// 回弹会在**收缩**方向上越过目标：胶囊缩得比目标还矮一截、内容被裁剪框切一刀再弹回来，
    /// 一条消息这么缩一下很廉价。所以临时内容收缩时用纯 EaseOut（绝不越过目标），
    /// 只有长大时才留一点点回弹（仍然是一次"弹出来"的入场）。
    ///
    /// <c>island.bounce</c>（默认开）关掉后一律用纯 EaseOut：回弹对某些内容（实时波形、进度弧）
    /// 会让动画看起来"多弹一下"，不想要的人可以整体关掉。
    /// </summary>
    private EasingFunctionBase SizeEasing(bool temporary, double fromHeight, double toHeight)
    {
        if (_style == IslandStyleKind.Fluent || !_settings.Get(BounceKey, true))
            return new CubicEase { EasingMode = EasingMode.EaseOut };
        if (!temporary && toHeight > fromHeight) return new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.10 };
        return toHeight < fromHeight
            ? new CubicEase { EasingMode = EasingMode.EaseOut }
            : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.06 };
    }

    private static DoubleAnimation MakeSizeAnim(
        DependencyObject target, string property,
        double from, double to, TimeSpan duration, EasingFunctionBase easing)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }

    #endregion
}

