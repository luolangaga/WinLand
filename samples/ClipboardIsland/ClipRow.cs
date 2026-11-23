using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinIsland.Core;
using WinIsland.PluginViews;

namespace ClipboardIsland;

/// <summary>
/// 历史列表里的一行：[类型图标 / 图片缩略图] 内容摘要 [时间]。
///
/// 可点击的行（大岛展开态里的那几行、聚光卡里的每一行）点一下就会把这条重新复制并粘出去，
/// 带 hover 底色；点中时把 Tapped 标成 Handled，免得冒泡到岛体再弹一次聚光卡。
///
/// 传了 <c>onDelete</c> 的行还能**右滑删除**：按住往右拖过 <see cref="DeleteDistance"/> 松手
/// 就删掉这一条，没拖够就弹回原位。删除是「先播完退场动画、动画结束再回调」，
/// 所以看起来是这一行滑出去消失，而不是啪一下凭空不见。
///
/// 手势的落点全在 <c>_root</c> 上（它自己不动），指针坐标才稳定；跟着手指平移的是里面的
/// <c>_frame</c>。要是把事件挂在会动的元素上，<c>GetCurrentPoint</c> 的坐标系会跟着元素跑，
/// 位移就被重复计算了。
///
/// <paramref name="dense"/> 是「紧凑但可点」：大岛展开态只有 168 DIP 高，要塞下 3 行，
/// 沿用聚光卡那种带内边距的行会撑爆 —— 所以尺寸按紧凑走，只额外提供 hover 与点击。
///
/// 配色跟着岛体主题走（浅色岛体上写死白色就是白字压白底）：中性色全部由 <paramref name="theme"/> 决定。
/// 只有删除底色是写死的红 —— 红底白字在任何主题下都成立。
/// </summary>
internal sealed class ClipRow : UserControl
{
    /// <summary>横向拖过这么多像素才算「在滑」，否则还按点击处理（手抖不该把点击吃掉）。</summary>
    private const double SwipeSlop = 8;

    /// <summary>松手时滑过这么多就删掉这一条。</summary>
    private const double DeleteDistance = 64;

    /// <summary>最多能拖到多远：拖到底就停住，不跟着手指无限跑。</summary>
    private const double MaxSwipe = 132;

    /// <summary>确认删除后这一行往右滑出的距离。</summary>
    private const double ExitDistance = 220;

    private const double ExitMilliseconds = 170;
    private const double SnapMilliseconds = 150;

    /// <summary>裁剪矩形给一个很大的高度：只靠宽度做「从左往右一点点露出红带」，竖向不裁。</summary>
    private const double ClipHeightSentinel = 4000;

    /// <summary>删除底色（Windows 的 critical 红）。白字白图标压在上面，任何岛体主题下都成立。</summary>
    private static readonly Windows.UI.Color DangerColor = Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C);

    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private readonly SolidColorBrush _textBrush;
    private readonly SolidColorBrush _hintBrush;
    private readonly SolidColorBrush _glyphBrush;
    private readonly SolidColorBrush _hoverBrush;
    private readonly SolidColorBrush _neutralFill;

    private readonly ClipItem _item;
    private readonly Border _frame;
    private readonly IIslandTheme _theme;
    private readonly Action<ClipItem>? _onDelete;

    /// <summary>命中测试与手势的落点：它自己永不移动，所以指针坐标是稳定参照。</summary>
    private readonly Grid _root;

    // ---- 右滑删除专用（这一行不可删除时全是 null） ----
    private readonly Border? _revealHost;
    private readonly RectangleGeometry? _revealClip;
    private readonly TextBlock? _revealLabel;
    private readonly TranslateTransform? _slide;
    private readonly DispatcherQueueTimer? _timer;

    private bool _pointerDown;
    private bool _swiping;
    private uint _pointerId;
    private double _originX;
    private double _offsetX;

    /// <summary>松手之后 Tapped 还会来一次，用它把这次点击吃掉（下一次按下时清零）。</summary>
    private bool _suppressTap;

    /// <summary>红带是否已经拖过删除线（只为了把提示文案从「右滑删除」换成「松手删除」）。</summary>
    private bool _armed;

    private bool _deleteAfterAnimation;
    private double _animateFrom;
    private double _animateTo;
    private DateTimeOffset _animateStart;
    private TimeSpan _animateDuration;

    public ClipRow(
        ClipItem item,
        IIslandTheme theme,
        bool interactive,
        Action<ClipItem>? invoke,
        bool dense = false,
        Action<ClipItem>? onDelete = null)
    {
        _item = item;
        _theme = theme;
        _onDelete = onDelete;

        _textBrush = new SolidColorBrush(Neutral(theme, 235));
        _hintBrush = new SolidColorBrush(Neutral(theme, 125));
        _glyphBrush = new SolidColorBrush(Neutral(theme, 190));
        _hoverBrush = new SolidColorBrush(Neutral(theme, 26));
        _neutralFill = new SolidColorBrush(Neutral(theme, 34));

        var leadingSize = dense ? 20d : interactive ? 24d : 20d;
        var fontSize = dense ? 12d : interactive ? 12.5 : 12;
        var padding = dense
            ? new Thickness(4, 3, 4, 3)
            : interactive ? new Thickness(10, 6, 10, 6) : new Thickness(0, 3, 0, 3);

        var glyph = new FontIcon
        {
            Glyph = item.IsImage ? "\uEB9F" : item.IsFiles ? "\uE8B7" : "\uE8A5",
            FontSize = leadingSize * 0.62,
            Foreground = _glyphBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var leading = new Grid
        {
            Width = leadingSize,
            Height = leadingSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leading.Children.Add(new Border { Background = _neutralFill, CornerRadius = new CornerRadius(6) });
        leading.Children.Add(glyph);

        if (item.IsImage && item.ImagePath is not null)
        {
            var thumb = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
            var thumbFrame = new Border
            {
                CornerRadius = new CornerRadius(5),
                Opacity = 0,
                Background = _neutralFill,
                Child = thumb,
            };
            leading.Children.Add(thumbFrame);

            // 缩略图异步补齐；拿不到就继续显示类型图标，不报错
            _ = FillThumbnailAsync(thumb, thumbFrame, glyph, item.ImagePath, (int)(leadingSize * 3));
        }

        var summary = new TextBlock
        {
            Text = item.Summary,
            FontSize = fontSize,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textBrush,
        };

        var time = new TextBlock
        {
            Text = ClipText.Ago(item.CapturedAt),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _hintBrush,
        };

        var grid = new Grid { ColumnSpacing = dense ? 8 : interactive ? 10 : 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(leading);
        Grid.SetColumn(summary, 1);
        grid.Children.Add(summary);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        var highlight = new Border { Background = _hoverBrush, CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
        Motion.Visual(highlight).Opacity = 0;
        var layers = new Grid();
        layers.Children.Add(highlight);
        layers.Children.Add(new Border { Padding = padding, Child = grid });
        _frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            // 命中测试需要非 null 的画刷（全透明也行），否则 Tapped 不会触发
            Background = interactive ? TransparentBrush : null,
            Child = layers,
        };

        if (interactive)
        {
            _frame.PointerEntered += (_, _) => Motion.Scalar(Motion.Visual(highlight), "Opacity", 1, 140);
            _frame.PointerExited += (_, _) => Motion.Scalar(Motion.Visual(highlight), "Opacity", 0, 160);
            _frame.Tapped += (_, e) =>
            {
                // 必须吃掉事件：宿主只看得到 ButtonBase/Slider 这类控件，
                // 不标记 Handled 的话这次点击会继续冒泡到岛体，又弹一次聚光卡
                e.Handled = true;

                // 刚结束一次滑动，这个 Tapped 是滑动的尾巴，不是点击
                if (_suppressTap) return;
                invoke?.Invoke(_item);
            };
        }

        _root = new Grid();

        if (interactive && onDelete is not null)
        {
            var whiteBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

            _revealClip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, 0, ClipHeightSentinel),
            };

            var revealInner = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Padding = new Thickness(dense ? 7 : 13, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = "\uE74D",   // Delete
                        FontSize = dense ? 11 : 13,
                        Foreground = whiteBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };

            // 大岛展开态那几行又矮又密，塞不下「右滑删除」这几个字，只留一个垃圾桶图标
            if (!dense)
            {
                _revealLabel = new TextBlock
                {
                    Text = "右滑删除",
                    FontSize = 12,
                    Foreground = whiteBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                revealInner.Children.Add(_revealLabel);
            }

            // 红底整行铺满，靠 Clip 只露出左边那一条 —— 所以「往右滑」看到的是左侧露出的红带，
            // 而不是整行变红（整行变红等于给文字换了底，浅色主题下很难看）
            _revealHost = new Border
            {
                Background = new SolidColorBrush(DangerColor),
                CornerRadius = new CornerRadius(6),
                Child = revealInner,
                Clip = _revealClip,
            };

            _slide = new TranslateTransform();
            _frame.RenderTransform = _slide;

            // 透明画刷才吃得到指针（null 画刷不参与命中测试）
            _root.Background = TransparentBrush;
            _root.Children.Add(_revealHost);
            _root.Children.Add(_frame);

            _root.PointerPressed += OnPointerPressed;
            _root.PointerMoved += OnPointerMoved;
            _root.PointerReleased += OnPointerReleased;
            _root.PointerCaptureLost += (_, _) => OnGestureLost();

            // 逐帧动画用的 UI 线程定时器；拿不到就退化成"直接落终态"
            // （插件程序集里不能用 Storyboard，原因见 ClipboardIslandView.StartMorph 的注释）
            _timer = DispatcherQueue?.CreateTimer();
            if (_timer is not null)
            {
                _timer.Interval = TimeSpan.FromMilliseconds(16);
                _timer.IsRepeating = true;
                _timer.Tick += (_, _) => OnAnimationTick();
            }

            Unloaded += (_, _) => _timer?.Stop();
        }
        else
        {
            _root.Children.Add(_frame);
        }

        Content = _root;
    }

    public ClipItem Item => _item;

    /// <summary>岛体换主题时就地重刷中性色（视图颜色烘在画刷里，不重刷会留在旧主题）。</summary>
    internal void RefreshTheme()
    {
        _textBrush.Color = Neutral(_theme, 235);
        _hintBrush.Color = Neutral(_theme, 125);
        _glyphBrush.Color = Neutral(_theme, 190);
        _hoverBrush.Color = Neutral(_theme, 26);
        _neutralFill.Color = Neutral(_theme, 34);
    }

    // ---------------------------------------------------------------- 右滑删除

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 上一次滑动的尾巴到这里就翻篇了
        _suppressTap = false;

        // 还在弹回 / 滑出动画中按下：停住动画，从当前位置接着跟手
        _deleteAfterAnimation = false;
        _timer?.Stop();

        // 上一次的退场动画可能把这一行淡掉了
        _frame.Opacity = 1;
        if (_revealHost is not null) _revealHost.Opacity = 1;

        _pointerId = e.Pointer.PointerId;
        _originX = e.GetCurrentPoint(_root).Position.X;
        _pointerDown = true;
        _swiping = false;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown || e.Pointer.PointerId != _pointerId) return;

        var dx = e.GetCurrentPoint(_root).Position.X - _originX;

        if (!_swiping)
        {
            // 往左滑不做事（只做右滑删除）；位移太小也还是当点击
            if (dx < SwipeSlop) return;

            _swiping = true;
            _root.CapturePointer(e.Pointer);
        }

        ApplyOffset(Math.Min(dx, MaxSwipe));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown || e.Pointer.PointerId != _pointerId) return;

        var wasSwiping = _swiping;
        _pointerDown = false;
        _swiping = false;

        // 释放捕获可能再触发一次 PointerCaptureLost；此时 _swiping 已归 false，不会重复播动画
        _root.ReleasePointerCapture(e.Pointer);

        if (!wasSwiping)
        {
            // 这一行正停在半路，用户只是点了一下：弹回原位，别顺手把内容粘出去
            if (_offsetX > 0.5)
            {
                _suppressTap = true;
                StartAnimation(0, SnapMilliseconds, deleteAfter: false);
            }

            return;
        }

        _suppressTap = true;   // 滑动已经吃掉了这一次点击

        if (_offsetX >= DeleteDistance)
        {
            StartAnimation(ExitDistance, ExitMilliseconds, deleteAfter: true);
        }
        else
        {
            StartAnimation(0, SnapMilliseconds, deleteAfter: false);
        }
    }

    /// <summary>指针被系统抢走（比如手势中途切了窗口）：弹回原位，别让这一行卡在半路。</summary>
    private void OnGestureLost()
    {
        _pointerDown = false;
        if (!_swiping) return;

        _swiping = false;
        StartAnimation(0, SnapMilliseconds, deleteAfter: false);
    }

    private void StartAnimation(double target, double milliseconds, bool deleteAfter)
    {
        _animateFrom = _offsetX;
        _animateTo = target;
        _deleteAfterAnimation = deleteAfter;
        _animateStart = DateTimeOffset.UtcNow;
        _animateDuration = Motion.Duration(milliseconds);

        if (_timer is null)
        {
            // 拿不到 UI 定时器就别留下"滑到一半"的中间态：直接落终态
            ApplyOffset(target);
            if (deleteAfter) NotifyDelete();
            return;
        }

        _timer.Start();
    }

    private void OnAnimationTick()
    {
        var total = Math.Max(1, _animateDuration.TotalMilliseconds);
        var elapsed = (DateTimeOffset.UtcNow - _animateStart).TotalMilliseconds;
        var t = Math.Clamp(elapsed / total, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);   // CubicEaseOut

        ApplyOffset(_animateFrom + (_animateTo - _animateFrom) * eased);

        if (_deleteAfterAnimation)
        {
            // 滑出去的同时淡掉：看着像"被拖走了"，而不是"滑到边上被裁掉"
            var fade = 1 - eased;
            _frame.Opacity = fade;
            if (_revealHost is not null) _revealHost.Opacity = fade;
        }

        if (t < 1) return;

        _timer?.Stop();
        ApplyOffset(_animateTo);

        if (!_deleteAfterAnimation) return;

        _deleteAfterAnimation = false;
        NotifyDelete();
    }

    private void ApplyOffset(double x)
    {
        _offsetX = Math.Clamp(x, 0, ExitDistance);

        if (_slide is not null) _slide.X = _offsetX;

        if (_revealClip is not null)
        {
            // 红带宽度 = 已经滑出去的距离，正好补上左边空出来的那块
            _revealClip.Rect = new Windows.Foundation.Rect(0, 0, Math.Min(_offsetX, MaxSwipe), ClipHeightSentinel);
        }

        if (_revealLabel is null) return;

        var armed = _offsetX >= DeleteDistance;
        if (armed == _armed) return;

        _armed = armed;
        _revealLabel.Text = armed ? "松手删除" : "右滑删除";
    }

    private void NotifyDelete()
    {
        try
        {
            _onDelete?.Invoke(_item);
        }
        catch
        {
            // 删除回调出错不能冒到宿主；这一行已经滑走了，上层会记日志
        }
    }

    /// <summary>中性色：岛体深色时是白色系，浅色（Fluent + 浅色系统）时是黑色系。</summary>
    private static Windows.UI.Color Neutral(IIslandTheme theme, byte alpha) => theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    private static async Task FillThumbnailAsync(Image target, Border frame, FontIcon glyph, string path, int decodeWidth)
    {
        try
        {
            var image = await ThumbnailLoader.LoadAsync(path, decodeWidth);
            if (image is null) return;

            target.Source = image;
            target.Opacity = 1;
            frame.Opacity = 1;
            Motion.Reveal(frame, 180, 0);
            Motion.Scalar(Motion.Visual(glyph), "Opacity", 0, 180);
        }
        catch
        {
            // 缩略图加载失败：保持类型图标，什么都不用做（这里绝不能把异常抛给宿主）
        }
    }
}
