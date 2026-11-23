using AgentHub.Core;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

if (args.Contains("--live"))
{
    var codexHome = TranscriptReader.Home("CODEX_HOME", ".codex");
    var exe = EngineQuota.Find("codex", "");
    Console.WriteLine("Native Codex engine discovered: " + (exe != null));
    if (exe != null)
    {
        try { foreach (var row in await EngineQuota.Fetch("Codex", exe, codexHome, Path.Combine(Path.GetTempPath(), "agenthub-live-quota"), default))
            Console.WriteLine($"LIVE Codex {row.Name}: used {row.UsedPercent:F1}%, reset {row.Reset:O}, account confirmed {row.Account is { Length: > 0 }}"); }
        catch (Exception ex) { Console.WriteLine("LIVE quota unavailable: " + ex.Message); }
    }
    var realReader = new TranscriptReader(readAdditional: false);
    var real = realReader.Read(codexHome, TranscriptReader.Home("CLAUDE_CONFIG_DIR", ".claude"), default);
    Console.WriteLine($"LIVE local logs: {real.Sessions.Length} sessions, {real.Events.Length} usage records, indexing: {real.Notices.Any(n => n.Contains("索引"))}");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "agenthub-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
string Record(string type, object payload, DateTimeOffset? time = null) => JsonSerializer.Serialize(new { type, timestamp = (time ?? DateTimeOffset.UtcNow).ToString("O"), payload }) + "\n";
try
{
    var codex = Path.Combine(root, "codex"); var claude = Path.Combine(root, "claude");
    Directory.CreateDirectory(Path.Combine(codex, "sessions")); Directory.CreateDirectory(Path.Combine(claude, "projects"));
    var path = Path.Combine(codex, "sessions", "rollout-test.jsonl");
    var start = DateTimeOffset.UtcNow.AddMinutes(-1);
    File.WriteAllText(path, Record("session_meta", new { id = "one", timestamp = start.ToString("O"), cwd = root }) +
        Record("turn_context", new { model = "test-model" }) +
        Record("event_msg", new { type = "token_count", info = new { total_token_usage = new { input_tokens = 100, output_tokens = 50, cached_input_tokens = 40 } } }));
    var reader = new TranscriptReader(readAdditional: false); var s = reader.Read(codex, claude, default);
    Check(s.Events.Single().Input == 60 && s.Events.Single().Cache == 40 && s.Events.Single().Output == 50, "Codex cache separated");
    Check(s.Events.Single().Model == "test-model", "Codex model from turn context");
    Check(reader.Read(codex, claude, default).Events.Length == 1, "Repeated scan counts once");
    var line = Record("event_msg", new { type = "token_count", info = new { total_token_usage = new { input_tokens = 150, output_tokens = 60, cached_input_tokens = 50 } } });
    File.AppendAllText(path, line[..^1]); Check(reader.Read(codex, claude, default).Events.Length == 1, "Half-written line deferred");
    File.AppendAllText(path, "\n"); s = reader.Read(codex, claude, default);
    Check(s.Events.Sum(e => e.Input) == 100 && s.Events.Sum(e => e.Output) == 60, "Append differenced, not summed cumulative");
    Directory.CreateDirectory(Path.Combine(codex, "archived_sessions")); File.Copy(path, Path.Combine(codex, "archived_sessions", "rollout-copy.jsonl"));
    Check(reader.Read(codex, claude, default).Events.Length == 2, "Archive copy deduplicated");
    var fork = Path.Combine(codex, "sessions", "fork.jsonl");
    File.WriteAllText(fork, Record("session_meta", new { id = "fork", timestamp = start.ToString("O"), cwd = root, subagent_history_start_ordinal = 10 }) +
        JsonSerializer.Serialize(new { type = "event_msg", timestamp = DateTimeOffset.UtcNow.ToString("O"), ordinal = 5, payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = 900, output_tokens = 300, cached_input_tokens = 200 } } } }) + "\n" +
        JsonSerializer.Serialize(new { type = "event_msg", timestamp = DateTimeOffset.UtcNow.ToString("O"), ordinal = 11, payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = 950, output_tokens = 310, cached_input_tokens = 210 } } } }) + "\n");
    var fe = reader.Read(codex, claude, default).Events.Single(e => e.SessionId == "fork");
    Check(fe.Input == 40 && fe.Output == 10 && fe.Cache == 10, "Inherited ordinal history sets baseline without billing parent again");
    var partial = Path.Combine(codex, "sessions", "partial.jsonl");
    File.WriteAllText(partial, Record("session_meta", new { id = "partial", timestamp = start.ToString("O"), cwd = root }) + Record("event_msg", new { type = "token_count", info = new {
        total_token_usage = new { input_tokens = 10000, output_tokens = 3000, cached_input_tokens = 4000 }, last_token_usage = new { input_tokens = 100, output_tokens = 30, cached_input_tokens = 40 } } }));
    var pe = reader.Read(codex, claude, default).Events.Single(e => e.SessionId == "partial");
    Check(pe.Input == 60 && pe.Output == 30 && pe.Cache == 40, "First observed token event uses last call rather than historical total");
    File.WriteAllText(path, Record("session_meta", new { id = "two", timestamp = start.ToString("O"), cwd = root }));
    Check(reader.Read(codex, claude, default).Sessions.Any(s => s.Id == "two"), "Truncated file replaces state");
    var cp = Path.Combine(claude, "projects", "c.jsonl");
    string Claude(int output) => JsonSerializer.Serialize(new { type = "assistant", sessionId = "c", timestamp = DateTimeOffset.UtcNow.ToString("O"), message = new { id = "msg", model = "claude-test", usage = new { input_tokens = 20, output_tokens = output, cache_read_input_tokens = 30, cache_creation_input_tokens = 10 } } }) + "\n";
    File.WriteAllText(cp, Claude(5) + Claude(9));
    var ce = reader.Read(codex, claude, default).Events.Single(e => e.Agent == "Claude Code");
    Check(ce.Input == 30 && ce.Output == 9 && ce.Cache == 30, "Claude streamed message replaces usage by message id");
    using var q = JsonDocument.Parse("{\"rateLimitsByLimitId\":{},\"rateLimits\":{\"primary\":{\"usedPercent\":99}}}");
    Check(EngineQuota.ParseCodex(q.RootElement).Length == 0, "Authoritative empty quota map stays empty");
    using var nullQuota = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":null}}}");
    Check(EngineQuota.ParseCodex(nullQuota.RootElement).Length == 0, "Unavailable quota is not zero");
    using var unknown = JsonDocument.Parse("{\"turn_id\":\"turn\"}"); Check(!HookServer.UserReviewer(unknown.RootElement), "Codex unconfirmed reviewer cannot approve");
    var transcript = Path.Combine(root, "reviewer.jsonl");
    File.WriteAllText(transcript, Record("turn_context", new { turn_id = "turn", approvals_reviewer = "user" }));
    using var user = JsonDocument.Parse(JsonSerializer.Serialize(new { transcript_path = transcript, turn_id = "turn" }));
    Check(HookServer.UserReviewer(user.RootElement), "Codex explicit user reviewer recognized");
    File.AppendAllText(transcript, Record("turn_context", new { turn_id = "turn", approvals_reviewer = "auto" }));
    Check(!HookServer.UserReviewer(user.RootElement), "Latest matching reviewer wins");

    var server = new HookServer(() => { }); server.Start();
    async Task<string?> Client(string session)
    {
        using var pipe = new NamedPipeClientStream(".", HookServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { agent = "Claude Code", payload = new { session_id = session, hook_event_name = "PermissionRequest", tool_name = "Bash", tool_input = new { command = "echo test" } } });
        await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length)); await pipe.WriteAsync(bytes); await pipe.FlushAsync();
        using var sr = new StreamReader(pipe); return await sr.ReadLineAsync();
    }
    var a = Client("a"); var b = Client("b");
    using var wait = new CancellationTokenSource(5000);
    while (server.Pending.Length != 2) await Task.Delay(10, wait.Token);
    var pa = server.Pending.Single(p => p.Payload.At("session_id").Text() == "a");
    Check(server.Answer(pa.Id, HookServer.Decision(false)), "User can deny one request");
    Check((await a)!.Contains("deny"), "Reply routed to matching pipe");
    Check(!b.IsCompleted, "Concurrent request not implicitly approved");
    await server.DisposeAsync(); Check(await b == "{}", "Shutdown releases pending hook to native approval");
    Console.WriteLine($"{passed} checks passed");
}
finally { Directory.Delete(root, true); }
