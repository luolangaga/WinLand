using Microsoft.UI.Xaml;
using WinIsland.Core;

namespace WeatherIsland;

/// <summary>
/// 插件入口。必须是 public sealed 且有公共无参构造函数，一个包里只允许一个这样的类型。
/// 小岛（紧凑态）显示天气图标 + 当前温度；展开后显示地点、更新时间和三天预报。
/// </summary>
public sealed class WeatherIslandPlugin : IslandPluginBase
{
    private const int DefaultRefreshMinutes = 30;
    private const int MinRefreshMinutes = 5;
    private const int MaxRefreshMinutes = 360;
    private const string DefaultCity = "北京";

    private WeatherService _service = null!;
    private WeatherIslandView _view = null!;
    private IslandLiveContent _content = null!;
    private WeatherSnapshot _snapshot = WeatherSnapshot.Empty;
    private IDisposable? _timer;
    private string _city = DefaultCity;

    /// <summary>取数串行化：同一时刻只跑一次网络请求，其余请求排队等待，绝不丢弃。
    /// （早期版本用「忙就直接 return」的单飞模式，导致「改城市 + 点刷新」时城市变更被吞掉。）</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _refreshing;
    private bool _stopped;

    protected override Task OnInitializeAsync()
    {
        _stopped = false;

        _service = new WeatherService();
        Context.Register(_service);
        Context.Register(new ActionDisposable(() => _stopped = true));

        _city = ReadCity();
        _view = new WeatherIslandView(Theme);
        _view.Apply(_snapshot with { Place = _city });
        // 岛体换主题（Fluent 跟随系统明暗）：代码搭的视图颜色烘在画刷里，就地重刷一遍
        Context.Theme.Changed += OnThemeChanged;
        Context.Register(new ActionDisposable(() => Context.Theme.Changed -= OnThemeChanged));

        _content = new IslandLiveContent
        {
            Priority = Settings.Get("priority", 40),
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Windows.UI.Color.FromArgb(255, 0x5A, 0xB0, 0xF5),
            MorphView = _view,
            CompactSize = new Windows.Foundation.Size(250, 40),
            ExpandedSize = new Windows.Foundation.Size(420, 140),
            OnTap = ShowDetail,
        };

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE9CA",
            () => new WeatherSettingsPage(Context, this), order: 110));

        Context.Register(Context.OnSettingsChanged("enabled", ApplyEnabled));
        Context.Register(Context.OnSettingsChanged("city", ApplyCity));
        Context.Register(Context.OnSettingsChanged("refresh", ApplyRefreshInterval));

        ApplyEnabled();
        ApplyRefreshInterval();

        Log.Info($"天气小岛已启动（城市 {_city}，每 {CurrentRefreshMinutes} 分钟刷新一次）");
        _ = RefreshAsync("首次加载");
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        _stopped = true;
        _timer?.Dispose();
        _timer = null;
        SetContent(null);
        Log.Info("天气小岛已停用");
        return Task.CompletedTask;
    }

    public string DisplayName => Manifest.Name;

    public string CurrentCity => _city;

    public int CurrentRefreshMinutes => Math.Clamp(Settings.Get("refresh", DefaultRefreshMinutes), MinRefreshMinutes, MaxRefreshMinutes);

    public WeatherSnapshot LastSnapshot => _snapshot;

    public bool IsRefreshing => Volatile.Read(ref _refreshing) == 1;

    /// <summary>每次岛上的数据更新后触发（UI 线程）。设置页用它同步状态文本，
    /// 这样改完城市能立刻看到新城市的天气，而不是停在上一座城市。</summary>
    public event Action<WeatherSnapshot>? SnapshotApplied;

    /// <summary>立即取一次天气（设置页的「立即刷新」和点击小岛都用它）。</summary>
    public Task<WeatherSnapshot> RefreshNowAsync() => RefreshAsync("手动刷新");

    private async Task<WeatherSnapshot> RefreshAsync(string reason)
    {
        if (_stopped) return _snapshot;

        // 排队而不是丢弃：城市刚改完、定时器又恰好触发这类并发，两个请求都要跑到
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _refreshing, 1);
        try
        {
            return await FetchOnceAsync(reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("获取天气时出错（已忽略）", ex);
            return _snapshot;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
            _refreshGate.Release();
        }
    }

    private async Task<WeatherSnapshot> FetchOnceAsync(string reason)
    {
        if (_stopped) return _snapshot;

        // 每次取数都重新读设置，而不是用缓存的 _city：
        // 这样「改完城市马上点立即刷新」即使设置变更回调还没跑，也不会拿旧城市去查
        var city = ReadCity();
        _city = city;

        Log.Info($"获取天气（{reason}，城市 {city}）");
        var snapshot = await _service.FetchAsync(city).ConfigureAwait(false);
        if (_stopped) return _snapshot;

        if (snapshot.HasData)
        {
            _snapshot = snapshot;
            Log.Info($"{snapshot.Place} {snapshot.Temperature:0.#}°C {WeatherCodes.Describe(snapshot.Code)}，共 {snapshot.Days.Count} 天预报");
        }
        else if (!_snapshot.HasData)
        {
            _snapshot = snapshot;
            Log.Warn($"获取天气失败：{snapshot.Error}");
        }
        else
        {
            Log.Warn($"刷新失败，继续显示上次数据：{snapshot.Error}");
        }

        var applied = _snapshot;
        Context.RunOnUI(() =>
        {
            if (_stopped) return;
            _view.Apply(applied);

            // 设置页的监听者出错不能影响宿主：这里单独兜住
            try
            {
                SnapshotApplied?.Invoke(applied);
            }
            catch (Exception ex)
            {
                Log.Warn($"设置页状态同步失败（已忽略）：{ex.Message}");
            }
        });

        return applied;
    }

    /// <summary>是否在灵动岛上展示。关掉后插件继续在后台刷新，随时可以再打开。</summary>
    private void ApplyEnabled() => SetContent(Settings.Get("enabled", true) ? _content : null);

    /// <summary>岛体换主题：视图里的中性色就地重刷（代码搭的视图颜色烘在画刷里）。</summary>
    private void OnThemeChanged()
    {
        Log.Info($"岛体主题已切换为{(Theme.IsLight ? "浅色" : "深色")}，重刷岛视图配色。");
        _view?.RefreshTheme();
    }

    private void ApplyCity()
    {
        var city = ReadCity();
        if (string.Equals(city, _city, StringComparison.Ordinal)) return;

        _city = city;
        _ = RefreshAsync("城市变更");
    }

    private void ApplyRefreshInterval()
    {
        _timer?.Dispose();
        _timer = Context.CreateTimer(TimeSpan.FromMinutes(CurrentRefreshMinutes), repeat: true, OnTimerTick);
    }

    private void OnTimerTick() => _ = RefreshAsync("定时刷新");

    private string ReadCity()
    {
        var city = Settings.Get("city", DefaultCity);
        return string.IsNullOrWhiteSpace(city) ? DefaultCity : city.Trim();
    }

    /// <summary>点击小岛：弹一条当天的详细消息；还没有数据就顺手取一次。</summary>
    private void ShowDetail()
    {
        var snapshot = _snapshot;
        if (!snapshot.HasData)
        {
            _ = RefreshAsync("点击刷新");
            return;
        }

        var today = snapshot.Days.Count > 0 ? snapshot.Days[0] : null;
        var text = today is null
            ? $"{snapshot.Temperature:0.#}°C {WeatherCodes.Describe(snapshot.Code)}"
            : $"{snapshot.Temperature:0.#}°C {WeatherCodes.Describe(snapshot.Code)}，今天 {today.Max:0}° / {today.Min:0}°";

        Context.Island.ShowMessage(new IslandMessage
        {
            Title = $"天气 · {snapshot.Place}",
            Text = text,
            Glyph = Manifest.IconGlyph ?? "\uE9CA",
            Duration = TimeSpan.FromSeconds(3),
        });
    }
}
