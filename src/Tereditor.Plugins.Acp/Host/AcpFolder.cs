using System.Text;
using System.Text.Json.Nodes;

using SharpRail.Plugins.Chat;

namespace SharpRail.Plugins.Acp.Host;

/// <summary>
/// Folds ACP <c>session/update</c> payloads into the chat's ordered, id-keyed item list. Pure state, no process and no publishing, so the
/// checks drive it with recorded updates. Not thread-safe; the session serializes calls.
/// </summary>
internal sealed class AcpFolder
{
    private readonly List<ChatItem> items = [];
    private readonly Dictionary<string, int> index = [];
    private readonly Dictionary<string, string> full = [];
    private readonly HashSet<string> changed = [];
    private long seq;
    private int counter;
    private bool reset;
    private bool headerChanged;
    // The id of the message or thought still growing; any other kind of update ends it.
    private string? streaming;

    public ChatHeader Header { get; private set; } = new(ChatStatus.Starting);

    public IReadOnlyList<ChatItem> Items => items;

    public string? Detail(string id) => full.GetValueOrDefault(id);

    public (IReadOnlyList<ChatItem> Items, bool Reset, bool Header) Drain()
    {
        IReadOnlyList<ChatItem> drained = [.. changed.Select(id => items[index[id]]).OrderBy(item => item.Seq)];
        var result = (drained, reset, headerChanged);
        changed.Clear();
        reset = false;
        headerChanged = false;
        return result;
    }

    public void Update(Func<ChatHeader, ChatHeader> edit)
    {
        Header = edit(Header);
        headerChanged = true;
    }

    public void SetStatus(ChatStatus status, string? error = null) =>
        Update(header => header with { Status = status, Error = error ?? (status is ChatStatus.Failed or ChatStatus.Exited ? header.Error : null) });

    /// <summary>Drops the conversation (a session/load replays it from the start).</summary>
    public void Clear()
    {
        items.Clear();
        index.Clear();
        full.Clear();
        changed.Clear();
        streaming = null;
        reset = true;
    }

    private void Put(ChatItem item)
    {
        if (index.TryGetValue(item.Id, out var at)) items[at] = item;
        else
        {
            index[item.Id] = items.Count;
            items.Add(item);
        }
        changed.Add(item.Id);
    }

    private T? Get<T>(string id) where T : ChatItem => index.TryGetValue(id, out var at) ? items[at] as T : null;

    private long Next() => ++seq;

    public ChatUserItem AddUser(string text, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        CloseStreaming();
        var item = new ChatUserItem("u" + ++counter, Next(), text) { Attachments = attachments is { Count: > 0 } ? attachments : null };
        Put(item);
        return item;
    }

    public void Notice(ChatNoticeLevel level, string text, string? category = null, bool? retryable = null)
    {
        CloseStreaming();
        Put(new ChatNoticeItem("n" + ++counter, Next(), level, text) { Category = category, Retryable = retryable });
    }

    /// <summary>Ends the growing message or thought so the next chunk starts a new item.</summary>
    public void CloseStreaming()
    {
        if (streaming is null) return;
        switch (Get<ChatItem>(streaming))
        {
            case ChatAssistantItem assistant: Put(assistant with { Streaming = false }); break;
            case ChatThinkingItem thinking: Put(thinking with { Streaming = false }); break;
        }
        streaming = null;
    }

    public static string Str(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>The text of a content block; other blocks read as a short description.</summary>
    public static string BlockText(JsonNode? block) => Str(block, "type") switch
    {
        "text" => Str(block, "text"),
        "image" => "[image]",
        "audio" => "[audio]",
        "resource_link" => "[" + (Str(block, "title") is { Length: > 0 } title ? title : Str(block, "name")) + "](" + Str(block, "uri") + ")",
        "resource" => Str(block?["resource"], "text") is { Length: > 0 } text ? text : "[" + Str(block?["resource"], "uri") + "]",
        _ => ""
    };

    /// <summary>Applies one <c>update</c> object. Returns false for a kind this host does not fold.</summary>
    public bool Apply(JsonObject update)
    {
        switch (Str(update, "sessionUpdate"))
        {
            case "user_message_chunk":
                Chunk(update, user: true);
                return true;
            case "agent_message_chunk":
                Chunk(update, thought: false);
                return true;
            case "agent_thought_chunk":
                Chunk(update, thought: true);
                return true;
            case "tool_call":
            case "tool_call_update":
                Tool(update);
                return true;
            case "plan":
                Plan(update);
                return true;
            case "available_commands_update":
                Update(header => header with
                {
                    Commands = [.. (update["availableCommands"] as JsonArray ?? []).OfType<JsonObject>().Select(command =>
                        new ChatCommand(Str(command, "name"), Str(command, "description") is { Length: > 0 } text ? text : null) { ArgumentHint = Str(command["input"], "hint") is { Length: > 0 } hint ? hint : null })]
                });
                return true;
            case "current_mode_update":
                Update(header => header with { PermissionMode = Str(update, "currentModeId") });
                return true;
            case "config_option_update":
                ApplyConfig(update["configOptions"] as JsonArray);
                return true;
            case "session_info_update":
                if (Str(update, "title") is { Length: > 0 } title) Update(header => header with { Title = title });
                return true;
            case "usage_update":
                Update(header => header with
                {
                    Usage = header.Usage with
                    {
                        ContextTokens = update["used"]?.GetValue<long>() ?? header.Usage.ContextTokens,
                        ContextWindow = update["size"]?.GetValue<long>() ?? header.Usage.ContextWindow,
                        CostUsd = update["cost"] is JsonObject { } cost && cost["amount"] is { } amount ? amount.GetValue<double>() : header.Usage.CostUsd
                    }
                });
                return true;
            default:
                return false;
        }
    }

    private void Chunk(JsonObject update, bool user = false, bool thought = false)
    {
        var text = BlockText(update["content"]);
        if (user)
        {
            // A replayed user turn arrives in pieces; they join while no other kind of item came between.
            if (streaming is { } open && Get<ChatUserItem>(open) is { } current) { Put(current with { Text = current.Text + text }); return; }
            var item = AddUser(text);
            streaming = item.Id;
            return;
        }
        if (streaming is { } id)
        {
            if (!thought && Get<ChatAssistantItem>(id) is { } message) { Put(message with { Text = message.Text + text }); return; }
            if (thought && Get<ChatThinkingItem>(id) is { } thinking) { Put(thinking with { Text = thinking.Text + text }); return; }
        }
        CloseStreaming();
        var next = "a" + ++counter;
        Put(thought ? new ChatThinkingItem(next, Next(), text) { Streaming = true } : new ChatAssistantItem(next, Next(), text) { Streaming = true });
        streaming = next;
    }

    private void Plan(JsonObject update)
    {
        CloseStreaming();
        var text = new StringBuilder("**Plan**\n\n");
        foreach (var entry in (update["entries"] as JsonArray ?? []).OfType<JsonObject>())
            text.Append(Str(entry, "status") switch { "completed" => "- [x] ", "in_progress" => "- [~] ", _ => "- [ ] " }).AppendLine(Str(entry, "content"));
        var existing = Get<ChatAssistantItem>("plan");
        Put(existing is null ? new ChatAssistantItem("plan", Next(), text.ToString()) : existing with { Text = text.ToString() });
    }

    private static ChatToolState StateOf(string status) => status switch
    {
        "in_progress" => ChatToolState.Running,
        "completed" => ChatToolState.Done,
        "failed" => ChatToolState.Failed,
        _ => ChatToolState.Pending
    };

    private void Tool(JsonObject update)
    {
        CloseStreaming();
        var callId = Str(update, "toolCallId");
        var id = "t" + callId;
        var existing = Get<ChatToolItem>(id);
        var name = Str(update, "title") is { Length: > 0 } title ? title : existing?.Name ?? (Str(update, "kind") is { Length: > 0 } kind ? kind : "Tool");
        var state = update.ContainsKey("status") ? StateOf(Str(update, "status")) : existing?.State ?? ChatToolState.Pending;
        var path = (update["locations"] as JsonArray)?.OfType<JsonObject>().Select(location => Str(location, "path")).FirstOrDefault(p => p.Length > 0) ?? existing?.FilePath;
        string? output = existing?.Output;
        if (update["content"] is JsonArray content)
        {
            var text = new StringBuilder();
            foreach (var block in content.OfType<JsonObject>())
            {
                switch (Str(block, "type"))
                {
                    case "content": text.AppendLine(BlockText(block["content"])); break;
                    case "diff":
                        path = Str(block, "path") is { Length: > 0 } diffPath ? diffPath : path;
                        text.AppendLine("--- " + Str(block, "path")).AppendLine("+++ " + Str(block, "path"));
                        foreach (var line in Str(block, "oldText").Split('\n').Where(l => l.Length > 0)) text.Append('-').AppendLine(line);
                        foreach (var line in Str(block, "newText").Split('\n').Where(l => l.Length > 0)) text.Append('+').AppendLine(line);
                        break;
                    case "terminal": text.AppendLine("[terminal " + Str(block, "terminalId") + "]"); break;
                }
            }
            output = text.ToString().TrimEnd();
        }
        var input = update["rawInput"] is { } raw ? AcpLocalJson.Input(raw) : existing?.Input ?? AcpLocalJson.Input(null);
        var truncated = output is { } o && o.Length > ChatContract.OutputLimit;
        if (truncated) full[id] = output!;
        var summary = existing?.Summary ?? (path is { Length: > 0 } ? path : null);
        Put(new ChatToolItem(id, existing?.Seq ?? Next(), name, input, state) { Summary = summary, Output = truncated ? output![..ChatContract.OutputLimit] : output, OutputTruncated = truncated, FilePath = path });
    }

    /// <summary>The agent's session options as the header's mode, model and effort lists.</summary>
    public void ApplyConfig(JsonArray? options)
    {
        foreach (var option in (options ?? []).OfType<JsonObject>())
        {
            var values = (option["options"] as JsonArray ?? []).OfType<JsonObject>().Where(value => value.ContainsKey("value")).ToArray();
            var current = Str(option, "currentValue");
            switch (Str(option, "category"))
            {
                case "model":
                    Update(header => header with
                    {
                        Model = current,
                        Models = [.. values.Select(value => new ChatModel(Str(value, "value"), Str(value, "name")) { Description = Str(value, "description") is { Length: > 0 } d ? d : null })]
                    });
                    break;
                case "mode":
                    Update(header => header with
                    {
                        PermissionMode = current,
                        PermissionModes = [.. values.Select(value => new ChatMode(Str(value, "value"), Str(value, "name"), Str(value, "description")))]
                    });
                    break;
                case "thought_level":
                    Update(header => header with { Effort = current });
                    break;
            }
        }
    }

    /// <summary>Modes from <c>session/new</c>: <c>{currentModeId, availableModes: [{id, name, description}]}</c>.</summary>
    public void ApplyModes(JsonObject? modes)
    {
        if (modes is null) return;
        Update(header => header with
        {
            PermissionMode = Str(modes, "currentModeId"),
            PermissionModes = [.. (modes["availableModes"] as JsonArray ?? []).OfType<JsonObject>().Select(mode => new ChatMode(Str(mode, "id"), Str(mode, "name"), Str(mode, "description")))]
        });
    }

    /// <summary>Models from <c>session/new</c> (<c>{currentModelId, availableModels: [{modelId, name}]}</c>).</summary>
    public void ApplyModels(JsonObject? models)
    {
        if (models is null) return;
        Update(header => header with
        {
            Model = Str(models, "currentModelId"),
            Models = [.. (models["availableModels"] as JsonArray ?? []).OfType<JsonObject>().Select(model => new ChatModel(Str(model, "modelId"), Str(model, "name")) { Description = Str(model, "description") is { Length: > 0 } d ? d : null })]
        });
    }

    public ChatPermissionItem AddPermission(string requestId, JsonObject toolCall, IReadOnlyList<AcpPermissionOption> options)
    {
        CloseStreaming();
        var title = Str(toolCall, "title");
        var always = options.FirstOrDefault(option => option.Kind == "allow_always");
        var item = new ChatPermissionItem("p" + ++counter, Next(), requestId, title.Length > 0 ? title : Str(toolCall, "kind"), AcpLocalJson.Input(toolCall["rawInput"]))
        {
            Title = title,
            Prompt = title.Length > 0 ? $"Allow \"{title}\"?" : "Allow this action?",
            AlwaysLabel = always?.Name,
            Decision = ChatPermissionDecision.Pending
        };
        Put(item);
        return item;
    }

    public void Settle(string requestId, ChatPermissionDecision decision)
    {
        var item = items.OfType<ChatPermissionItem>().FirstOrDefault(p => p.RequestId == requestId);
        if (item is not null) Put(item with { Decision = decision });
    }
}

/// <summary>One choice of a permission request: the agent's option id and label, and its kind (allow_once, allow_always, reject_once, reject_always).</summary>
internal sealed record AcpPermissionOption(string Id, string Name, string Kind);