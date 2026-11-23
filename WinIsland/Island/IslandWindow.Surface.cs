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
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using WinIsland.Core;
using WinIsland.Core.Plugins;

namespace WinIsland.Island;

public sealed partial class IslandWindow
{
    #region 岛体表面（主岛与队列卡片共用）

    /// <summary>队列卡片：与主岛同款表面（底衬 + 描边 + 内容）。</summary>
    private static IslandSurface CreateCardSurface() => IslandSurface.CreateCard();

    /// <summary>
    /// 岛体表面：底衬、描边与内容，主岛与队列卡片共用同一套外观规则。
    /// 材质是窗口级的（见 <see cref="ApplyBackdrop"/>），这里只负责圆角、底色与描边 ——
    /// 圆角会被 <see cref="UpdateHitRegion"/> 读走，是点击穿透形状的唯一来源。
    /// </summary>
    private sealed class IslandSurface
    {
        private readonly ContentPresenter _content;
        /// <summary>上缘 1px 渐变高光（只有 Fluent 有；Apple 的哑光纯黑胶囊不加）。</summary>
        private readonly Border _highlight;
        private readonly Grid _body;
        private readonly CompositionRoundedRectangleGeometry _bodyGeometry;
        private bool _moving;

        public void SetMoving(bool moving)
        {
            if (_moving == moving) return;
            _moving = moving;
            // The native material contour and XAML stroke can be presented a frame
            // apart. Keep the moving silhouette single; restore the fine edge at rest.
            if (Border.BorderBrush != null) Border.BorderBrush.Opacity = moving ? 0 : 1;
            _highlight.Opacity = !moving && _highlight.Background != null ? 1 : 0;
        }

        public IslandSurface(Border border, ContentPresenter content)
        {
            Border = border;
            _content = content;
            _highlight = new Border
            {
                Height = 1,
                Margin = new Thickness(1, 1, 1, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Opacity = 0,
            };

            if (border.Child is Grid host)
            {
                _body = host;
                // 主岛：XAML 里那层 Grid（ContentHost + ContentLeaving），高光挂在最上面
                host.Children.Add(_highlight);
            }
            else
            {
                // 卡片：必须先把 Border 的 Child 换成包裹层（这时 content 才从 Border 上摘下来），
                // 再往包裹层里挂 —— 顺序反了就是"元素已挂在另一个父级下"，构造当场抛 COMException。
                var wrapper = new Grid();
                _body = wrapper;
                border.Child = wrapper;
                wrapper.Children.Add(content);
                wrapper.Children.Add(_highlight);
            }
            // Clip plugin visuals and their effects to the same inner rounded contour.
            // The opaque surface itself stays painted while content animates inside it.
            var visual = ElementCompositionPreview.GetElementVisual(_body);
            _bodyGeometry = visual.Compositor.CreateRoundedRectangleGeometry();
            visual.Clip = visual.Compositor.CreateGeometricClip(_bodyGeometry);
            _body.SizeChanged += (_, _) => UpdateContour();
        }

        /// <summary>
        /// 独立卡片：Border 与内容宿主自己搭起来（主岛的两个元素来自 XAML）。
        /// 内容层的缩放变换是给翻页动画用的（只动内容、不动卡片矩形，所以点击形状不受影响）——
        /// 只有卡片有它，主岛那个 ContentHost 的变换归内容交接（<c>_contentSlide</c>）所有。
        /// </summary>
        public static IslandSurface CreateCard()
        {
            var content = new ContentPresenter();
            var surface = new IslandSurface(new Border { Child = content }, content);
            content.RenderTransform = surface.ContentScale;
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            return surface;
        }

        public Border Border { get; }

        public ContentPresenter Content => _content;
        public bool IsPageOverlay { get; set; }
        public double RestingHeight { get; set; }

        /// <summary>卡片内容层的缩放（翻页时的进出微缩放）。</summary>
        public ScaleTransform ContentScale { get; } = new();

        /// <summary>卡片对应的活动：拆卸时按这个记录停止形态动画，顺序变化也安全。</summary>
        public string? Owner { get; set; }

        /// <summary>卡片内容的形态视图（可能为 null，例如只有双视图的插件）。</summary>
        public IMorphView? MorphView { get; set; }

        public void ApplyStyle(IslandStyleKind style, bool materialApplied, bool light)
        {
            if (IsPageOverlay)
            {
                Border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                Border.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                Border.BorderThickness = IslandStyle.CreateStroke(style, light) == null ? new Thickness(0) : new Thickness(1);
                _highlight.Opacity = 0;
                return;
            }
            var stroke = IslandStyle.CreateStroke(style, light);
            Border.Background = IslandStyle.CreateSurfaceFill(style, materialApplied, light);
            Border.BorderBrush = stroke;
            if (stroke != null) stroke.Opacity = _moving ? 0 : 1;
            Border.BorderThickness = stroke == null ? new Thickness(0) : new Thickness(1);

            var highlight = IslandStyle.CreateTopHighlight(style, light);
            _highlight.Background = highlight;
            _highlight.Opacity = highlight == null || _moving ? 0 : 1;
            UpdateContour();
        }

        public void SetRadius(double radius)
        {
            Border.CornerRadius = new CornerRadius(radius);
            UpdateContour();
        }

        private void UpdateContour()
        {
            float radius = (float)Math.Max(0, Border.CornerRadius.TopLeft - Border.BorderThickness.Left);
            var size = new Vector2((float)_body.ActualWidth, (float)_body.ActualHeight);
            _bodyGeometry.Size = size;
            _bodyGeometry.CornerRadius = new Vector2(Math.Min(radius, Math.Min(size.X, size.Y) / 2));
            // Highlight belongs inside the stroke, over the flat span only.
            _highlight.Margin = new Thickness(radius, 0, radius, 0);
        }

        private Grid? _scrollBody;

        public void SetContent(UIElement? content)
        {
            // Release the plugin view itself before reparenting it to the main island
            // or another queue card; dropping only the ScrollViewer is insufficient.
            _scrollBody?.Children.Clear();
            _scrollBody = null;
            _content.Content = content;
        }

        public void SetScrollableContent(UIElement content, double naturalHeight)
        {
            SetContent(null);
            _scrollBody = new Grid { Height = naturalHeight };
            _scrollBody.Children.Add(content);
            _content.Content = new ScrollViewer
            {
                Content = _scrollBody,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
        }
    }

    /// <summary>
    /// 队列翻页按钮：圆形外壳（<see cref="Border"/>，走和卡片同一套点击形状与配色通道）
    /// 加一枚内嵌的原生 <see cref="Button"/> —— 交互与悬停/按下态交给控件模板自己的主题态，不手搓。
    /// </summary>
    private sealed class QueuePager
    {
        private readonly FontIcon _glyph;

        public QueuePager(double size, string glyph, RoutedEventHandler clicked)
        {
            _glyph = new FontIcon
            {
                Glyph = glyph,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var button = new Button
            {
                Width = size - 2,
                Height = size - 2,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                IsTabStop = false,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(size / 2 - 1),
                Content = _glyph,
            };
            button.Click += clicked;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "QueueNext");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "下一个活动");

            Border = new Border
            {
                Width = size,
                Height = size,
                // 正圆：不走卡片那套 ResolveQueueRadius（那是给矩形卡片用的）
                CornerRadius = new CornerRadius(size / 2),
                Child = button,
            };
            ToolTipService.SetToolTip(Border, "下一个活动");
        }

        /// <summary>面板里的那个 Border：点击形状按它的矩形与圆角生成。</summary>
        public Border Border { get; }

        public void ApplyStyle(IslandStyleKind style, bool materialApplied, bool light)
        {
            var stroke = IslandStyle.CreateStroke(style, light);
            Border.Background = IslandStyle.CreateSurfaceFill(style, materialApplied, light);
            Border.BorderBrush = stroke;
            Border.BorderThickness = stroke == null ? new Thickness(0) : new Thickness(1);
            _glyph.Foreground = IslandStyle.CreateMessageTextBrush(light);
        }
    }

    #endregion
}

