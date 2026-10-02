using System.Text.Json;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>One Codex hook's stdin, as the hook command POSTs it verbatim.</summary>
public sealed record CodexHookReport(string Event, CodexStatus Status)
{
    public string? SessionId { get; init; }
    public string? Model { get; init; }
    public string? Cwd { get; init; }
    public string? TranscriptPath { get; init; }
}

public static class CodexStatusReports
{
    private static readonly Dictionary<string, CodexStatus> StatusOfEvent = new()
    {
        ["SessionStart"] = CodexStatus.Idle,
        ["UserPromptSubmit"] = CodexStatus.Running,
        ["PostToolUse"] = CodexStatus.Running,
        ["PermissionRequest"] = CodexStatus.Blocked,
        ["Stop"] = CodexStatus.Done,
        ["Interrupt"] = CodexStatus.Idle
    };

    private static string? Text(JsonElement fields, string key) =>
        fields.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>The report's event and status with the facts it carries; null for an unreadable payload or an event without a status.</summary>
    public static CodexHookReport? Parse(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } fields) return null;
        if (Text(fields, "hook_event_name") is not { } name || !StatusOfEvent.TryGetValue(name, out var status)) return null;
        return new(name, status)
        {
            SessionId = Text(fields, "session_id"),
            Model = Text(fields, "model"),
            Cwd = Text(fields, "cwd"),
            TranscriptPath = Text(fields, "transcript_path")
        };
    }
}

/// <summary>The last status push per terminal, per workspace, for the snapshot a UI half hydrates from.</summary>
public sealed class CodexStatusStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, CodexStatusPush>> byWorkspace = [];

    public void Record(CodexStatusPush push)
    {
        lock (gate)
        {
            if (!byWorkspace.TryGetValue(push.WorkspaceId, out var tabs)) byWorkspace[push.WorkspaceId] = tabs = [];
            // A report without a CWD keeps the tab's last one, which scopes its configuration.
            tabs[push.TabKey] = push.Cwd is null && tabs.GetValueOrDefault(push.TabKey)?.Cwd is { } cwd ? push with { Cwd = cwd } : push;
        }
    }

    public IReadOnlyList<CodexStatusPush> Snapshot(string workspaceId)
    {
        lock (gate) return byWorkspace.TryGetValue(workspaceId, out var tabs) ? [.. tabs.Values] : [];
    }

    public void Forget(string workspaceId, string tabKey)
    {
        lock (gate) byWorkspace.GetValueOrDefault(workspaceId)?.Remove(tabKey);
    }
}