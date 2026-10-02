namespace SharpRail.Plugins.Codex.UI;

/// <summary>What one Codex terminal last reported; totals and the plan survive a push that carries neither.</summary>
public sealed record CodexSessionState(CodexStatus Status)
{
    public string? Model { get; init; }
    public string? Cwd { get; init; }
    public CodexTokenUsage? Usage { get; init; }
    public IReadOnlyList<CodexPlanItem>? Plan { get; init; }
}

/// <summary>The UI half's state: each terminal's status, its session-only IDE-context choice, and the model catalog.</summary>
public sealed class CodexStore
{
    private readonly Dictionary<(string Workspace, string Tab), CodexSessionState> sessions = [];
    private readonly Dictionary<(string Workspace, string Tab), bool> ideContext = [];

    public IReadOnlyList<CodexModel> Models { get; private set; } = [];

    /// <summary>Raised on the UI thread after any change.</summary>
    public event Action? Changed;

    public CodexSessionState? Session(string workspaceId, string tabKey) => sessions.GetValueOrDefault((workspaceId, tabKey));

    public void Apply(CodexStatusPush push)
    {
        var previous = Session(push.WorkspaceId, push.TabKey);
        sessions[(push.WorkspaceId, push.TabKey)] = new(push.Status)
        {
            Model = push.Model ?? previous?.Model,
            Cwd = push.Cwd ?? previous?.Cwd,
            Usage = push.Usage ?? previous?.Usage,
            Plan = push.Plan ?? previous?.Plan
        };
        Changed?.Invoke();
    }

    public bool? IdeContext(string workspaceId, string tabKey) => ideContext.TryGetValue((workspaceId, tabKey), out var enabled) ? enabled : null;

    public void SetIdeContext(string workspaceId, string tabKey, bool enabled)
    {
        ideContext[(workspaceId, tabKey)] = enabled;
        Changed?.Invoke();
    }

    public void SetModels(IReadOnlyList<CodexModel> models)
    {
        Models = models;
        Changed?.Invoke();
    }

    public void EvictWorkspace(string workspaceId)
    {
        var removed = sessions.Keys.Where(key => key.Workspace == workspaceId).ToArray();
        foreach (var key in removed) sessions.Remove(key);
        foreach (var key in ideContext.Keys.Where(key => key.Workspace == workspaceId).ToArray()) ideContext.Remove(key);
        if (removed.Length > 0) Changed?.Invoke();
    }
}