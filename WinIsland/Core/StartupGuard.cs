using System.Text.Json;

namespace WinIsland.Core;

/// <summary>
/// 启动守卫 / 安全模式：连续多次「启动后没撑到健康检查」就判定为启动崩溃，
/// 下一次以安全模式启动（不加载外部插件），并把这个决定记进 <c>island.safeMode</c>，
/// 让设置页能显示「本次是安全模式」并给出一键退出。
///
/// 为什么用「未清理的启动标记」而不是读取 Windows 错误报告：后者按进程名聚合、有延迟、
/// 而且拿不到「是本进程的哪一次启动」，判不出来。这里只做一件事 ——
/// 每次启动写一个标记，跑够 <see cref="HealthyAfter"/> 就删掉；标记还在就说明上次没跑完。
/// </summary>
internal sealed class StartupGuard : IDisposable
{
    /// <summary>安全模式的设置键（宿主自己写，设置页与插件管理读）。</summary>
    public const string SafeModeKey = "island.safeMode";

    /// <summary>连续多少次不健康启动后进入安全模式。</summary>
    public const int FailureThreshold = 3;

    /// <summary>启动后活过这么久才算「这一次是健康的」（随后清掉标记）。</summary>
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(25);

    private readonly string _markerPath;
    private readonly IPluginLogger _log;
    private readonly System.Threading.Timer _healthyTimer;
    private bool _cleared;

    private StartupGuard(string markerPath, int attempt, IPluginLogger log)
    {
        _markerPath = markerPath;
        Attempt = attempt;
        _log = log;
        _healthyTimer = new System.Threading.Timer(_ => MarkHealthy(), null, HealthyAfter, Timeout.InfiniteTimeSpan);
    }

    /// <summary>本次是第几次连续启动（1 = 上次是干净退出）。</summary>
    public int Attempt { get; }

    /// <summary>本次应当以安全模式运行。</summary>
    public bool SafeModeRequested => Attempt >= FailureThreshold;

    /// <summary>
    /// 记录一次启动。返回的对象负责在启动存活足够久之后清掉标记；
    /// 进程在此之前退出（或崩溃）时标记留到下次，计数继续累加。
    /// </summary>
    public static StartupGuard Record(IPluginLogger log, string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland");
        var path = Path.Combine(dir, "startup.state");

        int attempt = 1;
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<StartupState>(File.ReadAllText(path));
                attempt = Math.Max(1, (state?.Attempts ?? 0) + 1);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new StartupState { Attempts = attempt }));
        }
        catch (Exception ex)
        {
            // 写不出标记只是失去这个保护，不能挡住启动
            log.Warn($"启动守卫无法写入状态文件（安全模式不可用）：{ex.Message}");
        }

        if (attempt > 1)
        {
            log.Warn($"检测到上次启动未正常结束（连续第 {attempt} 次）。");
        }

        return new StartupGuard(path, attempt, log);
    }

    /// <summary>启动已经稳定运行：清掉标记。幂等，可在退出时再调一次。</summary>
    public void MarkHealthy()
    {
        if (_cleared) return;
        _cleared = true;
        try
        {
            if (File.Exists(_markerPath)) File.Delete(_markerPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"启动守卫无法清除状态文件：{ex.Message}");
        }
    }

    /// <summary>用户主动退出安全模式：清掉标记与设置，下次启动恢复正常。</summary>
    public static void LeaveSafeMode(ISettingsStore settings, string? directory = null)
    {
        settings.Set(SafeModeKey, false);
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland");
        try
        {
            var path = Path.Combine(dir, "startup.state");
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉只会让用户再看到一次安全模式，可以在设置页再点一次
        }
    }

    public void Dispose()
    {
        _healthyTimer.Dispose();
        MarkHealthy();
    }

    private sealed class StartupState
    {
        public int Attempts { get; set; }
    }
}
