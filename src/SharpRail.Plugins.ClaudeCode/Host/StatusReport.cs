using System.Text.Json;

namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>What a posted report means: a status (null for a report that only carries facts) and the report itself.</summary>
internal sealed record StatusDelivery(ClaudeCodeStatus? Status, AgentStatusReport Report);

/// <summary>
/// Decides what a body the hook plugin posted means, or refuses it. Pure: the token to terminal resolution happens in the
/// route before this is called. The hook adds fields of its own (<c>v</c>, <c>agent</c>, a plugin version), so the body
/// is read leniently, field by field, rather than as a strict contract.
/// </summary>
internal static class StatusReports
{
    public static StatusDelivery? Parse(ReadOnlySpan<byte> body)
    {
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(body); }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object || String(root, "event") is not { } @event) return null;
        // An event this version does not know moves nothing; a newer hook must not shift a badge by accident.
        if (!ClaudeValues.AgentEventKnown(@event)) return null;
        var report = new AgentStatusReport(@event)
        {
            SessionId = String(root, "session_id"),
            Cwd = String(root, "cwd"),
            Project = String(root, "project"),
            TranscriptPath = String(root, "transcript_path"),
            Summary = String(root, "summary"),
            Query = String(root, "query"),
            Response = String(root, "response"),
            ToolName = String(root, "tool_name"),
            ErrorType = String(root, "error_type"),
            Model = String(root, "model"),
            Effort = String(root, "effort"),
            Notify = root.TryGetProperty("notify", out var notify) && notify.ValueKind is JsonValueKind.True or JsonValueKind.False ? notify.GetBoolean() : null,
            Todos = root.TryGetProperty("todos", out var todos) ? ParseTodos(todos) : null
        };
        return new(ClaudeValues.StatusForAgentEvent(@event), report);
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The plan items a renderer can trust, from a payload the hook wrote; null when it is not a list.</summary>
    public static IReadOnlyList<AgentTodoItem>? ParseTodos(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        var items = new List<AgentTodoItem>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || String(entry, "content") is not { } content || String(entry, "status") is not { } status) continue;
            AgentTodoStatus? parsed = status switch
            {
                "pending" => AgentTodoStatus.Pending,
                "in_progress" => AgentTodoStatus.InProgress,
                "completed" => AgentTodoStatus.Completed,
                _ => null
            };
            if (parsed is null) continue;
            items.Add(new(content, parsed.Value) { ActiveForm = String(entry, "activeForm") });
        }
        return items;
    }
}

/// <summary>
/// The last push seen per tab, so the status snapshot can answer a client that mounts or reconnects mid-session; the
/// channel itself never replays. Ephemeral: a revived terminal's agent re-reports on its own.
/// </summary>
internal sealed class StatusStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, ClaudeCodeStatusPush>> byWorkspace = [];

    public void Record(ClaudeCodeStatusPush push)
    {
        lock (gate)
        {
            if (!byWorkspace.TryGetValue(push.WorkspaceId, out var tabs)) byWorkspace[push.WorkspaceId] = tabs = [];
            tabs[push.TabKey] = push;
        }
    }

    public IReadOnlyList<ClaudeCodeStatusPush> Snapshot(string workspaceId)
    {
        lock (gate) return byWorkspace.TryGetValue(workspaceId, out var tabs) ? [.. tabs.Values] : [];
    }

    public void Forget(string workspaceId, string tabKey)
    {
        lock (gate) byWorkspace.GetValueOrDefault(workspaceId)?.Remove(tabKey);
    }
}