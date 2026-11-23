using System.Globalization;

namespace WinIsland.Core;

/// <summary>
/// 宿主级「勿扰 / 暂歇」：<c>island.quietUntil</c> 记一个到期时刻，期内岛不弹临时消息、不自动展开。
/// 只是读一个时间戳判断，不需要定时器 —— 到期后由看门狗那一拍顺带刷新可见性。
/// </summary>
internal static class QuietMode
{
    public const string UntilKey = "island.quietUntil";

    /// <summary>淡入/淡出时长选项（分钟）。</summary>
    public static readonly int[] SnoozeMinutes = { 15, 30, 60, 120 };

    public static bool IsActive(ISettingsStore settings) => Remaining(settings) > TimeSpan.Zero;

    public static TimeSpan Remaining(ISettingsStore settings)
    {
        var text = settings.Get<string?>(UntilKey, null);
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var until)) return TimeSpan.Zero;
        var left = until - DateTimeOffset.Now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>静默一段时间（从现在起）。</summary>
    public static void Snooze(ISettingsStore settings, TimeSpan duration)
        => settings.Set(UntilKey, DateTimeOffset.Now.Add(duration).ToString("O", CultureInfo.InvariantCulture));

    /// <summary>
    /// 立刻结束静默。写一个过去的时间戳而不是删键：<see cref="ISettingsStore"/> 没有 Remove，
    /// 而"到期时间已过"与"没设过"在 <see cref="Remaining"/> 里是同一个含义（0）。
    /// </summary>
    public static void End(ISettingsStore settings)
        => settings.Set(UntilKey, DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>托盘/右键菜单上的文案：剩余时间按分钟向上取整。</summary>
    public static string DescribeRemaining(ISettingsStore settings)
    {
        var left = Remaining(settings);
        if (left <= TimeSpan.Zero) return "未启用";
        int minutes = (int)Math.Ceiling(left.TotalMinutes);
        return minutes >= 60 ? $"{minutes / 60} 小时 {minutes % 60} 分钟" : $"{minutes} 分钟";
    }
}
