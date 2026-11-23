using System.Text;
using System.Text.Json;

namespace AgentHub.Core;

/// <summary>Incremental JSONL reader. Only newline-committed records are consumed. Truncation
/// replaces that file's ledger; copied/archived sessions are merged by event identity.</summary>
public sealed class TranscriptReader(bool readAdditional = true)
{
    private sealed class FileState
    {
        public long Offset, Length; public DateTime Modified;
        public Session? Session; public readonly List<UsageEvent> Events = [];
        public long In, Out, Cache, Reasoning;
        public DateTimeOffset Started;
        public string Model = "";
        public bool Skipping;
        public bool HasTotals, Indexing;
        public long? InheritedOrdinal;
        public readonly Dictionary<string, UsageEvent> Messages = [];
    }
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _titles = [];
    public static string Home(string variable, string fallback) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } p
        ? Path.GetFullPath(p) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);

    public Snapshot Read(string codexHome, string claudeHome, CancellationToken token)
    {
        var notices = new List<string>();
        ReadTitles(Path.Combine(codexHome, "session_index.jsonl"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        Scan("Codex", Path.Combine(codexHome, "sessions"));
        Scan("Codex", Path.Combine(codexHome, "archived_sessions"));
        Scan("Claude Code", Path.Combine(claudeHome, "projects"));
        if (readAdditional)
        {
            Scan("Qwen Code", Path.Combine(Home("QWEN_RUNTIME_DIR", Environment.GetEnvironmentVariable("QWEN_HOME") ?? ".qwen"), "projects"));
            Scan("CodeBuddy", Path.Combine(Home("CODEBUDDY_CONFIG_DIR", ".codebuddy"), "projects"));
            Scan("WorkBuddy", Path.Combine(Home("WORKBUDDY_CONFIG_DIR", ".workbuddy"), "projects"));
            Scan("Pi", Path.Combine(Home("PI_CODING_AGENT_DIR", ".pi/agent"), "sessions"));
            Scan("OpenClaw", Path.Combine(Home("OPENCLAW_STATE_DIR", ".openclaw"), "agents"));
        }
        foreach (var stale in _files.Keys.Where(p => !roots.Any(root => p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) ||
            (!seen.Contains(p) && !File.Exists(p))).ToArray()) _files.Remove(stale);
        var sessions = _files.Values.Where(f => f.Session != null).Select(f => f.Session!)
            .GroupBy(s => (s.Agent, s.Id)).Select(g => g.MaxBy(s => s.Updated)!)
            .Select(s => s.Agent == "Codex" && _titles.TryGetValue(s.Id, out var t) ? s with { Title = t } : s).ToArray();
        var events = _files.Values.SelectMany(f => f.Events.Concat(f.Messages.Values))
            .Where(e => e.Time >= DateTimeOffset.UtcNow.AddDays(-30))
            .GroupBy(e => (e.Agent, e.SessionId, e.Id)).Select(g => g.Last()).ToArray();
        if (_files.Values.Any(f => f.Indexing)) notices.Add("正在索引历史日志，Token 统计尚未完整");
        return new(sessions, events, [], notices.ToArray());

        void Scan(string agent, string root)
        {
            roots.Add(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            if (!Directory.Exists(root)) return;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    token.ThrowIfCancellationRequested(); seen.Add(path);
                    if (agent == "Qwen Code" && (!path.Contains("chats") || path.Contains("subagents") || path.EndsWith(".ledger.jsonl"))) continue;
                    if (agent is "CodeBuddy" or "WorkBuddy" && path.Contains("tool-results")) continue;
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-31)) continue;
                    if (!_files.TryGetValue(path, out var state) || info.Length < state.Length ||
                        (info.Length == state.Length && info.LastWriteTimeUtc != state.Modified)) _files[path] = state = new();
                    if (state.Offset == info.Length && state.Modified == info.LastWriteTimeUtc) continue;
                    try
                    {
                        ReadLines(path, state, agent, token);
                        state.Length = info.Length; state.Modified = info.LastWriteTimeUtc;
                    }
                    catch (IOException) { notices.Add(agent + "：日志暂时无法读取，保留上次结果"); }
                    catch (UnauthorizedAccessException) { notices.Add(agent + "：日志访问被拒绝"); }
                }
            }
            catch (IOException) { notices.Add(agent + "：目录暂时无法枚举"); }
        }
    }
    private void ReadTitles(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            while (reader.ReadLine() is { } line)
                try { using var d = JsonDocument.Parse(line); var r = d.RootElement; var t = r.At("thread_name").Text();
                    if (t.Length > 0) _titles[r.At("id").Text()] = Json.Title(t); } catch (JsonException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static void ReadLines(string path, FileState state, string agent, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = state.Offset;
        using var buffered = new BufferedStream(stream, 65536);
        using var line = new MemoryStream();
        long position = state.Offset; var oversized = state.Skipping;
        // Bound each pass to 16 MiB; incomplete records are retried from their beginning.
        var budget = 16 * 1024 * 1024;
        while (budget-- > 0 && buffered.ReadByte() is var b && b != -1)
        {
            position++;
            if ((position & 65535) == 0) token.ThrowIfCancellationRequested();
            if (b != 10) { if (line.Length < 2 * 1024 * 1024 && !oversized) line.WriteByte((byte)b); else oversized = true; continue; }
            token.ThrowIfCancellationRequested();
            if (!oversized)
                try { using var d = JsonDocument.Parse(line.ToArray()); Parse(d.RootElement, state, agent, path, position); }
                catch (JsonException) { }
            state.Offset = position; line.SetLength(0); oversized = false;
        }
        // An oversized record is deliberately skipped; advance across passes until its newline.
        // A small partial record is retained at its original offset for retry.
        state.Skipping = oversized;
        if (oversized) state.Offset = position;
        state.Indexing = budget <= 0 && state.Offset < stream.Length;
    }
    private static void Parse(JsonElement r, FileState f, string agent, string path, long ordinal)
    {
        var type = r.At("type").Text(); var p = r.At("payload");
        var time = r.At("timestamp").Date() ?? DateTimeOffset.MinValue;
        if (agent == "Codex")
        {
            if (type == "session_meta" && f.Session == null)
            {
                var id = p.At("id").Text(); if (id.Length == 0) return;
                f.Started = p.At("timestamp").Date() ?? time;
                f.InheritedOrdinal = p.At("subagent_history_start_ordinal").ValueKind == JsonValueKind.Number ? p.At("subagent_history_start_ordinal").Number() : null;
                var cwd = p.At("cwd").Text();
                var parent = p.At("source", "subagent", "thread_spawn", "parent_thread_id").Text();
                if (parent.Length == 0) parent = p.At("parent_thread_id").Text();
                if (parent.Length == 0 && p.At("source", "subagent").ValueKind == JsonValueKind.Object) parent = "subagent";
                f.Session = new(agent, id, Json.Title(Path.GetFileName(cwd)), cwd, "未知", time, parent.Length > 0 ? parent : null);
            }
            if (f.Session == null) return;
            if (type == "turn_context") { f.Model = p.At("model").Text(); return; }
            if (type != "event_msg") return;
            var ev = p.At("type").Text();
            bool inherited = time < f.Started || (f.InheritedOrdinal is { } boundary && r.At("ordinal").ValueKind == JsonValueKind.Number && r.At("ordinal").Number() <= boundary);
            if (ev == "token_count")
            {
                var u = p.At("info", "total_token_usage"); var last = p.At("info", "last_token_usage");
                if (u.ValueKind != JsonValueKind.Object) return;
                long input = u.At("input_tokens").Number(), output = u.At("output_tokens").Number(), cache = u.At("cached_input_tokens").Number(), reasoning = u.At("reasoning_output_tokens").Number();
                bool reset = input < f.In || output < f.Out || cache < f.Cache;
                long Counter(string key, long fallback) => last.At(key).ValueKind == JsonValueKind.Number ? last.At(key).Number() : fallback;
                long di = reset || !f.HasTotals ? Counter("input_tokens", input) : input - f.In;
                long dout = reset || !f.HasTotals ? Counter("output_tokens", output) : output - f.Out;
                long dc = reset || !f.HasTotals ? Counter("cached_input_tokens", cache) : cache - f.Cache;
                long dr = reset || !f.HasTotals ? Counter("reasoning_output_tokens", reasoning) : Math.Max(0, reasoning - f.Reasoning);
                f.In = input; f.Out = output; f.Cache = cache; f.Reasoning = reasoning;
                f.HasTotals = true;
                if (!inherited && time >= DateTimeOffset.UtcNow.AddDays(-30) && di + dout + dc > 0)
                    f.Events.Add(new(time.ToString("O") + ":" + input + ":" + output, agent, f.Session.Id,
                        f.Model, time, Math.Max(0, di - dc), dout, dc, dr));
            }
            if (inherited) return;
            var title = ev == "user_message" ? Json.Title(p.At("message").Text()) : f.Session.Title;
            f.Session = f.Session with { Updated = time, Title = title.Length > 0 ? title : f.Session.Title,
                State = ev switch { "task_started" => "运行中", "task_complete" => "新回复", "turn_aborted" => "已中断", _ => f.Session.State } };
        }
        else if (agent == "Claude Code")
        {
            var id = r.At("sessionId").Text(); if (id.Length == 0) return;
            if (f.Session != null && f.Session.Id != id) return; // inherited parent records are not fork usage
            if (f.Session == null && id != Path.GetFileNameWithoutExtension(path) && !Path.GetFileName(path).StartsWith("agent-")) return;
            var cwd = r.At("cwd").Text();
            f.Session ??= new(agent, id, Json.Title(Path.GetFileName(cwd)), cwd, "未知", time,
                path.Contains("subagents", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).StartsWith("agent-") ? "subagent" : null);
            if (type is "custom-title" or "ai-title")
            { var title = r.At(type == "custom-title" ? "customTitle" : "aiTitle").Text(); if (title.Length > 0) f.Session = f.Session with { Title = Json.Title(title) }; }
            if (type == "user" && f.Session.Title == Json.Title(Path.GetFileName(cwd)))
            { var text = r.At("message", "content").Text(); if (text.Length > 0) f.Session = f.Session with { Title = Json.Title(text) }; }
            var m = r.At("message"); var u = m.At("usage"); var mid = m.At("id").Text();
            if (type == "assistant" && mid.Length > 0 && m.At("model").Text() != "<synthetic>" && u.ValueKind == JsonValueKind.Object && time > DateTimeOffset.UtcNow.AddDays(-30))
                f.Messages[mid] = new(mid, agent, id, m.At("model").Text(), time,
                    u.At("input_tokens").Number() + u.At("cache_creation_input_tokens").Number(), u.At("output_tokens").Number(), u.At("cache_read_input_tokens").Number(), u.At("output_tokens_details", "thinking_tokens").Number());
            if (time != DateTimeOffset.MinValue) f.Session = f.Session with { Updated = time };
            // Transcript timestamps alone do not prove an engine is still running. Hooks supply live state.
        }
        else
        {
            if (time == DateTimeOffset.MinValue && r.At("timestamp").Number() is > 0 and < 253402300800000L)
                time = DateTimeOffset.FromUnixTimeMilliseconds(r.At("timestamp").Number());
            var id = r.At("sessionId").Text();
            if (id.Length == 0) id = Path.GetFileNameWithoutExtension(path);
            if (agent is "CodeBuddy" or "WorkBuddy" && Path.GetFileName(Path.GetDirectoryName(path)) == "subagents")
                id = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) ?? id;
            var cwd = r.At("cwd").Text();
            if (type == "session") { id = r.At("id").Text(); cwd = r.At("cwd").Text(); }
            f.Session ??= new(agent, id, Json.Title(Path.GetFileName(cwd)), cwd, "未知", time);
            if (time != DateTimeOffset.MinValue) f.Session = f.Session with { Updated = time };
            JsonElement usage = default; string model = "", key = r.At("uuid").Text();
            long input = 0, output = 0, cache = 0, reasoning = 0;
            if (agent == "Qwen Code")
            {
                if (r.At("forkedFrom").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) return;
                if (type != "system" || r.At("subtype").Text() != "ui_telemetry") return;
                var e = r.At("systemPayload", "uiEvent"); if (e.At("event.name").Text() != "qwen-code.api_response") return;
                time = e.At("event.timestamp").Date() ?? time; model = e.At("model").Text();
                var prompt = e.At("input_token_count").Number(); cache = e.At("cached_content_token_count").Number();
                if (cache > prompt) return; input = prompt - cache; output = e.At("output_token_count").Number();
                reasoning = e.At("thoughts_token_count").Number();
                if (reasoning > 0 && e.At("total_token_count").Number() == prompt + output + reasoning) output += reasoning;
            }
            else if (agent is "CodeBuddy" or "WorkBuddy")
            {
                if (type != "function_call" && !(type == "message" && r.At("role").Text() == "assistant")) return;
                if (r.At("status").Text() is { Length: > 0 } status && status != "completed") return;
                usage = r.At("message", "usage");
                if (usage.ValueKind != JsonValueKind.Object) usage = r.At("providerData", "usage");
                if (usage.ValueKind != JsonValueKind.Object) usage = r.At("providerData", "rawUsage");
                if (usage.ValueKind != JsonValueKind.Object) return;
                key = r.At("providerData", "messageId").Text(); if (key.Length == 0) key = r.At("providerData", "traceId").Text();
                if (key.Length == 0) key = r.At("id").Text();
                model = r.At("providerData", "model").Text();
                if (model.Length == 0) model = r.At("providerData", "requestModelId").Text();
                if (model.Length == 0) model = r.At("message", "model").Text();
                long First(params string[] fields) => fields.Select(field => usage.At(field)).FirstOrDefault(v => v.ValueKind == JsonValueKind.Number).Number();
                var prompt = First("input_tokens", "inputTokens", "prompt_tokens");
                var listed = First("cache_read_input_tokens", "cacheReadInputTokens", "cacheTokens", "prompt_cache_hit_tokens", "cached_tokens");
                var details = new[] { "inputTokensDetails", "input_tokens_details", "prompt_tokens_details" }
                    .Select(field => usage.At(field, "cached_tokens")).FirstOrDefault(v => v.ValueKind == JsonValueKind.Number);
                cache = listed > 0 ? listed : details.Number();
                output = First("output_tokens", "outputTokens", "completion_tokens");
                var write = First("cache_creation_input_tokens", "cacheCreationInputTokens", "cachedWriteTokens", "prompt_cache_write_tokens");
                reasoning = First("completion_thinking_tokens", "completionThinkingTokens", "reasoningTokens");
                bool inclusive = (listed == 0 && details.ValueKind == JsonValueKind.Number) || First("total_tokens", "totalTokens") == prompt + output;
                if (inclusive && cache > prompt) return;
                input = inclusive ? prompt - cache : prompt + write;
                if (!inclusive) output += reasoning;
            }
            else if (agent is "Pi" or "OpenClaw")
            {
                var message = r.At("message"); if (type != "message" || message.At("role").Text() != "assistant") return;
                usage = message.At("usage"); if (usage.ValueKind != JsonValueKind.Object) return;
                model = message.At("model").Text(); key = r.At("id").Text();
                input = usage.At("input").Number() + usage.At("cacheWrite").Number();
                output = usage.At("output").Number(); cache = usage.At("cacheRead").Number();
            }
            if (key.Length == 0) key = ordinal.ToString();
            if (time > DateTimeOffset.UtcNow.AddDays(-30) && input + output + cache > 0)
                f.Messages[key] = new(key, agent, f.Session.Id, model, time, input, output, cache, reasoning);
        }
    }
}
