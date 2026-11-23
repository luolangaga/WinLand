using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace AgentHub.Core;

public sealed record PendingRequest(string Id, string Agent, JsonElement Payload, DateTimeOffset Expires,
    TaskCompletionSource<string> Reply);
/// <summary>Current-user-only Windows transport; concurrent requests never share an answer.</summary>
public sealed class HookServer : IAsyncDisposable
{
    public static string PipeName => "WinIsland.AgentHub." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16];
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly Action _changed;
    private Task? _loop;
    private int _clientId;
    public HookServer(Action changed) => _changed = changed;
    public PendingRequest[] Pending => _pending.Values.OrderBy(p => p.Expires).ToArray();
    public Session[] Sessions => _sessions.Values.Where(s => s.Updated > DateTimeOffset.UtcNow.AddHours(-24)).ToArray();
    public void Start() => _loop = Listen();
    public bool Answer(string id, string response) => _pending.TryGetValue(id, out var p) &&
        p.Expires > DateTimeOffset.UtcNow && p.Reply.TrySetResult(p.Agent == "Codex" && !UserReviewer(p.Payload) ? "{}" : response);
    private async Task Listen()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 32, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch { pipe.Dispose(); throw; }
                var id = Interlocked.Increment(ref _clientId);
                var task = Handle(pipe); _clients[id] = task;
                _ = task.ContinueWith(_ => _clients.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task Handle(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            string? id = null;
            try
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                lifetime.CancelAfter(TimeSpan.FromMinutes(10));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                // Length prefix prevents an unbounded ReadLine allocation from a malformed hook.
                var prefix = new byte[4]; await pipe.ReadExactlyAsync(prefix, lifetime.Token);
                var size = BitConverter.ToInt32(prefix); if (size is <= 0 or > 262144) return;
                var bytes = new byte[size]; await pipe.ReadExactlyAsync(bytes, lifetime.Token);
                using var doc = JsonDocument.Parse(bytes); var envelope = doc.RootElement;
                var agent = envelope.At("agent").Text(); var p = envelope.At("payload").Clone();
                if (!AllowedAgents.Contains(agent)) { await writer.WriteLineAsync("{}"); return; }
                var ev = p.At("hook_event_name").Text(); var sid = p.At("session_id").Text(); var cwd = p.At("cwd").Text();
                var tool = p.At("tool_name").Text();
                if (ev == "PermissionRequest" && (p.At("tool_input").ValueKind != JsonValueKind.Object || tool.Length == 0 ||
                    tool == "ExitPlanMode" || (agent == "Qwen Code" && tool is "ask_user_question" or "exit_plan_mode") ||
                    (agent is "CodeBuddy" or "WorkBuddy" or "Qoder" or "Qoder CN" or "QoderWork" && tool == "AskUserQuestion") ||
                    (agent == "Codex" && !UserReviewer(p)))) { await writer.WriteLineAsync("{}"); return; }
                if (sid.Length > 0)
                    _sessions[agent + ":" + sid] = new(agent, sid, Json.Title(Path.GetFileName(cwd)), cwd,
                        ev switch { "PermissionRequest" => "等待审批", "Stop" => "新回复", "SessionEnd" => "已结束", "Notification" => "需要关注", _ => "运行中" }, DateTimeOffset.UtcNow);
                if (ev == "PermissionRequest")
                {
                    id = Guid.NewGuid().ToString("N"); var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending[id] = new(id, agent, p, DateTimeOffset.UtcNow.AddMinutes(10), reply); _changed();
                    // Also observe client disconnect; an approval answered in the original UI must disappear.
                    var disconnected = reader.ReadAsync(new char[1].AsMemory(), lifetime.Token).AsTask();
                    var completed = await Task.WhenAny(reply.Task, disconnected, Task.Delay(Timeout.Infinite, lifetime.Token));
                    await writer.WriteLineAsync(completed == reply.Task ? await reply.Task : "{}");
                }
                else { _changed(); await writer.WriteLineAsync("{}"); }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException) { }
            finally { if (id != null) _pending.TryRemove(id, out _); _changed(); }
        }
    }
    public static readonly HashSet<string> AllowedAgents = ["Claude Code", "Codex", "CodeBuddy", "WorkBuddy", "Qwen Code", "ZCode", "Qoder", "Qoder CN", "QoderWork"];
    public static bool UserReviewer(JsonElement p)
    {
        var path = p.At("transcript_path").Text(); var turn = p.At("turn_id").Text();
        if (path.Length == 0 || turn.Length == 0) return false;
        string reviewer = "";
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            while (reader.ReadLine() is { } line)
            {
                try { using var d = JsonDocument.Parse(line); var r = d.RootElement;
                    if (r.At("type").Text() == "turn_context" && r.At("payload", "turn_id").Text() == turn)
                        reviewer = r.At("payload", "approvals_reviewer").Text(); } catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        return reviewer == "user";
    }
    public static string Decision(bool allow) => JsonSerializer.Serialize(new { hookSpecificOutput = new {
        hookEventName = "PermissionRequest", decision = new { behavior = allow ? "allow" : "deny" } } });
    public async ValueTask DisposeAsync()
    {
        foreach (var p in _pending.Values) p.Reply.TrySetResult("{}");
        _stop.Cancel(); if (_loop != null) await _loop;
        await Task.WhenAll(_clients.Values.ToArray()); _stop.Dispose();
    }
}
