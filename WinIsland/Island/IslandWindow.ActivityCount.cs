using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinIsland.Island;

public sealed partial class IslandWindow
{
    private const string ActivityCountKey = "island.showActivityCount";
    private const double ActivityCountSize = 24;
    private const double ActivityCountGap = 6;
    private bool _showActivityCount = true;
    private int _displayedActivityCount;
    private double ActivityCountExtent => _showActivityCount && _live.Count > 1
        ? ActivityCountSize + ActivityCountGap : 0;

    private void ApplyActivityCountStyle()
    {
        var stroke = IslandStyle.CreateStroke(_style, _light);
        ActivityCountBadge.Background = IslandStyle.CreateSurfaceFill(_style, _materialApplied, _light);
        ActivityCountBadge.BorderBrush = stroke;
        ActivityCountBadge.BorderThickness = stroke == null ? new Thickness(0) : new Thickness(1);
        ActivityCountText.Foreground = IslandStyle.CreateMessageTextBrush(_light);
    }

    // 总数来自活动注册表，包含主岛和未翻到的队列卡片，不把临时消息算作活动。
    // 与尺寸动画共用 UpdateHitRegion，实际渲染位置和窗口形状同帧更新。
    private ShapeRect? UpdateActivityCountBadge(double scale)
    {
        bool visible = IslandRoot.IsLoaded && _showActivityCount && _live.Count > 1 && !_dropping
                       && !_spotlightOccluded && (_tempContent == null || _expanded);
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (ActivityCountBadge.Visibility != visibility) ActivityCountBadge.Visibility = visibility;
        if (!visible) return null;

        if (_displayedActivityCount != _live.Count)
        {
            _displayedActivityCount = _live.Count;
            ActivityCountText.Text = _live.Count.ToString();
            ActivityCountText.FontSize = _live.Count >= 100 ? 9 : 11;
            AutomationProperties.SetName(ActivityCountBadge, $"{_live.Count} 个活动岛");
        }

        bool left = _horizontal == HorizontalRight;
        var main = IslandRoot.TransformToVisual(IslandStack)
            .TransformBounds(new Rect(0, 0, IslandRoot.ActualWidth, IslandRoot.ActualHeight));
        Canvas.SetLeft(ActivityCountBadge, left
            ? main.X - ActivityCountGap - ActivityCountSize : main.Right + ActivityCountGap);
        Canvas.SetTop(ActivityCountBadge, main.Y + (main.Height - ActivityCountSize) / 2);

        var shape = MainIslandShapeRect(scale);
        double x = left ? shape.X1 - (ActivityCountGap + ActivityCountSize) * scale
                        : shape.X2 + ActivityCountGap * scale;
        double y = (shape.Y1 + shape.Y2 - ActivityCountSize * scale) / 2;
        return new ShapeRect((int)Math.Round(x), (int)Math.Round(y),
            (int)Math.Round(x + ActivityCountSize * scale),
            (int)Math.Round(y + ActivityCountSize * scale),
            (int)Math.Round(ActivityCountSize * scale / 2));
    }
}
