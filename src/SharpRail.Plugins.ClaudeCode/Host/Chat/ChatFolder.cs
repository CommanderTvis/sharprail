using System.Text;
using System.Text.Json;

namespace SharpRail.Plugins.ClaudeCode.Host.Chat;

/// <summary>
/// Folds Claude Code's stream-json messages (live or read back from a transcript) into the chat's ordered, id-keyed item
/// list and header. Pure and single-threaded: the owner serializes calls. Items changed since the last <see cref="Drain"/>
/// are what a push must carry, so many partials of one item cost one push.
/// </summary>
internal sealed partial class ChatFolder
{
    private readonly Dictionary<string, ChatItem> items = [];
    private readonly Dictionary<string, long> thinkingStarted = [];

    /// <summary>Whether the session was launched able to enter bypass mode (<c>--dangerously-skip-permissions</c> or <c>--allow-dangerously-skip-permissions</c>); without it the CLI refuses the switch, so the mode is not offered.</summary>
    internal bool AllowBypass { get; set; }
    private readonly List<string> order = [];
    private readonly HashSet<string> dirty = [];
    private readonly List<string> removed = [];
    private readonly Dictionary<string, JsonElement> questionInput = [];
    private readonly Dictionary<string, string> fullText = [];
    // Per API message: how many blocks of each type have been seen, so the nth streamed block and the nth block of the
    // complete message meet in one item.
    private readonly Dictionary<string, Dictionary<string, int>> started = [];
    private readonly Dictionary<string, Dictionary<string, int>> completed = [];
    private readonly Dictionary<string, string> streamMessage = [];
    private readonly Dictionary<string, string> toolOfRequest = [];
    private readonly Dictionary<string, ChatTask> tasks = [];
    private long sequence;
    private bool headerDirty;

    public ChatHeader Header { get; private set; } = new(ChatStatus.Starting);

    /// <summary>The CLI's context size after the latest top-level assistant message, for the usage chip.</summary>
    private long? contextTokens;

    public IReadOnlyList<ChatItem> Items => [.. order.Select(id => items[id])];

    public ChatItem? Item(string id) => items.GetValueOrDefault(id);

    public string? FullText(string id) => fullText.GetValueOrDefault(id);

    public string? ToolOfRequest(string requestId) => toolOfRequest.GetValueOrDefault(requestId);

    public void Clear()
    {
        items.Clear(); order.Clear(); dirty.Clear(); fullText.Clear(); started.Clear(); completed.Clear(); streamMessage.Clear(); toolOfRequest.Clear(); tasks.Clear(); removed.Clear(); questionInput.Clear();
        Header = Header with { Tasks = [] };
        headerDirty = true;
    }

    /// <summary>The items and header changed since the last call.</summary>
    public (IReadOnlyList<ChatItem> Items, ChatHeader? Header, IReadOnlyList<string> Removed) Drain()
    {
        var changed = order.Where(dirty.Contains).Select(id => items[id]).ToList();
        dirty.Clear();
        var header = headerDirty ? Header : null;
        headerDirty = false;
        var gone = removed.ToArray();
        removed.Clear();
        return (changed, header, gone);
    }

    /// <summary>Drops an item (a cancelled queued message); the next drain reports it removed.</summary>
    public bool Remove(string id)
    {
        if (!items.Remove(id)) return false;
        order.Remove(id);
        dirty.Remove(id);
        fullText.Remove(id);
        removed.Add(id);
        return true;
    }

    public void SetHeader(Func<ChatHeader, ChatHeader> change)
    {
        var next = change(Header);
        if (next == Header) return;
        Header = next;
        headerDirty = true;
    }

    public void SetStatus(ChatStatus status, string? error = null) => SetHeader(header => header with { Status = status, Error = error ?? (status == ChatStatus.Failed ? header.Error : null) });

    private void Put(ChatItem item)
    {
        if (!items.ContainsKey(item.Id)) order.Add(item.Id);
        items[item.Id] = item;
        dirty.Add(item.Id);
    }

    private long Next() => ++sequence;

    public ChatUserItem AddUser(string text, IReadOnlyList<ChatAttachment>? attachments, bool queued, string? id = null, string? uuid = null, string? parent = null)
    {
        var item = new ChatUserItem(id ?? "u:" + (sequence + 1), Next(), text) { Attachments = attachments, Queued = queued, Uuid = uuid, ParentId = parent };
        Put(item);
        return item;
    }

    /// <summary>Clears the queued mark of every user message: the turn that held them has ended.</summary>
    public void ReleaseQueued()
    {
        foreach (var id in order.ToArray())
            if (items[id] is ChatUserItem { Queued: true } user) Put(user with { Queued = false });
    }

    public ChatNoticeItem AddNotice(ChatNoticeLevel level, string text, string? kind = null, string? subtype = null, bool? retryable = null)
    {
        var id = "n:" + (sequence + 1);
        var shown = Cut(text, out var cut);
        if (cut) fullText[id] = text;
        var item = new ChatNoticeItem(id, Next(), level, shown) { Category = kind, Subtype = subtype, Retryable = retryable };
        Put(item);
        return item;
    }

    public ChatPermissionItem AddPermission(string requestId, string tool, JsonElement input, string? toolUseId, string? title, string? description, JsonElement? suggestions, string? parent)
    {
        if (toolUseId is not null) toolOfRequest[requestId] = toolUseId;
        var (always, scoped, destination) = ChatPermissions.Always(suggestions);
        var item = new ChatPermissionItem("p:" + requestId, Next(), requestId, tool, input)
        {
            Title = title,
            Description = description,
            Suggestions = suggestions,
            ParentId = parent,
            Prompt = ChatPermissions.Prompt(tool, input),
            AlwaysLabel = always,
            AlwaysScoped = scoped,
            DefaultDestination = destination ?? (always is null ? null : ChatPermissionDestinations.LocalSettings),
            Destinations = always is null ? [] : ChatPermissions.Destinations
        };
        Put(item);
        return item;
    }

    /// <summary>Adds the card for an AskUserQuestion request, keeping the input the answers are sent back inside.</summary>
    public ChatQuestionItem AddQuestion(string requestId, JsonElement input, string? parent, string? toolUseId = null)
    {
        questionInput[requestId] = input;
        var questions = new List<ChatQuestion>();
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("questions", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var entry in list.EnumerateArray())
            {
                if (Str(entry, "question") is not { } text) continue;
                var options = entry.TryGetProperty("options", out var raw) && raw.ValueKind == JsonValueKind.Array
                    ? raw.EnumerateArray().Select(option => option.ValueKind == JsonValueKind.String ? new ChatQuestionOption(option.GetString()!) : new ChatQuestionOption(Str(option, "label") ?? "", Str(option, "description")) { Preview = Str(option, "preview") }).ToList()
                    : [];
                questions.Add(new ChatQuestion(text, options) { Header = Str(entry, "header"), MultiSelect = Flag(entry, "multiSelect") });
            }
        var item = new ChatQuestionItem("q:" + requestId, Next(), requestId, questions) { ParentId = parent, ToolUseId = toolUseId };
        Put(item);
        return item;
    }

    public JsonElement? QuestionInput(string requestId) => questionInput.TryGetValue(requestId, out var input) ? input : null;

    public void DecideQuestion(string requestId, ChatPermissionDecision decision, IReadOnlyDictionary<string, string>? answers)
    {
        if (items.GetValueOrDefault("q:" + requestId) is ChatQuestionItem question) Put(question with { Decision = decision, Answers = answers });
    }

    public void Decide(string requestId, ChatPermissionDecision decision)
    {
        if (items.GetValueOrDefault("p:" + requestId) is ChatPermissionItem permission) Put(permission with { Decision = decision });
        if (decision == ChatPermissionDecision.Denied && toolOfRequest.GetValueOrDefault(requestId) is { } tool && items.GetValueOrDefault(tool) is ChatToolItem { State: ChatToolState.Pending or ChatToolState.Running } running)
            Put(running with { State = ChatToolState.Denied });
    }

    public bool HasPendingPermission() => items.Values.OfType<ChatPermissionItem>().Any(item => item.Decision == ChatPermissionDecision.Pending) ||
        items.Values.OfType<ChatQuestionItem>().Any(item => item.Decision == ChatPermissionDecision.Pending);

    /// <summary>Turns every open item into a finished one when the process is gone or the turn was interrupted.</summary>
    public void Settle(ChatToolState openTools)
    {
        foreach (var id in order.ToArray())
            switch (items[id])
            {
                case ChatAssistantItem { Streaming: true } text: Put(text with { Streaming = false }); break;
                case ChatThinkingItem { Streaming: true } thinking: Put(thinking with { Streaming = false, DurationMs = ThinkingTook(thinking.Id) }); break;
                case ChatToolItem { State: ChatToolState.Pending or ChatToolState.Running } tool: Put(tool with { State = openTools }); break;
                case ChatQuestionItem { Decision: ChatPermissionDecision.Pending } question: Put(question with { Decision = ChatPermissionDecision.Denied }); break;
                case ChatPermissionItem { Decision: ChatPermissionDecision.Pending } permission: Put(permission with { Decision = ChatPermissionDecision.Denied }); break;
            }
    }

    /// <summary>What a message means for the turn cycle, beyond the items it changed.</summary>
    public enum Signal { None, Init, Result }

    public sealed record Folded(Signal Signal, string? Title = null, bool Failed = false);

    public Folded Apply(JsonElement message, string? defaultParent = null)
    {
        var type = Str(message, "type");
        var parent = Str(message, "parent_tool_use_id") ?? defaultParent;
        switch (type)
        {
            case "system": return SystemFrame(message, parent);
            case "stream_event": StreamEvent(message, parent); return new(Signal.None);
            case "assistant": Assistant(message, parent); return new(Signal.None);
            case "user": User(message, parent); return new(Signal.None);
            case "rate_limit_event": RateLimit(message); return new(Signal.None);
            case "result": return Result(message);
            default: return new(Signal.None);
        }
    }

    private Folded SystemFrame(JsonElement message, string? parent)
    {
        switch (Str(message, "subtype"))
        {
            case "init":
                var skills = Strings(message, "skills").ToHashSet();
                var commands = Strings(message, "slash_commands").Select(name => new ChatCommand(name) { Source = SourceOf(name, skills) }).ToList();
                SetHeader(header => header with
                {
                    SessionId = Str(message, "session_id") ?? header.SessionId,
                    Model = Str(message, "model") ?? header.Model,
                    PermissionMode = Str(message, "permissionMode") ?? header.PermissionMode,
                    Cwd = Str(message, "cwd") ?? header.Cwd,
                    Version = Str(message, "claude_code_version") ?? header.Version,
                    Commands = commands.Count > 0 ? commands : header.Commands,
                    Agents = Strings(message, "agents") is { Length: > 0 } agents ? agents : header.Agents,
                    McpServers = McpServers(message),
                    PermissionModes = AllowBypass ? PermissionModeList : [.. PermissionModeList.Where(mode => mode.Key != "bypassPermissions")]
                });
                return new(Signal.Init);
            case "commands_changed":
                if (message.TryGetProperty("commands", out var list) && list.ValueKind == JsonValueKind.Array)
                    SetHeader(header => header with { Commands = [.. Commands(list, header.Commands)] });
                return new(Signal.None);
            case "task_started":
                {
                    var id = Str(message, "task_id");
                    if (id is null) return new(Signal.None);
                    var toolUse = Str(message, "tool_use_id");
                    var type = Str(message, "task_type");
                    UpsertTask(new ChatTask(id, KindOf(type), Str(message, "description") ?? Str(message, "prompt") ?? id, ChatTaskStatus.Running)
                    {
                        ToolUseId = toolUse,
                        TaskType = type,
                        TypeLabel = TypeLabel(type),
                        Background = Flag(message, "is_backgrounded"),
                        Prompt = Str(message, "prompt")
                    });
                    if (toolUse is not null && items.GetValueOrDefault(toolUse) is ChatToolItem tool) Put(tool with { TaskId = id });
                    return new(Signal.None);
                }
            case "task_progress":
                {
                    var id = Str(message, "task_id");
                    if (id is null || !tasks.TryGetValue(id, out var task)) return new(Signal.None);
                    var usage = message.TryGetProperty("usage", out var found) && found.ValueKind == JsonValueKind.Object ? found : default;
                    UpsertTask(task with
                    {
                        Summary = Str(message, "summary") ?? task.Summary,
                        LastTool = Str(message, "last_tool_name") ?? task.LastTool,
                        Tokens = Long(usage, "total_tokens") ?? task.Tokens,
                        ToolUses = (int?)Long(usage, "tool_uses") ?? task.ToolUses,
                        DurationMs = Long(usage, "duration_ms") ?? task.DurationMs
                    });
                    return new(Signal.None);
                }
            case "task_notification":
            case "task_updated":
                {
                    var id = Str(message, "task_id");
                    if (id is null || !tasks.TryGetValue(id, out var task)) return new(Signal.None);
                    var patch = message.TryGetProperty("patch", out var found) && found.ValueKind == JsonValueKind.Object ? found : default;
                    var raw = Str(message, "status") ?? Str(patch, "status");
                    var status = raw switch
                    {
                        "completed" => ChatTaskStatus.Done,
                        "failed" => ChatTaskStatus.Failed,
                        "killed" or "stopped" => ChatTaskStatus.Stopped,
                        _ => task.Status
                    };
                    var backgrounded = patch.ValueKind == JsonValueKind.Object && patch.TryGetProperty("is_backgrounded", out var bg) && bg.ValueKind is JsonValueKind.True or JsonValueKind.False ? bg.GetBoolean() : task.Background;
                    UpsertTask(task with
                    {
                        Status = status,
                        Summary = Str(message, "summary") ?? task.Summary,
                        Background = backgrounded,
                        Error = Str(patch, "error") ?? task.Error,
                        OutputFile = Str(message, "output_file") ?? task.OutputFile
                    });
                    return new(Signal.None);
                }
            case "background_tasks_changed":
                {
                    // A background task the list no longer names has finished.
                    var live = message.TryGetProperty("tasks", out var running) && running.ValueKind == JsonValueKind.Array
                        ? running.EnumerateArray().Select(entry => Str(entry, "task_id")).OfType<string>().ToHashSet() : [];
                    foreach (var gone in tasks.Values.Where(task => task.Background && task.Status == ChatTaskStatus.Running && !live.Contains(task.TaskId)).ToList())
                        UpsertTask(gone with { Status = ChatTaskStatus.Done });
                    return new(Signal.None);
                }
            case "status":
                {
                    var activity = Str(message, "status") == "compacting" ? "Compacting" : Long(message, "startupHooksRunning") is > 0 ? "Running start-up hooks" : null;
                    SetHeader(header => header with { Activity = activity, PermissionMode = Str(message, "permissionMode") ?? header.PermissionMode });
                    if (Flag(message, "interruptedTurnLeft"))
                        PutNotice("n:interrupted-turn", ChatNoticeLevel.Info, "Continuing the step that was interrupted when the session was reloaded.", ChatNoticeKinds.Meta);
                    return new(Signal.None);
                }
            case "thinking_tokens":
                {
                    var tokens = Long(message, "estimated_tokens");
                    var streaming = order.Select(id => items[id]).OfType<ChatThinkingItem>().LastOrDefault(item => item.Streaming);
                    if (streaming is not null) Put(streaming with { Tokens = tokens });
                    return new(Signal.None);
                }
            case "command_lifecycle":
                {
                    // A CLI that reports its queue releases a held prompt when it starts, finishes, drops or refuses it.
                    var id = "u:" + Str(message, "command_uuid");
                    if (items.GetValueOrDefault(id) is ChatUserItem { Queued: true } held && Str(message, "state") is "started" or "completed" or "cancelled" or "discarded" or "refused" or "ran_ahead")
                    {
                        var ranAhead = Flag(message, "ran_ahead") || Str(message, "state") == "ran_ahead";
                        Put(held with { Queued = false, RanAhead = ranAhead });
                        if (ranAhead) PutNotice("n:ran-ahead-" + held.Id, ChatNoticeLevel.Info, "Ran at once, while Claude was working.", ChatNoticeKinds.Meta);
                    }
                    return new(Signal.None);
                }
            case "model_refusal_fallback":
                {
                    var original = Str(message, "original_model") ?? "the model";
                    var fallback = Str(message, "fallback_model") ?? "another model";
                    if (message.TryGetProperty("retracted_message_uuids", out var retracted) && retracted.ValueKind == JsonValueKind.Array)
                    {
                        var gone = retracted.EnumerateArray().Select(entry => entry.GetString()).OfType<string>().ToHashSet();
                        foreach (var id in order.Where(id => items[id].Uuid is { } uuid && gone.Contains(uuid)).ToList()) Remove(id);
                    }
                    PutNotice("n:refusal-" + (Str(message, "uuid") ?? (sequence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), ChatNoticeLevel.Warning,
                        $"{original} declined to answer; continuing with {fallback}.", ChatNoticeKinds.RefusalFallback);
                    return new(Signal.None);
                }
            case "informational":
                // Info-level lines are dropped, and the rest shown only once the conversation has content (a labelled one always).
                if (Str(message, "content") is { Length: > 0 } content && (Str(message, "tag") == "brass_thimble" || Str(message, "level") is not (null or "info") && order.Count > 0))
                    PutNotice("n:" + (Str(message, "uuid") ?? (sequence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), Str(message, "level") is "error" ? ChatNoticeLevel.Error : ChatNoticeLevel.Warning, Sanitise(content), ChatNoticeKinds.Meta);
                return new(Signal.None);
            case "api_retry":
                AddNotice(ChatNoticeLevel.Warning, "Retrying the request" + (Str(message, "error") is { } error ? ": " + error : "") + ".", ChatNoticeKinds.Retry);
                return new(Signal.None);
            case "compact_boundary":
                {
                    var meta = message.TryGetProperty("compact_metadata", out var found) ? found : default;
                    var trigger = Str(meta, "trigger") == "manual" ? "manual" : "auto";
                    var freed = Long(meta, "pre_tokens") is { } tokens ? $" · {(long)Math.Round(tokens / 1000.0)}k tokens freed" : "";
                    var item = PutNotice("c:" + (Str(message, "uuid") ?? (sequence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), ChatNoticeLevel.Info, $"Compacted chat · {trigger}{freed}", ChatNoticeKinds.Compaction);
                    pendingCompaction = item.Id;
                }
                contextTokens = null;
                SetHeader(header => header with { Usage = header.Usage with { ContextTokens = 0 } });
                return new(Signal.None);
            default: return new(Signal.None);
        }
    }

    private void UpsertTask(ChatTask task)
    {
        tasks[task.TaskId] = task;
        SetHeader(header => header with { Tasks = [.. tasks.Values] });
    }

    private static string? TypeLabel(string? type) => type switch
    {
        "local_agent" => "subagent",
        "local_workflow" => "workflow",
        "local_bash" => "shell",
        "monitor_mcp" or "monitor_ws" => "monitor",
        "mcp_task" => "MCP task",
        "in_process_teammate" => "teammate",
        "dream" => "dream",
        "auto_mode_scan" => "auto-mode scan",
        "local_memory_import" => "memory import",
        "remote_agent" => "cloud session",
        _ => type
    };

    private static ChatTaskKind KindOf(string? type) => type switch
    {
        "local_agent" or "remote_agent" or "subagent" => ChatTaskKind.Subagent,
        "local_bash" or "shell" => ChatTaskKind.Shell,
        { } other when other.Contains("workflow", StringComparison.OrdinalIgnoreCase) => ChatTaskKind.Workflow,
        { } other when other.Contains("monitor", StringComparison.OrdinalIgnoreCase) => ChatTaskKind.Monitor,
        _ => ChatTaskKind.Other
    };

    private static readonly IReadOnlyList<ChatMode> PermissionModeList =
    [
        new("default", "Manual", "Claude will ask for approval before making each edit"),
        new("acceptEdits", "Edit automatically", "Claude will edit your selected text or the whole file"),
        new("plan", "Plan", "Claude will explore the code and present a plan before editing"),
        new("auto", "Auto", "Claude will approve actions that pass a safety check and pause for anything risky"),
        new("bypassPermissions", "Bypass permissions", "Claude will not ask for approval before running potentially dangerous commands"),
        new("dontAsk", "Don't ask", "Claude will deny actions that need approval instead of asking")
    ];

    private static string? SourceOf(string name, HashSet<string> skills) => skills.Contains(name) ? "skill" : name.Contains(':') ? "plugin" : null;

    private static IEnumerable<ChatCommand> Commands(JsonElement list, IReadOnlyList<ChatCommand> known)
    {
        var source = known.ToDictionary(command => command.Name, command => command.Source);
        foreach (var entry in list.EnumerateArray())
        {
            var name = entry.ValueKind == JsonValueKind.String ? entry.GetString() : Str(entry, "name");
            if (name is null) continue;
            yield return new ChatCommand(name, Str(entry, "description") is { Length: > 0 } description ? description : null, Str(entry, "argumentHint") is { Length: > 0 } hint ? hint : null)
            { Source = source.GetValueOrDefault(name) ?? (name.Contains(':') ? "plugin" : null) };
        }
    }

    private static IReadOnlyList<ChatMcpServer> McpServers(JsonElement message)
    {
        if (!message.TryGetProperty("mcp_servers", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return [.. list.EnumerateArray().Select(entry => new ChatMcpServer(Str(entry, "name") ?? "?", Str(entry, "status") ?? "unknown"))];
    }

    /// <summary>The answer to the <c>initialize</c> control request: the models, commands and agents the session offers.</summary>
    public void ApplyInitialize(JsonElement response)
    {
        var models = new List<ChatModel>();
        if (response.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var entry in list.EnumerateArray())
                if ((Str(entry, "value") ?? Str(entry, "id")) is { } id)
                    models.Add(new ChatModel(id, Str(entry, "displayName") ?? Str(entry, "label") ?? id)
                    {
                        Description = Str(entry, "description"),
                        Family = new[] { "opus", "sonnet", "haiku" }.FirstOrDefault(family => id.Contains(family, StringComparison.OrdinalIgnoreCase)),
                        Provider = id.Contains("claude", StringComparison.OrdinalIgnoreCase) || new[] { "opus", "sonnet", "haiku" }.Any(family => id.Contains(family, StringComparison.OrdinalIgnoreCase)) ? "Anthropic" : null,
                        Efforts = Strings(entry, "supportedEffortLevels")
                    });
        SetHeader(header =>
        {
            var next = header with { Models = models.Count > 0 ? models : header.Models };
            if (response.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array) next = next with { Commands = [.. Commands(commands, header.Commands)] };
            if (response.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
                next = next with { Agents = [.. agents.EnumerateArray().Select(agent => agent.ValueKind == JsonValueKind.String ? agent.GetString() : Str(agent, "name")).OfType<string>()] };
            return next;
        });
    }

    private void StreamEvent(JsonElement message, string? parent)
    {
        if (!message.TryGetProperty("event", out var @event)) return;
        var key = parent ?? "";
        switch (Str(@event, "type"))
        {
            case "message_start":
                if (@event.TryGetProperty("message", out var start) && Str(start, "id") is { } messageId) streamMessage[key] = messageId;
                break;
            case "content_block_start":
                {
                    if (!streamMessage.TryGetValue(key, out var openMessage) || !@event.TryGetProperty("content_block", out var block)) break;
                    var kind = Str(block, "type");
                    if (kind is "text" or "thinking")
                    {
                        var id = BlockId(started, openMessage, kind);
                        if (kind != "text") thinkingStarted[id] = Environment.TickCount64;
                        Put(kind == "text"
                            ? new ChatAssistantItem(id, Next(), "") { Streaming = true, ParentId = parent }
                            : new ChatThinkingItem(id, Next(), "") { Streaming = true, ParentId = parent });
                    }
                    else if (kind is "tool_use" && Str(block, "id") is { } toolId && !items.ContainsKey(toolId) && Str(block, "name") != "ToolSearch")
                    {
                        var name = Str(block, "name") ?? "tool";
                        Put(new ChatToolItem(toolId, Next(), name, JsonSerializer.SerializeToElement(new { }), ChatToolState.Pending) { ParentId = parent });
                    }
                    break;
                }
            case "content_block_delta":
                {
                    if (!streamMessage.TryGetValue(key, out var openMessage) || !@event.TryGetProperty("delta", out var delta)) break;
                    var kind = Str(delta, "type") switch { "text_delta" => "text", "thinking_delta" => "thinking", _ => null };
                    if (kind is null) break;
                    // The delta belongs to the latest started block of its type.
                    var count = started.GetValueOrDefault(openMessage)?.GetValueOrDefault(kind) ?? 0;
                    if (count == 0) break;
                    var id = $"{(kind == "text" ? "a" : "k")}:{openMessage}:{count - 1}";
                    var piece = Str(delta, kind == "text" ? "text" : "thinking") ?? "";
                    switch (items.GetValueOrDefault(id))
                    {
                        case ChatAssistantItem text: Put(text with { Text = text.Text + piece }); break;
                        case ChatThinkingItem thinking: Put(thinking with { Text = thinking.Text + piece }); break;
                    }
                    break;
                }
            case "message_stop":
                streamMessage.Remove(key);
                break;
        }
    }

    private static string BlockId(Dictionary<string, Dictionary<string, int>> counts, string messageId, string kind)
    {
        if (!counts.TryGetValue(messageId, out var perType)) counts[messageId] = perType = [];
        var n = perType.GetValueOrDefault(kind);
        perType[kind] = n + 1;
        return $"{(kind == "text" ? "a" : "k")}:{messageId}:{n}";
    }

    private void Assistant(JsonElement message, string? parent)
    {
        if (!message.TryGetProperty("message", out var body)) return;
        var messageId = Str(body, "id") ?? Str(message, "uuid") ?? Guid.NewGuid().ToString("N");
        var uuid = Str(message, "uuid");
        if (body.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                switch (Str(block, "type"))
                {
                    case "text":
                        {
                            var id = BlockId(completed, messageId, "text");
                            var text = Str(block, "text") ?? "";
                            var seq = items.GetValueOrDefault(id)?.Seq ?? Next();
                            Put(new ChatAssistantItem(id, seq, text) { ParentId = parent, Uuid = uuid });
                            break;
                        }
                    case "thinking":
                        {
                            var id = BlockId(completed, messageId, "thinking");
                            var streamed = (items.GetValueOrDefault(id) as ChatThinkingItem)?.Text;
                            var text = Str(block, "thinking") is { Length: > 0 } full ? full : streamed ?? "";
                            Put(new ChatThinkingItem(id, items.GetValueOrDefault(id)?.Seq ?? Next(), text) { ParentId = parent, Uuid = uuid, DurationMs = ThinkingTook(id) });
                            break;
                        }
                    case "tool_use":
                        if (Str(block, "id") is { } toolId) ToolUse(toolId, Str(block, "name") ?? "tool", block.TryGetProperty("input", out var input) ? input.Clone() : JsonSerializer.SerializeToElement(new { }), parent, uuid);
                        break;
                }
        if (parent is null && body.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            contextTokens = (Long(usage, "input_tokens") ?? 0) + (Long(usage, "cache_read_input_tokens") ?? 0) + (Long(usage, "cache_creation_input_tokens") ?? 0) + (Long(usage, "output_tokens") ?? 0);
            SetHeader(header => header with { Usage = header.Usage with { ContextTokens = contextTokens } });
        }
    }

    private void ToolUse(string id, string name, JsonElement input, string? parent, string? uuid)
    {
        if (name == "ToolSearch") return;
        var existing = items.GetValueOrDefault(id) as ChatToolItem;
        var state = existing is { State: ChatToolState.Done or ChatToolState.Failed or ChatToolState.Denied } ? existing.State : ChatToolState.Running;
        Put(new ChatToolItem(id, existing?.Seq ?? Next(), name, input, state)
        {
            Summary = ToolSummary(name, input),
            FilePath = ToolFile(name, input),
            ParentId = parent,
            Uuid = uuid ?? existing?.Uuid,
            Output = existing?.Output,
            OutputTruncated = existing?.OutputTruncated ?? false,
            TaskId = existing?.TaskId
        });
    }

    // The tool result of an AskUserQuestion call tells how the question ended.
    private void SettleQuestion(string toolUseId, string result)
    {
        var question = items.Values.OfType<ChatQuestionItem>().FirstOrDefault(item => item.ToolUseId == toolUseId);
        if (question is null) return;
        string? outcome = null;
        string? reason = null;
        if (result.StartsWith("Your questions have been answered", StringComparison.Ordinal) || result.StartsWith("The user answered", StringComparison.Ordinal) || result.StartsWith("The user responded", StringComparison.Ordinal)) outcome = "answered";
        else if (result.StartsWith("The user did not answer", StringComparison.Ordinal)) outcome = "no-answer";
        else if (result.StartsWith("The user doesn't want to proceed", StringComparison.Ordinal))
        {
            outcome = "declined";
            const string marker = "following reason for the rejection:";
            var at = result.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0) reason = result[(at + marker.Length)..].Trim();
        }
        else if (result.Contains("session ended", StringComparison.OrdinalIgnoreCase) || result.Contains("Tool call interrupted", StringComparison.Ordinal) || result.Contains("stream closed", StringComparison.OrdinalIgnoreCase) || result.Contains("Tool permission", StringComparison.Ordinal)) outcome = "session-ended";
        if (outcome is null) return;
        var decision = question.Decision == ChatPermissionDecision.Pending ? outcome == "answered" ? ChatPermissionDecision.Allowed : ChatPermissionDecision.Denied : question.Decision;
        Put(question with { Outcome = outcome, Reason = reason, Decision = decision });
    }

    private void ToolResult(JsonElement block)
    {
        if (Str(block, "tool_use_id") is not { } id || items.GetValueOrDefault(id) is not ChatToolItem tool) return;
        var text = new StringBuilder();
        if (block.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (Str(part, "type") == "text") text.Append(Str(part, "text"));
        }
        var shown = Cut(text.ToString(), out var cut);
        if (cut) fullText[id] = text.ToString(); else fullText.Remove(id);
        var failed = Flag(block, "is_error");
        SettleQuestion(id, text.ToString());
        Put(tool with { Output = shown, OutputTruncated = cut, State = tool.State == ChatToolState.Denied ? ChatToolState.Denied : failed ? ChatToolState.Failed : ChatToolState.Done });
    }

    private Folded Result(JsonElement message)
    {
        var isError = Flag(message, "is_error");
        // A result draws nothing. A model-less one (a task notification delivered without a model call) is no turn at all.
        var modelLess = Long(message, "num_turns") == 0 && OriginOf(message) is { Kind: "task-notification" };
        var subtype = Str(message, "subtype");
        if (!modelLess && (isError || subtype is not (null or "success")))
        {
            var detail = Str(message, "result");
            var failure = subtype switch
            {
                "error_max_turns" => "The turn limit was reached before the task finished.",
                "error_max_budget_usd" => "The budget limit was reached before the task finished.",
                "error_max_structured_output_retries" => "The structured output could not be produced.",
                _ => detail is { Length: > 0 } ? detail : "The turn ended in an error."
            };
            var retryable = subtype is null or "error_during_execution" || (detail?.StartsWith("API Error", StringComparison.OrdinalIgnoreCase) ?? false);
            AddNotice(ChatNoticeLevel.Error, failure, ChatNoticeKinds.Failure, subtype, retryable);
        }
        var usage = message.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        long? window = null;
        long? maxOutput = null;
        if (message.TryGetProperty("modelUsage", out var perModel) && perModel.ValueKind == JsonValueKind.Object)
            foreach (var model in perModel.EnumerateObject())
            {
                window = Long(model.Value, "contextWindow") ?? window;
                maxOutput = Long(model.Value, "maxOutputTokens");
                if (model.Name == Header.Model) break;
            }
        SetHeader(header => header with
        {
            Usage = header.Usage with
            {
                Input = usage.ValueKind == JsonValueKind.Object ? Long(usage, "input_tokens") ?? header.Usage.Input : header.Usage.Input,
                Output = usage.ValueKind == JsonValueKind.Object ? Long(usage, "output_tokens") ?? header.Usage.Output : header.Usage.Output,
                CacheRead = usage.ValueKind == JsonValueKind.Object ? Long(usage, "cache_read_input_tokens") ?? header.Usage.CacheRead : header.Usage.CacheRead,
                CacheWrite = usage.ValueKind == JsonValueKind.Object ? Long(usage, "cache_creation_input_tokens") ?? header.Usage.CacheWrite : header.Usage.CacheWrite,
                ContextTokens = contextTokens ?? header.Usage.ContextTokens,
                ContextWindow = window ?? header.Usage.ContextWindow,
                MaxOutputTokens = maxOutput ?? header.Usage.MaxOutputTokens,
                CostUsd = message.TryGetProperty("total_cost_usd", out var cost2) && cost2.ValueKind == JsonValueKind.Number ? cost2.GetDouble() : header.Usage.CostUsd
            }
        });
        if (message.TryGetProperty("fast_mode_state", out var fast) && fast.ValueKind == JsonValueKind.String) SetHeader(header => header with { FastMode = fast.GetString() });
        return new(Signal.Result, null, !modelLess && (isError || subtype is not (null or "success")));
    }

    private static readonly Dictionary<string, string> LimitNames = new()
    {
        ["five_hour"] = "session limit",
        ["seven_day"] = "weekly limit",
        ["seven_day_opus"] = "weekly Opus limit",
        ["seven_day_sonnet"] = "weekly Sonnet limit",
        ["seven_day_overage_included"] = "Fable limit",
        ["overage"] = "usage credit limit"
    };

    /// <summary>The relative time the extension words a reset as: "soon", "in 5m", "in 2h", "in 3d".</summary>
    // The milliseconds since a thinking block began streaming; null for one that never streamed here (a transcript's).
    private long? ThinkingTook(string id) => thinkingStarted.Remove(id, out var begun) ? Environment.TickCount64 - begun : null;

    internal static string Relative(long resetsAt, DateTimeOffset now)
    {
        var seconds = resetsAt - now.ToUnixTimeSeconds();
        if (seconds < 60) return "soon";
        if (seconds < 3600) return $"in {seconds / 60}m";
        if (seconds < 86400) return $"in {seconds / 3600}h";
        return $"in {seconds / 86400}d";
    }

    // A rate_limit_event becomes the header's banner: none while allowed, a warning from 70% use, an error once rejected.
    private void RateLimit(JsonElement message)
    {
        if (!message.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object) return;
        var status = Str(info, "status");
        var type = Str(info, "rateLimitType") ?? "overage";
        var name = LimitNames.GetValueOrDefault(type, type.Replace('_', ' '));
        var reset = info.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var epoch) ? " · resets " + Relative(epoch, DateTimeOffset.UtcNow) : "";
        ChatBanner? banner = null;
        if (status == "rejected")
            banner = new ChatBanner($"{type}:rejected", $"You've hit your {name}{reset}" + (Str(info, "overageStatus") == "allowed" ? " · continue with usage credits" : ""), ChatNoticeLevel.Error);
        else if (status == "allowed_warning" && info.TryGetProperty("utilization", out var used) && used.ValueKind == JsonValueKind.Number && used.GetDouble() >= 0.7)
            banner = new ChatBanner($"{type}:warning", $"You've used {(int)Math.Round(used.GetDouble() * 100)}% of your {name}{reset}", ChatNoticeLevel.Warning);
        SetHeader(header => header with { Banner = banner });
    }

    private static string Cut(string text, out bool cut)
    {
        cut = text.Length > ChatContract.OutputLimit;
        return cut ? text[..ChatContract.OutputLimit] : text;
    }

    internal static string? ToolSummary(string name, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        string[] keys = name switch
        {
            "Bash" => ["command"],
            "Read" or "Edit" or "Write" or "MultiEdit" => ["file_path"],
            "NotebookEdit" => ["notebook_path"],
            "Grep" or "Glob" => ["pattern"],
            "WebFetch" => ["url"],
            "WebSearch" => ["query"],
            "Task" or "Agent" => ["description", "prompt"],
            _ => []
        };
        foreach (var key in keys)
            if (Str(input, key) is { Length: > 0 } value) return OneLine(value);
        foreach (var property in input.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } fallback) return OneLine(fallback);
        return null;
    }

    private static string OneLine(string value)
    {
        var line = value.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    internal static string? ToolFile(string name, JsonElement input) =>
        name is "Edit" or "Write" or "MultiEdit" && input.ValueKind == JsonValueKind.Object ? Str(input, "file_path")
        : name == "NotebookEdit" && input.ValueKind == JsonValueKind.Object ? Str(input, "notebook_path") : null;

    internal static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : Str(item, "name")).OfType<string>()] : [];

    private static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}