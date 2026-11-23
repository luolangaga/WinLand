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
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using WinIsland.Core;
using WinIsland.Core.Plugins;

namespace WinIsland.Island;

public sealed partial class IslandWindow
{
    #region 窗口位置 / 可见性

    /// <summary>系统 DPI 比例（窗口所在屏）。</summary>
    private double DpiScale => Win32.GetDpiForWindow(_hwnd) / 96.0;

    /// <summary>大岛缩放比（island.scale.island）。</summary>
    private double _islandScale = 1.0;

    /// <summary>
    /// DIP → 物理像素的总系数 = DPI × 大岛缩放。整套几何（画布、落点、形状、悬停判定）都从它算；
    /// XAML 侧由 <see cref="ApplyScaleTransform"/> 给岛体栈挂等比渲染缩放，两边保持一致。
    /// </summary>
    private double Scale => DpiScale * _islandScale;

    /// <summary>
    /// 岛体栈等比缩放：窗口客户区的布局尺寸是「物理像素 ÷ DPI」= 画布 × 缩放，
    /// 所以栈以锚定侧为原点缩放，边距也同比缩放，渲染结果恰好落在 <see cref="Scale"/> 算出的位置。
    /// </summary>
    private void ApplyScaleTransform()
    {
        double k = _islandScale;
        IslandStack.RenderTransform = new ScaleTransform { ScaleX = k, ScaleY = k };
        double ox = _horizontal switch { HorizontalLeft => 0.0, HorizontalRight => 1.0, _ => 0.5 };
        IslandStack.RenderTransformOrigin = new Point(ox, _bottomAnchored ? 1.0 : 0.0);
        IslandStack.Margin = _bottomAnchored ? new Thickness(0, 0, 0, Pad * k) : new Thickness(0, Pad * k, 0, 0);
    }

    /// <summary>
    /// 应用 island.position / island.horizontal：岛体在画布内锚定顶部（队列向下）或底部（队列向上）。
    /// 默认值就是 XAML 里写死的顶部居中，因此没写过这两个键时行为与改动前一致。
    /// </summary>
    private void ApplyPositionMode()
    {
        bool bottom = string.Equals(_settings.Get(PositionKey, PositionTop), PositionBottom, StringComparison.OrdinalIgnoreCase);
        string horizontal = _settings.Get(HorizontalKey, HorizontalCenter) ?? HorizontalCenter;
        if (_positionApplied && bottom == _bottomAnchored && horizontal == _horizontal) return;

        // 切换瞬间岛体的屏幕位置（用「旧」方向/对齐方式算），滑动从它开始，保证岛体不跳变
        bool animate = _positionApplied && _shown && _windowPhysH > 0;
        double islandWidth = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        double islandHeight = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;
        _slideOriginIslandTop = animate
            ? AppWindow.Position.Y + IslandTopInWindow(_bottomAnchored, Scale, islandHeight)
            : null;
        _slideOriginIslandLeft = animate
            ? AppWindow.Position.X + IslandLeftInWindow(Scale, islandWidth)
            : null;

        _bottomAnchored = bottom;
        _horizontal = horizontal;
        _positionApplied = true;

        // 岛体锚定到画布的另一侧，队列面板换到主岛的另一边
        IslandStack.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Grid.SetRow(IslandRoot, bottom ? 1 : 0);
        Grid.SetRow(QueuePanel, bottom ? 0 : 1);

        // 水平对齐：整栈 + 岛体 + 队列一起靠同一侧。
        // 岛体跟着对齐侧生长（靠左向右长、靠右向左长），否则展开时会向两侧对称生长、
        // 贴边那一侧直接长到屏幕外（溢出）。
        var align = horizontal switch
        {
            HorizontalLeft => HorizontalAlignment.Left,
            HorizontalRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        IslandStack.HorizontalAlignment = align;
        IslandRoot.HorizontalAlignment = align;
        QueuePanel.HorizontalAlignment = align;
        ApplyScaleTransform();

        // 已展开时按新方向重建队列（卡片顺序镜像：最先入队的活动仍离岛体最近）
        if (_expanded) BuildQueue();

        // 看门狗与位置模式无关：顶部模式一样会被后来断言过置顶的窗口（shell 浮层、任务管理器、
        // 各种悬浮窗/输入法候选框）压住，而顶部模式没有别的重升时机 —— 曾经只在底部模式启动它，
        // 结果顶部模式被盖住后要等重启才恢复。任务栏跟随只是它在底部模式下额外做的事。
        _watchdog ??= CreateWatchdog();
        _watchdog.Start();
        // 前台窗口一变（点任务栏/开始菜单/搜索）就立刻查一次置顶，不必等一个轮询周期
        Win32.InstallForegroundWatcher(() => DispatcherQueue.TryEnqueue(OnOtherWindowActivated));

        if (bottom)
        {
            _taskbarHidden = false;
            RefreshFreeBands(force: true);      // 进入底部模式立刻量一次任务栏空闲段
        }
        else
        {
            RefreshFreeBands();                 // 顶部模式不需要空闲段（清空，落点回到屏幕居中）
        }
    }

    /// <summary>
    /// 岛体（锚点侧）边缘距客户区顶部的物理像素偏移：点击形状与悬停判定的唯一来源，
    /// 顶部模式取距画布顶的 Pad，底部模式从客户区底部往上量（岛体在画布内向上生长）。
    /// </summary>
    private double IslandTopInWindow(bool bottom, double s, double islandHeight)
        => bottom ? _windowPhysH - (Pad + islandHeight) * s : Pad * s;

    /// <summary>
    /// 岛体（对齐侧）左边缘距客户区左侧的物理像素偏移：靠左 = 0、靠右 = 贴窗口右边、居中 = 两侧等分。
    /// 靠左/靠右时窗口按岛体边缘落位，岛体因此只朝远离屏幕边缘的一侧生长。
    /// </summary>
    private double IslandLeftInWindow(double s, double islandWidth)
    {
        double width = islandWidth * s;
        return _horizontal switch
        {
            HorizontalLeft => 0,
            HorizontalRight => _windowPhysW - width,
            _ => (_windowPhysW - width) / 2.0,
        };
    }

    /// <summary>DisplayArea 的矩形转 Win32 矩形（同为物理像素、同一坐标系）。</summary>
    private static Win32.RECT ToRect(Windows.Graphics.RectInt32 r) => new()
    {
        Left = r.X,
        Top = r.Y,
        Right = r.X + r.Width,
        Bottom = r.Y + r.Height,
    };

    /// <summary>MonitorInfo 的外框转 Win32 矩形。</summary>
    private static Win32.RECT OuterOf(MonitorInfo m) => new()
    {
        Left = m.X,
        Top = m.Y,
        Right = m.Right,
        Bottom = m.Bottom,
    };

    /// <summary>MonitorInfo 的工作区转 Win32 矩形。</summary>
    private static Win32.RECT WorkOf(MonitorInfo m) => new()
    {
        Left = m.WorkX,
        Top = m.WorkY,
        Right = m.WorkRight,
        Bottom = m.WorkBottom,
    };

    /// <summary>
    /// 岛该锚定到哪块显示器：按 <c>island.display</c>（auto = 跟随鼠标所在屏）解析。
    /// 这是全窗口唯一的显示器来源 —— 绝不要再出现 <c>DisplayAreaFallback.Primary</c>：
    /// 它只认主屏，多屏用户的岛会永远赖在主屏，跟随鼠标/指定屏都失效。
    ///
    /// 枚举走 Win32（<see cref="MonitorInfo"/>），不用 <c>DisplayArea.FindAll()</c> ——
    /// 后者在多屏上遍历会抛 <c>InvalidCastException</c>，旧实现吞掉异常后多屏机器全部退化成主屏。
    /// 窗口还没定位时用「当前窗口矩形」当兜底，绝不退回主屏。
    /// </summary>
    private MonitorInfo ResolveDisplay()
    {
        var fallback = new Win32.RECT();
        if (_hwnd != nint.Zero) Win32.TryGetWindowRect(_hwnd, out fallback);
        else if (_windowPhysW > 0)
        {
            var pos = AppWindow.Position;
            fallback = new Win32.RECT
            {
                Left = pos.X, Top = pos.Y, Right = pos.X + _windowPhysW, Bottom = pos.Y + _windowPhysH,
            };
        }

        return DisplayResolver.Resolve(_settings.Get<string?>(DisplayKey, null), fallback);
    }

    /// <summary>可靠性测试用：岛此刻解析到的那块屏（生产路径不读它）。</summary>
    internal MonitorInfo ResolvedDisplayForTest => ResolveDisplay();

    private static Windows.Graphics.RectInt32 ToRectInt32(Win32.RECT r)
        => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>
    /// 底部模式：查一次任务栏空闲段（UIA 实测 explorer 自己占了哪几段，后台线程调用）。
    /// 节流 + 单飞；展开动画期间查到的新结果只标记待应用，等收起后再落位 —— 绝不在展开中移动窗口。
    /// </summary>
    private void RefreshFreeBands(bool force = false)
    {
        if (!_bottomAnchored)
        {
            _freeBands = Array.Empty<Windows.Graphics.RectInt32>();
            _freeBandsDirty = false;
            return;
        }

        if (_freeBandsInFlight) return;
        if (!force && Environment.TickCount64 - _freeBandsAt < FreeBandsThrottleMs) return;
        _freeBandsAt = Environment.TickCount64;

        var outer = OuterOf(ResolveDisplay());
        if (Win32.GetTaskbarStripFor(outer) is not { Visible: true } tray || tray.Docked.Bottom < outer.Bottom - Win32.SliverPx)
            return;

        var hwnd = Win32.GetTaskbarWindow(outer);
        if (hwnd == nint.Zero) return;

        var strip = ToRectInt32(tray.Docked);
        _freeBandsInFlight = true;
        _ = Task.Run(() =>
        {
            var bands = TaskbarLayout.GetFreeBands(hwnd, strip);
            DispatcherQueue.TryEnqueue(() => OnFreeBandsReady(bands));
        });
    }

    private void OnFreeBandsReady(Windows.Graphics.RectInt32[] bands)
    {
        _freeBandsInFlight = false;
        bool changed = !bands.SequenceEqual(_freeBands);
        _freeBands = bands;

        if (bands.Length == 0)
        {
            if (!_freeBandsFallbackLogged)
            {
                _freeBandsFallbackLogged = true;
                _log.Info($"任务栏空闲段：未取到（{TaskbarLayout.LastError ?? "没有空白区"}），落点回退为按条带边缘对齐。");
            }
        }
        else
        {
            _freeBandsFallbackLogged = false;
            if (changed)
            {
                _log.Info("任务栏空闲段（物理像素）：" + string.Join("、", bands.Select(b => $"{b.X}..{b.X + b.Width}")));
            }
        }

        if (!changed) return;

        if (_expanded)
        {
            // 展开中不移动窗口（会打断动画、也会让岛在光标下乱跳）：收起后再落位
            _freeBandsDirty = true;
            return;
        }

        ApplyCanvasBounds();
    }

    /// <summary>
    /// 按对齐方式挑一条空闲段：靠左 = 最左、靠右 = 最右、居中 = 离条带中心最近。没有空闲段时返回 null。
    /// </summary>
    private Windows.Graphics.RectInt32? SelectFreeBand(double stripCenterX)
    {
        if (_freeBands.Length == 0) return null;
        return _horizontal switch
        {
            HorizontalLeft => _freeBands[0],
            HorizontalRight => _freeBands[^1],
            _ => _freeBands.OrderBy(b => Math.Abs(b.X + b.Width / 2.0 - stripCenterX)).First(),
        };
    }

    /// <summary>
    /// 紧凑尺寸：受 <see cref="CompactWidthMax"/> 限制 —— 上限对所有位置模式一致，不随任务栏动态变化。
    /// </summary>
    private static Size CompactSizeOf(IslandLiveContent live)
        => live.CompactSize.Width <= CompactWidthMax
            ? live.CompactSize
            : new Size(CompactWidthMax, live.CompactSize.Height);

    /// <summary>
    /// 定位用的岛体宽度（逻辑像素）：取「当前实际宽度」与「当前内容会占的宽度」中的较大者。
    /// 不能只用 ActualWidth —— 内容刚 SetLive 时宽度动画还没跑，ActualWidth 仍是空闲态宽度，
    /// 落点会按错的宽度算；而动画结束后窗口不会再移动（动画期间绝不移动窗口），于是就停在错的位置。
    /// 这里只用于算落点，尺寸本身由 <see cref="CompactSizeOf"/> 与插件声明决定。
    /// </summary>
    private double PlacementWidth()
    {
        double width = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        // 落点用静止态（收起 = 小岛常驻那张）的宽度
        if (CompactLive is { } live) width = Math.Max(width, CompactSizeOf(live).Width);
        return width;
    }

    /// <summary>
    /// 岛体左边缘的目标位置（物理像素）。底部模式优先落进实测空闲段：
    /// 靠左 = 最左空闲段、靠右 = 最右空闲段、居中 = 离屏幕中心最近的空闲段；
    /// 段内放得下就贴对齐侧内边，放不下就段内居中（两侧均摊溢出，压到的东西最少）。
    /// 查不到空闲段时退回「按条带边缘/托盘边界对齐」。
    /// </summary>
    private double ResolveIslandLeft(Win32.RECT strip, double islandPx, double s, bool embedded, int trayLeft)
    {
        double badgePx = ActivityCountExtent * s;
        double leadingPx = _horizontal == HorizontalRight ? badgePx : 0;
        double occupiedPx = islandPx + badgePx;
        if (embedded && SelectFreeBand((strip.Left + strip.Right) / 2.0) is { } band)
        {
            if (occupiedPx <= band.Width)
            {
                double inset = badgePx > 0 ? Math.Min(EdgeInset * s, band.Width - occupiedPx) : EdgeInset * s;
                return _horizontal switch
                {
                    HorizontalLeft => band.X + inset,
                    HorizontalRight => band.X + band.Width - inset - islandPx,
                    _ => band.X + (band.Width - occupiedPx) / 2.0 + leadingPx,
                };
            }

            return band.X + (band.Width - occupiedPx) / 2.0 + leadingPx;
        }

        double leftBound = strip.Left + EdgeInset * s;
        double rightBound = trayLeft > 0
            ? trayLeft - EdgeInset * s
            : strip.Right - (embedded ? TrayReserve + EdgeInset : EdgeInset) * s;
        return _horizontal switch
        {
            HorizontalLeft => leftBound,
            HorizontalRight => rightBound - islandPx,
            _ => (strip.Left + strip.Right - islandPx) / 2.0,
        };
    }

    /// <summary>
    /// 应用预留画布的尺寸与位置。只在内容集合变化（活动上岛/临时消息）、设置变化或 DPI 变化时调用，
    /// 绝不在悬停展开/收起动画中调用 —— 见 <see cref="AnimateIslandSize"/>。
    /// </summary>
    private void ApplyCanvasBounds()
    {
        var s = Scale;
        int w = (int)Math.Round(_canvas.Width * s);
        int h = (int)Math.Round(_canvas.Height * s);

        var display = ResolveDisplay();
        _displayOuter = ToRectInt32(OuterOf(display));
        Win32.RECT strip = WorkOf(display);
        bool embedded = false;
        int trayLeft = 0;
        if (_bottomAnchored)
        {
            // 优先嵌进任务栏条带：与任务栏同一条带、同一高度基准。
            // 任务栏不可用（自动隐藏收起/不在屏幕底部/取不到）时退回屏幕外框底边。
            var outer = OuterOf(display);
            var tray = Win32.GetTaskbarStripFor(outer);
            if (tray is { Visible: true } t && t.Docked.Bottom >= outer.Bottom - Win32.SliverPx)
            {
                embedded = true;
                strip = t.Docked;
                trayLeft = t.TrayLeft;
            }
            else
            {
                strip = outer;
            }

            _lastStripBottom = strip.Bottom;
        }

        _stripCenterX = (strip.Left + strip.Right) / 2.0;

        double offset = _settings.Get(_bottomAnchored ? BottomOffsetKey : TopOffsetKey, DefaultOffset);
        int y = _bottomAnchored
            ? strip.Bottom - h + (int)Math.Round((Pad - offset) * s)   // 岛体底边距条带底边 offset
            : strip.Top + (int)Math.Round((offset - Pad) * s);         // 岛体顶边距工作区顶部 offset

        // 拖拽落点（island.dragX / island.dragY）优先于对齐方式：用户手动摆过就以手动为准，
        // 换成自动对齐（或改了位置/对齐设置）时这两项会被清掉，见 ClearDragPlacement。
        int x = 0;
        bool dragPlaced = TryDragPlacement(w, h, display, strip, s, out x, out y);
        if (!dragPlaced)
        {
            // 水平落点：先算「岛体左边缘」的目标位置（空闲段/对齐落点 + 水平偏移，夹在条带内），
            // 再按当前对齐方式回推窗口 x —— 靠左/靠右时窗口随岛体边缘走，展开只朝一侧生长。
            double hOffset = _settings.Get(HorizontalOffsetKey, 0.0);
            if (_horizontal == HorizontalCenter && hOffset == 0
                && !(_bottomAnchored && embedded && _freeBands.Length > 0))
            {
                // 居中且无偏移（默认；底部模式查到空闲段时改走空闲段落点）：与改动前逐像素一致
                x = strip.Left + (strip.Right - strip.Left - w) / 2;
            }
            else
            {
                double islandWidth = PlacementWidth();
                double islandPx = islandWidth * s;
                double badgePx = ActivityCountExtent * s;
                double minLeft = display.X + (_horizontal == HorizontalRight ? badgePx : 0);
                double maxLeft = display.X + display.Width - islandPx - (_horizontal == HorizontalRight ? 0 : badgePx);
                double islandLeft = Math.Clamp(
                    ResolveIslandLeft(strip, islandPx, s, embedded, trayLeft) + hOffset * s,
                    minLeft,
                    Math.Max(minLeft, maxLeft));
                x = (int)Math.Round(islandLeft - IslandLeftInWindow(s, islandWidth));
            }
        }

        bool sizeChanged = w != _windowPhysW || h != _windowPhysH;
        _windowPhysW = w;
        _windowPhysH = h;
        _appliedScale = s;

        if ((_slideOriginIslandTop is not null || _slideOriginIslandLeft is not null) && !sizeChanged && !dragPlaced)
        {
            // 方向/水平对齐刚切换：先瞬移到「岛体屏幕位置不变」的起点（同一 UI 回合内完成，
            // 中间帧不可见），再缓动滑到终点。只移动窗口，尺寸不变。
            double islandWidth = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
            double islandHeight = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;

            int fromX = _slideOriginIslandLeft is double islandLeft0
                ? (int)Math.Round(islandLeft0 - IslandLeftInWindow(s, islandWidth))
                : AppWindow.Position.X;
            int fromY = _slideOriginIslandTop is double islandTop0
                ? (int)Math.Round(islandTop0 - IslandTopInWindow(_bottomAnchored, s, islandHeight))
                : AppWindow.Position.Y;

            _slideOriginIslandTop = null;
            _slideOriginIslandLeft = null;
            StartPositionSlide(fromX, fromY, x, y);
        }
        else
        {
            _slideOriginIslandTop = null;
            _slideOriginIslandLeft = null;
            StopPositionSlide();
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));

            if (sizeChanged)
                Win32.RemoveDwmBorder(_hwnd);
        }

        // resize 后新长出来的表面默认是不透明白，擦成透明，避免被形状暴露时闪白
        Win32.ClearClientTransparent(_hwnd);

        UpdateHitRegion(force: true);
    }

    // ---- 位置切换滑动：只移动窗口，绝不改窗口尺寸（改尺寸会让 DWM 用上一帧合成，出现残影）----

    private void StartPositionSlide(int fromX, int fromY, int toX, int toY)
    {
        StopPositionSlide();

        if (!Motion.Enabled)
        {
            AppWindow.Move(new Windows.Graphics.PointInt32(toX, toY));
            return;
        }

        // 先瞬移到起点：岛体屏幕位置与切换前重合
        AppWindow.Move(new Windows.Graphics.PointInt32(fromX, fromY));
        if (fromX == toX && fromY == toY) return;

        _slideFromX = fromX;
        _slideFromY = fromY;
        _slideToX = toX;
        _slideToY = toY;
        _slideStartTick = Environment.TickCount64;
        _positionSlide ??= CreateSlideTimer();
        _positionSlide.Start();
    }

    private DispatcherQueueTimer CreateSlideTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => PositionSlideTick();
        return timer;
    }

    private void PositionSlideTick()
    {
        double p = (Environment.TickCount64 - _slideStartTick) / (double)PositionSlideMs;
        if (p >= 1)
        {
            StopPositionSlide();
            AppWindow.Move(new Windows.Graphics.PointInt32(_slideToX, _slideToY));
            UpdateHitRegion(force: true);
            return;
        }

        double eased = 1 - Math.Pow(1 - p, 3);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            _slideFromX + (int)Math.Round((_slideToX - _slideFromX) * eased),
            _slideFromY + (int)Math.Round((_slideToY - _slideFromY) * eased)));
    }

    private void StopPositionSlide() => _positionSlide?.Stop();

    // ---- 任务栏跟随（只在底部模式运行）----

    /// <summary>
    /// 岛体（含队列卡片）在屏幕上的外接矩形：置顶自愈判定「谁把岛压在下面」用。
    /// 直接取点击形状矩形（客户区坐标）平移窗口位置，天然只覆盖真正可见的那块。
    /// </summary>
    private Win32.RECT IslandScreenRect()
    {
        var pos = AppWindow.Position;
        if (_hitRects.Count == 0)
        {
            double s = Scale;
            double ih = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;
            int top = pos.Y + (int)IslandTopInWindow(_bottomAnchored, s, ih);
            return new Win32.RECT
            {
                Left = pos.X,
                Top = top,
                Right = pos.X + _windowPhysW,
                Bottom = top + (int)Math.Round(ih * s),
            };
        }

        int left = int.MaxValue, top2 = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var rect in _hitRects)
        {
            left = Math.Min(left, rect.X1);
            top2 = Math.Min(top2, rect.Y1);
            right = Math.Max(right, rect.X2);
            bottom = Math.Max(bottom, rect.Y2);
        }

        return new Win32.RECT
        {
            Left = pos.X + left,
            Top = pos.Y + top2,
            Right = pos.X + right,
            Bottom = pos.Y + bottom,
        };
    }

    private DispatcherQueueTimer CreateWatchdog()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(WatchdogPollMs);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => WatchdogTick();
        return timer;
    }

    /// <summary>DisplayInformation 的事件在任意线程触发：立刻编组回 UI 线程。</summary>
    private void OnDisplaysInvalidatedFromAnyThread()
        => DispatcherQueue.TryEnqueue(OnDisplaysInvalidated);

    /// <summary>
    /// 显示器配置变了（热插拔/分辨率/排列/投影）：重新解析目标屏并把岛搬过去。
    ///
    /// 为什么是两拍：窗口的 DPI 是**跟着窗口当前所在屏**走的（<c>GetDpiForWindow</c>），
    /// 而 <c>ApplyCanvasBounds</c> 里的尺寸/落点又全按 <c>Scale</c> 算 —— 第一拍里窗口还压在旧屏上，
    /// 算出来的是「新屏的坐标 + 旧屏的缩放」，落点必然偏。所以：
    /// 第一拍只负责把窗口挪到新屏（顺带把 <c>_displayOuter</c>/<c>_displayKey</c> 更新掉），
    /// 第二拍等 DWM 把 DPI 传播完，再按正确的缩放重算一次，这才是最终落点。
    /// 两拍都必须在展开动画之外 —— 展开中移动窗口会打断动画（同空闲段那条规矩）。
    /// </summary>
    private void OnDisplaysInvalidated()
    {
        if (!_shown) return;

        // 空闲段是旧屏的物理坐标：不丢的话 SelectFreeBand 会把岛摆到新屏之外去
        _freeBands = Array.Empty<Windows.Graphics.RectInt32>();
        _freeBandsDirty = false;
        _freeBandsAt = 0;

        // 展开中：收起时 WatchdogTick 会因为 _freeBandsDirty 之外的路径不会自动补位，
        // 所以这里再挂一次待办，等收起后落位（复用空闲段那套「稍后应用」的机制）。
        if (_expanded)
        {
            _freeBandsDirty = true;
            return;
        }

        // 第一拍：把窗口挪到新屏（此时 Scale 可能还是旧屏的，落点先粗对）
        ApplyCanvasBounds();

        // 第二拍：DPI 传播完后按正确缩放重算 —— 只在真的换了屏/换了 DPI 时才需要
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_shown || _expanded) return;
            ApplyCanvasBounds();
            RefreshFreeBands(force: true);
        });
    }

    /// <summary>
    /// 看门狗。先无条件做一次置顶自愈 —— 位置模式与置顶无关，顶部模式曾经没有任何重升时机，
    /// 被盖住后只能重启恢复。任务栏跟随只在底部模式有意义，顶部模式到此为止。
    /// </summary>
    private void WatchdogTick()
    {
        UpdateFullscreenState();
        HealTopmost();

        // 勿扰到期（或刚被打开）：可见性跟着变一次。只是比一个时间戳，不必另开定时器。
        // 到期那一刻没有别的信号会来敲门，这一拍就是「静默结束，岛回到屏幕」的最晚时刻。
        bool quiet = _host?.IsQuiet == true;
        if (quiet != _lastQuiet)
        {
            _lastQuiet = quiet;
            UpdateVisibility();
        }

        // 锚定屏漂移自愈（两种位置模式都要）：热插拔事件在某些环境不广播（远程桌面、驱动更新、
        // 部分虚拟机），这里按外框定期核对一次，发现目标屏换了/尺寸变了就重新落位。
        // 只是比几个整数，成本可以忽略；展开中不移动窗口，留到收起后由 _freeBandsDirty 那条路径落位。
        if (HealDisplayDrift()) return;

        if (!_bottomAnchored) return;

        var outer = OuterOf(ResolveDisplay());
        var tray = Win32.GetTaskbarStripFor(outer);
        // 只有「停靠在屏幕底部、但被收起」的任务栏才让岛跟着收起（自动隐藏/全屏）。
        // 停靠位置看 ABM_GETTASKBARPOS 的停靠矩形 —— 任务栏收起时它仍报告停靠位置；
        // 任务栏不在底部（Win10 风格顶部任务栏）或根本没有任务栏时，岛退回屏幕底边并保持显示。
        bool hidden = tray is { } t && !t.Visible && t.Docked.Bottom >= outer.Bottom - Win32.SliverPx;

        if (hidden != _taskbarHidden)
        {
            _taskbarHidden = hidden;
            UpdateVisibility();
            if (!hidden) ApplyCanvasBounds();
            return;
        }
        if (hidden) return;

        // 展开期间查到的新落点，等收起后再应用 —— 绝不在动画中移动窗口
        if (_freeBandsDirty && !_expanded)
        {
            _freeBandsDirty = false;
            ApplyCanvasBounds();
            return;
        }

        // 只在条带真的变了才动窗口：4 次/秒的无谓 MoveAndResize 会白白惊动 DWM
        if (tray is { } strip && _lastStripBottom != strip.Docked.Bottom)
        {
            _lastStripBottom = strip.Docked.Bottom;
            RefreshFreeBands(force: true);      // 条带位置/分辨率变了，空闲段坐标要重新量
            ApplyCanvasBounds();
        }
    }

    /// <summary>
    /// 锚定屏漂移自愈：目标屏的外框变了（热插拔 / 换了分辨率 / 显示器排列调整 / 用户把设置改成跟随鼠标
    /// 而鼠标挪到了别的屏）就重新落位。返回 true 表示已经处理并重新定位，调用方本轮不必再动。
    /// 展开中不移动窗口（会打断动画），改为标记待应用，收起后由看门狗那条 <c>_freeBandsDirty</c> 路径落位。
    /// </summary>
    private bool HealDisplayDrift()
    {
        // 还没落过位（_windowPhysW 为 0）：_displayOuter 是初始值，此刻比对必然"漂移"。
        // 首次定位由 RefreshFromSettings / 构造流程负责，不归看门狗管。
        if (_windowPhysW <= 0 || _displayOuter.Width <= 0) return false;

        var current = ResolveDisplay();
        if (current.X == _displayOuter.X && current.Y == _displayOuter.Y
            && current.Width == _displayOuter.Width && current.Height == _displayOuter.Height)
        {
            return false;
        }

        // 旧屏的空闲段在新屏上无效
        _freeBands = Array.Empty<Windows.Graphics.RectInt32>();
        _freeBandsAt = 0;

        if (_expanded)
        {
            _freeBandsDirty = true;
            return false;
        }

        ApplyCanvasBounds();
        RefreshFreeBands(force: true);
        return true;
    }

    /// <summary>
    /// 置顶自愈：被任何可见的相交窗口压在下面时立刻重升，并把元凶记进日志（同原因 2 秒内只记一次）。
    /// 触发点：看门狗轮询（所有位置模式）+ 前台窗口变化事件（任务栏/开始菜单/搜索抬层时不必等一个周期）。
    /// 隐藏期间不做：窗口不在屏幕上，重升没有意义。
    /// </summary>
    private void HealTopmost()
    {
        if (!_shown) return;

        if (Win32.EnsureTopmost(_hwnd, IslandScreenRect()) is not { } coverer) return;

        _topmostHealCount++;
        if (coverer == _topmostHealLast && Environment.TickCount64 - _topmostHealLoggedAt < 2000) return;
        _topmostHealLast = coverer;
        _topmostHealLoggedAt = Environment.TickCount64;
        _log.Warn($"岛被「{coverer}」压在下面，已重新置顶（本次运行第 {_topmostHealCount} 次）。");
    }

    /// <summary>
    /// 别的窗口被激活（前台窗口变化，点任务栏/开始菜单/别的应用都算；也包括焦点变化）：
    /// 既立刻查一次置顶，也收掉触控展开 —— 触摸点在其他应用上不一定产生鼠标消息
    /// （原生处理 WM_POINTER 的应用会吃掉它，低级鼠标钩子就看不到），
    /// 前台/焦点变化是这类情况的兜底信号。
    /// </summary>
    private void OnOtherWindowActivated()
    {
        HealTopmost();
        DismissTouchExpand();
    }

    /// <summary>
    /// 把岛屿的「真实布局形状」写入窗口形状（SetWindowRgn）：形状之外不绘制也不参与命中测试，
    /// 透明画布区域的点击/触摸因此会真正落到下层窗口（跨进程有效）。
    /// 形状按岛体的圆角生成 —— 若用矩形近似，圆角外侧那几块像素会被窗口表面填充成白色实块。
    /// 形状还要比岛体渲染范围外扩一圈（见 <see cref="ShapeSlackDip"/>）：贴着渲染边缘的形状
    /// 会把圆角的抗锯齿和 1px 描边切成硬阶梯，那才是肉眼看到的锯齿。
    /// 必须跟随尺寸动画逐帧更新，且绝不能为空：没有形状时整块画布都会拦截点击。
    /// </summary>
    private void UpdateHitRegion(bool force = false)
    {
        // 窗口还没定位（首次应用之前）时岛体位置无从计算，形状先不下发 ——
        // 否则底部模式会算出负数 y 的空形状/错形状。
        if (_windowPhysW <= 0 || _windowPhysH <= 0) return;

        var s = Scale;
        if (_materialApplied)
            _backdrop?.SetGeometryMoving(_sizeStoryboard != null);
        bool movingMaterial = _materialApplied && _backdrop?.GeometryMoving == true;
        _mainSurface.SetMoving(movingMaterial);
        foreach (var surface in _queueSurfaces) surface.SetMoving(movingMaterial);
        double dpi = DpiScale;      // TransformToVisual 的结果已含栈的渲染缩放，只差 DPI
        var rects = new List<ShapeRect>(QueueVisibleCards + 2);
        var materialBounds = new List<(float X, float Y, float Width, float Height, float Radius)>();
        void AddMaterialBounds(Border border)
        {
            if (!border.IsLoaded) return;
            var box = border.TransformToVisual(RootGrid)
                .TransformBounds(new Rect(0, 0, border.ActualWidth, border.ActualHeight));
            materialBounds.Add(((float)(box.X * dpi), (float)(box.Y * dpi),
                (float)(box.Width * dpi), (float)(box.Height * dpi),
                (float)(border.CornerRadius.TopLeft * s)));
        }

        // 主岛（空闲 / 活动 / 临时消息都走这里）：形状必须跟随岛体「当前渲染尺寸」。
        // 绝不能取动画目标尺寸 —— 目标比内容多出来的那一圈会落进形状里，
        // 由窗口表面填成不透明白色，切换瞬间就会闪白块。
        rects.Add(GrowShape(MainIslandShapeRect(s), s));
        AddMaterialBounds(IslandRoot);
        if (UpdateActivityCountBadge(s) is { } badgeRect)
        {
            rects.Add(GrowShape(badgeRect, s));
            AddMaterialBounds(ActivityCountBadge);
        }

        // 队列小岛：各子元素的实际布局矩形与圆角（DIP → 物理）
        foreach (var child in QueuePanel.Children)
        {
            if (QueuePanel.Visibility != Visibility.Visible || child is not Border card
                || !card.IsLoaded || card.Visibility != Visibility.Visible || card.ActualWidth <= 0 || card.ActualHeight <= 0)
                continue;

            var box = card.TransformToVisual(RootGrid)
                          .TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            rects.Add(GrowShape(new ShapeRect(
                (int)Math.Round(box.X * dpi), (int)Math.Round(box.Y * dpi),
                (int)Math.Round((box.X + box.Width) * dpi), (int)Math.Round((box.Y + box.Height) * dpi),
                (int)Math.Round(card.CornerRadius.TopLeft * s)), s));
            AddMaterialBounds(card);
        }

        // Update even when integer input bounds haven't changed: the material follows
        // subpixel XAML geometry, including bottom anchoring and fractional DPI.
        if (_materialApplied) _backdrop?.UpdateClip(materialBounds, _windowPhysW, _windowPhysH);
        if (rects.Count == 0 || (!force && SameRects(_hitRects, rects))) return;
        _hitRects = rects;

        // 主机制：窗口形状 —— 跨进程、触控都生效的点击穿透
        Win32.ApplyWindowShape(_hwnd, BuildRegion(rects));
        // 兜底：WM_NCHITTEST 拦截（HTTRANSPARENT 只对同线程窗口有效，例如设置窗与岛重叠时）
        Win32.SetHitRegion(BuildRegion(rects));
    }

    private static bool SameRects(IReadOnlyList<ShapeRect> a, IReadOnlyList<ShapeRect> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>把一组圆角矩形合并成单个 GDI 区域。调用方保证 rects 非空。</summary>
    private static nint BuildRegion(IReadOnlyList<ShapeRect> rects)
    {
        var first = Win32.CreateRoundRectRegion(rects[0].X1, rects[0].Y1, rects[0].X2, rects[0].Y2, rects[0].Radius * 2, rects[0].Radius * 2);
        for (int i = 1; i < rects.Count; i++)
        {
            var next = Win32.CreateRoundRectRegion(rects[i].X1, rects[i].Y1, rects[i].X2, rects[i].Y2, rects[i].Radius * 2, rects[i].Radius * 2);
            Win32.CombineRgnInPlace(first, next);
            Win32.DeleteRegion(next);
        }
        return first;
    }

    /// <summary>
    /// 窗口形状相对岛体渲染边缘的外扩量（DIP，且至少 2 物理像素）。
    ///
    /// 形状是 1-bit 掩码，边界必然是硬阶梯；而岛体的圆角与 1px 描边是抗锯齿渲染的。
    /// 两者一旦重合，掩码就会把圆角的过渡像素和描边整条切掉 —— 屏幕上就是肉眼可见的锯齿
    /// （取整还会让不同边被切的程度不一样，有的边好、有的边碎）。外扩之后硬边界落到岛体渲染
    /// 范围**之外**：那里没有内容，形状边界不再可见，轮廓交回给 XAML 自己画的抗锯齿圆角。
    ///
    /// 取值依据：抗锯齿过渡约 1 物理像素，GDI 掩码相对理想圆弧最多内缩 0.5 像素，
    /// 布局矩形取整再占 0.5 像素 —— 2 物理像素刚好够，再大只是让下面那圈更宽。
    ///
    /// 系统材质另由 IslandBackdrop 按实际渲染轮廓做 alpha 遮罩；外扩圈因此保持透明，
    /// 不再露出一圈比底衬更亮的原始材质。
    /// </summary>
    private const double ShapeSlackDip = 1;

    /// <summary>
    /// 按外扩量放大形状矩形（圆角半径同增，弧线与岛体保持同心）：硬边界落到岛体渲染范围之外，
    /// 圆角与 1px 描边的抗锯齿才不会被 1-bit 掩码切碎（见 <see cref="ShapeSlackDip"/>）。
    /// </summary>
    private ShapeRect GrowShape(ShapeRect r, double scale)
    {
        // Direct system material fills the moving native contour. Slack is restored
        // only once the antialiased material mask has caught up with settled layout.
        if (_materialApplied && _backdrop?.GeometryMoving == true) return r;
        int slack = Math.Max(2, (int)Math.Ceiling(ShapeSlackDip * scale));
        return new ShapeRect(r.X1 - slack, r.Y1 - slack, r.X2 + slack, r.Y2 + slack, r.Radius + slack);
    }

    /// <summary>形状用矩形（物理像素）：X1,Y1,X2,Y2 为外接矩形，Radius 为圆角半径。</summary>
    private readonly record struct ShapeRect(int X1, int Y1, int X2, int Y2, int Radius);

    /// <summary>
    /// 主岛当前的渲染矩形与圆角（物理像素），<see cref="UpdateHitRegion"/> 与
    /// <see cref="MainIslandScreenRect"/> 共用同一份算法，保证窗口形状与聚光卡的落点永远一致。
    /// 只看 ActualWidth/ActualHeight（当前渲染尺寸），不看动画目标。
    /// </summary>
    private ShapeRect MainIslandShapeRect(double scale)
    {
        double iw = IslandRoot.ActualWidth > 0 ? IslandRoot.ActualWidth : _currentIsland.Width;
        double ih = IslandRoot.ActualHeight > 0 ? IslandRoot.ActualHeight : _currentIsland.Height;
        if (IslandRoot.IsLoaded && IslandRoot.ActualWidth > 0 && IslandRoot.ActualHeight > 0)
        {
            var box = IslandRoot.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, iw, ih));
            double dpi = DpiScale;
            return new ShapeRect((int)Math.Round(box.X * dpi), (int)Math.Round(box.Y * dpi),
                (int)Math.Round(box.Right * dpi), (int)Math.Round(box.Bottom * dpi),
                (int)Math.Round(IslandRoot.CornerRadius.TopLeft * scale));
        }
        double ix = IslandLeftInWindow(scale, iw);
        double iy = IslandTopInWindow(_bottomAnchored, scale, ih);
        return new ShapeRect(
            (int)Math.Round(ix), (int)Math.Round(iy),
            (int)Math.Round(ix + iw * scale), (int)Math.Round(iy + ih * scale),
            (int)Math.Round(IslandRoot.CornerRadius.TopLeft * scale));
    }

    #region 全屏应用：隐藏 / 小色条 / 照常

    private bool _fullscreen;
    private bool _lastIdleEdgeHidden;
    /// <summary>strip 模式下鼠标靠近色条后，岛体临时现身。</summary>
    private bool _fsPeek;
    private long _fsPeekLastOverAt;
    private EdgeStripWindow? _strip;

    private string FullscreenMode
    {
        get
        {
            var v = _settings.Get(FullscreenKey, FullscreenStrip) ?? FullscreenStrip;
            return v.Equals(FullscreenHide, StringComparison.OrdinalIgnoreCase) ? FullscreenHide
                 : v.Equals(FullscreenNormal, StringComparison.OrdinalIgnoreCase) ? FullscreenNormal
                 : FullscreenStrip;
        }
    }

    /// <summary>全屏应用在前台、且设置要求避让时，岛体不显示（strip 模式下色条被靠近后临时现身除外）。</summary>
    private bool IdleEdgeHidden => _settings.Get("island.idleEdgeHide", false) && ActiveLive == null && _tempContent == null && !_dropping;

    private bool FullscreenSuppressed => (_fullscreen && FullscreenMode != FullscreenNormal || IdleEdgeHidden) && !_fsPeek;

    /// <summary>
    /// 看门狗每拍调用：检测全屏、推进「靠近色条 → 岛体现身 → 离开后收回」，并同步色条窗口。
    /// 只在状态变化时才动窗口；检测本身是几次 Win32 查询，120ms 一次的成本可以忽略。
    /// </summary>
    private void UpdateFullscreenState(bool force = false)
    {
        string mode = FullscreenMode;
        bool fs = mode != FullscreenNormal && Win32.IsFullscreenForeground(OuterOf(ResolveDisplay()));
        bool changed = fs != _fullscreen || IdleEdgeHidden != _lastIdleEdgeHidden;
        _lastIdleEdgeHidden = IdleEdgeHidden;
        _fullscreen = fs;

        if (!(fs && mode == FullscreenStrip || IdleEdgeHidden))
        {
            if (_fsPeek) { _fsPeek = false; changed = true; }
        }
        else if (!_fsPeek)
        {
            if (!_spotlightOccluded && CursorNearStrip())
            {
                _fsPeek = true;
                _fsPeekLastOverAt = Environment.TickCount64;
                changed = true;
            }
        }
        else
        {
            if (IsCursorOverIsland() || CursorNearStrip()) _fsPeekLastOverAt = Environment.TickCount64;
            else if (Environment.TickCount64 - _fsPeekLastOverAt > FullscreenPeekGraceMs && !_dropping)
            {
                _fsPeek = false;
                changed = true;
            }
        }

        if (!changed && !force)
        {
            // 色条在场期间岛体换位置/缩放/显示器后也要跟上：落位本身只是一次 MoveAndResize，状态不变时不重复做
            return;
        }

        if (_fsPeek && changed && _windowPhysW > 0)
        {
            UpdateVisibility();
            if (_shown)
            {
                _hover = true;
                _hoverGuard.Start();
                OnHoverEnter();
            }
        }
        else
        {
            if (FullscreenSuppressed && _expanded)
            {
                _hover = false;
                _hoverGuard.Stop();
                OnHoverExit();
            }
            UpdateVisibility();
        }

        SyncStrip();
    }

    /// <summary>色条的屏幕矩形（物理像素）：贴目标屏顶/底边，横向跟着岛体的水平落点。</summary>
    private Win32.RECT StripRect()
    {
        var outer = OuterOf(ResolveDisplay());
        double dpi = DpiScale;
        double k = ReadScale(ScaleStripKey);
        int w = Math.Max(8, (int)Math.Round(EdgeStripWindow.BaseSize.Width * k * dpi));
        int h = Math.Max(3, (int)Math.Round(EdgeStripWindow.BaseSize.Height * k * dpi));

        double centerX = (outer.Left + outer.Right) / 2.0;
        if (_windowPhysW > 0)
        {
            var r = MainIslandScreenRect();
            double c = (r.Left + r.Right) / 2.0;
            if (c > outer.Left && c < outer.Right) centerX = c;
        }

        int x = (int)Math.Round(Math.Clamp(centerX - w / 2.0, outer.Left, outer.Right - w));
        int y = _bottomAnchored ? outer.Bottom - h : outer.Top;
        return new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    private bool CursorNearStrip()
    {
        var r = StripRect();
        int reach = (int)Math.Round(StripReachDip * DpiScale);
        Win32.GetCursorPos(out var pt);
        int top = _bottomAnchored ? r.Top - reach : r.Top - 1;
        int bottom = _bottomAnchored ? r.Bottom + 1 : r.Bottom + reach;
        return pt.X >= r.Left - reach && pt.X <= r.Right + reach && pt.Y >= top && pt.Y <= bottom;
    }

    private void SyncStrip()
    {
        bool want = (_fullscreen && FullscreenMode == FullscreenStrip || IdleEdgeHidden) && !_fsPeek && !_spotlightOccluded;
        if (!want)
        {
            _strip?.HideStrip();
            return;
        }

        _strip ??= new EdgeStripWindow();
        var r = StripRect();
        _strip.ShowAt(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    #endregion

    private void UpdateVisibility(bool force = false)
    {
        bool hideIdle = _settings.Get("island.hideWhenIdle", false);
        // 勿扰：岛体不因新活动/新消息自动现身（用户正在悬停、投放文件、临时消息已在屏上时照旧）。
        // 与 hideWhenIdle 不同 —— 那是「没有内容就藏」，这是「有内容也不打扰」。
        bool quiet = _host?.IsQuiet == true;
        bool visible = _settings.Get("island.visible", true)
                       && !(_bottomAnchored && _taskbarHidden)
                       && !_spotlightOccluded
                       && !FullscreenSuppressed
                       // 投放会话期间必须留在屏幕上：用户正拖着文件对着岛
                       && !(hideIdle && ActiveLive == null && _tempContent == null && !_dropping && !_fsPeek)
                       && !(quiet && _tempContent == null && !_hover && !_touchExpand && !_dropping
                            && !_expanded && !_fsPeek);
        if (!force && visible == _shown) return;
        _shown = visible;
        if (visible)
        {
            AppWindow.Show(false);
            _presenter.IsAlwaysOnTop = false;
            _presenter.IsAlwaysOnTop = true;
            // 输入会话中重断言必须保住可激活状态，否则会前功尽弃
            Win32.MakeIslandStyle(_hwnd, noActivate: !_input.IsActive);
            // MakeIslandStyle 里的 SWP_FRAMECHANGED 可能让窗口形状失效，重新断言一次
            UpdateHitRegion(force: true);
        }
        else
        {
            StopPositionSlide();
            _hoverGuard.Stop();
            _hover = false;
            ClearTouchExpand();
            _input.End();   // 隐藏前先还原样式；_shown 已为 false，Exited 不会再触发收起
            AppWindow.Hide();
        }
    }

    private void OnSettingChanged(string key)
    {
        // 翻页位与紧凑钉位是岛体自己写的界面状态：别为它们重扫一遍设置 ——
        // 那会重建队列/重挂内容，把刚点出来的翻页动画打断
        if (key is QueueTailKey or CompactPinKey) return;

        // 宿主级优先级（plugin.<id>.priority / plugin.<id>.expandedPriority）：立即重排活动并重建当前状态
        if (key.StartsWith("plugin.", StringComparison.Ordinal)
            && (key.EndsWith(".priority", StringComparison.Ordinal) || key.EndsWith(".expandedPriority", StringComparison.Ordinal)))
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(ApplyPriorityChange);
                return;
            }
            ApplyPriorityChange();
            return;
        }

        if (!key.StartsWith("island.", StringComparison.Ordinal)) return;

        // 勿扰只改可见性，不值得走一遍完整的外观/位置刷新
        if (key == QuietMode.UntilKey)
        {
            if (!DispatcherQueue.HasThreadAccess) DispatcherQueue.TryEnqueue(RefreshQuiet);
            else RefreshQuiet();
            return;
        }

        // 改位置/对齐/显示器 = 用户要回到自动落位：手动拖拽的落点到此作废，否则他改了设置却看不到效果
        if (key is PositionKey or HorizontalKey or DisplayKey)
        {
            ClearDragPlacement();
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshFromSettings);
            return;
        }
        RefreshFromSettings();
    }

    private void RefreshQuiet()
    {
        _lastQuiet = _host?.IsQuiet == true;
        UpdateVisibility();
    }

    /// <summary>优先级变化后重排活动并重建当前状态（临时消息独自占着主岛时留到它结束后自然生效）。</summary>
    private void ApplyPriorityChange()
    {
        SortLive();
        if (_dropping) return;
        if (_tempContent != null && !_expanded) return;
        TransitionToLiveState();
        UpdateVisibility();
    }

    #endregion
}

