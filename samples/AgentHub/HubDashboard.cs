using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace AgentHub;

internal sealed class HubDashboard : UserControl, IDisposable
{
    private readonly IPluginContext _context;
    private readonly HubPalette _palette;
    private readonly Func<string, string, bool> _answer;
    private readonly StackPanel _requests = new() { Spacing = 14 }, _quotas = new() { Spacing = 12 }, _stats = new() { Spacing = 8 };
    private readonly ListView _sessions = new();
    private readonly TextBlock _notices = new() { TextWrapping = TextWrapping.Wrap };
    private string _requestIds = "";
    private readonly ComboBox _period = new() { Header = "统计时间", ItemsSource = new[] { "5 小时", "24 小时", "7 天", "30 天" }, SelectedIndex = 1 };
    private Snapshot _snapshot = Snapshot.Empty;
    private readonly ContentControl _page = new();
    private readonly TextBlock _tokenValue = new(), _activeValue = new(), _pendingValue = new();
    private readonly SelectorBar _navigation = new();
    private readonly StackPanel _side = new() { Spacing = 16 };
    private readonly Grid _overview = new() { ColumnSpacing = 24, RowSpacing = 20 };
    private string _sessionSignature = "";
    private string _quotaSignature = "", _sidebarSignature = "";
    private HubCharts.HistogramChart? _statisticsChart;
    private readonly StackPanel _breakdown = new() { Spacing = 10 };
    private TextBlock? _statisticsTotal;
    private readonly Dictionary<string, (UIElement View, Action<Quota> Update)> _quotaCards = [], _sideQuotaCards = [];
    private readonly Border _sidebar;
    private readonly UIElement _requestPage, _quotaPage, _statsPage;
    public HubDashboard(IPluginContext context, Func<string, string, bool> answer)
    {
        _context = context; _answer = answer; _palette = new(context.Theme);
        var root = new Grid { Padding = new Thickness(28, 24, 28, 24), RowSpacing = 18 };
        for (int i = 0; i < 3; i++) root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var logo = new Border { Width = 40, Height = 40, CornerRadius = new(12), Background = _palette.Surface,
            Child = new FontIcon { Glyph = "\uE99A", FontSize = 21, Foreground = _palette.Mint } }; header.Children.Add(logo);
        var title = new StackPanel { Spacing = 1 }; title.Children.Add(_palette.Label("AgentHub", 22)); title.Children.Add(_palette.Label("你的代理，尽在眼前", 12, true));
        Grid.SetColumn(title, 1); header.Children.Add(title);
        var local = _palette.Label("●  本机", 11, true); local.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(local, 2); header.Children.Add(local);
        root.Children.Add(header);
        foreach (var text in new[] { "总览", "待处理", "Token 统计", "额度" }) _navigation.Items.Add(new SelectorBarItem { Text = text });
        _navigation.SelectedItem = _navigation.Items[0];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_navigation, "AgentHubNavigation");
        Grid.SetRow(_navigation, 1); root.Children.Add(_navigation);
        var metrics = new Grid { ColumnSpacing = 24, Padding = new Thickness(0, 2, 0, 18), BorderBrush = _palette.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        for (int i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        AddMetric("过去 24 小时", _tokenValue, 0); AddMetric("正在运行", _activeValue, 1); AddMetric("等待处理", _pendingValue, 2);
        Grid.SetRow(metrics, 2); root.Children.Add(metrics);
        void AddMetric(string label, TextBlock value, int column)
        {
            value.FontSize = 28; value.FontWeight = new() { Weight = 600 }; value.Foreground = column == 2 ? _palette.Amber : _palette.Text;
            var metric = new StackPanel { Spacing = 4 }; metric.Children.Add(_palette.Label(label, 11, true)); metric.Children.Add(value); Grid.SetColumn(metric, column); metrics.Children.Add(metric);
        }
        _sessions.SelectionMode = ListViewSelectionMode.None; _sessions.Padding = new Thickness(0); _sessions.Background = null;
        _overview.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); _overview.ColumnDefinitions.Add(new() { Width = new(232) });
        _overview.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); _overview.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var sessionArea = new Grid { RowSpacing = 12 }; sessionArea.RowDefinitions.Add(new() { Height = GridLength.Auto }); sessionArea.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        sessionArea.Children.Add(_palette.Label("最近会话", 14)); Grid.SetRow(_sessions, 1); sessionArea.Children.Add(_sessions); _overview.Children.Add(sessionArea);
        _sidebar = new Border { Background = _palette.Surface, CornerRadius = new(12), Padding = new Thickness(18), Child = new ScrollViewer { Content = _side } };
        Grid.SetColumn(_sidebar, 1); _overview.Children.Add(_sidebar);
        _overview.SizeChanged += (_, e) => {
            bool narrow = e.NewSize.Width < 640;
            _overview.ColumnDefinitions[1].Width = narrow ? new(0) : new(232);
            Grid.SetColumn(_sidebar, narrow ? 0 : 1); Grid.SetRow(_sidebar, narrow ? 1 : 0);
            _sidebar.MaxHeight = narrow ? 190 : double.PositiveInfinity;
        };
        _requestPage = new ScrollViewer { Content = _requests };
        var quotaPanel = new StackPanel { Spacing = 16 }; quotaPanel.Children.Add(_quotas); quotaPanel.Children.Add(_notices);
        _quotaPage = new ScrollViewer { Content = quotaPanel };
        var statsPanel = new StackPanel { Spacing = 16 }; statsPanel.Children.Add(_period); statsPanel.Children.Add(_stats);
        _period.HorizontalAlignment = HorizontalAlignment.Left; _period.MinWidth = 160;
        _statsPage = new ScrollViewer { Content = statsPanel };
        _period.SelectionChanged += (_, _) => RenderStats();
        _navigation.SelectionChanged += (_, _) => _page.Content = _navigation.SelectedItem == _navigation.Items[1] ? _requestPage :
            _navigation.SelectedItem == _navigation.Items[2] ? _statsPage : _navigation.SelectedItem == _navigation.Items[3] ? _quotaPage : _overview;
        _page.Content = _overview; _page.HorizontalContentAlignment = HorizontalAlignment.Stretch; _page.VerticalContentAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(_page, 3); root.Children.Add(_page);
        Content = root; context.Theme.Changed += ThemeChanged; ThemeChanged();
    }
    private void ThemeChanged() => RequestedTheme = _context.Theme.IsLight ? ElementTheme.Light : ElementTheme.Dark;
    public void Update(Snapshot s, PendingRequest[] pending)
    {
        _snapshot = s;
        var day = s.Events.Where(e => e.Time > DateTimeOffset.UtcNow.AddDays(-1)).Sum(e => e.Input + e.Output + e.Cache);
        _tokenValue.Text = s.Notices.Any(n => n.Contains("索引")) ? "统计中…" : Short(day) + " tokens";
        _activeValue.Text = s.Sessions.Count(session => session.Parent == null && session.State == "运行中" && session.Updated > DateTimeOffset.UtcNow.AddMinutes(-5)).ToString("D2");
        _pendingValue.Text = pending.Length.ToString("D2");
        var signature = string.Join("|", s.Sessions.Select(session => session.Id + session.Title + session.State + session.Updated.ToString("g"))) + s.Events.Length;
        if (signature != _sessionSignature)
        {
            _sessionSignature = signature; _sessions.Items.Clear();
            foreach (var session in s.Sessions.Where(session => session.Parent == null).Take(200)) AddSession(session, s);
            if (_sessions.Items.Count == 0) _sessions.Items.Add(_palette.Label("尚未发现会话\n启动代理后，会话会出现在这里。", 13, true));
        }
        var quotaSignature = string.Join("|", s.Quotas.Select(q => q.Agent + q.Name + q.UsedPercent + q.Reset + q.ReadAt + q.Account)) + string.Join("|", s.Notices);
        if (_sidebarSignature != quotaSignature + pending.Length) {
        _sidebarSignature = quotaSignature + pending.Length; _side.Children.Clear();
        if (pending.Length > 0)
        {
            var notice = _palette.Label($"{pending.Length} 个请求需要你处理", 13); notice.Foreground = _palette.Amber; _side.Children.Add(notice);
            var review = new Button { Content = "查看请求  →", HorizontalAlignment = HorizontalAlignment.Stretch, Background = _palette.Surface, BorderBrush = _palette.Line, Padding = new Thickness(12, 8, 12, 8) };
            review.Click += (_, _) => _navigation.SelectedItem = _navigation.Items[1]; _side.Children.Add(review);
            _side.Children.Add(new Border { Height = 1, Background = _palette.Line });
        }
        _side.Children.Add(_palette.Label("订阅额度", 14));
        foreach (var q in s.Quotas.Take(4)) _side.Children.Add(QuotaRow(q, sidebar: true));
        if (s.Quotas.Length == 0) _side.Children.Add(_palette.Label("尚未读取到额度", 12, true));
        _side.Children.Add(_palette.Label("由代理自身的登录状态读取", 10, true));
        }
        if (_quotaSignature != quotaSignature) { _quotaSignature = quotaSignature; _quotas.Children.Clear();
        foreach (var q in s.Quotas) _quotas.Children.Add(QuotaRow(q));
        }
        _notices.Foreground = _palette.Secondary; _notices.FontSize = 12;
        _notices.Text = string.Join("\n", s.Notices);
        var ids = string.Join(";", pending.Select(p => p.Id));
        if (ids != _requestIds || (_requests.Children.Count == 0))
        {
            _requestIds = ids; _requests.Children.Clear();
            foreach (var p in pending) BuildRequest(p);
            if (pending.Length == 0) _requests.Children.Add(new TextBlock { Text = "没有待处理请求。审批必须由你点击确认；10 分钟无人处理会回到代理自身界面。", TextWrapping = TextWrapping.Wrap });
        }
        RenderStats();
    }
    private static string Short(long value) => value >= 1000000 ? $"{value / 1000000.0:F1}M" : value >= 1000 ? $"{value / 1000.0:F1}k" : value.ToString("N0");
    private UIElement QuotaRow(Quota q, bool sidebar = false)
    {
        var cache = sidebar ? _sideQuotaCards : _quotaCards; var key = q.Agent + "|" + q.Name;
        if (cache.TryGetValue(key, out var existing)) { existing.Update(q); return existing.View; }
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(_palette.Label(q.Agent, 12)); var percent = _palette.Label($"{q.UsedPercent:F0}%", 12); percent.Foreground = q.UsedPercent >= 90 ? _palette.Coral : q.UsedPercent >= 70 ? _palette.Amber : _palette.Mint;
        Grid.SetColumn(percent, 1); header.Children.Add(percent); panel.Children.Add(header);
        var bar = HubCharts.Progress(q.UsedPercent, percent.Foreground, _palette.Line); panel.Children.Add(bar);
        var caption = _palette.Label("", 10, true); panel.Children.Add(caption);
        void Update(Quota value) { percent.Text = $"{value.UsedPercent:F0}%"; percent.Foreground = value.UsedPercent >= 90 ? _palette.Coral : value.UsedPercent >= 70 ? _palette.Amber : _palette.Mint; bar.Foreground = percent.Foreground; bar.Update(value.UsedPercent); caption.Text = value.Name.Replace("five_hour", "5 小时").Replace("seven_day", "每周") + (value.Reset is { } reset ? $" · {Math.Max(0, (reset - DateTimeOffset.UtcNow).TotalHours):F0}h 后重置" : ""); }
        Update(q); cache[key] = (panel, Update);
        return panel;
    }
    private void AddSession(Session session, Snapshot snapshot)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 12, 0, 12), BorderBrush = _palette.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var mark = _palette.Mark(session.Agent, 34); mark.VerticalAlignment = VerticalAlignment.Top; row.Children.Add(mark);
        var info = new StackPanel { Spacing = 5 }; info.Children.Add(_palette.Label(session.Title.Length > 0 ? session.Title : session.Id, 14));
        info.Children.Add(_palette.Label(session.Agent + " · " + session.State, 11, true));
        var tokens = snapshot.Events.Where(e => e.Agent == session.Agent && e.SessionId == session.Id).Sum(e => e.Input + e.Output + e.Cache);
        info.Children.Add(_palette.Label(Short(tokens) + " tokens · " + session.Updated.LocalDateTime.ToString("HH:mm"), 11, true)); Grid.SetColumn(info, 1); row.Children.Add(info);
        var menu = new MenuFlyout(); var copy = new MenuFlyoutItem { Text = "复制会话 ID", Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += (_, _) => Safe(() => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(session.Id); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); }); menu.Items.Add(copy);
        var folder = new MenuFlyoutItem { Text = "打开工作区", Icon = new FontIcon { Glyph = "\uE8B7" }, IsEnabled = Directory.Exists(session.Cwd) };
        folder.Click += async (_, _) => { try { await Windows.System.Launcher.LaunchFolderPathAsync(session.Cwd); } catch (Exception ex) { _context.Log.Error("打开工作区", ex); } }; menu.Items.Add(folder);
        var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 14 }, Flyout = menu, Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Top };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "会话操作：" + session.Title); Grid.SetColumn(more, 2); row.Children.Add(more); _sessions.Items.Add(row);
    }
    private void BuildRequest(PendingRequest request)
    {
        var p = request.Payload; var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = request.Agent + " · " + p.At("tool_name").Text(), FontSize = 18 });
        panel.Children.Add(new TextBox { Header = "工具输入 / 命令 / 补丁", Text = p.At("tool_input").GetRawText(), IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 180 });
        var questions = p.At("tool_input", "questions").Items().ToArray(); var answers = new List<(string Question, TextBox Box)>();
        foreach (var q in questions)
        {
            var text = q.At("question").Text(); var box = new TextBox { Header = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            foreach (var option in q.At("options").Items())
            {
                var label = option.At("label").Text(); var button = new Button { Content = label };
                button.Click += (_, _) => box.Text = q.At("multiSelect").ValueKind == JsonValueKind.True && box.Text.Length > 0 ? box.Text + ", " + label : label;
                panel.Children.Add(button);
            }
            panel.Children.Add(box); answers.Add((text, box));
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Reply(string value)
        {
            Safe(() => { if (_answer(request.Id, value)) { actions.IsHitTestVisible = false; status.Text = "已发送"; } else status.Text = "请求已结束，请回到代理确认状态。"; });
        }
        if (questions.Length > 0 && request.Agent != "Codex")
        {
            var submit = new Button { Content = "提交回答" };
            submit.Click += (_, _) => Safe(() => {
                if (answers.Any(a => string.IsNullOrWhiteSpace(a.Box.Text))) { status.Text = "请填写每个问题，或选择回到代理。"; return; }
                var input = JsonNode.Parse(p.At("tool_input").GetRawText())!.AsObject();
                var written = new JsonObject(); foreach (var (question, box) in answers) written[question] = box.Text;
                input["answers"] = written;
                Reply(new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input } } }.ToJsonString());
            }); actions.Children.Add(submit);
        }
        else
        {
            var allow = new Button { Content = "允许一次" }; allow.Click += (_, _) => Reply(HookServer.Decision(true)); actions.Children.Add(allow);
            var deny = new Button { Content = "拒绝" }; deny.Click += (_, _) => Reply(HookServer.Decision(false)); actions.Children.Add(deny);
        }
        var leave = new Button { Content = "回到代理处理" }; leave.Click += (_, _) => Reply("{}"); actions.Children.Add(leave);
        panel.Children.Add(actions); panel.Children.Add(status); _requests.Children.Add(panel);
    }
    private void RenderStats()
    {
        var hours = _period.SelectedIndex switch { 0 => 5, 2 => 168, 3 => 720, _ => 24 };
        var events = _snapshot.Events.Where(e => e.Time > DateTimeOffset.UtcNow.AddHours(-hours)).ToArray();
        if (_statisticsChart == null) {
            _statisticsTotal = _palette.Label("", 18); _stats.Children.Add(_statisticsTotal);
            _statisticsChart = HubCharts.Histogram(events, _palette, 90); _stats.Children.Add(_statisticsChart); _stats.Children.Add(_breakdown);
            _stats.Children.Add(new TextBlock { Text = "输入不含缓存读取，输出已包含推理；缓存另列。订阅额度由引擎查询，与 Token 总数分开。未配置价格表时不推算账单。", Opacity = .65, TextWrapping = TextWrapping.Wrap });
        }
        _statisticsTotal!.Text = $"In {events.Sum(e => e.Input):N0}   Out {events.Sum(e => e.Output):N0}   Cache {events.Sum(e => e.Cache):N0}";
        _statisticsChart.Update(events); _breakdown.Children.Clear();
        foreach (var group in events.GroupBy(e => (e.Agent, e.Model)).OrderByDescending(g => g.Sum(e => e.Input + e.Output + e.Cache)))
            _breakdown.Children.Add(new TextBlock { Text = $"{group.Key.Agent} · {(group.Key.Model.Length > 0 ? group.Key.Model : "模型未记录")}   {group.Sum(e => e.Input + e.Output + e.Cache):N0} tokens · {group.Count()} 次记录", TextWrapping = TextWrapping.Wrap });
    }
    private void Safe(Action action) { try { action(); } catch (Exception ex) { _context.Log.Error("AgentHub 操作", ex); } }
    public void Dispose() { _context.Theme.Changed -= ThemeChanged; _palette.Dispose(); }
}
