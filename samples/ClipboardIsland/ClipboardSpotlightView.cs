using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace ClipboardIsland;

/// <summary>
/// 「超级展开」聚光卡的内容：点岛体后由宿主从岛体位置带倾角飞入、居中放大的大卡片。
///
/// 三条要点：
///   1. 必须是**独立于岛视图**的另一棵可视树（每个窗口一棵树），实例可以复用；
///   2. 不需要实现 IMorphView：飞入飞回、遮罩、圆角、层级全由宿主负责，这里只管最终形态；
///   3. 每次打开前插件会调 <see cref="Refresh"/> 把最新历史铺进来。
/// </summary>
public sealed class ClipboardSpotlightView : UserControl
{
    private readonly PluginManifest _manifest;
    private readonly Action<ClipItem>? _onPick;
    private readonly Action? _onClear;
    private readonly IIslandTheme _theme;

    private readonly SolidColorBrush _textBrush;
    private readonly SolidColorBrush _hintBrush;
    private readonly TextBlock _count;
    private readonly Button _clearButton;
    private readonly StackPanel _rows;
    private readonly TextBlock _empty;
    private readonly ScrollViewer _scroller;

    private bool _confirmClear;

    /// <summary>中性色：岛体深色时是白色系，浅色（Fluent + 浅色系统）时是黑色系。</summary>
    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    public ClipboardSpotlightView(PluginManifest manifest, Action<ClipItem>? onPick, Action? onClear, IIslandTheme theme)
    {
        _manifest = manifest;
        _onPick = onPick;
        _onClear = onClear;
        _theme = theme;

        // 聚光卡也跟着岛体主题明暗：浅色卡片上写死白色就是白字压白底
        _textBrush = new SolidColorBrush(Neutral(255));
        _hintBrush = new SolidColorBrush(Neutral(150));

        var title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _count = new TextBlock
        {
            FontSize = 13,
            Foreground = _hintBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _clearButton = new Button { Content = "清空历史" };
        _clearButton.Click += (_, _) => OnClearClick();

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(_count, 1);
        header.Children.Add(_count);
        Grid.SetColumn(_clearButton, 2);
        header.Children.Add(_clearButton);

        var hint = new TextBlock
        {
            Text = "点任意一条直接粘进你正在输入的窗口 · 按 Esc 或点卡片外区域收起",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _hintBrush,
        };

        _rows = new StackPanel { Spacing = 2 };

        _empty = new TextBlock
        {
            Text = "还没有记录。复制一段文字或一张图片，这里就会记下来。",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _hintBrush,
            Margin = new Thickness(2, 12, 2, 0),
            Visibility = Visibility.Collapsed,
        };

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = _rows,
        };

        var root = new Grid
        {
            Padding = new Thickness(30, 26, 30, 26),
            RowSpacing = 12,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(header);
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);
        Grid.SetRow(_scroller, 2);
        root.Children.Add(_scroller);
        Grid.SetRow(_empty, 2);
        root.Children.Add(_empty);

        Content = root;
    }

    /// <summary>把最新的历史铺进卡片（每次打开前由插件调用）。</summary>
    public void Refresh(IReadOnlyList<ClipItem> items)
    {
        ResetClearButton();

        _count.Text = items.Count > 0 ? $"{items.Count} 条" : string.Empty;

        _rows.Children.Clear();
        foreach (var item in items)
        {
            _rows.Children.Add(new ClipRow(item, _theme, interactive: true, invoke: _onPick));
        }

        var hasItems = items.Count > 0;
        _scroller.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        _clearButton.IsEnabled = hasItems;
    }

    /// <summary>宿主收起卡片时回调（点卡片外 / Esc / 被别的插件替换 / 插件停用）。</summary>
    public void OnHostClosed() => ResetClearButton();

    private void OnClearClick()
    {
        // 没有弹窗权限的顾虑，就用「点两次」当确认，比 ContentDialog 省事也更稳
        if (!_confirmClear)
        {
            _confirmClear = true;
            _clearButton.Content = "再点一次确认清空";
            return;
        }

        ResetClearButton();
        _onClear?.Invoke();
    }

    private void ResetClearButton()
    {
        _confirmClear = false;
        _clearButton.Content = "清空历史";
    }
}
