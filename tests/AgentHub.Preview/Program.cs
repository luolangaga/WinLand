using AgentHub;
using AgentHub.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace AgentHubPreview;

internal static class Program
{
    [STAThread] private static void Main() => Application.Start(_ => {
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread())); new PreviewApp(); });
}
public sealed partial class PreviewApp : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    private Window? _window;
    private Window? _dashboardWindow;
    private readonly CancellationTokenSource _stop = new();
    public PreviewApp() { InitializeComponent(); UnhandledException += (_, e) => { File.WriteAllText(Path.Combine(Path.GetTempPath(), "agenthub-preview-error.txt"), e.Exception.ToString()); }; }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var context = new PreviewContext();
            bool verifyCharts = Environment.GetCommandLineArgs().Contains("--verify-charts");
            bool demo = Environment.GetCommandLineArgs().Contains("--demo") || verifyCharts;
            var dashboard = new HubDashboard(context, (_, _) => demo);
            void Details()
            {
                if (_dashboardWindow != null) { _dashboardWindow.Activate(); return; }
                var surface = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 27, 31)), Child = dashboard };
                _dashboardWindow = new Window { Title = "AgentHub · 完整明细预览", Content = surface }; _dashboardWindow.AppWindow.Resize(new(1640, 1300));
                _dashboardWindow.Closed += (_, _) => { surface.Child = null; _dashboardWindow = null; }; _dashboardWindow.Activate();
            }
            var view = new HubView(context, (_, _) => demo, Details, demo); view.AnimateToExpanded(TimeSpan.FromMilliseconds(1));
            var now = DateTimeOffset.UtcNow;
            using var json = System.Text.Json.JsonDocument.Parse("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"dotnet test\"}}");
            var snapshot = new Snapshot([new("Codex", "demo-session", "AgentHub Windows 集成", "C:\\example", "运行中", now),
                new("Claude Code", "demo-claude", "检查插件 SDK 的兼容性", "C:\\example", "等待审批", now)],
                Enumerable.Range(0, 24).Select(i => new UsageEvent("demo" + i, "Codex", "demo-session", "test-model", now.AddHours(i - 23).AddMinutes(-1),
                    (long)(600 + 1900 * Math.Abs(Math.Sin(i * .45))), (long)(160 + 600 * Math.Abs(Math.Sin(i * .7))), (long)(80 + 600 * Math.Abs(Math.Cos(i * .4))))).ToArray(),
                [new("Codex", "5 小时", 43, now.AddHours(2), now), new("Claude Code", "seven_day", 78, now.AddDays(3), now)], []);
            PendingRequest[] pending = [new("request", "Claude Code", json.RootElement.Clone(), now.AddMinutes(10), new())];
            if (demo) { dashboard.Update(snapshot, pending); view.Update(snapshot, pending); }
            else { view.Update(Snapshot.Empty with { Notices = ["正在索引本机历史日志"] }, []); _ = ReadLive(context, view, dashboard, _stop.Token); }
            var root = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 24, 24)), RowSpacing = 12, Padding = new Thickness(12) };
            root.RowDefinitions.Add(new() { Height = new(286) });
            var card = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(context.Theme.IsLight ? Windows.UI.Color.FromArgb(255, 246, 247, 249) : Microsoft.UI.Colors.Black), CornerRadius = new(20), Width = 464, Child = view };
            root.Children.Add(card);
            _window = new Window { Title = demo ? "AgentHub · 演示（所有数值均为示例）" : "AgentHub · 本机数据", Content = root }; _window.Activate();
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window)) / 96.0;
            _window.AppWindow.Resize(new((int)(520 * scale), (int)(350 * scale)));
            _window.Closed += (_, _) => { _stop.Cancel(); view.Dispose(); dashboard.Dispose(); _dashboardWindow?.Close(); };
            if (verifyCharts)
            {
                var timer = context.Dispatcher.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(1);
                timer.Tick += (_, _) => {
                    timer.Stop();
                    try {
                        var before = Charts(view).ToArray(); if (before.Length != 2) throw new Exception("Expected overview ring and histogram");
                        var firstBar = ((HubCharts.HistogramChart)before.Single(c => c is HubCharts.HistogramChart)).Children.OfType<Border>().First();
                        var height = firstBar.Height;
                        for (int i = 0; i < 20; i++) {
                            view.Update(snapshot with { Quotas = snapshot.Quotas.Select(q => q with { UsedPercent = q.UsedPercent + i % 3, ReadAt = now.AddSeconds(i) }).ToArray(),
                                Events = snapshot.Events.Append(new UsageEvent("update", "Codex", "demo-session", "test-model", now, i * 100, i * 10, 0)).ToArray() }, pending);
                            var after = Charts(view).ToArray(); if (after.Length != before.Length || before.Any(c => !after.Contains(c))) throw new Exception("Chart instance rebuilt during refresh");
                            if (Math.Abs(firstBar.Height - height) > .001) throw new Exception("Histogram reset before transition tick");
                        }
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "agenthub-chart-check.txt"), "PASS: 20 changed-data updates preserve ring/histogram instances and displayed bar heights");
                    } catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "agenthub-chart-check.txt"), "FAIL: " + ex); }
                    _window.Close();
                }; timer.Start();
            }
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "agenthub-preview-error.txt"), ex.ToString()); Exit(); }
    }
    private static IEnumerable<DependencyObject> Charts(DependencyObject node)
    {
        if (node is HubCharts.RingChart or HubCharts.HistogramChart or HubCharts.ProgressChart) yield return node;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var chart in Charts(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i))) yield return chart;
    }
    private static async Task ReadLive(PreviewContext context, HubView view, HubDashboard dashboard, CancellationToken token)
    {
        var reader = new TranscriptReader(); Quota[] quotas = []; var notices = new List<string>(); var nextQuota = DateTimeOffset.MinValue;
        var codexHome = TranscriptReader.Home("CODEX_HOME", ".codex"); var claudeHome = TranscriptReader.Home("CLAUDE_CONFIG_DIR", ".claude");
        try
        {
            while (!token.IsCancellationRequested)
            {
                var snapshot = await Task.Run(() => reader.Read(codexHome, claudeHome, token), token);
                if (DateTimeOffset.UtcNow >= nextQuota)
                {
                    nextQuota = DateTimeOffset.UtcNow.AddMinutes(5); notices.Clear(); var rows = new List<Quota>();
                    foreach (var (agent, name, home) in new[] { ("Codex", "codex", codexHome), ("Claude Code", "claude", claudeHome) })
                    {
                        var exe = EngineQuota.Find(name, ""); if (exe == null) { notices.Add(agent + "：未找到引擎，额度不可用"); continue; }
                        try { rows.AddRange(await EngineQuota.Fetch(agent, exe, home, Path.Combine(Path.GetTempPath(), "AgentHubPreviewReal", name), token)); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { notices.Add(agent + "：查询超时"); }
                        catch (Exception ex) { if (token.IsCancellationRequested) return; notices.Add(agent + "：" + ex.Message); }
                    }
                    quotas = rows.ToArray();
                }
                if (token.IsCancellationRequested) return;
                snapshot = snapshot with { Quotas = quotas, Notices = snapshot.Notices.Concat(notices).ToArray() };
                view.Update(snapshot, []); dashboard.Update(snapshot, []);
                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { context.Log.Error("真实数据预览", ex); }
    }
}
internal sealed class PreviewContext : IPluginContext
{
    public PluginManifest Manifest => new() { Id = "preview", Name = "Preview", Version = "1.0.0" }; public string PluginDirectory => "";
    public Version HostVersion => new(2, 5); public DispatcherQueue Dispatcher => DispatcherQueue.GetForCurrentThread();
    public IPluginLogger Log { get; } = new Logger(); public IIslandTheme Theme { get; } = new PreviewTheme();
    public ISettingsStore Settings => throw new NotSupportedException(); public IIslandSurface Island => throw new NotSupportedException();
    public IDisposable Register(IDisposable d) => d;
    public IDisposable OnSettingsChanged(string key, Action handler) => throw new NotSupportedException();
    public IDisposable CreateTimer(TimeSpan interval, bool repeat, Action tick) => throw new NotSupportedException();
    public void RunOnUI(Action action) => action(); public Task RunOnUIAsync(Func<Task> action) => action();
    private sealed class PreviewTheme : IIslandTheme { public bool IsLight => Environment.GetCommandLineArgs().Contains("--light"); public event Action? Changed { add { } remove { } } }
    private sealed class Logger : IPluginLogger
    { public void Debug(string m) { } public void Info(string m) { } public void Warn(string m) { } public void Error(string m) { }
      public void Error(string m, Exception e) => File.WriteAllText(Path.Combine(Path.GetTempPath(), "agenthub-preview-error.txt"), m + e); }
}

