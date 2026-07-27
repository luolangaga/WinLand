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

public sealed partial class IslandWindow : Window
{
    private const double Pad = 24;
    private static readonly Size IdleSize = new(128, 34);
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(333);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(250);
    private const double SplitGap = 8;
    private const double ExpandedItemGap = 6;
    private const double StaggerDelayMs = 60;

    private readonly SettingsService _settings;
    private readonly nint _hwnd;
    private readonly OverlappedPresenter _presenter;

    private readonly List<(string Owner, IslandLiveContent Content)> _live = new();
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
    private bool IsMultiActive => _live.Count > 1;

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

    #region 公开操作

    public void SetLive(string owner, IslandLiveContent? content)
    {
        _live.RemoveAll(t => t.Owner == owner);
        if (content != null) _live.Add((owner, content));

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

        _expanded = false;

        if (IsMultiActive)
            AnimateAllMorphToCompact(CollapseDuration);
        else
        {
            var live = ActiveLive;
            if (live?.MorphView != null)
                live.MorphView.AnimateToCompact(CollapseDuration);
        }

        ShowSingleContent(content);
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

    #region 内容切换辅助

    private void ShowSingleContent(UIElement content)
    {
        MultiHost.Visibility = Visibility.Collapsed;
        ContentHost.Visibility = Visibility.Visible;
        ContentHost.Content = content;
        IslandRoot.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
    }

    private void ShowMultiContent()
    {
        ContentHost.Visibility = Visibility.Collapsed;
        ContentHost.Content = null;
        MultiHost.Visibility = Visibility.Visible;
    }

    private void ShowSingleLive(IslandLiveContent live)
    {
        MultiHost.Visibility = Visibility.Collapsed;
        ContentHost.Visibility = Visibility.Visible;
        IslandRoot.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
        if (live.MorphView != null)
            ContentHost.Content = live.MorphView.View;
        else
            ContentHost.Content = live.CompactContent ?? _idleContent;
    }

    #endregion

    #region 状态机

    private void TransitionToLiveState()
    {
        if (_live.Count == 0)
        {
            _expanded = false;
            ShowSingleContent(_idleContent);
            AnimateSize(IdleSize, CollapseDuration);
            SetCornerRadius(IdleSize.Height / 2);
            return;
        }

        if (_live.Count == 1)
        {
            TransitionSingleLive();
            return;
        }

        TransitionMultiLive();
    }

    private void TransitionSingleLive()
    {
        var live = ActiveLive!;
        bool wantExpand = _hover;

        ShowSingleLive(live);

        if (live.MorphView != null)
        {
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

    private void TransitionMultiLive()
    {
        bool wantExpand = _hover;

        if (wantExpand)
        {
            _expanded = true;
            RenderMultiExpanded();
        }
        else
        {
            _expanded = false;
            RenderMultiCompact();
        }
    }

    #endregion

    #region 多模块紧凑态 — 分屏药丸

    private void RenderMultiCompact()
    {
        ShowMultiContent();
        MultiHost.Orientation = Orientation.Horizontal;
        MultiHost.Spacing = SplitGap;
        MultiHost.Children.Clear();

        double totalW = 0;
        double maxH = 0;

        for (int i = 0; i < _live.Count; i++)
        {
            var entry = _live[i];
            var content = entry.Content;
            var cs = content.CompactSize;

            var pill = new Border
            {
                Width = cs.Width,
                Height = cs.Height,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E)),
                CornerRadius = new CornerRadius(cs.Height / 2),
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, cs.Width, cs.Height) },
                Child = content.MorphView?.View ?? content.CompactContent,
                Tag = entry.Owner,
            };
            MultiHost.Children.Add(pill);

            totalW += cs.Width;
            maxH = Math.Max(maxH, cs.Height);

            if (content.MorphView != null)
                content.MorphView.AnimateToCompact(CollapseDuration);
        }

        totalW += SplitGap * (_live.Count - 1);

        IslandRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AnimateSize(new Size(totalW, maxH), CollapseDuration);
        SetCornerRadius(maxH / 2);
    }

    #endregion

    #region 多模块展开态 — 垂直堆叠

    private void RenderMultiExpanded()
    {
        ShowMultiContent();
        IslandRoot.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
        MultiHost.Orientation = Orientation.Vertical;
        MultiHost.Spacing = ExpandedItemGap;
        MultiHost.Children.Clear();

        double maxW = 0;
        double totalH = 0;

        for (int i = 0; i < _live.Count; i++)
        {
            var entry = _live[i];
            var content = entry.Content;
            var expSize = content.ExpandedSize;

            var itemBorder = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(4),
                Tag = entry.Owner,
                Opacity = 0,
                RenderTransform = new TranslateTransform { Y = 12 },
            };

            if (content.MorphView != null)
            {
                itemBorder.Child = content.MorphView.View;
            }
            else if (content.ExpandedContent != null)
            {
                itemBorder.Child = content.ExpandedContent;
            }
            else
            {
                itemBorder.Child = content.CompactContent;
            }

            MultiHost.Children.Add(itemBorder);

            maxW = Math.Max(maxW, expSize.Width + 8);
            totalH += expSize.Height + 8 + ExpandedItemGap;
        }

        totalH -= ExpandedItemGap;

        AnimateSize(new Size(maxW, totalH), ExpandDuration);
        SetCornerRadius(Math.Min(28, totalH / 2));

        AnimateMultiExpandItems();
        AnimateAllMorphToExpanded(ExpandDuration);
    }

    private void AnimateMultiExpandItems()
    {
        var sb = new Storyboard();
        for (int i = 0; i < MultiHost.Children.Count; i++)
        {
            if (MultiHost.Children[i] is not FrameworkElement item) continue;
            var delay = TimeSpan.FromMilliseconds(StaggerDelayMs * i);

            var opacityAnim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(opacityAnim, item);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            sb.Children.Add(opacityAnim);

            var yAnim = new DoubleAnimation
            {
                From = 12,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                BeginTime = delay,
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(yAnim, item.RenderTransform);
            Storyboard.SetTargetProperty(yAnim, "Y");
            sb.Children.Add(yAnim);
        }
        sb.Begin();
    }

    #endregion

    #region MorphView 批量操作

    private void AnimateAllMorphToExpanded(TimeSpan duration)
    {
        foreach (var entry in _live)
        {
            if (entry.Content.MorphView != null)
                entry.Content.MorphView.AnimateToExpanded(duration);
        }
    }

    private void AnimateAllMorphToCompact(TimeSpan duration)
    {
        foreach (var entry in _live)
        {
            if (entry.Content.MorphView != null)
                entry.Content.MorphView.AnimateToCompact(duration);
        }
    }

    #endregion

    #region 悬浮交互

    private void OnHoverEnter()
    {
        if (_tempContent != null) return;

        if (_live.Count == 0) return;

        if (_live.Count == 1)
        {
            var live = ActiveLive!;
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
            return;
        }

        if (_expanded) return;
        _expanded = true;

        RenderMultiExpanded();
    }

    private void OnHoverExit()
    {
        if (_tempContent != null) return;
        if (!_expanded) return;

        if (_live.Count == 0) return;

        if (_live.Count == 1)
        {
            var live = ActiveLive;
            if (live == null) return;

            _expanded = false;

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
            return;
        }

        _expanded = false;

        RenderMultiCompact();
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

        double overshootW = Math.Abs(target.Width - fromW) * 0.5;
        double overshootH = Math.Abs(target.Height - fromH) * 0.5;
        var union = new Size(
            Math.Max(Math.Max(fromW, target.Width), _currentIsland.Width) + overshootW,
            Math.Max(Math.Max(fromH, target.Height), _currentIsland.Height) + overshootH);
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
                       && !(hideIdle && _live.Count == 0 && _tempContent == null);
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

    #region 指针交互

    private void IslandRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hover = true;
        _hoverGuard.Start();

        if (_tempContent != null)
        {
            _tempTimer.Stop();
            _tempTimer.Start();
            return;
        }
        OnHoverEnter();
    }

    private void IslandRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (IsCursorOverIsland()) return;
        _hover = false;
        _hoverGuard.Stop();

        if (_tempContent != null) return;
        OnHoverExit();
    }

    private void IslandRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
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
