using System.Text.Json;

namespace AgentHub.Core;

public sealed record UsageEvent(string Id, string Agent, string SessionId, string Model,
    DateTimeOffset Time, long Input, long Output, long Cache, long Reasoning = 0);
public sealed record Quota(string Agent, string Name, double UsedPercent, DateTimeOffset? Reset,
    DateTimeOffset ReadAt, string? Account = null);
public sealed record Session(string Agent, string Id, string Title, string Cwd, string State,
    DateTimeOffset Updated, string? Parent = null, int ProcessId = 0);
public sealed record Snapshot(Session[] Sessions, UsageEvent[] Events, Quota[] Quotas, string[] Notices)
{
    public static Snapshot Empty => new([], [], [], []);
}
public static class Json
{
    public static JsonElement At(this JsonElement value, params string[] keys)
    {
        foreach (var key in keys)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return default;
        return value;
    }
    public static string Text(this JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public static long Number(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
    public static double? Decimal(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    public static IEnumerable<JsonElement> Items(this JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    public static DateTimeOffset? Date(this JsonElement value) => DateTimeOffset.TryParse(value.Text(), out var time) ? time : null;
    public static string Title(string text) => text.Split('\n')[0].Trim() is { } s ? s[..Math.Min(60, s.Length)] : "";
}
