namespace WinIsland.Core;

/// <summary>
/// 一块显示器（物理像素、虚拟桌面坐标）。纯数据 —— 不依赖 WinRT/Win32，
/// 回归测试可以直接链接本文件验证命中 / 就近 / 重叠选择。
/// </summary>
internal readonly record struct MonitorInfo(
    string Name,
    bool IsPrimary,
    int X,
    int Y,
    int Width,
    int Height,
    int WorkX,
    int WorkY,
    int WorkWidth,
    int WorkHeight)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int WorkRight => WorkX + WorkWidth;
    public int WorkBottom => WorkY + WorkHeight;

    /// <summary>设置里存的主键：<c>\\.\DISPLAYn</c>（用户手改 settings.json 时能对得上号）。</summary>
    public string Key => Name;

    /// <summary><c>\\.\DISPLAYn</c> 里的 n（解析不出时为 0）。</summary>
    public int Index
    {
        get
        {
            const string prefix = @"\\.\DISPLAY";
            if (!Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
            return int.TryParse(Name.AsSpan(prefix.Length), out int n) ? n : 0;
        }
    }

    /// <summary>给人看的名字：「显示器 1（主）· 2560×1440」。</summary>
    public string Label
    {
        get
        {
            string name = Index > 0 ? $"显示器 {Index}" : "显示器";
            string primary = IsPrimary ? "（主）" : string.Empty;
            return $"{name}{primary} · {Width}×{Height}";
        }
    }

    /// <summary>卡片副标题：「\\.\DISPLAY2 · 2560×1440 @ 0,0」这类给排查用的细节。</summary>
    public string Detail => $"{Name} · {Width}×{Height} @ {X},{Y}";

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>点是落在两块屏的重叠区（镜像/排列重叠）时取重叠面积最大的那块。</summary>
    public long OverlapArea(int x, int y, int width, int height)
    {
        long left = Math.Max(X, x), top = Math.Max(Y, y);
        long right = Math.Min(Right, (long)x + width), bottom = Math.Min(Bottom, (long)y + height);
        return right > left && bottom > top ? (right - left) * (bottom - top) : 0;
    }

    /// <summary>点到显示器外框的平方距离（点在内部为 0）。虚拟桌面死区、负坐标时用。</summary>
    public long DistanceSquared(int x, int y)
    {
        long dx = Math.Max(0, Math.Max((long)X - x, (long)x - Right));
        long dy = Math.Max(0, Math.Max((long)Y - y, (long)y - Bottom));
        return dx * dx + dy * dy;
    }
}

/// <summary>
/// 显示器选择规则：把「鼠标在哪」/「设置里记的是哪台」变成一块具体的显示器。
///
/// 不使用 <c>DisplayArea.FindAll()</c>：它在多屏机器上遍历会抛 <c>InvalidCastException</c>
/// （microsoft-ui-xaml#6454），旧实现把异常吞掉之后所有多屏机器都被当成「只有一块显示器」，
/// 指定显示器与跟随鼠标因此全部失效。
/// </summary>
internal static class MonitorLayout
{
    /// <summary>主屏排最前，其余按排列坐标从左到右、从上到下（与设置页卡片同序）。</summary>
    public static MonitorInfo[] Sort(IEnumerable<MonitorInfo> monitors)
        => monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.X).ThenBy(m => m.Y).ToArray();

    public static MonitorInfo? At(IReadOnlyList<MonitorInfo> monitors, int x, int y)
    {
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Contains(x, y)) return monitors[i];
        }

        return null;
    }

    /// <summary>最近的一块（用在虚拟桌面死区、负坐标边界、光标落在屏外时）。</summary>
    public static MonitorInfo? Nearest(IReadOnlyList<MonitorInfo> monitors, int x, int y)
    {
        MonitorInfo? best = null;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < monitors.Count; i++)
        {
            long distance = monitors[i].DistanceSquared(x, y);
            if (distance >= bestDistance) continue;
            best = monitors[i];
            bestDistance = distance;
        }

        return best;
    }

    public static MonitorInfo? ByName(IReadOnlyList<MonitorInfo> monitors, string name)
    {
        name = name.Trim();
        for (int i = 0; i < monitors.Count; i++)
        {
            if (string.Equals(monitors[i].Name, name, StringComparison.OrdinalIgnoreCase)) return monitors[i];
        }

        // 用户手写的纯数字：当作 \\.\DISPLAYn 的序号
        if (int.TryParse(name, out int index))
        {
            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i].Index == index) return monitors[i];
            }
        }

        return null;
    }

    public static MonitorInfo? Primary(IReadOnlyList<MonitorInfo> monitors)
    {
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].IsPrimary) return monitors[i];
        }

        return monitors.Count > 0 ? monitors[0] : null;
    }

    /// <summary>
    /// 矩形落在哪块屏上：先按重叠面积（聚光卡要覆盖的就是它所在的那块屏，岛矩形与目标屏必然重叠），
    /// 完全不相交（还没定位好、或坐标是 0,0 的空矩形）时退回最近的一块。
    /// </summary>
    public static MonitorInfo? BestFit(IReadOnlyList<MonitorInfo> monitors, int x, int y, int width, int height)
    {
        MonitorInfo? best = null;
        long bestOverlap = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            long overlap = monitors[i].OverlapArea(x, y, width, height);
            if (overlap > bestOverlap)
            {
                best = monitors[i];
                bestOverlap = overlap;
            }
        }

        return best ?? Nearest(monitors, x + width / 2, y + height / 2);
    }

    /// <summary>
    /// 设置值 + 光标位置 → 目标显示器。顺序：
    /// 设置里指定的那一台（拔掉了就静默跳过，见 <paramref name="pinnedName"/>）→ 光标所在屏 →
    /// 光标最近的屏 → <paramref name="fallback"/>（通常是窗口当前所在屏）→ 系统主屏。
    /// 绝不用「永远主屏」兜底：那在多屏机器上等于把跟随鼠标/指定屏全部废掉。
    /// </summary>
    public static MonitorInfo? Pick(
        IReadOnlyList<MonitorInfo> monitors, string? pinnedName, int? cursorX, int? cursorY, MonitorInfo? fallback)
    {
        if (monitors.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(pinnedName) && ByName(monitors, pinnedName) is { } pinned) return pinned;

        if (cursorX is int x && cursorY is int y)
        {
            if (At(monitors, x, y) is { } hover) return hover;
            if (Nearest(monitors, x, y) is { } near) return near;
        }

        return fallback ?? Primary(monitors);
    }
}
