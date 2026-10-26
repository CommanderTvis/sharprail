using System.Text.Json;
using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.ClaudeCode;

// Chat mode: Claude Code driven headless over its stream-json protocol, shown as a conversation. The host owns the
// child process and folds its events into an ordered, id-keyed list of items; the UI renders that list and sends
// requests back. Both halves agree on the records below and nothing else.

/// <summary>How a SharpRail launch of Claude Code is presented: the CLI's terminal UI, or the chat view over stream-json.</summary>
public enum ClaudeCodeMode { Terminal, Chat }

/// <summary>Where a chat is in its turn cycle.</summary>
public enum ChatStatus { Starting, Idle, Running, AwaitingPermission, Failed, Exited }

public enum ChatToolState { Pending, Running, Done, Failed, Denied }

public enum ChatNoticeLevel { Info, Warning, Error }

public enum ChatTaskKind { Subagent, Workflow, Monitor, Shell, Other }

public enum ChatTaskStatus { Running, Done, Failed, Stopped }

public enum ChatPermissionDecision { Pending, Allowed, Denied }

/// <summary>One entry of a chat's conversation. Ids are stable, so an update to an item replaces it in place.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChatUserItem), "user")]
[JsonDerivedType(typeof(ChatAssistantItem), "assistant")]
[JsonDerivedType(typeof(ChatThinkingItem), "thinking")]
[JsonDerivedType(typeof(ChatToolItem), "tool")]
[JsonDerivedType(typeof(ChatPermissionItem), "permission")]
[JsonDerivedType(typeof(ChatQuestionItem), "question")]
[JsonDerivedType(typeof(ChatNoticeItem), "notice")]
[JsonDerivedType(typeof(ChatResultItem), "result")]
public abstract record ChatItem(string Id, long Seq)
{
    /// <summary>The <c>tool_use</c> id of the subagent call this item happened inside; null at the top level. Items nest under it.</summary>
    public string? ParentId { get; init; }

    /// <summary>The CLI's message uuid, the handle rewind and fork take; null for items that are not a message of their own.</summary>
    public string? Uuid { get; init; }
}

public sealed record ChatAttachment(string Name, string Path)
{
    /// <summary>For a chip drawn from the IDE (a selection or an opened file): the line range, when the CLI sent one.</summary>
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    /// <summary>"image", "document", "selection", "file" or null for a mention.</summary>
    public string? Kind { get; init; }
    /// <summary>
    /// For an image or PDF the UI machine holds: its bytes, sent over the wire so a host on another machine needs no path. The host hands
    /// them to the CLI as a base64 <c>image</c> or <c>document</c> block and never keeps them in the transcript. At most <see cref="MaxDataBytes"/>.
    /// </summary>
    public byte[]? Data { get; init; }
    /// <summary>The media type of <see cref="Data"/>, such as <c>image/png</c> or <c>application/pdf</c>.</summary>
    public string? MediaType { get; init; }
    public const int MaxDataBytes = 8 * 1024 * 1024;
}

/// <summary>Who a user-role frame came from, as the CLI says it (<c>origin</c>). Absent means a human typed it.</summary>
public sealed record ChatOrigin(string Kind)
{
    public string? Name { get; init; }
    public string? SenderTaskId { get; init; }
    public string? Subkind { get; init; }
}

public sealed record ChatUserItem(string Id, long Seq, string Text) : ChatItem(Id, Seq)
{
    public IReadOnlyList<ChatAttachment>? Attachments { get; init; }
    /// <summary>Sent while a turn runs: the CLI holds it until it takes it up (its echo, or a command_lifecycle frame, releases it).</summary>
    public bool Queued { get; init; }
    /// <summary>The prompt was injected while the turn ran and ran at once ("Ran at once, while Claude was working.").</summary>
    public bool RanAhead { get; init; }
    /// <summary>Not typed by a human (a channel, plugin or SDK prompt); null for a human prompt.</summary>
    public ChatOrigin? Origin { get; init; }
}

/// <summary>Markdown, growing while <see cref="Streaming"/>.</summary>
public sealed record ChatAssistantItem(string Id, long Seq, string Text) : ChatItem(Id, Seq)
{
    public bool Streaming { get; init; }
}

public sealed record ChatThinkingItem(string Id, long Seq, string Text) : ChatItem(Id, Seq)
{
    public bool Streaming { get; init; }
    /// <summary>The CLI's running token estimate while it streams (<c>thinking_tokens</c>).</summary>
    public long? Tokens { get; init; }
    /// <summary>How long the block took, from its first delta to its end; null for one read from a transcript. The view says "Thought for Ns".</summary>
    public long? DurationMs { get; init; }
}

/// <summary>A tool call and its result. Output beyond the wire limit is cut and fetched whole with <see cref="ChatContract.ItemDetail"/>.</summary>
public sealed record ChatToolItem(string Id, long Seq, string Name, JsonElement Input, ChatToolState State) : ChatItem(Id, Seq)
{
    /// <summary>One line the collapsed card shows: the command, the path, the pattern.</summary>
    public string? Summary { get; init; }
    public string? Output { get; init; }
    public bool OutputTruncated { get; init; }
    /// <summary>For an edit or write: the file the call changes, so the card can open the IDE diff.</summary>
    public string? FilePath { get; init; }
    /// <summary>The subagent, workflow or background task this call started, when it started one.</summary>
    public string? TaskId { get; init; }
}

/// <summary>The CLI asking whether a tool may run (a <c>can_use_tool</c> control request), and the answer once given.</summary>
public sealed record ChatPermissionItem(string Id, long Seq, string RequestId, string ToolName, JsonElement Input) : ChatItem(Id, Seq)
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    /// <summary>The CLI's own "always allow" suggestions, echoed back when the user picks one.</summary>
    public JsonElement? Suggestions { get; init; }
    public ChatPermissionDecision Decision { get; init; }
    /// <summary>The card's question, as the extension words it for the tool ("Allow this bash command?", "Make this edit to a.cs?").</summary>
    public string? Prompt { get; init; }
    /// <summary>The label of the "don't ask again" answer without its destination ("Yes, allow Bash(ls)"); null when the CLI offered no suggestion.</summary>
    public string? AlwaysLabel { get; init; }
    /// <summary>Whether <see cref="AlwaysLabel"/> is followed by " for destination" (a rule or directory) or stands alone (a mode change).</summary>
    public bool AlwaysScoped { get; init; }
    /// <summary>The destination the CLI suggested, one of <see cref="ChatPermissionDestinations"/>.</summary>
    public string? DefaultDestination { get; init; }
    public IReadOnlyList<ChatDestination> Destinations { get; init; } = [];
}

public sealed record ChatDestination(string Key, string Label, string Tooltip);

public static class ChatPermissionDestinations
{
    public const string LocalSettings = "localSettings";
    public const string UserSettings = "userSettings";
    public const string ProjectSettings = "projectSettings";
    public const string Session = "session";
    public const string CliArg = "cliArg";
}

public sealed record ChatSection(string Label, string Text);

public sealed record ChatQuestionOption(string Label, string? Description = null)
{
    /// <summary>Markdown the question's side pane shows while the option is hovered; only single-select questions use it.</summary>
    public string? Preview { get; init; }
}

/// <summary>One question of an <c>AskUserQuestion</c> call: choose one option, or several when <see cref="MultiSelect"/>; "Other" is free text.</summary>
public sealed record ChatQuestion(string Question, IReadOnlyList<ChatQuestionOption> Options)
{
    public string? Header { get; init; }
    public bool MultiSelect { get; init; }
}

/// <summary>
/// The CLI asking the user questions (an <c>AskUserQuestion</c> <c>can_use_tool</c> request). It is answered with
/// <see cref="ChatContract.AnswerQuestion"/>, not <see cref="ChatContract.Answer"/>. <see cref="Decision"/> is Allowed once
/// answered (with <see cref="Answers"/>, keyed by question text, a multi-select answer being its labels joined by ", ") and
/// Denied when cancelled or superseded.
/// </summary>
public sealed record ChatQuestionItem(string Id, long Seq, string RequestId, IReadOnlyList<ChatQuestion> Questions) : ChatItem(Id, Seq)
{
    /// <summary>Once settled: "answered", "declined" (with <see cref="Reason"/>), "no-answer" or "session-ended"; null while pending.</summary>
    public string? Outcome { get; init; }
    public string? Reason { get; init; }
    public string? ToolUseId { get; init; }
    public ChatPermissionDecision Decision { get; init; }
    public IReadOnlyDictionary<string, string>? Answers { get; init; }
}

/// <summary>What a notice is about, so the view can draw the compaction line or the failure card instead of a plain line.</summary>
public static class ChatNoticeKinds
{
    /// <summary>The conversation was compacted; <see cref="ChatNoticeItem.Text"/> says why and how much.</summary>
    public const string Compaction = "compaction";
    /// <summary>A turn ended in an error; <see cref="ChatNoticeItem.Retryable"/> says whether sending the last prompt again may help.</summary>
    public const string Failure = "failure";
    /// <summary>The CLI is retrying a failed API request.</summary>
    public const string Retry = "retry";
    /// <summary>The CLI or SharpRail restarted the child (an effort change, a rewind).</summary>
    public const string Restart = "restart";
    /// <summary>"Interrupted" or "Tool interrupted": the CLI's interrupt marker line.</summary>
    public const string Interrupt = "interrupt";
    /// <summary>A background task's notification: Text is its summary, the sections the details.</summary>
    public const string TaskNotification = "task-notification";
    /// <summary>A message from another session, agent, teammate or channel; collapsed row with the sender in Label.</summary>
    public const string Peer = "peer";
    /// <summary>A slash-command record: Text is "/cmd args", the sections its parts.</summary>
    public const string Command = "command";
    /// <summary>A slash command's local output; Level Error for stderr; Detail holds the whole text of a multi-line result.</summary>
    public const string CommandResult = "command-result";
    /// <summary>A terminal command's output with Output, Error output and Exit code sections.</summary>
    public const string BashOutput = "bash-output";
    /// <summary>A wrapped notice (command caveat, teammate message, tick, MCP update); Label names it, Detail has the body.</summary>
    public const string Wrapped = "wrapped";
    /// <summary>IDE diagnostics the CLI attached after a tool call.</summary>
    public const string Diagnostics = "diagnostics";
    /// <summary>The CLI fell back to another model after a refusal.</summary>
    public const string RefusalFallback = "refusal-fallback";
    /// <summary>A note that follows a prompt or action ("Ran at once, while Claude was working.", a rewind's skipped files).</summary>
    public const string Meta = "meta";
    /// <summary>Lines that could not be shown, coalesced into one notice.</summary>
    public const string Skipped = "skipped";
}

public sealed record ChatNoticeItem(string Id, long Seq, ChatNoticeLevel Level, string Text) : ChatItem(Id, Seq)
{
    /// <summary>One of <see cref="ChatNoticeKinds"/>; null for a plain notice. (Not named Kind: that is the wire discriminator.)</summary>
    public string? Category { get; init; }
    /// <summary>For a failure: the result subtype that ended the turn (such as <c>error_during_execution</c>).</summary>
    public string? Subtype { get; init; }
    /// <summary>For a failure: whether resending the last prompt is a sensible recovery.</summary>
    public bool? Retryable { get; init; }
    /// <summary>The collapsed row's lead ("Message from @x", "Command caveat", "Sender not recorded").</summary>
    public string? Label { get; init; }
    /// <summary>The body shown when the row is opened: a peer message, a compaction summary, a multi-line command result.</summary>
    public string? Detail { get; init; }
    public IReadOnlyList<ChatSection> Sections { get; init; } = [];
}

/// <summary>Obsolete and never emitted: the extension draws nothing for a result; its numbers go to the header, a failure to a notice.</summary>
public sealed record ChatResultItem(string Id, long Seq, bool IsError) : ChatItem(Id, Seq)
{
    public string? Subtype { get; init; }
    public double? CostUsd { get; init; }
    public long? DurationMs { get; init; }
    public int? Turns { get; init; }
    public string? Text { get; init; }
}

/// <summary>A slash command, skill or agent the session offers; the composer's <c>/</c> menu is built from these.</summary>
public sealed record ChatCommand(string Name, string? Description = null, string? ArgumentHint = null)
{
    /// <summary>Where it comes from: built-in, a skill, a plugin or a project command; null when the CLI does not say.</summary>
    public string? Source { get; init; }
    /// <summary>The menu section: "Slash Commands" for everything the CLI reports; the view defines the built-in sections.</summary>
    public string Section { get; init; } = "Slash Commands";
}

public sealed record ChatModel(string Id, string Label)
{
    public string? Description { get; init; }
    /// <summary>The group the picker lists it under, such as "Anthropic".</summary>
    public string? Provider { get; init; }
    /// <summary>The model family (opus, sonnet, haiku), when it can be told from the id.</summary>
    public string? Family { get; init; }
    /// <summary>The effort levels this model accepts, as the CLI reports them; empty when it reports none (or the model has no effort).</summary>
    public IReadOnlyList<string> Efforts { get; init; } = [];
}

public sealed record ChatMcpServer(string Name, string Status);

public sealed record ChatBanner(string Key, string Text, ChatNoticeLevel Level);

public sealed record ChatMode(string Key, string Label, string Description);

/// <summary>A background unit of work: a subagent, workflow run, monitor or shell.</summary>
public sealed record ChatTask(string TaskId, ChatTaskKind Kind, string Description, ChatTaskStatus Status)
{
    /// <summary>The tool call that started it, which the nested items hang from.</summary>
    public string? ToolUseId { get; init; }
    public string? Summary { get; init; }
    public string? LastTool { get; init; }
    public long? Tokens { get; init; }
    /// <summary>The CLI's task type as sent (<c>local_agent</c>) and its display name ("subagent", "workflow", "shell", "monitor", ...).</summary>
    public string? TaskType { get; init; }
    public string? TypeLabel { get; init; }
    public bool Background { get; init; }
    public string? Prompt { get; init; }
    public string? Error { get; init; }
    public string? OutputFile { get; init; }
    public int? ToolUses { get; init; }
    public long? DurationMs { get; init; }
}

public sealed record ChatUsage
{
    public long Input { get; init; }
    public long Output { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }
    public long? ContextTokens { get; init; }
    public long? ContextWindow { get; init; }
    public long? MaxOutputTokens { get; init; }
    public double CostUsd { get; init; }
}

/// <summary>Everything about a chat that is not a conversation entry: the facts the chips show and the menus offer.</summary>
public sealed record ChatHeader(ChatStatus Status)
{
    public string? SessionId { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? PermissionMode { get; init; }
    public string? Version { get; init; }
    public string? Error { get; init; }
    /// <summary>A change that is under way and not yet in effect, such as <c>effort high</c> while the child restarts; null otherwise.</summary>
    public string? Pending { get; init; }
    /// <summary>What the CLI is doing besides a turn: "Compacting", "Running start-up hooks"; null otherwise.</summary>
    public string? Activity { get; init; }
    /// <summary>"on", "off" or "cooldown" as <c>fast_mode_state</c> says; null when unknown.</summary>
    public string? FastMode { get; init; }
    /// <summary>The rate-limit banner, or null; <see cref="ChatBanner.Key"/> is what a dismissal is remembered by.</summary>
    public ChatBanner? Banner { get; init; }
    /// <summary>The permission modes the session offers, in menu order, with the extension's labels.</summary>
    public IReadOnlyList<ChatMode> PermissionModes { get; init; } = [];
    public IReadOnlyList<ChatCommand> Commands { get; init; } = [];
    public IReadOnlyList<ChatModel> Models { get; init; } = [];
    public IReadOnlyList<string> Agents { get; init; } = [];
    public IReadOnlyList<ChatMcpServer> McpServers { get; init; } = [];
    public IReadOnlyList<ChatTask> Tasks { get; init; } = [];
    public ChatUsage Usage { get; init; } = new();
    /// <summary>The <c>--advisor</c> model the child was launched with; null when none.</summary>
    public string? Advisor { get; init; }
    /// <summary>Whether the child was launched with <c>--chrome</c>.</summary>
    public bool Chrome { get; init; }
}

/// <summary>One push on a chat's channel. Every payload carries the key fields the channel is scoped by.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChatItemPush), "item")]
[JsonDerivedType(typeof(ChatHeaderPush), "header")]
[JsonDerivedType(typeof(ChatResetPush), "reset")]
[JsonDerivedType(typeof(ChatRemovePush), "remove")]
public abstract record ChatPush(string WorkspaceId, string ChatId);

/// <summary>An item was added, or replaced in place (same id).</summary>
public sealed record ChatItemPush(string WorkspaceId, string ChatId, ChatItem Item) : ChatPush(WorkspaceId, ChatId);

public sealed record ChatHeaderPush(string WorkspaceId, string ChatId, ChatHeader Header) : ChatPush(WorkspaceId, ChatId);

/// <summary>The item list is being rebuilt from the transcript (a revival or rewind): drop what was shown.</summary>
public sealed record ChatResetPush(string WorkspaceId, string ChatId) : ChatPush(WorkspaceId, ChatId);

/// <summary>An item is gone (a queued message that was cancelled): drop it from the list.</summary>
public sealed record ChatRemovePush(string WorkspaceId, string ChatId, string ItemId) : ChatPush(WorkspaceId, ChatId);

public sealed record ChatKey(string WorkspaceId, string ChatId);

public sealed record ChatSnapshot(IReadOnlyList<ChatItem> Items, ChatHeader Header);

public sealed record ChatOpenParams(string WorkspaceId, string ChatId)
{
    /// <summary>Resume this session instead of starting a new one.</summary>
    public string? ResumeSessionId { get; init; }
    /// <summary>With <see cref="ResumeSessionId"/>: continue under a new session id.</summary>
    public bool? Fork { get; init; }
    public string? Model { get; init; }
    public string? PermissionMode { get; init; }
    public string? Effort { get; init; }
}

public sealed record ChatSendParams(string WorkspaceId, string ChatId, string Text)
{
    public IReadOnlyList<ChatAttachment>? Attachments { get; init; }
}

public sealed record ChatSetParams(string WorkspaceId, string ChatId, string Value);

public sealed record ChatAnswerParams(string WorkspaceId, string ChatId, string RequestId, bool Allow)
{
    public string? Message { get; init; }
    /// <summary>The input to run with when the user edited it; null keeps the request's.</summary>
    public JsonElement? UpdatedInput { get; init; }
    /// <summary>Echoes the request's suggestions, which makes the CLI remember the rule.</summary>
    public bool? Remember { get; init; }
    /// <summary>With <see cref="Remember"/>: where to save it, one of <see cref="ChatPermissionDestinations"/>; null keeps the suggestion's own.</summary>
    public string? Destination { get; init; }
    /// <summary>With a denial: what to tell Claude instead. Empty gives the extension's plain rejection (or the plan-mode one for ExitPlanMode).</summary>
    public string? Reason { get; init; }
    /// <summary>For ExitPlanMode approval: the mode to continue in ("acceptEdits", "default" or "auto"); null changes nothing.</summary>
    public string? PlanMode { get; init; }
    /// <summary>For ExitPlanMode kept planning: comments on selected plan text, sent as feedback.</summary>
    public IReadOnlyList<ChatPlanComment>? PlanComments { get; init; }
}

public sealed record ChatPlanComment(string Quote, string Comment);

public sealed record ChatAnswerQuestionParams(string WorkspaceId, string ChatId, string RequestId)
{
    /// <summary>The answer to each question by question text (a multi-select answer is its labels joined by ", "; "Other" is the typed text); ignored when <see cref="Cancelled"/>.</summary>
    public IReadOnlyDictionary<string, string>? Answers { get; init; }
    public bool? Cancelled { get; init; }
}

public sealed record ChatQueuedParams(string WorkspaceId, string ChatId, string ItemId);

public sealed record ChatEditQueuedParams(string WorkspaceId, string ChatId, string ItemId, string Text);

public sealed record ChatRewindParams(string WorkspaceId, string ChatId, string Uuid)
{
    /// <summary>Restore the files too, not only the conversation.</summary>
    public bool? Files { get; init; }
}

public sealed record ChatTaskParams(string WorkspaceId, string ChatId, string TaskId);

public sealed record ChatDetailParams(string WorkspaceId, string ChatId, string ItemId);

public sealed record ChatDetail(string Text);

public sealed record ChatForked(string ChatId)
{
    /// <summary>The prompt text to seed the new conversation's composer with.</summary>
    public string? PromptText { get; init; }
}

/// <summary>What rewinding the code to a prompt would change, for the confirm dialog.</summary>
public sealed record ChatRewindPreview(bool CanRewind)
{
    public string? Error { get; init; }
    public IReadOnlyList<string> FilesChanged { get; init; } = [];
    public int? Insertions { get; init; }
    public int? Deletions { get; init; }
}

public sealed record ChatSessionsParams(string WorkspaceId);

public sealed record ChatSessionInfo(string SessionId, string Title, string LastModified)
{
    public int? Messages { get; init; }
    /// <summary>The chat of this workspace that is running this session right now, so a second one is never started on it; null when none is.</summary>
    public string? ChatId { get; init; }
    /// <summary>"chat" when a SharpRail chat started the session (the host has its record); null when the host cannot say, as for a terminal session.</summary>
    public string? StartedIn { get; init; }
}

public sealed record ChatSessionList(IReadOnlyList<ChatSessionInfo> Sessions);

public sealed record ChatFilesParams(string WorkspaceId, string Query);

public sealed record ChatFileList(IReadOnlyList<string> Paths);

/// <summary>The chat wire contract. It is declared apart from <see cref="ClaudeCodeContract"/> and merged into it there.</summary>
public static class ChatContract
{
    /// <summary>Starts, resumes or reattaches the chat and returns its header. Idempotent per chat id.</summary>
    public static readonly PluginMethod<ChatOpenParams, ChatHeader> Open = new("chatOpen");
    /// <summary>The conversation so far and the header: what a view that just mounted starts from.</summary>
    public static readonly PluginMethod<ChatKey, IReadOnlyList<ChatPush>> Snapshot = new("chatSnapshot");
    public static readonly PluginMethod<ChatSendParams, Ack> Send = new("chatSend");
    public static readonly PluginMethod<ChatKey, Ack> Interrupt = new("chatInterrupt");
    /// <summary>Ends the child process; the chat's transcript stays on disk and a later <see cref="Open"/> resumes it.</summary>
    public static readonly PluginMethod<ChatKey, Ack> Close = new("chatClose");
    /// <summary>Switches the model of the running session. The header changes only once the CLI has confirmed it.</summary>
    public static readonly PluginMethod<ChatSetParams, ChatHeader> SetModel = new("chatSetModel");
    public static readonly PluginMethod<ChatSetParams, ChatHeader> SetPermissionMode = new("chatSetPermissionMode");
    public static readonly PluginMethod<ChatSetParams, ChatHeader> SetEffort = new("chatSetEffort");
    public static readonly PluginMethod<ChatAnswerParams, Ack> Answer = new("chatAnswer");
    /// <summary>Answers an <see cref="ChatQuestionItem"/>; the CLI receives the answers as the tool's updated input.</summary>
    public static readonly PluginMethod<ChatAnswerQuestionParams, Ack> AnswerQuestion = new("chatAnswerQuestion");
    /// <summary>Cancels a queued user message before the CLI takes it up (removing it with a <see cref="ChatRemovePush"/>); fails when the CLI already has.</summary>
    public static readonly PluginMethod<ChatQueuedParams, Ack> Dequeue = new("chatDequeue");
    /// <summary>Replaces a queued message's text: cancels it and sends the new text as a queued message, as one step; fails when the CLI already took the old one up.</summary>
    public static readonly PluginMethod<ChatEditQueuedParams, Ack> EditQueued = new("chatEditQueued");
    public static readonly PluginMethod<ChatRewindParams, Ack> Rewind = new("chatRewind");
    /// <summary>A dry run of the code rewind to a prompt (<c>rewind_files</c> with <c>dry_run</c>): what would change.</summary>
    public static readonly PluginMethod<ChatRewindParams, ChatRewindPreview> RewindPreview = new("chatRewindPreview");
    public static readonly PluginMethod<ChatRewindParams, ChatForked> Fork = new("chatFork");
    public static readonly PluginMethod<ChatTaskParams, Ack> StopTask = new("chatStopTask");
    public static readonly PluginMethod<ChatDetailParams, ChatDetail> ItemDetail = new("chatItemDetail");
    public static readonly PluginMethod<ChatSessionsParams, ChatSessionList> Sessions = new("chatSessions");
    public static readonly PluginMethod<ChatFilesParams, ChatFileList> Files = new("chatFiles");

    /// <summary>A chat's pushes, scoped by workspace and chat id; the snapshot is the same list a late subscriber replays.</summary>
    public static readonly PluginChannel<ChatPush> Updates = PluginChannel<ChatPush>.State("chatUpdates", Snapshot, "workspaceId", "chatId");

    /// <summary>The scheme of the editor-area document a chat opens in.</summary>
    public const string DocumentScheme = "claude-chat";

    /// <summary>The wire cut for a tool's output and a notice's text; the rest is fetched with <see cref="ItemDetail"/>.</summary>
    public const int OutputLimit = 16 * 1024;

    public static readonly IReadOnlyList<PluginMethodSpec> Methods =
        [Open, Snapshot, Send, Interrupt, Close, SetModel, SetPermissionMode, SetEffort, Answer, AnswerQuestion, Dequeue, EditQueued, Rewind, RewindPreview, Fork, StopTask, ItemDetail, Sessions, Files];
}