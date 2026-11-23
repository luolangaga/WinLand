using AgentHub.Core;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace AgentHub;

public sealed class AgentHubPlugin : IslandPluginBase
{
    private CancellationTokenSource? _stop;
    private HookServer? _hooks;
    private HubView? _view;
    private HubDashboard? _dashboard;
    private Snapshot _snapshot = Snapshot.Empty;
    private Task? _worker;
    private readonly TranscriptReader _reader = new();
    private readonly List<Quota> _quotas = [];
    private readonly List<string> _quotaNotices = [];
    private readonly Dictionary<string, DateTimeOffset> _warned = [];
    private IPluginHostServices Host => (IPluginHostServices)Context;
    private string CodexHome => Settings.Get("codexHome", TranscriptReader.Home("CODEX_HOME", ".codex"));
    private string ClaudeHome => Settings.Get("claudeHome", TranscriptReader.Home("CLAUDE_CONFIG_DIR", ".claude"));
    protected override Task OnInitializeAsync()
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(Host.LifetimeToken);
        _view = new HubView(Context, (id, reply) => _hooks?.Answer(id, reply) == true, OpenDashboard); Context.Register(_view);
        _hooks = new HookServer(() => RunOnUI(Publish)); _hooks.Start();
        SetContent(new IslandLiveContent { OwnerLabel = "AgentHub", OwnerGlyph = "\uE99A", Priority = 45,
            MorphView = _view, CompactSize = new(250, 40), ExpandedSize = new(464, 286) });
        Context.Island.AddSettingsPage(new("agenthub", "AgentHub", "\uE99A", BuildSettings));
        _worker = Monitor(_stop.Token);
        return Task.CompletedTask;
    }
    private async Task Monitor(CancellationToken token)
    {
        var nextQuota = DateTimeOffset.MinValue;
        string quotaConfiguration = "";
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Snapshot settings on UI before taking the read onto a background thread.
                string codex = "", claude = "", codexExe = "", claudeExe = ""; bool query = false;
                await Context.RunOnUIAsync(() => { codex = CodexHome; claude = ClaudeHome;
                    codexExe = Settings.Get("codexExe", ""); claudeExe = Settings.Get("claudeExe", ""); query = Settings.Get("queryQuota", true); return Task.CompletedTask; });
                var configuration = string.Join("|", codex, claude, codexExe, claudeExe, query);
                if (configuration != quotaConfiguration) { quotaConfiguration = configuration; _quotas.Clear(); _quotaNotices.Clear(); nextQuota = DateTimeOffset.MinValue; }
                var result = await Task.Run(() => _reader.Read(codex, claude, token), token);
                if (query && DateTimeOffset.UtcNow >= nextQuota)
                {
                    nextQuota = DateTimeOffset.UtcNow.AddMinutes(5); _quotaNotices.Clear();
                    foreach (var (agent, home, configured, name) in new[] { ("Codex", codex, codexExe, "codex"), ("Claude Code", claude, claudeExe, "claude") })
                    {
                        var executable = EngineQuota.Find(name, configured);
                        _quotas.RemoveAll(q => q.Agent == agent); // never reuse another login's quota
                        if (executable == null) { _quotaNotices.Add(agent + "：未找到原生 exe，额度不可用（可在设置中指定）"); continue; }
                        try { _quotas.AddRange(await EngineQuota.Fetch(agent, executable, home, Path.Combine(Host.DataDirectory, "engine", name), token)); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { _quotaNotices.Add(agent + "：额度查询超时"); }
                        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException) { _quotaNotices.Add(agent + "：" + ex.Message); }
                    }
                }
                var snapshot = result with { Quotas = query ? _quotas.ToArray() : [], Notices = result.Notices.Concat(_quotaNotices).Distinct().ToArray() };
                await Context.RunOnUIAsync(() => { _snapshot = snapshot; Publish(); return Task.CompletedTask; });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error("AgentHub 读取失败", ex); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), token); } catch (OperationCanceledException) { break; }
        }
    }
    private void Publish()
    {
        if (_stop?.IsCancellationRequested != false || _hooks == null) return;
        var sessions = _snapshot.Sessions.Concat(_hooks.Sessions).GroupBy(s => (s.Agent, s.Id))
            .Select(g => { var latest = g.MaxBy(s => s.Updated)!; var title = g.FirstOrDefault(s => s.Title.Length > 0)?.Title;
                return latest with { Title = title ?? latest.Id }; })
            .OrderByDescending(s => s.State is "等待审批" or "需要关注").ThenByDescending(s => s.Updated).ToArray();
        var snapshot = _snapshot with { Sessions = sessions };
        _view?.Update(snapshot, _hooks.Pending);
        _dashboard?.Update(snapshot, _hooks.Pending);
        foreach (var q in snapshot.Quotas.Where(q => q.UsedPercent >= 90))
        {
            var key = q.Agent + q.Name;
            if (_warned.TryGetValue(key, out var t) && t > DateTimeOffset.UtcNow.AddHours(-1)) continue;
            _warned[key] = DateTimeOffset.UtcNow;
            Context.Island.ShowMessage(new IslandMessage { Title = q.Agent + " 额度提醒", Text = q.Name + $" 已使用 {q.UsedPercent:F0}%" });
        }
    }
    private void OpenDashboard()
    {
        if (_dashboard != null) { Context.Island.CloseSpotlight(); return; }
        var dashboard = new HubDashboard(Context, (id, reply) => _hooks?.Answer(id, reply) == true);
        _dashboard = dashboard;
        Context.Island.OpenSpotlight(new IslandSpotlight { Content = dashboard, Size = new(820, 650),
            OnClosed = () => { dashboard.Dispose(); if (_dashboard == dashboard) _dashboard = null; } });
        Publish();
    }
    private UIElement BuildSettings()
    {
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = "AgentHub", FontSize = 26 });
        panel.Children.Add(new TextBlock { Text = "展开岛体，用左右按钮切换概览、会话、额度、Token 和待处理。斜箭头打开完整明细。额度圆环显示已用比例，与本地 Token 统计分开。", TextWrapping = TextWrapping.Wrap });
        var fields = new Dictionary<string, TextBox>();
        foreach (var (key, title, fallback) in new[] { ("codexHome", "Codex 数据目录", CodexHome), ("claudeHome", "Claude 数据目录", ClaudeHome),
            ("codexExe", "Codex 原生 exe 路径（留空从 PATH 查找）", ""), ("claudeExe", "Claude 原生 exe 路径（留空从 PATH 查找）", "") })
        { var box = new TextBox { Header = title, Text = Settings.Get(key, fallback) }; fields[key] = box; panel.Children.Add(box); }
        var quota = new ToggleSwitch { Header = "查询引擎订阅额度（每 5 分钟）", IsOn = Settings.Get("queryQuota", true) }; panel.Children.Add(quota);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "保存设置" };
        save.Click += (_, _) => { try { foreach (var (key, box) in fields) Settings.Set(key, box.Text.Trim().Trim('"')); Settings.Set("queryQuota", quota.IsOn); status.Text = "已保存；下一轮读取应用目录，额度在下一次刷新时更新。"; } catch (Exception ex) { status.Text = ex.Message; Log.Error("保存设置失败", ex); } };
        panel.Children.Add(save);
        panel.Children.Add(new TextBlock { Text = "实时事件与审批需要客户端 hooks。点击下方按钮打开接入目录，按 README 安装；不会修改 Codex 的信任状态。停用插件时 hook 返回原生审批。", TextWrapping = TextWrapping.Wrap });
        var scripts = new Button { Content = "打开 hook 脚本与接入说明" };
        scripts.Click += async (_, _) => { try { await Windows.System.Launcher.LaunchFolderPathAsync(PluginDirectory); } catch (Exception ex) { status.Text = ex.Message; } };
        panel.Children.Add(scripts); panel.Children.Add(status);
        return new ScrollViewer { Content = panel };
    }
    protected override async Task OnShutdownAsync()
    {
        _stop?.Cancel(); if (_hooks != null) await _hooks.DisposeAsync();
        if (_worker != null) await _worker;
        _dashboard?.Dispose(); _dashboard = null; _view?.Dispose(); _stop?.Dispose();
    }
}
