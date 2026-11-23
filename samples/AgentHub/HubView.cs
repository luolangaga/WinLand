using AgentHub.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace AgentHub;

internal sealed class HubView : UserControl, IMorphView, IDisposable
{
    private readonly IPluginContext _context;
    private readonly HubPalette _palette;
    private readonly Func<string, string, bool> _answer;
    private readonly Action _details;
    private readonly bool _demo;
    private readonly TextBlock _headline, _counter, _pageName;
    private readonly Border _statusDot;
    private readonly Grid _viewport = new();
    private readonly StackPanel _detail;
    private readonly DispatcherQueueTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private double _progress, _from, _to, _duration;
    private int _page;
    private Snapshot _snapshot = Snapshot.Empty;
    private PendingRequest[] _pending = [];
    private string _signature = "", _requestIds = "";
    private readonly List<Action> _refresh = [];
    private readonly string[] _pages = ["概览", "会话", "额度", "Token", "待处理"];
    public UIElement View => this;
    public HubView(IPluginContext context, Func<string, string, bool>? answer = null, Action? details = null, bool demo = false)
    {
        _context = context; _palette = new(context.Theme); _answer = answer ?? ((_, _) => false); _details = details ?? (() => { }); _demo = demo;
        var root = new StackPanel { Padding = new(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid { Height = 36, ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _statusDot = new Border { Width = 7, Height = 7, CornerRadius = new(4), Background = _palette.Mint, VerticalAlignment = VerticalAlignment.Center }; header.Children.Add(_statusDot);
        _headline = _palette.Label("AgentHub", 13); _headline.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_headline, 1); header.Children.Add(_headline);
        _counter = _palette.Label("就绪", 11, true); _counter.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_counter, 2); header.Children.Add(_counter); root.Children.Add(header);
        _detail = new StackPanel { Height = 0, Opacity = 0, Spacing = 10 }; _viewport.Height = 190;
        _viewport.SizeChanged += (_, e) => _viewport.Clip = new RectangleGeometry { Rect = new(0, 0, e.NewSize.Width, e.NewSize.Height) }; _detail.Children.Add(_viewport);
        var footer = new Grid { Height = 28, ColumnSpacing = 8, BorderBrush = _palette.Line, BorderThickness = new(0, 1, 0, 0), Padding = new(0, 4, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(Arrow("\uE76B", "上一页", -1)); var right = Arrow("\uE76C", "下一页", 1); Grid.SetColumn(right, 2); footer.Children.Add(right);
        _pageName = _palette.Label("概览 · 1 / 5", 11, true); _pageName.VerticalAlignment = VerticalAlignment.Center; _pageName.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(_pageName, 1); footer.Children.Add(_pageName);
        var expand = new Button { Content = new FontIcon { Glyph = "\uE740", FontSize = 11, Foreground = _palette.Secondary }, Width = 26, Height = 24, Padding = new(0), Background = null, BorderThickness = new(0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expand, "打开完整明细"); expand.Click += (_, _) => Safe(_details); Grid.SetColumn(expand, 3); footer.Children.Add(expand);
        _detail.Children.Add(footer); root.Children.Add(_detail); Content = root;
        _timer = context.Dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(16); _timer.Tick += Tick; Unloaded += OnUnloaded; context.Theme.Changed += ApplyTheme; ApplyTheme(); Render(0);
    }
    private Button Arrow(string glyph, string label, int direction)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 11, Foreground = _palette.Text }, Width = 28, Height = 24, Padding = new(0), Background = _palette.Surface, BorderThickness = new(0), CornerRadius = new(8) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, direction < 0 ? "AgentHubPrevious" : "AgentHubNext");
        button.Click += (_, _) => Safe(() => { _page = (_page + direction + 5) % 5; Render(direction); }); return button;
    }
    public void Update(Snapshot snapshot, PendingRequest[] pending)
    {
        _snapshot = snapshot; _pending = pending;
        int running = snapshot.Sessions.Count(s => s.Parent == null && s.State == "运行中" && s.Updated > DateTimeOffset.UtcNow.AddMinutes(-5));
        _headline.Text = (_demo ? "演示 · " : "") + (pending.Length > 0 ? $"{pending.Length} 个请求等待处理" : "AgentHub"); _counter.Text = _demo ? "示例数据" : pending.Length > 0 ? "需要你" : running > 0 ? $"{running} 运行中" : "就绪"; _statusDot.Background = pending.Length > 0 ? _palette.Amber : _palette.Mint;
        var ids = string.Join(";", pending.Select(p => p.Id));
        var signature = snapshot.Events.Sum(e => e.Input + e.Output + e.Cache) + ids + string.Join(";", snapshot.Notices) + string.Join(";", snapshot.Quotas.Select(q => q.UsedPercent + q.Name + q.Account + q.ReadAt)) + string.Join(";", snapshot.Sessions.Select(s => s.Id + s.Title + s.State));
        if (_signature != signature) {
            _signature = signature;
            if (_page is 0 or 2 or 3) Safe(() => { foreach (var refresh in _refresh.ToArray()) refresh(); });
            else if (_page != 4 || ids != _requestIds) Render(0);
        }
        _requestIds = ids;
    }
    private void Render(int direction)
    {
        Safe(() => {
            _refresh.Clear();
            _pageName.Text = _pages[_page] + $" · {_page + 1} / 5" + (_demo ? " · 示例数据" : "");
            var panel = new StackPanel { Spacing = 10, Padding = new(0, 4, 0, 0) };
            switch (_page) { case 0: Overview(panel); break; case 1: Sessions(panel); break; case 2: Quotas(panel); break; case 3: Tokens(panel); break; case 4: Requests(panel); break; }
            var content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _viewport.Children.Clear(); _viewport.Children.Add(content); HubCharts.Reveal(content, direction);
        });
    }
    private void Overview(StackPanel panel)
    {
        var hero = new Grid { ColumnSpacing = 18 }; hero.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); hero.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var stats = new StackPanel { Spacing = 3 }; stats.Children.Add(_palette.Label("过去 24 小时", 10, true));
        var total = _palette.Label("", 28); var hint = _palette.Label("", 10, true); stats.Children.Add(total); stats.Children.Add(hint); hero.Children.Add(stats);
        Watch(() => { bool indexing = _snapshot.Notices.Any(n => n.Contains("索引")); total.Text = indexing ? "统计中…" : HubCharts.Short(DayEvents().Sum(e => e.Input + e.Output + e.Cache)); hint.Text = indexing ? "正在索引历史日志" : "tokens · 输入 / 输出 / 缓存"; });
        var ringPanel = new StackPanel { Spacing = 2 }; Grid.SetColumn(ringPanel, 1); hero.Children.Add(ringPanel); HubCharts.RingChart? ring = null;
        var agent = _palette.Label("", 9, true); agent.HorizontalAlignment = HorizontalAlignment.Center;
        Watch(() => { if (_snapshot.Quotas.MaxBy(q => q.UsedPercent) is { } q) { if (ring == null) { ring = HubCharts.Ring(q, _palette, 60); ringPanel.Children.Add(ring); ringPanel.Children.Add(agent); } else ring.Update(q); agent.Text = q.Agent; ringPanel.Visibility = Visibility.Visible; } else ringPanel.Visibility = Visibility.Collapsed; });
        panel.Children.Add(hero); var histogram = HubCharts.Histogram(_snapshot.Events, _palette, 30); panel.Children.Add(histogram); Watch(() => histogram.Update(_snapshot.Events));
        var sessions = new StackPanel { Spacing = 10 }; panel.Children.Add(sessions); string rows = "";
        Watch(() => { var source = _snapshot.Sessions.Where(s => s.Parent == null).Take(2).ToArray(); var key = string.Join("|", source.Select(s => s.Id + s.Title + s.State)); if (rows == key && sessions.Children.Count > 0) return; rows = key; sessions.Children.Clear(); foreach (var s in source) sessions.Children.Add(SessionRow(s)); if (source.Length == 0) sessions.Children.Add(_palette.Label("启动代理后，会话会出现在这里", 12, true)); });
    }
    private UsageEvent[] DayEvents() => _snapshot.Events.Where(e => e.Time > DateTimeOffset.UtcNow.AddDays(-1)).ToArray();
    private void Watch(Action refresh) { _refresh.Add(refresh); refresh(); }
    private UIElement SessionRow(Session s)
    {
        var row = new Grid { ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(_palette.Mark(s.Agent, 24)); var title = _palette.Label(s.Title.Length > 0 ? s.Title : s.Id, 12); title.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(title, 1); row.Children.Add(title);
        var state = _palette.Label(s.State, 10, true); state.VerticalAlignment = VerticalAlignment.Center; state.Foreground = s.State == "等待审批" ? _palette.Amber : _palette.Secondary; Grid.SetColumn(state, 2); row.Children.Add(state); return row;
    }
    private void Sessions(StackPanel panel)
    {
        foreach (var s in _snapshot.Sessions.Where(s => s.Parent == null).Take(20)) {
            var item = new StackPanel { Spacing = 4 }; item.Children.Add(SessionRow(s));
            var footer = new Grid(); footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.Children.Add(_palette.Label(s.Agent + " · " + s.Updated.LocalDateTime.ToString("HH:mm"), 10, true));
            var open = new Button { Content = "工作区 ↗", FontSize = 10, Padding = new(6, 1, 6, 1), Background = null, BorderThickness = new(0), IsEnabled = Directory.Exists(s.Cwd) };
            open.Click += async (_, _) => { try { await Windows.System.Launcher.LaunchFolderPathAsync(s.Cwd); } catch (Exception ex) { _context.Log.Error("打开工作区", ex); } }; Grid.SetColumn(open, 1); footer.Children.Add(open); item.Children.Add(footer); panel.Children.Add(item);
        }
        if (panel.Children.Count == 0) panel.Children.Add(_palette.Label("暂无会话", 13, true));
    }
    private void Quotas(StackPanel panel)
    {
        var rows = new Dictionary<string, (StackPanel Item, Action<Quota> Update)>();
        Watch(() => {
            var keys = _snapshot.Quotas.Select(q => q.Agent + "|" + q.Name).ToHashSet();
            foreach (var key in rows.Keys.Where(key => !keys.Contains(key)).ToArray()) { panel.Children.Remove(rows[key].Item); rows.Remove(key); }
        foreach (var q in _snapshot.Quotas) {
            var key = q.Agent + "|" + q.Name;
            if (rows.TryGetValue(key, out var existing)) { existing.Update(q); continue; }
            var row = new Grid { ColumnSpacing = 16 }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.Children.Add(HubCharts.Ring(q, _palette));
            var info = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center }; info.Children.Add(_palette.Label(q.Agent + " · " + q.Name.Replace("five_hour", "5 小时").Replace("seven_day", "每周"), 12));
            var progress = HubCharts.Progress(q.UsedPercent, _palette.Agent(q.Agent), _palette.Line); info.Children.Add(progress);
            var remaining = _palette.Label("", 10, true); info.Children.Add(remaining); Grid.SetColumn(info, 1); row.Children.Add(info);
            var item = new StackPanel { Spacing = 6 }; item.Children.Add(row); var readAt = _palette.Label("", 9, true); item.Children.Add(readAt); panel.Children.Add(item);
            void Update(Quota value) { ((HubCharts.RingChart)row.Children[0]).Update(value); progress.Update(value.UsedPercent); remaining.Text = value.Reset is { } reset ? $"剩余 {100 - value.UsedPercent:F0}% · {Math.Max(0, (reset - DateTimeOffset.UtcNow).TotalHours):F1} 小时后重置" : "重置时间未知"; readAt.Text = "读取 " + value.ReadAt.ToLocalTime().ToString("HH:mm:ss") + (value.Account is { Length: > 0 } ? " · " + value.Account : " · 账户未确认"); }
            rows[key] = (item, Update); Update(q);
        }
        });
        var notices = new StackPanel { Spacing = 6 }; panel.Children.Add(notices); Watch(() => { notices.Children.Clear(); if (_snapshot.Quotas.Length == 0) notices.Children.Add(_palette.Label("额度不可用", 18)); foreach (var notice in _snapshot.Notices.Where(n => !n.Contains("索引"))) { var text = _palette.Label(notice, 10, true); text.TextWrapping = TextWrapping.Wrap; notices.Children.Add(text); } });
    }
    private void Tokens(StackPanel panel)
    {
        var totalLabel = _palette.Label("", 24); panel.Children.Add(totalLabel); var histogram = HubCharts.Histogram(DayEvents(), _palette, 48); panel.Children.Add(histogram);
        Watch(() => { totalLabel.Text = _snapshot.Notices.Any(n => n.Contains("索引")) ? "统计中 · 暂未完成索引" : HubCharts.Short(DayEvents().Sum(e => e.Input + e.Output + e.Cache)) + " tokens"; histogram.Update(DayEvents()); });
        foreach (var (name, selector) in new (string, Func<UsageEvent, long>)[] { ("输入", e => e.Input), ("输出", e => e.Output), ("缓存", e => e.Cache) }) {
            var row = new Grid { ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new() { Width = new(36) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(56) }); row.Children.Add(_palette.Label(name, 10, true));
            var bar = HubCharts.Progress(0, _palette.Mint, _palette.Line); Grid.SetColumn(bar, 1); row.Children.Add(bar); var value = _palette.Label("", 10, true); value.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(value, 2); row.Children.Add(value); panel.Children.Add(row);
            Watch(() => { var day = DayEvents(); var count = day.Sum(selector); var total = Math.Max(1, day.Sum(e => e.Input + e.Output + e.Cache)); bar.Update(count / (double)total * 100); value.Text = HubCharts.Short(count); });
        }
    }
    private void Requests(StackPanel panel)
    {
        if (_pending.Length == 0) { panel.Children.Add(_palette.Label("一切就绪", 22)); panel.Children.Add(_palette.Label("没有需要你处理的请求", 12, true)); return; }
        foreach (var request in _pending) panel.Children.Add(HubRequest.Build(request, _context, _palette, _answer, _details));
    }
    public void AnimateToExpanded(TimeSpan duration) => Animate(1, duration);
    public void AnimateToCompact(TimeSpan duration) => Animate(0, duration);
    private void Animate(double target, TimeSpan duration) { _from = _progress; _to = target; _duration = HubCharts.Animate ? Math.Max(1, duration.TotalMilliseconds) : 1; _clock.Restart(); _timer.Start(); }
    private void Tick(DispatcherQueueTimer sender, object args) { try { var t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _duration, 0, 1); _progress = _from + (_to - _from) * (1 - Math.Pow(1 - t, 3)); _detail.Height = 228 * _progress; _detail.Opacity = _progress; if (t >= 1) _timer.Stop(); } catch (Exception ex) { _timer.Stop(); _context.Log.Error("AgentHub morph", ex); } }
    private void Safe(Action action) { try { action(); } catch (Exception ex) { _context.Log.Error("AgentHub 界面", ex); } }
    private void OnUnloaded(object sender, RoutedEventArgs args) => _timer.Stop();
    private void ApplyTheme() => RequestedTheme = _context.Theme.IsLight ? ElementTheme.Light : ElementTheme.Dark;
    public void Dispose() { _timer.Stop(); _timer.Tick -= Tick; Unloaded -= OnUnloaded; _context.Theme.Changed -= ApplyTheme; _palette.Dispose(); }
}
