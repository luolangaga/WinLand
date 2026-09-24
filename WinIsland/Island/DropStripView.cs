using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text;
using Windows.Foundation;
using Windows.Storage.Streams;
using WinIsland.Core;

namespace WinIsland.Island;

/// <summary>
/// 一次拖拽读出来的载荷（宿主内部表示；对插件暴露的是 <see cref="IslandDropContext"/>）。
/// </summary>
internal sealed record DropPayload(
    IslandDropKind Kind,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Names,
    string? Text = null,
    byte[]? ImageBytes = null)
{
    /// <summary>
    /// 还没读完 / 读不出来时的占位载荷：此时所有卡片都算「可投」，
    /// 免得用户快速松手时一张卡片都点不亮；真正的匹配在 Drop 时按实际载荷复核。
    /// </summary>
    public static DropPayload Unknown { get; } = new(IslandDropKind.None, Array.Empty<string>(), Array.Empty<string>());

    public bool IsKnown => Kind != IslandDropKind.None;
}

/// <summary>
/// 投放面板：拖文件到岛上时岛体展开成的那一条。
/// 左边是固定不动的载荷摘要（图标 + 文件名 / 「N 项」），右边是可横向滚动的投放卡片。
///
/// 三条设计约束：
/// 1. 卡片长在**岛体内部**，所以点击穿透形状（只取主岛矩形）不需要任何改动；
/// 2. 面板尺寸是常量，由 <c>IslandWindow.ComputeCanvasFootprint</c> 提前预留进画布 ——
///    拖拽全程不改窗口几何，避免 DWM 残影与 OLE 命中被破坏；
/// 3. 悬停/进场动画只用组合级属性（RenderTransform、Opacity），不触发布局与重绘，
///    每一帧都要和边缘滚动同帧共存。
///
/// 这里只管"长什么样、怎么动、点在谁身上"；会话状态（何时展开/收回、边缘滚动的速度与方向）
/// 全在 <see cref="IslandWindow"/>。
/// </summary>
internal sealed class DropStripView
{
    private const double StripPaddingX = 20;
    private const double StripPaddingY = 14;
    private const double SummaryWidth = 116;
    private const double TileWidth = 72;
    private const double TileHeight = 76;
    private const double TileSpacing = 10;
    private const double EdgeFadeWidth = 22;
    private const double HoverScale = 1.05;

    private static readonly TimeSpan HoverInDuration = TimeSpan.FromMilliseconds(167);
    private static readonly TimeSpan HoverOutDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan EnterFadeDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan EnterSlideDuration = TimeSpan.FromMilliseconds(250);

    private readonly IslandStyleKind _style;
    private readonly bool _materialApplied;
    private readonly bool _light;

    private readonly FontIcon _summaryGlyph;
    private readonly Image _summaryThumb;
    private readonly Border _summaryThumbHost;
    private readonly TextBlock _summaryTitle;
    private readonly TextBlock _summaryDetail;
    private readonly StackPanel _tilePanel;
    private readonly ScrollViewer _scroller;
    private readonly Border _leftFade;
    private readonly Border _rightFade;
    private readonly TranslateTransform _slide = new();
    private readonly List<DropTile> _tiles = new();

    private DropPayload _payload = DropPayload.Unknown;
    /// <summary>载荷版本：异步解出来的缩略图要能判断自己是不是已经过期。</summary>
    private int _payloadVersion;
    private int _armedIndex = -1;

    public DropStripView(IslandStyleKind style, bool materialApplied, bool light)
    {
        _style = style;
        _materialApplied = materialApplied;
        _light = light;

        _summaryGlyph = new FontIcon
        {
            Glyph = "\uE7C3",
            FontSize = 18,
            Foreground = IslandStyle.CreatePanelSecondaryBrush(light),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 图片载荷时用缩略图替掉图标（同一格里切换显隐，布局不跳）
        _summaryThumb = new Image { Stretch = Stretch.UniformToFill };
        _summaryThumbHost = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(7),
            Visibility = Visibility.Collapsed,
            Child = _summaryThumb,
        };

        var iconBox = new Grid
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _summaryGlyph, _summaryThumbHost },
        };

        _summaryTitle = new TextBlock
        {
            Text = "拖放内容",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = IslandStyle.CreatePanelTextBrush(light),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _summaryDetail = new TextBlock
        {
            Text = "拖到卡片上松开",
            FontSize = 11,
            Foreground = IslandStyle.CreatePanelHintBrush(light),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBox, 0);
        Grid.SetColumn(_summaryTitle, 1);
        head.Children.Add(iconBox);
        head.Children.Add(_summaryTitle);

        var summary = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { head, _summaryDetail },
        };

        _tilePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = TileSpacing,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
            Content = _tilePanel,
        };
        _scroller.ViewChanged += (_, _) => UpdateEdgeFades();
        _scroller.SizeChanged += (_, _) => UpdateEdgeFades();

        // 左右渐隐：告诉用户"这一排还能往两边滚"
        _leftFade = BuildEdgeFade(leftAligned: true);
        _rightFade = BuildEdgeFade(leftAligned: false);

        var scrollHost = new Grid { Children = { _scroller, _leftFade, _rightFade } };

        var root = new Grid
        {
            Padding = new Thickness(StripPaddingX, StripPaddingY, StripPaddingX, StripPaddingY),
            ColumnSpacing = 10,
            RenderTransform = _slide,
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SummaryWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(summary, 0);
        Grid.SetColumn(scrollHost, 1);
        root.Children.Add(summary);
        root.Children.Add(scrollHost);

        Root = root;
    }

    public UIElement Root { get; }

    /// <summary>滚动视口的宽度（DIP）：边缘带的落点判定用它。</summary>
    public double ViewportWidth => _scroller.ViewportWidth;

    /// <summary>可滚动总量（DIP）。为 0 表示所有卡片都放得下，不需要边缘滚动。</summary>
    public double ScrollableWidth => Math.Max(0, _scroller.ScrollableWidth);

    /// <summary>当前滚动位置（DIP）。卡片增减后宿主用它重新对齐自己的滚动偏移。</summary>
    public double HorizontalOffset => _scroller.HorizontalOffset;

    /// <summary>当前高亮（可投放）的卡片索引，-1 表示没有。</summary>
    public int ArmedIndex => _armedIndex;

    /// <summary>
    /// 把 <see cref="Root"/> 坐标换算成滚动视口坐标：边缘带判定必须相对视口，
    /// 而 Root 里还有左侧摘要那一列，直接拿 Root 坐标算会把边缘带整体左移。
    /// </summary>
    public Point ToViewportPoint(Point rootPoint)
    {
        var origin = _scroller.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        return new Point(rootPoint.X - origin.X, rootPoint.Y - origin.Y);
    }

    /// <summary>重建卡片。投放会话进行中也会被调用（插件注册/注销目标）。</summary>
    public void SetTargets(IReadOnlyList<(string Owner, IslandDropTarget Target)> targets)
    {
        _tilePanel.Children.Clear();
        _tiles.Clear();
        _armedIndex = -1;

        foreach (var (_, target) in targets)
        {
            var tile = new DropTile(target, _style, _materialApplied, _light);
            _tiles.Add(tile);
            _tilePanel.Children.Add(tile.Root);
        }

        RefreshPayloadState();
        UpdateEdgeFades();
    }

    /// <summary>载荷变化：决定每张卡片接不接受、并刷新左侧摘要（文件 / 文本 / 图片各有各的样子）。</summary>
    public void SetPayload(DropPayload payload)
    {
        _payload = payload;
        _payloadVersion++;
        RefreshPayloadState();
        UpdateEdgeFades();
    }

    /// <summary>命中测试（坐标相对 <see cref="Root"/>）。返回卡片索引，-1 表示没命中可投放的卡片。</summary>
    public int HitTest(Point point)
    {
        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (!tile.Accepts) continue;

            if (tile.Root.ActualWidth <= 0 || tile.Root.ActualHeight <= 0) continue;
            var box = tile.Root.TransformToVisual(Root)
                .TransformBounds(new Rect(0, 0, tile.Root.ActualWidth, tile.Root.ActualHeight));
            if (point.X >= box.X && point.X <= box.X + box.Width
                && point.Y >= box.Y && point.Y <= box.Y + box.Height)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>设置高亮卡片（只动组合级属性，不触发布局）。返回是否真的变了。</summary>
    public bool SetArmed(int index)
    {
        if (index == _armedIndex) return false;

        if (_armedIndex >= 0 && _armedIndex < _tiles.Count)
        {
            _tiles[_armedIndex].SetArmed(false, animate: true);
        }

        _armedIndex = index;

        if (_armedIndex >= 0 && _armedIndex < _tiles.Count)
        {
            _tiles[_armedIndex].SetArmed(true, animate: true);
        }

        return true;
    }

    public string TitleOf(int index)
        => index >= 0 && index < _tiles.Count ? _tiles[index].Title : string.Empty;

    /// <summary>把滚动位置直接落到指定偏移（拖拽期间每帧调用，关掉滚动动画以保证跟手）。</summary>
    public void ScrollTo(double offset)
    {
        var clamped = Math.Clamp(offset, 0, ScrollableWidth);
        if (Math.Abs(_scroller.HorizontalOffset - clamped) < 0.5) return;
        _scroller.ChangeView(clamped, null, null, disableAnimation: true);
    }

    /// <summary>进场：淡入 + 轻微右移到位。尺寸 morph 由岛体自己负责。</summary>
    public void PlayEnter()
    {
        Root.Opacity = 0;
        _slide.X = -12;
        Animate(Root, "Opacity", 1, EnterFadeDuration);
        Animate(_slide, "X", 0, EnterSlideDuration);
    }

    /// <summary>载荷一变就重算每张卡片的可投状态，并把摘要换成对应类型的样式。</summary>
    private void RefreshPayloadState()
    {
        bool anyAccepts = false;
        foreach (var tile in _tiles)
        {
            tile.Accepts = tile.AcceptsPayload(_payload);
            // 不接受当前载荷的卡片**直接收起来**（不是变暗）：拖什么就只看到"能对它做什么"。
            // 收起而不是从列表里删掉，是为了让卡片索引与 _dropTargets 一一对应（Drop 时按索引取目标）。
            tile.Root.Visibility = tile.Accepts ? Visibility.Visible : Visibility.Collapsed;
            anyAccepts |= tile.Accepts;
        }

        // 高亮的那张可能刚被收起来，别再指着它
        if (_armedIndex >= 0 && (_armedIndex >= _tiles.Count || !_tiles[_armedIndex].Accepts)) _armedIndex = -1;

        SetThumbnail(null);

        string glyph;
        string title;
        string? detail;

        switch (_payload.Kind)
        {
            case IslandDropKind.Text:
                var preview = Collapse(_payload.Text);
                glyph = "\uE70F";
                title = preview.Length > 0 ? preview : "（空白文本）";
                detail = $"文本 · {_payload.Text?.Length ?? 0} 字符";
                break;

            case IslandDropKind.Image:
                glyph = "\uE91B";
                title = "图片";
                detail = $"图片 · {DescribeBytes(_payload.ImageBytes?.LongLength ?? 0)}";
                _ = LoadThumbnailAsync(_payload.ImageBytes);   // 解出缩略图再换上，解不出来就留着图标
                break;

            case IslandDropKind.Files:
                (glyph, title, detail) = DescribeFiles();
                break;

            default:
                glyph = "\uE7C3";
                title = "正在读取…";
                detail = null;
                break;
        }

        // 一张都没剩下：说清楚，而不是留一片空白
        if (_payload.IsKnown && !anyAccepts) title = "没有卡片能接收它";

        SetSummary(glyph, title, detail);

        // 卡片收起后可能不用滚了、也滚不到原来那么远：夹一次（ChangeView 自己会夹到合法范围）
        if (_scroller.HorizontalOffset > ScrollableWidth)
        {
            _scroller.ChangeView(ScrollableWidth, null, null, disableAnimation: true);
        }

        UpdateEdgeFades();
    }

    private (string Glyph, string Title, string? Detail) DescribeFiles()
    {
        var names = _payload.Names;
        if (names.Count == 1)
        {
            var path = _payload.Paths.Count > 0 ? _payload.Paths[0] : null;
            return (path != null && Directory.Exists(path) ? "\uE8B7" : "\uE7C3", names[0], "拖到卡片上松开");
        }

        return ("\uE8B7", $"{names.Count} 项", names.Count > 0 ? names[0] : null);
    }

    private void SetSummary(string glyph, string title, string? detail)
    {
        _summaryGlyph.Glyph = glyph;
        _summaryTitle.Text = title;
        _summaryDetail.Text = detail ?? string.Empty;
        _summaryDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetThumbnail(ImageSource? source)
    {
        _summaryThumb.Source = source;
        bool hasThumbnail = source != null;
        _summaryThumbHost.Visibility = hasThumbnail ? Visibility.Visible : Visibility.Collapsed;
        _summaryGlyph.Visibility = hasThumbnail ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadThumbnailAsync(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return;

        int version = _payloadVersion;
        var source = await DecodeThumbnailAsync(bytes);
        if (source == null || version != _payloadVersion) return;

        SetThumbnail(source);
    }

    /// <summary>把图片字节解成 96px 缩略图（解不出来就退回图标，不影响投放本身）。</summary>
    private static async Task<ImageSource?> DecodeThumbnailAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            writer.Dispose();

            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 96 };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>把多行文本压成一行摘要（超长截断）。</summary>
    private static string Collapse(string? text)
    {
        const int maxChars = 60;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(Math.Min(text.Length, maxChars + 1));
        bool pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (builder.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
            if (builder.Length > maxChars) break;
        }

        var result = builder.ToString().Trim();
        return result.Length > maxChars ? result[..maxChars] + "…" : result;
    }

    private static string DescribeBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / 1024.0 / 1024.0:0.#} MB";
    }

    private Border BuildEdgeFade(bool leftAligned)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(leftAligned ? 0 : 1, 0.5), EndPoint = new Point(leftAligned ? 1 : 0, 0.5) };
        // 从面板底色（深色主题的黑、浅色主题的白）过渡到同色透明，和材质/胶囊底衬同一套规则
        var fade = IslandStyle.PanelEdgeFadeColor(_light);
        brush.GradientStops.Add(new GradientStop { Color = fade, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, fade.R, fade.G, fade.B), Offset = 1 });

        return new Border
        {
            Width = EdgeFadeWidth,
            Background = brush,
            HorizontalAlignment = leftAligned ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            IsHitTestVisible = false,
            Opacity = 0,
        };
    }

    private void UpdateEdgeFades()
    {
        bool scrollable = ScrollableWidth > 1;
        double offset = _scroller.HorizontalOffset;
        _leftFade.Opacity = scrollable && offset > 1 ? 1 : 0;
        _rightFade.Opacity = scrollable && offset < ScrollableWidth - 1 ? 1 : 0;
    }

    /// <summary>
    /// 卡片是否接受这份载荷：先看类型（<see cref="IslandDropTarget.Kinds"/>），文件再看扩展名白名单
    /// （载荷里只要有一项匹配就接受）。卡片的变暗/命中判定与 Drop 时的复核共用这一份规则 —— 别在别处再写一遍。
    /// </summary>
    internal static bool Accepts(IslandDropTarget target, DropPayload payload)
    {
        // 载荷还没读出来时先都算可投：用户可能很快就松手，真正的匹配在 Drop 时按实际载荷复核
        if (!payload.IsKnown) return true;

        if ((target.Kinds & payload.Kind) == 0) return false;
        if (payload.Kind != IslandDropKind.Files) return true;

        var extensions = target.Extensions;
        if (extensions is not { Count: > 0 }) return true;

        foreach (var path in payload.Paths)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) continue;

            foreach (var wanted in extensions)
            {
                if (string.Equals(ext, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    /// <summary>组合级属性动画（不触发重排/重绘，和边缘滚动同帧也不掉帧）。</summary>
    private static void Animate(DependencyObject target, string property, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>一张投放卡片：底色 + 高亮层 + 图标/标题，悬停时放大 + 高亮淡入。</summary>
    private sealed class DropTile
    {
        private readonly Border _highlight;
        private readonly ScaleTransform _scale = new();
        private readonly IslandDropTarget _target;

        public DropTile(IslandDropTarget target, IslandStyleKind style, bool materialApplied, bool light)
        {
            _target = target;
            double radius = IslandStyle.ResolveDropTileRadius(style, TileHeight);
            var stroke = IslandStyle.CreateDropTileStroke(style, light);

            _highlight = new Border
            {
                CornerRadius = new CornerRadius(radius),
                Background = IslandStyle.CreateDropTileHighlight(style, target.AccentColor, light),
                Opacity = 0,
            };

            var glyph = new FontIcon
            {
                Glyph = target.Glyph,
                FontSize = 19,
                Foreground = IslandStyle.CreatePanelSecondaryBrush(light),
            };

            var label = new TextBlock
            {
                Text = target.Title,
                FontSize = 11.5,
                Foreground = IslandStyle.CreatePanelTextBrush(light),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                MaxLines = 2,
            };

            var stack = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { glyph, label },
            };

            var content = new Grid
            {
                RenderTransform = _scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Children = { _highlight, stack },
            };

            Root = new Border
            {
                Width = TileWidth,
                Height = TileHeight,
                CornerRadius = new CornerRadius(radius),
                Background = IslandStyle.CreateDropTileFill(style, materialApplied, light),
                BorderBrush = stroke,
                BorderThickness = stroke == null ? new Thickness(0) : new Thickness(1),
                Child = content,
            };
        }

        public Border Root { get; }

        public string Title => _target.Title;

        /// <summary>当前载荷是否被这张卡片接受（扩展名过滤）。</summary>
        public bool Accepts { get; set; } = true;

        /// <summary>这张卡片接不接受当前载荷（类型 + 扩展名，规则见 <see cref="Accepts"/>）。</summary>
        public bool AcceptsPayload(DropPayload payload) => Accepts(_target, payload);

        public void SetArmed(bool armed, bool animate)
        {
            double scale = armed ? HoverScale : 1.0;
            double highlight = armed ? 1 : 0;

            if (!animate)
            {
                _scale.ScaleX = scale;
                _scale.ScaleY = scale;
                _highlight.Opacity = highlight;
                return;
            }

            var duration = armed ? HoverInDuration : HoverOutDuration;
            DropStripView.Animate(_scale, "ScaleX", scale, duration);
            DropStripView.Animate(_scale, "ScaleY", scale, duration);
            DropStripView.Animate(_highlight, "Opacity", highlight, duration);
        }
    }
}
