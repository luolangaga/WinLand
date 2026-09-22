using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using WinIsland.Island;

namespace WinIsland.Settings;

/// <summary>
/// 带示意图的单选卡片：位置 / 水平对齐 / 外观 / 材质 / 自启共用。
/// 下拉框看不出"选了它会发生什么"，卡片直接把结果画出来——位置是一块迷你屏幕，
/// 外观与材质用 <see cref="IslandStyle"/> 的真实画刷画岛体预览，选中项用强调色描边。
/// 根元素是 Button（<c>ChoiceCardStyle</c> 自定义模板），点击、键盘与无障碍天然可用。
/// </summary>
internal sealed class ChoiceCard
{
    private static readonly Color Accent = Color.FromArgb(255, 0, 122, 255);
    private static readonly Color AccentSoft = Color.FromArgb(28, 0, 122, 255);

    private readonly Button _root;
    private bool _selected;

    private ChoiceCard(string automationId, FrameworkElement? diagram, string title, string? caption)
    {
        var content = new StackPanel { Spacing = 6 };

        if (diagram != null)
        {
            diagram.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(diagram);
        }

        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ThemeBrush("TextFillColorPrimaryBrush", Color.FromArgb(255, 255, 255, 255)),
        });

        if (!string.IsNullOrEmpty(caption))
        {
            content.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush", Color.FromArgb(255, 144, 144, 149)),
            });
        }

        _root = new Button
        {
            Style = (Style)Application.Current.Resources["ChoiceCardStyle"],
            Content = content,
            MinHeight = diagram == null ? 96 : 148,
        };
        AutomationProperties.SetAutomationId(_root, automationId);
        AutomationProperties.SetName(_root, title);
        UpdateSelectionVisual();
    }

    public Button Root => _root;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            UpdateSelectionVisual();
        }
    }

    public bool IsEnabled
    {
        get => _root.IsEnabled;
        set => _root.IsEnabled = value;
    }

    /// <summary>创建卡片；用户点击（或键盘 Enter/Space、UIA Invoke）时触发 <paramref name="onSelect"/>。</summary>
    public static ChoiceCard Build(
        string automationId,
        FrameworkElement? diagram,
        string title,
        string? caption,
        Action onSelect)
    {
        var card = new ChoiceCard(automationId, diagram, title, caption);
        card._root.Click += (_, _) => onSelect();
        return card;
    }

    /// <summary>把一组卡片等宽铺进一行（星号列 + 10px 间距），同一行里尺寸完全一致。</summary>
    public static void FillRow(Grid row, params ChoiceCard[] cards)
    {
        row.Children.Clear();
        row.ColumnDefinitions.Clear();
        row.ColumnSpacing = 10;
        for (int i = 0; i < cards.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(cards[i].Root, i);
            row.Children.Add(cards[i].Root);
        }
    }

    private void UpdateSelectionVisual()
    {
        _root.Background = _selected
            ? new SolidColorBrush(AccentSoft)
            : ThemeBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(13, 255, 255, 255));
        _root.BorderBrush = _selected
            ? new SolidColorBrush(Accent)
            : ThemeBrush("CardStrokeColorDefaultBrush", Color.FromArgb(26, 255, 255, 255));
    }

    // ---- 示意图 ----

    /// <summary>
    /// 迷你屏幕示意图：<paramref name="bottom"/> 为 true 时岛嵌进底部任务栏（屏幕模型里那条浅色条），
    /// false 时贴在屏幕顶部；<paramref name="horizontal"/> 0 居中 / 1 靠左 / 2 靠右。
    /// </summary>
    public static FrameworkElement ScreenDiagram(bool bottom, int horizontal)
    {
        var screen = new Border
        {
            Width = 120,
            Height = 76,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 36)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };

        var inner = new Grid();
        screen.Child = inner;

        inner.Children.Add(new Border
        {
            Height = 18,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(54, 255, 255, 255)),
            CornerRadius = new CornerRadius(0, 0, 5, 5),
        });

        inner.Children.Add(new Border
        {
            Width = 44,
            Height = 13,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Accent),
            HorizontalAlignment = horizontal switch
            {
                1 => HorizontalAlignment.Left,
                2 => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center,
            },
            VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            Margin = new Thickness(
                horizontal == 1 ? 10 : 0,
                bottom ? 0 : 8,
                horizontal == 2 ? 10 : 0,
                bottom ? 3 : 0),
        });

        return screen;
    }

    /// <summary>外观风格预览：Apple 黑胶囊 / Fluent 小圆角 + 描边，画刷直接取自 <see cref="IslandStyle"/>。</summary>
    public static FrameworkElement StylePreview(IslandStyleKind style)
        => PreviewFrame(
            style,
            materialApplied: style == IslandStyleKind.Fluent,
            new SolidColorBrush(style == IslandStyleKind.Apple
                ? Color.FromArgb(255, 20, 20, 24)
                : Color.FromArgb(255, 38, 38, 46)));

    /// <summary>材质预览：Acrylic 高透（彩色壁纸从底下透出来）/ Mica 偏不透明（低饱和壁纸色）。</summary>
    public static FrameworkElement MaterialPreview(IslandMaterialKind material)
    {
        bool acrylic = material == IslandMaterialKind.Acrylic;
        return PreviewFrame(
            IslandStyleKind.Fluent,
            materialApplied: acrylic,
            acrylic ? VividWallpaper() : MutedWallpaper());
    }

    private static FrameworkElement PreviewFrame(IslandStyleKind style, bool materialApplied, Brush wallpaper)
    {
        var frame = new Grid { Width = 120, Height = 76 };
        frame.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = wallpaper,
            BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        });

        const double height = 30;
        var stroke = IslandStyle.CreateStroke(style);
        var pill = new Border
        {
            Width = 96,
            Height = height,
            CornerRadius = new CornerRadius(IslandStyle.ResolveRadius(style, height, expanded: false)),
            Background = IslandStyle.CreateSurfaceFill(style, materialApplied),
            BorderBrush = stroke,
            BorderThickness = stroke == null ? new Thickness(0) : new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var content = new Grid();
        content.Children.Add(new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Accent),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        });
        content.Children.Add(new Border
        {
            Width = 42,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(30, 0, 0, 0),
        });
        pill.Child = content;

        frame.Children.Add(pill);
        return frame;
    }

    private static Brush VividWallpaper()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 47, 111, 224) });
        brush.GradientStops.Add(new GradientStop { Offset = 0.55, Color = Color.FromArgb(255, 176, 79, 216) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(255, 47, 156, 143) });
        return brush;
    }

    private static Brush MutedWallpaper()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 58, 66, 84) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(255, 78, 62, 92) });
        return brush;
    }

    private static Brush ThemeBrush(string key, Color fallback)
        => Application.Current.Resources[key] as Brush ?? new SolidColorBrush(fallback);
}
