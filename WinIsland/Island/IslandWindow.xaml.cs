using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinIsland.Core;

namespace WinIsland.Island;

/// <summary>
/// 灵动岛悬浮窗：无边框、透明、置顶、不抢焦点。
/// 主程序拥有全部状态机：空闲 → 紧凑 → 展开 → 临时消息。
/// 悬浮检测、尺寸动画、圆角、临时消息覆盖/恢复全部由主程序统一管理。
/// 插件只提供内容（IslandLiveContent），不接触尺寸。
/// </summary>
public sealed partial class IslandWindow : Window
{
    private const double Pad = 24;
    private static readonly Size IdleSize = new(128, 34);
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(333);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(250);

    private readonly SettingsService _settings;
    private readonly nint _hwnd;
    private readonly OverlappedPresenter _presenter;

    // 常驻内容栈
    private readonly List<(string Owner, IslandLiveContent Content)> _live = new();
    // 临时内容
    private UIElement? _tempContent;
    private Size _tempSize;

    private readonly DispatcherQueueTimer _tempTimer;
    private readonly DispatcherQueueTimer _hoverGuard;
    private readonly DispatcherQueueTimer _shrinkTimer;

    private readonly UIElement _idleContent;

    private bool _shown;
    private bool _hover;
    private bool _expanded;
    private Size _currentIsland;
    private int _windowPhysW;
    private int _windowPhysH;
    private Storyboard? _sizeStoryboard;
    private int _animVersion;
    private readonly RectangleGeometry _clip = new();

    private IslandLiveContent? ActiveLive => _live.Count > 0 ? _live[^1].Content : null;

    public IslandWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        AppWindow.IsShownInSwitchers = false;
        _presenter = OverlappedPresenter.Create();
        _presenter.SetBorderAndTitleBar(false, false);
        _presenter.IsAlwaysOnTop = true;
        _presenter.IsResizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsMaximizable = false;
        AppWindow.SetPresenter(_presenter);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Win32.InstallStyleGuard(_hwnd);
        SystemBackdrop = new TransparentBackdrop();
        _presenter.IsAlwaysOnTop = false;
        _presenter.IsAlwaysOnTop = true;
        Win32.MakeIslandStyle(_hwnd);

        _idleContent = BuildIdleContent();

        IslandRoot.Clip = _clip;
        IslandRoot.SizeChanged += (_, e) =>
        {
            _clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        };

        _tempTimer = DispatcherQueue.CreateTimer();
        _tempTimer.IsRepeating = false;
        _tempTimer.Tick += (_, _) => DismissTemporary();

        _hoverGuard = DispatcherQueue.CreateTimer();
        _hoverGuard.Interval = TimeSpan.FromMilliseconds(300);
        _hoverGuard.IsRepeating = true;
        _hoverGuard.Tick += (_, _) => HoverGuardTick();

        _shrinkTimer = DispatcherQueue.CreateTimer();
        _shrinkTimer.IsRepeating = false;
        _shrinkTimer.Interval = TimeSpan.FromMilliseconds(500);
        _shrinkTimer.Tick += (_, _) =>
        {
            if (_sizeStoryboard == null)
                ApplyWindowBounds(_currentIsland, growOnly: false);
        };

        _settings.Changed += OnSettingChanged;

        _currentIsland = IdleSize;
        IslandRoot.Width = IdleSize.Width;
        IslandRoot.Height = IdleSize.Height;
        ContentHost.Content = _idleContent;
        ApplyWindowBounds(IdleSize);
    }

    #region 公开操作（由 IslandService 在 UI 线程调用）

    public void SetLive(string owner, IslandLiveContent? content)
    {
        _live.RemoveAll(t => t.Owner == owner);
        if (content != null) _live.Add((owner, content));

        // 临时内容展示中不打断，结束后自然恢复
        if (_tempContent != null)
        {
            UpdateVisibility();
            return;
        }

        TransitionToLiveState();
        UpdateVisibility();
    }

    public void ShowTemporary(UIElement content, Size size, TimeSpan duration)
    {
        _tempContent = content;
        _tempSize = size;
        _tempTimer.Stop();
        _tempTimer.Interval = duration;
        _tempTimer.Start();

        // 收起展开态
        _expanded = false;

        var live = ActiveLive;
        if (live?.MorphView != null)
            live.MorphView.AnimateToCompact(CollapseDuration);

        ContentHost.Content = content;
        AnimateSize(size, CollapseDuration);
        SetCornerRadius(Math.Min(28, size.Height / 2));
        UpdateVisibility();
    }

    public void DismissTemporary()
    {
        if (_tempContent == null) return;
        _tempTimer.Stop();
        _tempContent = null;
        TransitionToLiveState();
        UpdateVisibility();
    }

    public void ShowMessage(IslandMessage msg)
    {
        var view = BuildMessageView(msg);
        const double width = 400;
        view.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Clamp(view.DesiredSize.Height, 76, 180);
        ShowTemporary(view, new Size(width, height), msg.Duration);
    }

    public void RefreshFromSettings()
    {
        UpdateVisibility();
        _shrinkTimer.Stop();
        ApplyWindowBounds(_currentIsland, growOnly: false);
    }

    public void ShowIsland()
    {
        UpdateVisibility(force: true);
    }

    #endregion

    #region 状态机

    /// <summary>根据当前状态（有无常驻内容、是否悬停）切换到正确的视觉态。</summary>
    private void TransitionToLiveState()
    {
        var live = ActiveLive;
        if (live == null)
        {
            // 无常驻内容 → 空闲态
            _expanded = false;
            ContentHost.Content = _idleContent;
            AnimateSize(IdleSize, CollapseDuration);
            SetCornerRadius(IdleSize.Height / 2);
            return;
        }

        // 有常驻内容 → 紧凑态（或展开态如果仍在悬停）
        bool wantExpand = _hover;

        if (live.MorphView != null)
        {
            ContentHost.Content = live.MorphView.View;
            if (wantExpand)
            {
                _expanded = true;
                AnimateSize(live.ExpandedSize, ExpandDuration);
                SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
                live.MorphView.AnimateToExpanded(ExpandDuration);
            }
            else
            {
                _expanded = false;
                AnimateSize(live.CompactSize, CollapseDuration);
                SetCornerRadius(live.CompactSize.Height / 2);
                live.MorphView.AnimateToCompact(CollapseDuration);
            }
        }
        else
        {
            // 非变形模式：紧凑/展开两个视图
            if (wantExpand && live.ExpandedContent != null)
            {
                _expanded = true;
                ContentHost.Content = live.ExpandedContent;
                AnimateSize(live.ExpandedSize, ExpandDuration);
                SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
            }
            else
            {
                _expanded = false;
                ContentHost.Content = live.CompactContent ?? _idleContent;
                AnimateSize(live.CompactSize, CollapseDuration);
                SetCornerRadius(live.CompactSize.Height / 2);
            }
        }
    }

    /// <summary>悬浮进入：展开常驻内容。</summary>
    private void OnHoverEnter()
    {
        if (_tempContent != null) return; // 临时内容展示中不展开

        var live = ActiveLive;
        if (live == null) return;
        if (live.MorphView == null && live.ExpandedContent == null) return;
        if (_expanded) return;

        _expanded = true;

        if (live.MorphView != null)
        {
            AnimateSize(live.ExpandedSize, ExpandDuration);
            SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
            live.MorphView.AnimateToExpanded(ExpandDuration);
        }
        else
        {
            ContentHost.Content = live.ExpandedContent;
            AnimateSize(live.ExpandedSize, ExpandDuration);
            SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
        }
    }

    /// <summary>悬浮离开：收起常驻内容。</summary>
    private void OnHoverExit()
    {
        if (_tempContent != null) return; // 临时内容展示中不收起
        if (!_expanded) return;

        _expanded = false;

        var live = ActiveLive;
        if (live == null) return;

        if (live.MorphView != null)
        {
            AnimateSize(live.CompactSize, CollapseDuration);
            SetCornerRadius(live.CompactSize.Height / 2);
            live.MorphView.AnimateToCompact(CollapseDuration);
        }
        else
        {
            ContentHost.Content = live.CompactContent ?? _idleContent;
            AnimateSize(live.CompactSize, CollapseDuration);
            SetCornerRadius(live.CompactSize.Height / 2);
        }
    }

    #endregion

    #region 尺寸动画

    private void SetCornerRadius(double radius)
    {
        IslandRoot.CornerRadius = new CornerRadius(radius);
    }

    private void AnimateSize(Size target, TimeSpan duration)
    {
        _shrinkTimer.Stop();

        if (target == _currentIsland && _sizeStoryboard == null)
        {
            IslandRoot.Width = target.Width;
            IslandRoot.Height = target.Height;
            ApplyWindowBounds(target, growOnly: false);
            return;
        }

        var fromW = double.IsNaN(IslandRoot.ActualWidth) || IslandRoot.ActualWidth <= 0
            ? _currentIsland.Width : IslandRoot.ActualWidth;
        var fromH = double.IsNaN(IslandRoot.ActualHeight) || IslandRoot.ActualHeight <= 0
            ? _currentIsland.Height : IslandRoot.ActualHeight;
        if (_sizeStoryboard != null)
        {
            _sizeStoryboard.Stop();
            _sizeStoryboard = null;
            IslandRoot.Width = fromW;
            IslandRoot.Height = fromH;
        }
        var version = ++_animVersion;

        var union = new Size(
            Math.Max(Math.Max(fromW, target.Width), _currentIsland.Width),
            Math.Max(Math.Max(fromH, target.Height), _currentIsland.Height));
        ApplyWindowBounds(union, growOnly: true);

        _currentIsland = target;

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        sb.Children.Add(MakeSizeAnim(IslandRoot, "Width", fromW, target.Width, duration, easing));
        sb.Children.Add(MakeSizeAnim(IslandRoot, "Height", fromH, target.Height, duration, easing));

        sb.Completed += (_, _) =>
        {
            if (version != _animVersion) return;
            _sizeStoryboard = null;
            IslandRoot.Width = target.Width;
            IslandRoot.Height = target.Height;
            _shrinkTimer.Start();
        };
        _sizeStoryboard = sb;
        sb.Begin();
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

    #region 窗口位置 / 可见性

    private double Scale => Win32.GetDpiForWindow(_hwnd) / 96.0;

    private void ApplyWindowBounds(Size islandLogical, bool growOnly = false)
    {
        var s = Scale;
        int neededW = (int)Math.Round((islandLogical.Width + Pad * 2) * s);
        int neededH = (int)Math.Round((islandLogical.Height + Pad * 2) * s);

        int w = growOnly ? Math.Max(neededW, _windowPhysW) : neededW;
        int h = growOnly ? Math.Max(neededH, _windowPhysH) : neededH;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double topOffset = _settings.Get("island.topOffset", 6.0);
        int x = area.X + (area.Width - w) / 2;
        int y = area.Y + (int)Math.Round((topOffset - Pad) * s);

        bool sizeChanged = w != _windowPhysW || h != _windowPhysH;
        _windowPhysW = w;
        _windowPhysH = h;

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));

        if (sizeChanged)
            Win32.RemoveDwmBorder(_hwnd);
    }

    private void UpdateVisibility(bool force = false)
    {
        bool hideIdle = _settings.Get("island.hideWhenIdle", false);
        bool visible = _settings.Get("island.visible", true)
                       && !(hideIdle && ActiveLive == null && _tempContent == null);
        if (!force && visible == _shown) return;
        _shown = visible;
        if (visible)
        {
            AppWindow.Show(false);
            _presenter.IsAlwaysOnTop = false;
            _presenter.IsAlwaysOnTop = true;
            Win32.MakeIslandStyle(_hwnd);
        }
        else
        {
            _shrinkTimer.Stop();
            _hoverGuard.Stop();
            _hover = false;
            AppWindow.Hide();
        }
    }

    private void OnSettingChanged(string key)
    {
        if (!key.StartsWith("island.", StringComparison.Ordinal)) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshFromSettings);
            return;
        }
        RefreshFromSettings();
    }

    #endregion

    #region 指针交互 — 主程序统一处理

    private void IslandRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hover = true;
        _hoverGuard.Start();

        if (_tempContent != null)
        {
            // 临时内容展示中：延长计时器
            _tempTimer.Stop();
            _tempTimer.Start();
            return;
        }
        OnHoverEnter();
    }

    private void IslandRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 动画中 resize 会误发 Exited；只有光标真离开岛体才收起
        if (IsCursorOverIsland()) return;
        _hover = false;
        _hoverGuard.Stop();

        if (_tempContent != null) return;
        OnHoverExit();
    }

    private void IslandRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // 点击临时消息（非按钮）=> 立即关闭
        if (_tempContent != null && e.OriginalSource is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
        {
            DismissTemporary();
        }
    }

    private void HoverGuardTick()
    {
        if (!_hover) return;
        if (!IsCursorOverIsland())
        {
            _hover = false;
            _hoverGuard.Stop();
            if (_tempContent != null) return;
            OnHoverExit();
        }
    }

    /// <summary>光标是否在岛体范围内（用目标尺寸而非动画中间值）。</summary>
    private bool IsCursorOverIsland()
    {
        Win32.GetCursorPos(out var pt);
        var s = Scale;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        double iw = _currentIsland.Width * s;
        double ih = _currentIsland.Height * s;
        double ix = pos.X + (size.Width - iw) / 2;
        double iy = pos.Y + Pad * s;
        const double slack = 6;
        return pt.X >= ix - slack && pt.X <= ix + iw + slack
               && pt.Y >= iy - slack && pt.Y <= iy + ih + slack;
    }

    #endregion

    #region 内置视图

    private static UIElement BuildIdleContent()
    {
        var grid = new Grid();
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 46, 50)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        grid.Children.Add(dot);
        return grid;
    }

    private static FrameworkElement BuildMessageView(IslandMessage msg)
    {
        var grid = new Grid
        {
            Padding = new Thickness(18, 14, 18, 14),
            ColumnSpacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var accent = msg.AccentColor ?? Windows.UI.Color.FromArgb(255, 0, 122, 255);
        var iconHost = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = msg.Glyph,
                FontSize = 18,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        grid.Children.Add(iconHost);

        var textPanel = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(textPanel, 1);

        var title = new TextBlock
        {
            Text = msg.Title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        textPanel.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(msg.Text))
        {
            textPanel.Children.Add(new TextBlock
            {
                Text = msg.Text,
                FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 3,
            });
        }
        grid.Children.Add(textPanel);
        return grid;
    }

    #endregion
}
