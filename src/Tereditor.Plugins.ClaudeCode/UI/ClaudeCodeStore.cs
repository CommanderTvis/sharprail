namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>What the client knows of one tab's Claude session, folded from the status pushes.</summary>
internal sealed record ClaudeSessionState(ClaudeCodeStatus Status)
{
    public string? Summary { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? Cwd { get; init; }
    public IReadOnlyList<AgentTodoItem>? Todos { get; init; }
    public AgentTokenUsage? Usage { get; init; }
}

/// <summary>
/// The one client-side fold of the status channel every consumer (the tab adornment, the terminal chips) reads, so they
/// agree. Model, effort and the plan ride the events that carry them and are absent from the rest, so the last reported
/// answer stands until a newer one arrives rather than blinking out between turns.
/// </summary>
internal sealed class ClaudeCodeStore
{
    private readonly Dictionary<string, Dictionary<string, ClaudeSessionState>> byWorkspace = [];
    private readonly Dictionary<(string, string), bool> ideContext = [];

    /// <summary>Raised on the UI thread with the workspace and tab whose session changed.</summary>
    public event Action<string, string>? Changed;

    public ClaudeSessionState? Session(string workspaceId, string tabKey) => byWorkspace.GetValueOrDefault(workspaceId)?.GetValueOrDefault(tabKey);

    public void Apply(ClaudeCodeStatusPush push)
    {
        var previous = Session(push.WorkspaceId, push.TabKey);
        // A facts-only push (a model switch) carries no status, so the badge keeps what it had.
        if ((push.Status ?? previous?.Status) is not { } settled) return;
        var report = push.Report;
        var next = new ClaudeSessionState(settled)
        {
            Summary = report.Summary is { Length: > 0 } summary ? summary : null,
            Model = Present(report.Model ?? previous?.Model),
            Effort = Present(report.Effort ?? previous?.Effort),
            Cwd = Present(report.Cwd ?? previous?.Cwd),
            Todos = report.Todos ?? previous?.Todos,
            Usage = push.Usage ?? previous?.Usage
        };
        if (!byWorkspace.TryGetValue(push.WorkspaceId, out var tabs)) byWorkspace[push.WorkspaceId] = tabs = [];
        tabs[push.TabKey] = next;
        Changed?.Invoke(push.WorkspaceId, push.TabKey);
    }

    // An empty report field says nothing, as the hook writes a field it has no value for.
    private static string? Present(string? value) => value is { Length: > 0 } ? value : null;

    /// <summary>Whether this terminal's session receives SharpRail's editor context; null until first known.</summary>
    public bool? IdeContext(string workspaceId, string tabKey) => ideContext.TryGetValue((workspaceId, tabKey), out var enabled) ? enabled : null;

    public void SetIdeContext(string workspaceId, string tabKey, bool enabled)
    {
        ideContext[(workspaceId, tabKey)] = enabled;
        Changed?.Invoke(workspaceId, tabKey);
    }

    public void EvictWorkspace(string workspaceId)
    {
        if (!byWorkspace.Remove(workspaceId)) return;
        foreach (var key in ideContext.Keys.Where(key => key.Item1 == workspaceId).ToArray()) ideContext.Remove(key);
    }
}