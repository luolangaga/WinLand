using System.Text.Json;
using WinIsland.Core;

namespace WinIsland.Modules.AcpMonitor;

public sealed class AgentEventReceiver : IAsyncDisposable
{
    private ICallbackServer? _server;
    private Func<JsonElement, Task>? _routeHandler;
    private bool _disposed;

    private readonly List<AgentPlanEntry> _planEntries = new();
    private readonly List<AgentToolCall> _toolCalls = new();

    public event Action<AgentSessionState>? StateChanged;
    public event Action<IReadOnlyList<AgentPlanEntry>>? PlanUpdated;
    public event Action<AgentToolCall>? ToolCallUpdated;
    public event Action<AgentUsageUpdate>? UsageUpdated;
    public event Action<string>? ErrorOccurred;
    public event Action<AgentEvent>? EventReceived;

    public AgentSessionState State { get; private set; } = AgentSessionState.Idle;
    public string AgentName { get; private set; } = "";
    public string AgentType { get; private set; } = "";
    public string SessionId { get; private set; } = "";
    public IReadOnlyList<AgentPlanEntry> PlanEntries => _planEntries;
    public IReadOnlyList<AgentToolCall> ToolCalls => _toolCalls;
    public bool IsActive => State is AgentSessionState.Running or AgentSessionState.Connected;

    public void Register(ICallbackServer server, string routePath)
    {
        _server = server;
        _routeHandler = OnEvent;
        server.RegisterRoute(routePath, _routeHandler);
    }

    public void Unregister()
    {
        if (_server != null && _routeHandler != null)
        {
            _server.UnregisterRoute("/agent/event", _routeHandler);
            _server = null;
            _routeHandler = null;
        }
    }

    private async Task OnEvent(JsonElement json)
    {
        try
        {
            var eventType = json.TryGetProperty("type", out var te) ? te.GetString() ?? "" : "";
            var agentType = json.TryGetProperty("agent", out var at) ? at.GetString() ?? "" : "";
            var sessionId = json.TryGetProperty("sessionId", out var si) ? si.GetString() ?? "" : "";

            if (!string.IsNullOrEmpty(agentType) && AgentType != agentType)
                AgentType = agentType;

            if (!string.IsNullOrEmpty(sessionId))
                SessionId = sessionId;

            var agentEvent = new AgentEvent
            {
                Type = eventType,
                Agent = agentType,
                SessionId = sessionId,
                Timestamp = json.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = json,
            };

            EventReceived?.Invoke(agentEvent);

            if (string.IsNullOrEmpty(eventType) && json.TryGetProperty("hook_event_name", out var hen))
            {
                eventType = hen.GetString() ?? "";
                agentEvent = new AgentEvent
                {
                    Type = eventType,
                    Agent = agentType,
                    SessionId = sessionId,
                    Timestamp = json.TryGetProperty("timestamp", out var ts2) ? ts2.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Data = json,
                };
                EventReceived?.Invoke(agentEvent);
            }

            switch (eventType)
            {
                case "session.start":
                case "SessionStart":
                    HandleSessionStart(json);
                    break;
                case "prompt.submit":
                case "user_prompt":
                case "UserPromptSubmit":
                    HandlePromptSubmit(json);
                    break;
                case "session.end":
                case "stop":
                case "Stop":
                    HandleSessionEnd(json);
                    break;
                case "session.idle":
                    HandleSessionIdle(json);
                    break;
                case "tool.start":
                case "PreToolUse":
                    HandleToolStart(json);
                    break;
                case "tool.end":
                case "PostToolUse":
                    HandleToolEnd(json);
                    break;
                case "plan.update":
                    HandlePlanUpdate(json);
                    break;
                case "usage.update":
                    HandleUsageUpdate(json);
                    break;
                case "message.chunk":
                    break;
                case "error":
                    var errMsg = json.TryGetProperty("message", out var em) ? em.GetString() ?? "unknown" : "unknown";
                    ErrorOccurred?.Invoke(errMsg);
                    break;
            }
        }
        catch { }

        await Task.CompletedTask;
    }

    private void HandleSessionStart(JsonElement json)
    {
        _planEntries.Clear();
        _toolCalls.Clear();
        PlanUpdated?.Invoke(_planEntries);

        if (json.TryGetProperty("agentName", out var an))
            AgentName = an.GetString() ?? AgentType;
    }

    private void HandlePromptSubmit(JsonElement json)
    {
        _planEntries.Clear();
        _toolCalls.Clear();
        PlanUpdated?.Invoke(_planEntries);

        if (json.TryGetProperty("agentName", out var an))
            AgentName = an.GetString() ?? AgentType;

        SetState(AgentSessionState.Running);
    }

    private void HandleSessionEnd(JsonElement json)
    {
        if (State == AgentSessionState.Running)
            SetState(AgentSessionState.Connected);
        else
            SetState(AgentSessionState.Idle);
    }

    private void HandleSessionIdle(JsonElement json)
    {
        if (State == AgentSessionState.Running)
            SetState(AgentSessionState.Connected);
    }

    private void HandleToolStart(JsonElement json)
    {
        var toolCallId = json.TryGetProperty("toolCallId", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N")[..8] : Guid.NewGuid().ToString("N")[..8];

        string toolName = "";
        if (json.TryGetProperty("toolName", out var tn))
            toolName = tn.GetString() ?? "";
        else if (json.TryGetProperty("tool_name", out var tn2))
            toolName = tn2.GetString() ?? "";

        var toolCall = new AgentToolCall
        {
            ToolCallId = toolCallId,
            ToolName = toolName,
            Status = AgentToolCallStatus.InProgress,
        };

        if (json.TryGetProperty("title", out var title))
            toolCall.Title = title.GetString() ?? toolCall.ToolName;
        else
            toolCall.Title = toolCall.ToolName;

        var existing = _toolCalls.FindIndex(tc => tc.ToolCallId == toolCall.ToolCallId);
        if (existing >= 0)
            _toolCalls[existing] = toolCall;
        else
            _toolCalls.Add(toolCall);

        SetState(AgentSessionState.Running);
        ToolCallUpdated?.Invoke(toolCall);
    }

    private void HandleToolEnd(JsonElement json)
    {
        var toolCallId = json.TryGetProperty("toolCallId", out var id) ? id.GetString() ?? "" : "";
        var idx = _toolCalls.FindIndex(tc => tc.ToolCallId == toolCallId);
        if (idx >= 0)
        {
            var tc = _toolCalls[idx];
            tc.Status = AgentToolCallStatus.Completed;
            if (json.TryGetProperty("result", out var result))
                tc.Result = result.GetString() ?? "";
            _toolCalls[idx] = tc;
            ToolCallUpdated?.Invoke(tc);
        }
    }

    private void HandlePlanUpdate(JsonElement json)
    {
        if (!json.TryGetProperty("entries", out var entries)) return;

        _planEntries.Clear();
        foreach (var entry in entries.EnumerateArray())
        {
            _planEntries.Add(new AgentPlanEntry
            {
                Content = entry.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                Status = entry.TryGetProperty("status", out var s) ? ParsePlanStatus(s.GetString()) : AgentPlanEntryStatus.Pending,
            });
        }
        PlanUpdated?.Invoke(_planEntries);
    }

    private void HandleUsageUpdate(JsonElement json)
    {
        var usage = new AgentUsageUpdate
        {
            UsedTokens = json.TryGetProperty("usedTokens", out var ut) ? ut.GetInt64() : 0,
            CostAmount = json.TryGetProperty("costAmount", out var ca) ? ca.GetDouble() : 0,
            CostCurrency = json.TryGetProperty("costCurrency", out var cc) ? cc.GetString() ?? "USD" : "USD",
        };
        UsageUpdated?.Invoke(usage);
    }

    private void SetState(AgentSessionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Reset()
    {
        _planEntries.Clear();
        _toolCalls.Clear();
        AgentName = "";
        AgentType = "";
        SessionId = "";
        SetState(AgentSessionState.Idle);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Unregister();
        return ValueTask.CompletedTask;
    }

    private static AgentPlanEntryStatus ParsePlanStatus(string? s) => s switch
    {
        "in_progress" or "running" => AgentPlanEntryStatus.InProgress,
        "completed" or "done" => AgentPlanEntryStatus.Completed,
        _ => AgentPlanEntryStatus.Pending
    };
}

public enum AgentSessionState
{
    Idle,
    Running,
    Connected,
}

public sealed class AgentPlanEntry
{
    public string Content { get; init; } = "";
    public AgentPlanEntryStatus Status { get; init; }
}

public enum AgentPlanEntryStatus
{
    Pending,
    InProgress,
    Completed
}

public sealed class AgentToolCall
{
    public string ToolCallId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string Title { get; set; } = "";
    public AgentToolCallStatus Status { get; set; }
    public string Result { get; set; } = "";
}

public enum AgentToolCallStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled
}

public sealed class AgentUsageUpdate
{
    public long UsedTokens { get; set; }
    public double CostAmount { get; set; }
    public string CostCurrency { get; set; } = "USD";
}

public sealed class AgentEvent
{
    public string Type { get; init; } = "";
    public string Agent { get; init; } = "";
    public string SessionId { get; init; } = "";
    public long Timestamp { get; init; }
    public JsonElement Data { get; init; }
}
