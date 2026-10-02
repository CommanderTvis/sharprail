using System.Text.Json;
using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Codex;

public enum CodexInstructionsTarget
{
    Global,
    Project,
    [JsonStringEnumMemberName("project-override")] ProjectOverride
}

public enum CodexStatus
{
    Idle,
    Running,
    Blocked,
    Done
}

public enum CodexPlanStatus
{
    Pending,
    [JsonStringEnumMemberName("in_progress")] InProgress,
    Completed
}

public enum CodexScope
{
    Project,
    User,
    System
}

public enum CodexWritableScope
{
    Project,
    User
}

public enum CodexInstructionsScope
{
    Global,
    Project,
    /// <summary>SharpRail's developer instructions, added to a session it launched with <c>developer_instructions</c>.</summary>
    Launch
}

public sealed record CodexTokenUsage(long Input, long Output, long CacheRead, long CacheWrite);

public sealed record CodexPlanItem(string Content, CodexPlanStatus Status);

public sealed record CodexStatusPush(string WorkspaceId, string TabKey, CodexStatus Status, string Event)
{
    public string? Model { get; init; }
    public string? Cwd { get; init; }
    public string? SessionId { get; init; }
    public CodexTokenUsage? Usage { get; init; }
    public IReadOnlyList<CodexPlanItem>? Plan { get; init; }
}

public sealed record CodexLayer(CodexScope Scope, string Path, bool Exists, bool Ignored)
{
    public string? Error { get; init; }
}

public sealed record CodexShadowedValue(JsonElement Value, CodexScope Scope, string Path);

public sealed record CodexSetting(string Key, IReadOnlyList<string> KeyPath, JsonElement Value, CodexScope Scope, string Path,
    IReadOnlyList<CodexShadowedValue> Shadowed);

public sealed record CodexMcpServer(string Name, CodexScope Scope, string Path, string Target);

public sealed record CodexInstructions(CodexInstructionsScope Scope, string Path, long Bytes)
{
    /// <summary>Worktree-relative, when the file lives in the worktree and can open in an editor tab.</summary>
    public string? RelativePath { get; init; }
}

/// <summary><see cref="Root"/> is the directory the instructions were discovered from: a Codex tab's CWD, else the worktree.</summary>
public sealed record CodexConfigSnapshot(string Root, string Home, IReadOnlyList<CodexLayer> Layers, IReadOnlyList<CodexSetting> Settings,
    IReadOnlyList<CodexMcpServer> McpServers, IReadOnlyList<CodexInstructions> Instructions, bool ProjectTrusted, bool HooksInstalled,
    bool HooksTrusted);

public sealed record CodexUsageWindow(string Id, string Label, double UsedPercent)
{
    public double? WindowDurationMins { get; init; }
    /// <summary>Unix seconds.</summary>
    public long? ResetsAt { get; init; }
}

public sealed record CodexAccount(bool LoggedIn, bool RequiresOpenaiAuth, IReadOnlyList<CodexUsageWindow> Usage)
{
    public string? Version { get; init; }
    public string? AuthMethod { get; init; }
    public string? Email { get; init; }
    public string? Plan { get; init; }
    public string? UsageFetchedAt { get; init; }
    public string? UsageError { get; init; }
}

public sealed record CodexModel(string Id, string Label);

public sealed record CodexIdePosition(int Line, int Character);

public sealed record CodexIdeRange(CodexIdePosition Start, CodexIdePosition End);

public sealed record CodexIdeFile(string Label, string Path);

public sealed record CodexIdeActiveFile(string Label, string Path, CodexIdeRange Selection, string ActiveSelectionContent);

public sealed record CodexIdeContext(IReadOnlyList<CodexIdeFile> OpenTabs)
{
    public CodexIdeActiveFile? ActiveFile { get; init; }
}

public sealed record CodexNoParams;

/// <summary>The developer instructions file on the host, and whether the user has changed it from SharpRail's text.</summary>
public sealed record CodexSystemPromptFile(string Path, bool Edited);

public sealed record CodexWorkspaceParams(string WorkspaceId);

public sealed record CodexIdeReply(string RequestId, string WorkspaceId, bool Focused, CodexIdeContext Context);

public sealed record CodexIdeRequest(string RequestId, string WorkspaceId);

/// <summary>Sets one key of a config.toml, or removes it when <see cref="Value"/> is absent.</summary>
public sealed record CodexSetValueParams(string WorkspaceId, CodexWritableScope Scope, IReadOnlyList<string> KeyPath)
{
    /// <summary>A string, number, boolean or list of strings.</summary>
    public JsonElement? Value { get; init; }
}

/// <summary>A workspace, scoped to one terminal tab's Codex session when <see cref="TabKey"/> names one.</summary>
public sealed record CodexConfigParams(string WorkspaceId)
{
    public string? TabKey { get; init; }
}

public sealed record CodexCreateInstructionsParams(string WorkspaceId, CodexInstructionsTarget Target);

public sealed record CodexCreatedInstructions(string Path)
{
    public string? RelativePath { get; init; }
}

public sealed record CodexSettings
{
    public string Command { get; init; } = "codex";
    /// <summary>Tells sessions SharpRail launches that they run in SharpRail; POSIX shells only.</summary>
    public bool AppendSystemPrompt { get; init; } = true;
    /// <summary>One of <see cref="CodexLaunch.PermissionModes"/>' ids; anything else adds no flags.</summary>
    public string PermissionMode { get; init; } = "default";
    /// <summary>Starts launcher-created Codex sessions by issuing <c>/ide on</c>.</summary>
    public bool IdeContext { get; init; }
    /// <summary>Hands every launched session SharpRail's MCP server.</summary>
    public bool Mcp { get; init; } = true;
}

public static class CodexContract
{
    public const int WireVersion = 3;

    public static readonly PluginMethod<CodexIdeReply, bool> IdeReply = new("ideReply");
    public static readonly PluginMethod<CodexNoParams, CodexAccount> Account = new("account");
    public static readonly PluginMethod<CodexNoParams, IReadOnlyList<CodexModel>> Models = new("models");
    public static readonly PluginMethod<CodexConfigParams, CodexConfigSnapshot> ConfigGet = new("configGet");
    public static readonly PluginMethod<CodexSetValueParams, CodexConfigSnapshot> SetValue = new("setValue");
    public static readonly PluginMethod<CodexCreateInstructionsParams, CodexCreatedInstructions> CreateInstructions = new("createInstructions");
    public static readonly PluginMethod<CodexConfigParams, CodexConfigSnapshot> InstallHooks = new("installHooks");
    public static readonly PluginMethod<CodexWorkspaceParams, IReadOnlyList<CodexStatusPush>> StatusSnapshot = new("statusSnapshot");
    public static readonly PluginMethod<CodexNoParams, CodexSystemPromptFile> SystemPromptGet = new("systemPromptGet");
    /// <summary>Writes SharpRail's own text over the user's edits.</summary>
    public static readonly PluginMethod<CodexNoParams, CodexSystemPromptFile> SystemPromptReset = new("systemPromptReset");

    public static readonly PluginChannel<CodexIdeRequest> IdeRequest = PluginChannel<CodexIdeRequest>.Event("ideRequest");
    // The fork declares this a state channel whose snapshot is a list; here a state channel's snapshot must be its
    // payload type, so it is an event channel and the UI half hydrates each workspace through StatusSnapshot itself.
    public static readonly PluginChannel<CodexStatusPush> Status = PluginChannel<CodexStatusPush>.Event("status");

    public static readonly PluginContract Contract = PluginContract.Create<CodexSettings>(CodexManifest.Id, WireVersion,
        [IdeReply, Account, Models, ConfigGet, SetValue, CreateInstructions, InstallHooks, StatusSnapshot, SystemPromptGet, SystemPromptReset], [IdeRequest, Status]);
}