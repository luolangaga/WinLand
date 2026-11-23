using System.Diagnostics;
using System.Text.Json;

namespace AgentHub.Core;

/// <summary>Metadata-only engine calls, following Agent HUD's own-engine approach. Never sends a prompt.</summary>
public static class EngineQuota
{
    public static string? Find(string name, string configured)
    {
        if (configured.Length > 0) return File.Exists(configured) ? configured : null;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Prepend(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        // Native executables only: do not interpolate untrusted paths into cmd.exe.
        foreach (var dir in paths)
        { var exe = Path.Combine(dir.Trim('"'), name + ".exe"); if (File.Exists(exe)) return exe; }
        foreach (var process in Process.GetProcessesByName(name))
            using (process)
                try { if (process.MainModule?.FileName is { } exe && File.Exists(exe)) return exe; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        if (name == "codex")
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(root)) return Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }
    public static async Task<Quota[]> Fetch(string agent, string executable, string home, string work, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
        token = timeout.Token;
        Directory.CreateDirectory(work);
        var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
        if (agent == "Codex")
        { foreach (var arg in new[] { "app-server", "--listen", "stdio://" }) start.ArgumentList.Add(arg); start.Environment["CODEX_HOME"] = home; }
        else
        {
            foreach (var arg in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--settings", "{\"disableAllHooks\":true}" }) start.ArgumentList.Add(arg);
            start.Environment["CLAUDE_CODE_ENTRYPOINT"] = "agent-hud"; start.Environment["CLAUDE_CONFIG_DIR"] = home;
        }
        using var process = Process.Start(start) ?? throw new IOException("无法启动引擎");
        var drain = Drain(process.StandardError, token);
        Quota[]? limits = null;
        try
        {
            async Task Send(object payload) { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload).AsMemory(), token); await process.StandardInput.FlushAsync(token); }
            if (agent == "Codex") await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "agenthub", version = "0.1.0" } } });
            else await Send(new { type = "control_request", request_id = "agenthub-usage", request = new { subtype = "get_usage", skip_behaviors = true } });
            long bytes = 0;
            string? account = null; bool accountAnswered = false;
            using var accountGrace = CancellationTokenSource.CreateLinkedTokenSource(token);
            while (await process.StandardOutput.ReadLineAsync(accountGrace.Token) is { } line)
            {
                bytes += line.Length; if (bytes > 8 * 1024 * 1024) throw new IOException("引擎响应超出上限");
                using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
                if (agent == "Codex")
                {
                    if (r.At("error").ValueKind == JsonValueKind.Object)
                    { if (r.At("id").Number() == 3) { accountAnswered = true; if (limits != null) return limits; continue; } throw new IOException("Codex 额度读取失败，请检查登录状态"); }
                    if (r.At("id").Number() == 1)
                    { await Send(new { method = "initialized" }); await Send(new { id = 2, method = "account/rateLimits/read" }); await Send(new { id = 3, method = "account/read", @params = new { } }); }
                    if (r.At("id").Number() == 3) { account = r.At("result", "account", "email").Text(); accountAnswered = true; }
                    if (r.At("id").Number() == 2) { limits = ParseCodex(r.At("result")); accountGrace.CancelAfter(TimeSpan.FromSeconds(2)); }
                    if (limits != null && accountAnswered) return limits.Select(q => q with { Account = account }).ToArray();
                }
                else if (r.At("type").Text() == "control_response")
                {
                    if (r.At("response", "subtype").Text() == "error") throw new IOException("Claude 引擎不支持 get_usage 或登录无效");
                    var p = r.At("response", "response");
                    if (p.At("rate_limits_available").ValueKind != JsonValueKind.True) throw new IOException("Claude 当前登录无订阅额度");
                    return ParseClaude(p.At("rate_limits"));
                }
            }
            throw new IOException("引擎未返回额度");
        }
        catch (OperationCanceledException) when (limits != null && !token.IsCancellationRequested) { return limits; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); await drain; } catch (OperationCanceledException) { }
        }
    }
    private static async Task Drain(StreamReader reader, CancellationToken token)
    { var buffer = new char[4096]; try { while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { } } catch (OperationCanceledException) { } }
    public static Quota[] ParseCodex(JsonElement value)
    {
        var rows = new List<Quota>(); var buckets = value.At("rateLimitsByLimitId");
        if (buckets.ValueKind == JsonValueKind.Object)
            foreach (var b in buckets.EnumerateObject()) Read(b.Value, b.Name);
        else Read(value.At("rateLimits"), "codex");
        return rows.ToArray();
        void Read(JsonElement bucket, string name)
        {
            foreach (var key in new[] { "primary", "secondary" })
            {
                var w = bucket.At(key); var used = w.At("usedPercent").Decimal(); if (used == null) continue;
                DateTimeOffset? reset = w.At("resetsAt").Number() is > 0 and < 253402300800L ? DateTimeOffset.FromUnixTimeSeconds(w.At("resetsAt").Number()) : null;
                var minutes = w.At("windowDurationMins").Number();
                var window = minutes <= 0 ? "额度窗口" : minutes == 10080 ? "每周" : minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes} 分钟";
                var label = bucket.At("limitName").Text(); if (label.Length == 0) label = name == "codex" ? "" : name;
                rows.Add(new("Codex", (label.Length > 0 ? label + " · " : "") + window, Math.Clamp(used.Value, 0, 100), reset, DateTimeOffset.UtcNow));
            }
        }
    }
    public static Quota[] ParseClaude(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return [];
        return value.EnumerateObject().Where(p => p.Value.At("utilization").Decimal() != null)
            .Select(p => new Quota("Claude Code", p.Name, Math.Clamp(p.Value.At("utilization").Decimal()!.Value, 0, 100),
                p.Value.At("resets_at").Date(), DateTimeOffset.UtcNow)).ToArray();
    }
}
