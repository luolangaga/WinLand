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
/// 状态机：空闲 → 紧凑 → 展开 → 临时消息。
/// 小岛时只展示优先级最高的一个活动；展开时所有活动
/// 按大岛样式从上到下排列，每个都是完整的 MorphView 大岛。
/// </summary>
public sealed partial class IslandWindow : Window
{
    private const double Pad = 24;
    private const double QueueSpacing = 8;
    private const int MaxExpandedItems = 4;
    private static readonly Size IdleSize = new(128, 34);
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(333);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(250);

    private readonly SettingsService _settings;
    private readonly nint _hwnd;
    private readonly OverlappedPresenter _presenter;

    // 常驻活动列表（按优先级降序排列，优先级相同则按插入顺序）
    private readonly List<(string Owner, IslandLiveContent Content)> _live = new();
    // 临时内容
    private UIElement? _tempContent;
    private Size _tempSize;

    private readonly DispatcherQueueTimer _tempTimer;
    private readonly DispatcherQueueTimer _hoverGuard;

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

    // 主活动（优先级最高）
    private IslandLiveContent? ActiveLive => _live.Count > 0 ? _live[0].Content : null;
    // 队列活动（除主活动外的其余活动）
    private IReadOnlyList<(string Owner, IslandLiveContent Content)> QueueItems
        => _live.Count > 1 ? _live.Skip(1).ToList() : Array.Empty<(string, IslandLiveContent)>();

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
        if (content != null)
        {
            _live.Add((owner, content));
            _live.Sort((a, b) => b.Content.Priority.CompareTo(a.Content.Priority));
        }

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

        // 收起时先安全拆卸队列，再收起主岛
        TeardownQueue();
        _expanded = false;

        var live = ActiveLive;
        if (live?.MorphView != null)
        {
            // 确保 View 在 ContentHost 里（可能刚从队列拆回来）
            ContentHost.Content = live.MorphView.View;
            live.MorphView.AnimateToCompact(CollapseDuration);
        }

        ContentHost.Content = content;
        AnimateIslandSize(size, CollapseDuration, isTemporary: true);
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
        ApplyWindowBounds(_currentTotalSize, growOnly: false);
    }

    public void ShowIsland()
    {
        UpdateVisibility(force: true);
    }

    #endregion

    #region 状态机

    private Size ComputeTotalSize()
    {
        var live = ActiveLive;
        if (live == null) return IdleSize;

        double mainW, mainH;
        if (_expanded)
        {
            mainW = live.ExpandedSize.Width;
            mainH = live.ExpandedSize.Height;
        }
        else
        {
            mainW = live.CompactSize.Width;
            mainH = live.CompactSize.Height;
        }

        var queue = QueueItems;
        if (_expanded && queue.Count > 0)
        {
            int count = Math.Min(queue.Count, MaxExpandedItems - 1);
            double queueH = 0;
            double maxQueueW = mainW;
            for (int i = 0; i < count; i++)
            {
                var q = queue[i].Content;
                queueH += q.ExpandedSize.Height + QueueSpacing;
                maxQueueW = Math.Max(maxQueueW, q.ExpandedSize.Width);
            }
            return new Size(maxQueueW, mainH + queueH);
        }

        return new Size(mainW, mainH);
    }

    private void TransitionToLiveState()
    {
        var live = ActiveLive;
        if (live == null)
        {
            _expanded = false;
            TeardownQueue();
            ContentHost.Content = _idleContent;
            AnimateIslandSize(IdleSize, CollapseDuration);
            SetCornerRadius(IdleSize.Height / 2);
            return;
        }

        bool wantExpand = _hover;

        if (live.MorphView != null)
        {
            // 确保 View 在 ContentHost 里
            ContentHost.Content = live.MorphView.View;
            if (wantExpand)
            {
                _expanded = true;
                AnimateIslandSize(live.ExpandedSize, ExpandDuration);
                SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
                live.MorphView.AnimateToExpanded(ExpandDuration);
                BuildQueue();
            }
            else
            {
                _expanded = false;
                TeardownQueue();
                AnimateIslandSize(live.CompactSize, CollapseDuration);
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
                AnimateIslandSize(live.ExpandedSize, ExpandDuration);
                SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
                BuildQueue();
            }
            else
            {
                _expanded = false;
                TeardownQueue();
                ContentHost.Content = live.CompactContent ?? _idleContent;
                AnimateIslandSize(live.CompactSize, CollapseDuration);
                SetCornerRadius(live.CompactSize.Height / 2);
            }
        }
    }

    /// <summary>构建队列：每个队列活动用独立的 Border 包裹其 MorphView.View。</summary>
    private void BuildQueue()
    {
        var queue = QueueItems;
        QueuePanel.Children.Clear();
        if (queue.Count == 0)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        int count = Math.Min(queue.Count, MaxExpandedItems - 1);
        for (int i = 0; i < count; i++)
        {
            var item = queue[i];
            var content = item.Content;

            UIElement inner;
            if (content.MorphView != null)
            {
                inner = content.MorphView.View;
                content.MorphView.AnimateToExpanded(ExpandDuration);
            }
            else
            {
                inner = content.ExpandedContent ?? content.CompactContent ?? BuildFallbackLabel(item.Owner, content);
            }

            var border = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0)),
                CornerRadius = new CornerRadius(17),
                Width = content.ExpandedSize.Width,
                Height = content.ExpandedSize.Height,
                Child = inner,
            };
            QueuePanel.Children.Add(border);
        }

        QueuePanel.Visibility = Visibility.Visible;
    }

    /// <summary>安全拆卸队列：先停止 Storyboard，把 MorphView.View 从 Border 中取出，
    /// 确保它回到 ContentHost 的掌控，最后清空 QueuePanel。</summary>
    private void TeardownQueue()
    {
        if (QueuePanel.Children.Count == 0)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var queue = QueueItems;
        int count = Math.Min(queue.Count, MaxExpandedItems - 1);

        // 先停止队列中所有 MorphView 的动画
        for (int i = 0; i < count; i++)
        {
            queue[i].Content.MorphView?.AnimateToCompact(CollapseDuration);
        }

        // 从 Border 中取出 MorphView.View（断开父子关系）
        // WinUI: 设置 Child=null 会断开引用
        foreach (var child in QueuePanel.Children)
        {
            if (child is Border b)
                b.Child = null;
        }

        QueuePanel.Children.Clear();
        QueuePanel.Visibility = Visibility.Collapsed;

        // 确保主活动的 View 在 ContentHost 里（可能被队列 Border 占用过）
        var live = ActiveLive;
        if (live?.MorphView != null && !ReferenceEquals(ContentHost.Content, live.MorphView.View))
        {
            ContentHost.Content = live.MorphView.View;
        }
    }

    private static UIElement BuildFallbackLabel(string owner, IslandLiveContent content)
    {
        var accent = content.OwnerAccent ?? Windows.UI.Color.FromArgb(255, 90, 90, 95);
        var label = content.OwnerLabel ?? owner;
        var glyph = content.OwnerGlyph ?? "\uE946";

        var grid = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconHost = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 14,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        Grid.SetColumn(iconHost, 0);

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        Grid.SetColumn(labelText, 1);

        grid.Children.Add(iconHost);
        grid.Children.Add(labelText);
        return grid;
    }

    private void OnHoverEnter()
    {
        if (_tempContent != null) return;

        var live = ActiveLive;
        if (live == null) return;
        if (live.MorphView == null && live.ExpandedContent == null) return;
        if (_expanded) return;

        _expanded = true;

        if (live.MorphView != null)
        {
            AnimateIslandSize(live.ExpandedSize, ExpandDuration);
            SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
            live.MorphView.AnimateToExpanded(ExpandDuration);
        }
        else
        {
            ContentHost.Content = live.ExpandedContent;
            AnimateIslandSize(live.ExpandedSize, ExpandDuration);
            SetCornerRadius(Math.Min(28, live.ExpandedSize.Height / 2));
        }

        BuildQueue();
    }

    private void OnHoverExit()
    {
        if (_tempContent != null) return;
        if (!_expanded) return;

        // 先安全拆卸队列（停止动画 + 断开 Border.Child + 把主 View 放回 ContentHost）
        TeardownQueue();

        _expanded = false;

        var live = ActiveLive;
        if (live == null) return;

        if (live.MorphView != null)
        {
            AnimateIslandSize(live.CompactSize, CollapseDuration);
            SetCornerRadius(live.CompactSize.Height / 2);
            live.MorphView.AnimateToCompact(CollapseDuration);
        }
        else
        {
            ContentHost.Content = live.CompactContent ?? _idleContent;
            AnimateIslandSize(live.CompactSize, CollapseDuration);
            SetCornerRadius(live.CompactSize.Height / 2);
        }
    }

    #endregion

    #region 尺寸动画

    private void SetCornerRadius(double radius)
    {
        IslandRoot.CornerRadius = new CornerRadius(radius);
    }

    private void AnimateIslandSize(Size islandTarget, TimeSpan duration, bool isTemporary = false)
    {
        var totalTarget = isTemporary ? islandTarget : ComputeTotalSize();

        if (islandTarget == _currentIsland && _sizeStoryboard == null)
        {
            IslandRoot.Width = islandTarget.Width;
            IslandRoot.Height = islandTarget.Height;
            ApplyWindowBounds(totalTarget, growOnly: false);
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

        var currentTotal = _currentTotalSize;
        var unionTotal = new Size(
            Math.Max(currentTotal.Width, totalTarget.Width),
            Math.Max(currentTotal.Height, totalTarget.Height));
        ApplyWindowBounds(unionTotal, growOnly: true);

        _currentIsland = islandTarget;
        _currentTotalSize = totalTarget;

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        sb.Children.Add(MakeSizeAnim(IslandRoot, "Width", fromW, islandTarget.Width, duration, easing));
        sb.Children.Add(MakeSizeAnim(IslandRoot, "Height", fromH, islandTarget.Height, duration, easing));

        sb.Completed += (_, _) =>
        {
            if (version != _animVersion) return;
            _sizeStoryboard = null;
            IslandRoot.Width = islandTarget.Width;
            IslandRoot.Height = islandTarget.Height;
        };
        _sizeStoryboard = sb;
        sb.Begin();
    }

    private Size _currentTotalSize = IdleSize;

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

    private void ApplyWindowBounds(Size totalLogical, bool growOnly = false)
    {
        var s = Scale;
        int neededW = (int)Math.Round((totalLogical.Width + Pad * 2) * s);
        int neededH = (int)Math.Round((totalLogical.Height + Pad * 2) * s);

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
            _tempTimer.Stop();
            _tempTimer.Start();
            return;
        }
        OnHoverEnter();
    }

    private void IslandRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (QueuePanel.Visibility == Visibility.Visible) return;
        if (IsCursorOverIsland()) return;
        _hover = false;
        _hoverGuard.Stop();

        if (_tempContent != null) return;
        OnHoverExit();
    }

    private void IslandStack_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hover = true;
        _hoverGuard.Start();
    }

    private void IslandStack_PointerExited(object sender, PointerRoutedEventArgs e)
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
            return;
        }

        if (e.OriginalSource is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
        {
            ActiveLive?.OnTap?.Invoke();
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
        double iw = _currentTotalSize.Width * s;
        double ih = _currentTotalSize.Height * s;
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
