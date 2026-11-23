using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WinIsland.Core;

namespace WinIsland.Island;

public sealed partial class IslandWindow
{
    #region 拖拽定位（按住 Alt + 左键 / 中键拖动岛体）

    /// <summary>拖拽落点：岛体左上角在「目标显示器外框」坐标系里的物理像素位置。</summary>
    private const string DragXKey = "island.dragX";
    private const string DragYKey = "island.dragY";

    /// <summary>开始拖拽需要先移动这么多物理像素，避免单击时的微小抖动把岛挪走。</summary>
    private const double DragStartPx = 4;

    /// <summary>拖拽中的光标位置与窗口原点之差（屏幕坐标，物理像素）。</summary>
    private Win32.POINT _dragGrab;
    private bool _dragging;
    private bool _dragArmed;
    private Win32.POINT _dragOriginCursor;

    /// <summary>
    /// 判断这次按下是否要开始拖拽：Alt + 左键，或中键。
    /// 两种都刻意避开「普通左键」—— 左键点击岛体是插件的主操作（播放/暂停、打开聚光卡），
    /// 不能被拖拽抢走。中键原本只用来关消息：按住拖动才算拖拽，单击仍关消息
    /// （调用点在 IslandRoot_PointerReleased）。
    ///
    /// 这里**不改 Handled**：中键单击还要靠 Tapped/松手走原来的路径，标了 handled
    /// 会让「关消息」那一下失效。真正开始拖动时才由 <see cref="ContinueDrag"/> 吃掉事件。
    /// </summary>
    private bool TryBeginDrag(PointerRoutedEventArgs e)
    {
        if (_dropping || _spotlightOccluded) return false;

        var point = e.GetCurrentPoint(IslandRoot);
        bool altDown = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu);
        bool middle = point.Properties.IsMiddleButtonPressed;

        if (!middle && !(altDown && point.Properties.IsLeftButtonPressed)) return false;

        Win32.GetCursorPos(out var cursor);
        _dragOriginCursor = cursor;
        _dragGrab = cursor;
        _dragArmed = true;
        _dragging = false;
        return true;
    }

    /// <summary>指针移动：越过起始阈值后真正开始拖动窗口。</summary>
    private void ContinueDrag(PointerRoutedEventArgs e)
    {
        if (!_dragArmed) return;

        Win32.GetCursorPos(out var cursor);
        int dx = cursor.X - _dragOriginCursor.X;
        int dy = cursor.Y - _dragOriginCursor.Y;
        if (!_dragging && Math.Abs(dx) < DragStartPx && Math.Abs(dy) < DragStartPx) return;

        if (!_dragging)
        {
            _dragging = true;
            // 展开态先收起来：拖着一大摞卡片走既难瞄也遮住落点
            if (_expanded) OnHoverExit();
            _hover = false;
            _hoverGuard.Stop();
            _hoverIntent.Stop();
            _log.Debug("开始拖拽岛体。");
        }

        MoveWithCursor(cursor);
        e.Handled = true;
    }

    /// <summary>按光标位置移动窗口（保持按下时的抓取偏移，窗口不会在指针下跳一下）。</summary>
    private void MoveWithCursor(Win32.POINT cursor)
    {
        int targetX = cursor.X - _dragGrab.X;
        int targetY = cursor.Y - _dragGrab.Y;

        // 位置要落成「岛体相对显示器外框」：窗口坐标里还含透明画布的 padding 与底部锚定偏移
        var display = ResolveDisplay();
        double s = Scale;
        double islandX = IslandLeftInWindow(s, _currentIsland.Width);
        double islandY = IslandTopInWindow(_bottomAnchored, s, _currentIsland.Height);

        int islandLeft = targetX + (int)Math.Round(islandX);
        int islandTop = targetY + (int)Math.Round(islandY);

        // 夹在目标屏外框内，别让岛被拖到看不见的地方
        var outer = OuterOf(display);
        double islandWidth = PlacementWidth() * s;
        islandLeft = Math.Clamp(islandLeft, outer.Left, Math.Max(outer.Left, outer.Right - (int)islandWidth));
        islandTop = Math.Clamp(islandTop, outer.Top, Math.Max(outer.Top, outer.Bottom - (int)(_currentIsland.Height * s)));

        AppWindow.Move(new Windows.Graphics.PointInt32(
            islandLeft - (int)Math.Round(islandX),
            islandTop - (int)Math.Round(islandY)));

        // 拖拽期间形状要跟着走（窗口移动后客户区坐标不变，但屏幕位置变了 → 悬停/置顶判定读的是屏幕矩形）
        UpdateHitRegion(force: true);
    }

    /// <summary>
    /// 松手：真正拖过才落盘（写 island.dragX / island.dragY），返回是否真的拖动过。
    /// 没移动则保持原样 —— 调用方据此把中键单击仍当成「关消息」。
    /// </summary>
    private bool EndDrag(Win32.POINT cursor)
    {
        if (!_dragArmed)
        {
            return false;
        }

        _dragArmed = false;
        if (!_dragging) return false;

        // 最后再对一次位：松手点在 PointerReleased 里的坐标可能比最后一次 PointerMoved 更准
        MoveWithCursor(cursor);
        _dragging = false;

        var display = ResolveDisplay();
        var outer = OuterOf(display);
        double s = Scale;
        var position = AppWindow.Position;
        double islandX = IslandLeftInWindow(s, _currentIsland.Width);
        double islandY = IslandTopInWindow(_bottomAnchored, s, _currentIsland.Height);

        // 存成相对显示器外框的坐标：换分辨率/排列时不会跑到屏幕外
        _settings.Set(DragXKey, position.X + (int)Math.Round(islandX) - outer.Left);
        _settings.Set(DragYKey, position.Y + (int)Math.Round(islandY) - outer.Top);

        _log.Debug($"岛体已拖到 ({position.X + (int)Math.Round(islandX)}, {position.Y + (int)Math.Round(islandY)})，位置已保存。");
        return true;
    }

    /// <summary>取消拖拽（指针捕获丢失、岛被隐藏等）：不落盘。</summary>
    private void CancelDrag()
    {
        _dragArmed = false;
        _dragging = false;
    }

    /// <summary>
    /// 读取拖拽落点并把窗口摆过去。返回 false = 没有手动落点（或落点在当前显示之外），
    /// 调用方回退到按对齐方式自动落位。
    /// </summary>
    private bool TryDragPlacement(int w, int h, MonitorInfo display,
        Win32.RECT strip, double s, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (_settings.Get<double?>(DragXKey, null) is not { } savedX) return false;
        if (_settings.Get<double?>(DragYKey, null) is not { } savedY) return false;

        var outer = OuterOf(display);
        double islandX = IslandLeftInWindow(s, PlacementWidth());
        double islandY = IslandTopInWindow(_bottomAnchored, s, _currentIsland.Height);

        // 存的是「岛体左上角相对外框」，换算回窗口坐标
        int islandLeft = outer.Left + (int)Math.Round(savedX);
        int islandTop = outer.Top + (int)Math.Round(savedY);

        // 显示器换了分辨率/排列以后旧落点可能已经在屏幕外：视为无效，回到自动落位
        if (islandLeft < outer.Left - 8 || islandLeft > outer.Right - 40) return false;
        if (islandTop < outer.Top - 8 || islandTop > outer.Bottom - 20) return false;

        x = islandLeft - (int)Math.Round(islandX);
        y = islandTop - (int)Math.Round(islandY);

        // 拖拽落点无视自动对齐；但底部模式下仍然不能跑到任务栏之外
        if (_bottomAnchored && y + h < strip.Top) y = strip.Top;
        _ = w;
        return true;
    }

    /// <summary>
    /// 清掉手动落点（设置页「恢复自动对齐」或切换位置/对齐方式时调用），
    /// 之后岛体回到按 <c>island.horizontal</c> 与偏移自动落位。
    /// </summary>
    private void ClearDragPlacement()
    {
        // SettingsService 有 Remove，但 ISettingsStore 没有；这里持有的是具体类型，直接删键更干净
        if (_settings is SettingsService service)
        {
            service.Remove(DragXKey);
            service.Remove(DragYKey);
            return;
        }

        _settings.Set<double?>(DragXKey, null);
        _settings.Set<double?>(DragYKey, null);
    }

    /// <summary>当前是否有手动落点（设置页据此显示「恢复自动对齐」）。</summary>
    internal bool HasManualPlacement => _settings.Get<double?>(DragXKey, null) is not null;

    #endregion
}
