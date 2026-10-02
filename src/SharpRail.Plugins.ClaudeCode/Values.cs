namespace SharpRail.Plugins.ClaudeCode;

/// <summary>The values both halves agree on: scopes, their wording, the hook events offered, and event → status.</summary>
public static class ClaudeValues
{
    /// <summary>Scopes an edit may target. <c>managed</c> is deliberately absent: it belongs to whoever deploys it.</summary>
    public static readonly IReadOnlyList<ClaudeWritableScope> WritableScopes = [ClaudeWritableScope.User, ClaudeWritableScope.Project, ClaudeWritableScope.Local];

    /// <summary>Plain language, because naming the file is what the tool this replaces never does.</summary>
    public static string ScopeWording(ClaudeWritableScope scope) => scope switch
    {
        ClaudeWritableScope.User => "you, in every project on this machine",
        ClaudeWritableScope.Project => "everyone who works on this project (checked into git)",
        _ => "you, in this project only (usually gitignored)"
    };

    /// <summary>Claude Code's own installer wording for plugin scopes, kept verbatim so both UIs say one thing.</summary>
    public static string PluginScopeWording(ClaudeWritableScope scope) => scope switch
    {
        ClaudeWritableScope.User => "for you",
        ClaudeWritableScope.Project => "for all collaborators on this repository",
        _ => "for you, in this repo only"
    };

    public static string Name(this ClaudeWritableScope scope) => scope.ToString().ToLowerInvariant();

    public static string Name(this ClaudeConfigScope scope) => scope.ToString().ToLowerInvariant();

    /// <summary>A template names one file, so it names one scope; offering three that write the same path is theatre.</summary>
    public static ClaudeWritableScope TemplateScope(ClaudeFileTemplate template) =>
        template == ClaudeFileTemplate.ProjectInstructions ? ClaudeWritableScope.Project : ClaudeWritableScope.Local;

    /// <summary>A skill is a directory Claude Code looks for in two places; there is no third, private one.</summary>
    public static readonly IReadOnlyList<ClaudeWritableScope> SkillScopes = [ClaudeWritableScope.User, ClaudeWritableScope.Project];

    /// <summary>The scopes an edit can honestly land in. The pane offers these and the host refuses anything else.</summary>
    public static IReadOnlyList<ClaudeWritableScope> EditScopes(ClaudeEdit edit) => edit switch
    {
        FileEdit file => [TemplateScope(file.Template)],
        SkillCreateEdit => SkillScopes,
        _ => WritableScopes
    };

    /// <summary>The hook events worth offering in a form. An unlisted one still resolves and still shows.</summary>
    public static readonly IReadOnlyList<ClaudeHookEvent> HookEvents = Enum.GetValues<ClaudeHookEvent>();

    // A facts-only event says what the session is running on without saying what it is doing.
    private static readonly Dictionary<string, ClaudeCodeStatus?> StatusByEvent = new()
    {
        ["session_start"] = ClaudeCodeStatus.Idle,
        ["prompt_submit"] = ClaudeCodeStatus.Running,
        ["tool_complete"] = ClaudeCodeStatus.Running,
        ["permission_request"] = ClaudeCodeStatus.Blocked,
        ["stop"] = ClaudeCodeStatus.Done,
        ["stop_failure"] = ClaudeCodeStatus.Failed,
        ["interrupted"] = ClaudeCodeStatus.Idle,
        ["model_switch"] = null
    };

    /// <summary>Null for an event this version does not know, or one that only carries facts.</summary>
    public static ClaudeCodeStatus? StatusForAgentEvent(string @event) => StatusByEvent.GetValueOrDefault(@event);

    /// <summary>Whether the event means anything here at all; a facts-only one does, without moving the badge.</summary>
    public static bool AgentEventKnown(string @event) => StatusByEvent.ContainsKey(@event);
}