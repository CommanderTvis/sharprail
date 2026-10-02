using System.Text.Json;
using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.ClaudeCode;

// The Claude Code, IDE-bridge and agent-status domain, as records the wire contract below names.

public enum ClaudeConfigScope { Managed, Local, Project, User, Default }

public enum ClaudeWritableScope { User, Project, Local }

public sealed record ClaudeConfigOrigin(ClaudeConfigScope Scope, string? Path = null)
{
    /// <summary>Where inside the file the value is declared, as JSON object keys; null when the file is the value.</summary>
    public IReadOnlyList<string>? KeyPath { get; init; }
}

public sealed record ClaudeShadowedValue(JsonElement Value, ClaudeConfigOrigin Origin);

public sealed record ClaudeSettingValue(string Key, JsonElement Value, ClaudeConfigOrigin Origin, IReadOnlyList<ClaudeShadowedValue> Shadowed)
{
    /// <summary>The key's entry in Claude Code's settings reference; null for a key that reference does not list.</summary>
    public string? DocsUrl { get; init; }
}

public enum ClaudeContextKind
{
    Instructions,
    [JsonStringEnumMemberName("local-instructions")] LocalInstructions,
    Rules,
    Memory,
    Import,
    [JsonStringEnumMemberName("system-prompt")] SystemPrompt
}

public sealed record ClaudeContextLayer(ClaudeContextKind Kind, string Label, string Path, ClaudeConfigOrigin Origin, long Bytes)
{
    public IReadOnlyList<string>? PathGlobs { get; init; }
    public bool? Lazy { get; init; }
    /// <summary>Nesting under the file that <c>@</c>-imported this one; null for a layer loaded in its own right.</summary>
    public int? Depth { get; init; }
}

public enum ClaudeCapabilityKind { Mcp, Plugin, Skill, Agent, Hook, Marketplace }

public sealed record ClaudeCapability(ClaudeCapabilityKind Kind, string Name, ClaudeConfigOrigin Origin, bool Enabled)
{
    public string? Detail { get; init; }
    /// <summary>The setting that switched this off, when something did.</summary>
    public ClaudeConfigOrigin? DisabledBy { get; init; }
}

public enum ClaudeUsageSeverity { Normal, Warning, Critical }

public sealed record ClaudeUsageWindow(string Id, string Label, int Percent, ClaudeUsageSeverity Severity)
{
    public string? ResetsAt { get; init; }
}

public sealed record ClaudeAccount(bool LoggedIn, IReadOnlyList<ClaudeUsageWindow> Usage)
{
    public string? Version { get; init; }
    public string? Email { get; init; }
    public string? Organization { get; init; }
    public string? Subscription { get; init; }
    public string? AuthMethod { get; init; }
    /// <summary>When Claude Code last refreshed the usage it caches; null when it never has.</summary>
    public string? UsageFetchedAt { get; init; }
}

public enum ClaudeProblemSeverity { Warning, Info }

public sealed record ClaudeConfigProblem(ClaudeProblemSeverity Severity, string Title, string Detail)
{
    public string? Path { get; init; }
}

public sealed record ClaudeInspectedFile(string Path, ClaudeConfigScope Scope, bool Exists);

public sealed record ClaudeConfigSnapshot(
    string WorkspaceId,
    string Root,
    IReadOnlyList<ClaudeContextLayer> Context,
    IReadOnlyList<ClaudeSettingValue> Settings,
    IReadOnlyList<ClaudeCapability> Capabilities,
    IReadOnlyList<ClaudeConfigProblem> Problems,
    IReadOnlyList<ClaudeInspectedFile> Inspected,
    // Every key Claude Code documents, so a key can be added without already appearing in a file.
    IReadOnlyList<string> KnownSettingKeys);

public enum HookPluginState { Enabled, Outdated, Absent, Unknown }

/// <summary>Whether SharpRail's own Claude Code hook plugin is registered and current in Claude's cache.</summary>
public sealed record HookPluginStatus(HookPluginState State, string AvailableVersion, string? InstalledVersion = null, string? PendingChange = null)
{
    /// <summary>Why the last install or update stopped short, in Claude Code's own words.</summary>
    public string? Problem { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AddMarketplace), "add")]
[JsonDerivedType(typeof(RemoveMarketplace), "remove")]
[JsonDerivedType(typeof(UpdateMarketplace), "update")]
public abstract record ClaudeMarketplaceAction;
public sealed record AddMarketplace(string Source, ClaudeWritableScope Scope) : ClaudeMarketplaceAction;
public sealed record RemoveMarketplace(string Name, ClaudeWritableScope Scope) : ClaudeMarketplaceAction;
public sealed record UpdateMarketplace(string Name) : ClaudeMarketplaceAction;

public enum ClaudeFileTemplate
{
    [JsonStringEnumMemberName("project-local-instructions")] ProjectLocalInstructions,
    [JsonStringEnumMemberName("project-instructions")] ProjectInstructions
}

public enum ClaudeMcpTransport { Stdio, Http, Sse }

public sealed record ClaudeMcpServerDraft(ClaudeMcpTransport Transport)
{
    public string? Command { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public enum ClaudeHookEvent
{
    [JsonStringEnumMemberName("PreToolUse")] PreToolUse,
    [JsonStringEnumMemberName("PostToolUse")] PostToolUse,
    [JsonStringEnumMemberName("UserPromptSubmit")] UserPromptSubmit,
    [JsonStringEnumMemberName("Notification")] Notification,
    [JsonStringEnumMemberName("Stop")] Stop,
    [JsonStringEnumMemberName("SubagentStop")] SubagentStop,
    [JsonStringEnumMemberName("SessionStart")] SessionStart,
    [JsonStringEnumMemberName("SessionEnd")] SessionEnd,
    [JsonStringEnumMemberName("PreCompact")] PreCompact
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GithubMarketplaceSource), "github")]
[JsonDerivedType(typeof(DirectoryMarketplaceSource), "directory")]
public abstract record ClaudeMarketplaceSource;
public sealed record GithubMarketplaceSource(string Repo) : ClaudeMarketplaceSource;
public sealed record DirectoryMarketplaceSource(string Path) : ClaudeMarketplaceSource;

/// <summary>One change the pane can propose; every one is shown as a diff and approved before it is written.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SettingEdit), "setting")]
[JsonDerivedType(typeof(McpEdit), "mcp")]
[JsonDerivedType(typeof(McpAddEdit), "mcp-add")]
[JsonDerivedType(typeof(PluginEdit), "plugin")]
[JsonDerivedType(typeof(PluginAddEdit), "plugin-add")]
[JsonDerivedType(typeof(HookEdit), "hook")]
[JsonDerivedType(typeof(SkillCreateEdit), "skill-create")]
[JsonDerivedType(typeof(SkillEdit), "skill")]
[JsonDerivedType(typeof(FileEdit), "file")]
public abstract record ClaudeEdit;
/// <summary>Sets a dotted key; a null value removes it. A value is a boolean, number, string or list of strings.</summary>
public sealed record SettingEdit(string Key, JsonElement? Value = null) : ClaudeEdit;
public sealed record McpEdit(string Server, bool Allowed) : ClaudeEdit;
public sealed record McpAddEdit(string Server, ClaudeMcpServerDraft Draft) : ClaudeEdit;
public sealed record PluginEdit(string Name, bool Enabled) : ClaudeEdit;
public sealed record PluginAddEdit(string Marketplace, ClaudeMarketplaceSource Source, string Plugin) : ClaudeEdit;
public sealed record HookEdit(ClaudeHookEvent Event, string Matcher, string Command) : ClaudeEdit;
public sealed record SkillCreateEdit(string Name, string Description) : ClaudeEdit;
public sealed record SkillEdit(string Name, bool Enabled) : ClaudeEdit;
public sealed record FileEdit(ClaudeFileTemplate Template) : ClaudeEdit;

public sealed record ClaudeEditRequest(string WorkspaceId, ClaudeWritableScope Scope, ClaudeEdit Edit)
{
    public string? TabKey { get; init; }
}

public sealed record ClaudeApplyRequest(string WorkspaceId, ClaudeWritableScope Scope, ClaudeEdit Edit, string BaseHash)
{
    public string? TabKey { get; init; }
}

public enum ClaudeDiffKind { Context, Add, Remove, Gap }

/// <summary>A diff line; a gap is elided unchanged text, carrying how many lines it stands for.</summary>
public sealed record ClaudeDiffLine(ClaudeDiffKind Kind, string Text);

public sealed record ClaudeEditPlan(
    string Path,
    bool Exists,
    // One sentence naming both the change and who it affects.
    string Summary,
    IReadOnlyList<ClaudeDiffLine> Diff,
    // Reasons the write may not do what the user expects; never a reason to refuse it.
    IReadOnlyList<string> Warnings,
    // The content this plan was built from, so applying can refuse if the file moved underneath it.
    string BaseHash,
    // False when the edit would leave the file exactly as it is.
    bool Changes);

public sealed record FileContent(string Content, string Hash);

/// <summary>A compare-and-swap write: written with the new hash, or refused with what is on disk now.</summary>
public sealed record FileWriteResult(bool Written)
{
    public string? Hash { get; init; }
    public FileContent? Disk { get; init; }
}

public sealed record IdeSelection(int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>Pushed by the client on every selection or active-file change while the plugin is active.</summary>
public sealed record IdeSelectionChanged(string WorkspaceId, string Path, string Text, IdeSelection Selection);

/// <summary>Pushed by the client when a tab holding one of these paths closes.</summary>
public sealed record IdeDocumentClosed(string WorkspaceId, string Path);

public enum IdeActionKind { OpenFile, OpenDiff, GetOpenEditors, CheckDocumentDirty, SaveDocument, CloseTab, CloseAllDiffTabs }

/// <summary>The union of every IDE action's parameters; each action reads its own.</summary>
public sealed record IdeActionParams
{
    public string? Path { get; init; }
    public bool? Preview { get; init; }
    public string? StartText { get; init; }
    public string? EndText { get; init; }
    public string? OldPath { get; init; }
    public string? NewPath { get; init; }
    public string? NewContent { get; init; }
    public string? TabName { get; init; }
}

/// <summary>A host-initiated action, pushed to the client that owns the terminal; exactly one reply is expected.</summary>
public sealed record IdeActionRequest(string Id, string WorkspaceId, IdeActionKind Kind, IdeActionParams Params);

public sealed record IdeOpenEditorInfo(string Path, bool IsDirty);

public sealed record IdeActionResult(bool Ok)
{
    public JsonElement? Value { get; init; }
    public string? Error { get; init; }
}

public sealed record IdeActionReply(string Id, IdeActionResult Result);

public enum ClaudeCodeStatus { Idle, Running, Blocked, Done, Failed }

public enum AgentTodoStatus { Pending, [JsonStringEnumMemberName("in_progress")] InProgress, Completed }

/// <summary>One item of the agent's own plan: Claude Code's TodoWrite list, relayed as it was written.</summary>
public sealed record AgentTodoItem(string Content, AgentTodoStatus Status)
{
    public string? ActiveForm { get; init; }
}

/// <summary>What the hook plugin posts for one Claude Code lifecycle event.</summary>
public sealed record AgentStatusReport(string Event)
{
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    public string? Cwd { get; init; }
    public string? Project { get; init; }
    /// <summary>Where Claude Code itself says the session's transcript is, honouring its own CLAUDE_CONFIG_DIR.</summary>
    [JsonPropertyName("transcript_path")] public string? TranscriptPath { get; init; }
    public string? Summary { get; init; }
    public string? Query { get; init; }
    public string? Response { get; init; }
    [JsonPropertyName("tool_name")] public string? ToolName { get; init; }
    [JsonPropertyName("error_type")] public string? ErrorType { get; init; }
    /// <summary>What the session is running on right now; both can change mid-chat.</summary>
    public string? Model { get; init; }
    public string? Effort { get; init; }
    /// <summary>False when the event settles status only: a continuation's Stop, which must not notify twice.</summary>
    public bool? Notify { get; init; }
    /// <summary>The agent's whole current plan; present only on a report that rewrote it.</summary>
    public IReadOnlyList<AgentTodoItem>? Todos { get; init; }
}

public sealed record AgentTokenUsage(long Input, long Output, long CacheRead, long CacheWrite);

public sealed record ClaudeCodeStatusPush(string WorkspaceId, string TabKey, AgentStatusReport Report, ClaudeCodeStatus? Status = null)
{
    /// <summary>Read by the host from the session's own transcript, never sent by the hook.</summary>
    public AgentTokenUsage? Usage { get; init; }
}

/// <summary>The plugin's settings namespace. Every member has a default, so a bare enable validates.</summary>
public sealed record ClaudeCodeSettings
{
    public string Command { get; init; } = "claude";
    public bool DisableAgentView { get; init; } = true;
    public bool AppendSystemPrompt { get; init; } = true;
}

public sealed record WorkspaceParams(string WorkspaceId)
{
    public string? TabKey { get; init; }
}

public sealed record AccountParams
{
    public bool? Refresh { get; init; }
}

public sealed record NoParams;

public sealed record PluginUninstallPlanParams(string WorkspaceId, string Name, ClaudeWritableScope Scope);

public sealed record PluginUninstallParams(string WorkspaceId, string Name, ClaudeWritableScope Scope)
{
    public string? TabKey { get; init; }
}

public sealed record PluginMovePlanParams(string WorkspaceId, string Name, ClaudeWritableScope From, ClaudeWritableScope To);

public sealed record PluginMoveParams(string WorkspaceId, string Name, ClaudeWritableScope From, ClaudeWritableScope To)
{
    public string? TabKey { get; init; }
}

public sealed record MarketplacePlanParams(string WorkspaceId, ClaudeMarketplaceAction Action);

public sealed record MarketplaceRunParams(string WorkspaceId, ClaudeMarketplaceAction Action)
{
    public string? TabKey { get; init; }
}

public sealed record ReadFileParams(string WorkspaceId, string Path)
{
    public string? TabKey { get; init; }
}

public sealed record WriteFileParams(string WorkspaceId, string Path, string Content, string BaseHash)
{
    public string? TabKey { get; init; }
}

public sealed record StatusSnapshotParams(string WorkspaceId);

public sealed record CommandPlan(IReadOnlyList<string> Command);

public sealed record CommandsPlan(IReadOnlyList<IReadOnlyList<string>> Commands);

public sealed record CommandOutput(string Output);

public sealed record McpListResult(IReadOnlyList<ClaudeCapability> Capabilities);

public sealed record Ack(bool Ok = true);

/// <summary>The plugin's wire contract: its methods under <c>plugin.claude-code.*</c> and its two channels.</summary>
public static class ClaudeCodeContract
{
    public const string Id = "claude-code";

    /// <summary>Names the host's system prompt file in every terminal; the launch line refers to it.</summary>
    public const string PromptFileVariable = "SHARPRAIL_CLAUDE_PROMPT_FILE";

    /// <summary>Names the folder SharpRail creates this project's worktrees in, in every terminal.</summary>
    public const string WorktreesVariable = "SHARPRAIL_WORKTREES_DIR";

    /// <summary>Where the hook plugin posts its reports: this plugin's status route, with the terminal's token.</summary>
    public const string StatusUrlVariable = "SHARPRAIL_AGENT_STATUS_URL";

    public static readonly PluginMethod<WorkspaceParams, ClaudeConfigSnapshot> ConfigGet = new("configGet");
    public static readonly PluginMethod<AccountParams, ClaudeAccount> Account = new("account");
    public static readonly PluginMethod<NoParams, HookPluginStatus> PluginStatus = new("pluginStatus");
    public static readonly PluginMethod<NoParams, HookPluginStatus> InstallPlugin = new("installPlugin");
    public static readonly PluginMethod<PluginUninstallPlanParams, CommandPlan> PluginUninstallPlan = new("pluginUninstallPlan");
    public static readonly PluginMethod<PluginUninstallParams, CommandOutput> PluginUninstall = new("pluginUninstall");
    public static readonly PluginMethod<PluginMovePlanParams, CommandsPlan> PluginMovePlan = new("pluginMovePlan");
    public static readonly PluginMethod<PluginMoveParams, CommandOutput> PluginMove = new("pluginMove");
    public static readonly PluginMethod<MarketplacePlanParams, CommandPlan> MarketplacePlan = new("marketplacePlan");
    public static readonly PluginMethod<MarketplaceRunParams, CommandOutput> MarketplaceRun = new("marketplaceRun");
    /// <summary>The servers Claude itself reaches (claude.ai connectors, plugin servers) that no file declares.</summary>
    public static readonly PluginMethod<WorkspaceParams, McpListResult> McpList = new("mcpList");
    public static readonly PluginMethod<ReadFileParams, FileContent> ReadFile = new("readFile");
    public static readonly PluginMethod<WriteFileParams, FileWriteResult> WriteFile = new("writeFile");
    public static readonly PluginMethod<ClaudeEditRequest, ClaudeEditPlan> PlanEdit = new("planEdit");
    public static readonly PluginMethod<ClaudeApplyRequest, ClaudeEditPlan> ApplyEdit = new("applyEdit");
    public static readonly PluginMethod<IdeSelectionChanged, Ack> SelectionChanged = new("selectionChanged");
    public static readonly PluginMethod<IdeDocumentClosed, Ack> DocumentClosed = new("documentClosed");
    public static readonly PluginMethod<IdeActionReply, Ack> ActionReply = new("actionReply");
    public static readonly PluginMethod<StatusSnapshotParams, IReadOnlyList<ClaudeCodeStatusPush>> StatusSnapshot = new("statusSnapshot");

    public static readonly PluginChannel<ClaudeCodeStatusPush> Status = PluginChannel<ClaudeCodeStatusPush>.State("status", StatusSnapshot, "workspaceId");
    public static readonly PluginChannel<IdeActionRequest> IdeAction = PluginChannel<IdeActionRequest>.Event("ideAction");

    public static readonly PluginContract Contract = PluginContract.Create<ClaudeCodeSettings>(Id, 1,
        [ConfigGet, Account, PluginStatus, InstallPlugin, PluginUninstallPlan, PluginUninstall, PluginMovePlan, PluginMove,
            MarketplacePlan, MarketplaceRun, McpList, ReadFile, WriteFile, PlanEdit, ApplyEdit, SelectionChanged, DocumentClosed,
            ActionReply, StatusSnapshot],
        [Status, IdeAction]);
}