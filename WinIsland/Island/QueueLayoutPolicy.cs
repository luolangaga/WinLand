namespace WinIsland.Island;

/// <summary>Queue geometry and tail selection, independent of XAML and window state.</summary>
internal static class QueueLayoutPolicy
{
    internal static int ResolveTailIndex(IReadOnlyList<string> owners, string? owner, int fallback, int visible)
    {
        for (int i = visible - 1; i < owners.Count; i++)
            if (owner != null && owners[i] == owner) return i;
        return Math.Clamp(fallback, visible - 1, Math.Max(visible - 1, owners.Count - 1));
    }

    // A large (possibly hidden) plugin must not inflate every queue card.
    // Keep slots stable while paging, but bound them by both a readable card size
    // and the space available on the anchored display (already converted to DIPs).
    internal static double CardHeight(double naturalHeight, double availableHeight, double mainHeight,
        int count, int visible, double spacing, double gap, double pagerSize)
    {
        int shown = Math.Min(count, visible);
        if (shown <= 0) return 0;
        double overhead = mainHeight + spacing + (shown - 1) * gap
            + (count > visible ? gap + pagerSize : 0);
        double budget = Math.Max(1, (availableHeight - overhead) / shown);
        return Math.Max(1, Math.Min(naturalHeight, Math.Min(180, budget)));
    }

    internal static double TotalHeight(double mainHeight, int count, int visible, double cardHeight,
        double spacing, double cardGap, double pagerSize)
    {
        if (count == 0) return mainHeight;
        int shown = Math.Min(count, visible);
        return mainHeight + spacing + shown * cardHeight + (shown - 1) * cardGap
            + (count > visible ? cardGap + pagerSize : 0);
    }
}
