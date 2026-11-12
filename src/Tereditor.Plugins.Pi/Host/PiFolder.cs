using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>
/// Folds Pi's RPC events and stored messages into the chat's ordered, id-keyed item list. Pure state: no process and no
/// publishing, so the checks drive it with recorded lines. Not thread-safe; the session serializes calls.
/// </summary>
internal sealed class PiFolder
{
    private readonly List<ChatItem> items = [];
    private readonly Dictionary<string, int> index = [];
    private readonly Dictionary<string, string> full = [];
    private readonly HashSet<string> changed = [];
    private readonly List<string> removed = [];
    private long seq;
    private int messages;
    private int queued;
    private bool reset;
    private bool headerChanged;

    public ChatHeader Header { get; private set; } = new(ChatStatus.Starting);

    public IReadOnlyList<ChatItem> Items => items;

    public string? Detail(string id) => full.GetValueOrDefault(id);

    /// <summary>What changed since the last call: the items to publish, the ids to drop, whether to start over and whether the header moved.</summary>
    public (IReadOnlyList<ChatItem> Items, IReadOnlyList<string> Removed, bool Reset, bool Header) Drain()
    {
        IReadOnlyList<ChatItem> drained = [.. changed.Select(id => items[index[id]]).OrderBy(item => item.Seq)];
        IReadOnlyList<string> dropped = [.. removed];
        var result = (drained, dropped, reset, headerChanged);
        changed.Clear();
        removed.Clear();
        reset = false;
        headerChanged = false;
        return result;
    }

    public void Update(Func<ChatHeader, ChatHeader> edit)
    {
        Header = edit(Header);
        headerChanged = true;
    }

    public void SetStatus(ChatStatus status, string? error = null) => Update(header => header with { Status = status, Error = error ?? (status is ChatStatus.Failed or ChatStatus.Exited ? header.Error : null) });

    public void Clear()
    {
        items.Clear();
        index.Clear();
        full.Clear();
        changed.Clear();
        removed.Clear();
        messages = 0;
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

    public ChatUserItem AddQueued(string text)
    {
        var item = new ChatUserItem("q" + ++queued, Next(), text) { Queued = true };
        Put(item);
        return item;
    }

    public void Notice(ChatNoticeLevel level, string text) => Put(new ChatNoticeItem("n" + Next(), seq, level, text));

    /// <summary>Applies one stdout line that is an event (not a response). Returns false for a line it does not know.</summary>
    public bool Apply(JsonElement line)
    {
        var type = Str(line, "type");
        switch (type)
        {
            case "agent_start":
                SetStatus(ChatStatus.Running);
                return true;
            case "agent_end":
                CloseStreaming();
                SetStatus(ChatStatus.Idle);
                return true;
            case "message_start" when line.TryGetProperty("message", out var started):
                Start(started);
                return true;
            case "message_update" when line.TryGetProperty("assistantMessageEvent", out var delta):
                Delta(delta);
                return true;
            case "message_end" when line.TryGetProperty("message", out var ended):
                End(ended);
                return true;
            case "tool_execution_start":
                Tool(line, ChatToolState.Running, null);
                return true;
            case "tool_execution_update" when line.TryGetProperty("partialResult", out var partial):
                Tool(line, ChatToolState.Running, ResultText(partial));
                return true;
            case "tool_execution_end":
                Tool(line, Bool(line, "isError") ? ChatToolState.Failed : ChatToolState.Done, line.TryGetProperty("result", out var result) ? ResultText(result) : "");
                return true;
            case "compaction_start":
                Update(header => header with { Activity = "Compacting" });
                return true;
            case "compaction_end":
                Update(header => header with { Activity = null });
                if (Str(line, "errorMessage") is { Length: > 0 } failure) Notice(ChatNoticeLevel.Error, "Compaction failed: " + failure);
                return true;
            case "auto_retry_start":
                Notice(ChatNoticeLevel.Info, $"Retrying (attempt {Int(line, "attempt")} of {Int(line, "maxAttempts")}): {Str(line, "errorMessage")}");
                return true;
            case "auto_retry_end" when !Bool(line, "success"):
                Notice(ChatNoticeLevel.Error, "Gave up after retrying: " + Str(line, "finalError"));
                return true;
            case "auto_retry_end" or "queue_update" or "turn_start" or "turn_end":
                return true;
            case "extension_error":
                Notice(ChatNoticeLevel.Warning, $"An extension failed ({Str(line, "event")}): {Str(line, "error")}");
                return true;
            default:
                return false;
        }
    }

    /// <summary>Rebuilds the list from stored messages (<c>get_messages</c>), as a resumed session shows its past.</summary>
    public void Replay(IEnumerable<JsonElement> stored)
    {
        Clear();
        foreach (var message in stored)
        {
            Start(message);
            End(message);
        }
        CloseStreaming();
    }

    private void Start(JsonElement message)
    {
        switch (Str(message, "role"))
        {
            case "user":
                var text = UserText(message);
                // Pi echoes a prompt the host already showed as queued: that row is released instead of a second one added.
                var waiting = items.OfType<ChatUserItem>().FirstOrDefault(item => item.Queued && item.Text == text);
                if (waiting is not null) Put(waiting with { Queued = false });
                else Put(new ChatUserItem("u" + Next(), seq, text));
                break;
            case "assistant":
                messages++;
                break;
        }
    }

    private void Delta(JsonElement delta)
    {
        var block = Int(delta, "contentIndex");
        switch (Str(delta, "type"))
        {
            case "text_start":
                Put(new ChatAssistantItem(Block("a", block), Next(), "") { Streaming = true });
                break;
            case "text_delta":
                if (Get<ChatAssistantItem>(Block("a", block)) is { } text) Put(text with { Text = text.Text + Str(delta, "delta"), Streaming = true });
                break;
            case "text_end":
                if (Get<ChatAssistantItem>(Block("a", block)) is { } done) Put(done with { Text = Str(delta, "content") is { Length: > 0 } final ? final : done.Text, Streaming = false });
                break;
            case "thinking_start":
                Put(new ChatThinkingItem(Block("t", block), Next(), "") { Streaming = true });
                break;
            case "thinking_delta":
                if (Get<ChatThinkingItem>(Block("t", block)) is { } thought) Put(thought with { Text = thought.Text + Str(delta, "delta"), Streaming = true });
                break;
            case "thinking_end":
                if (Get<ChatThinkingItem>(Block("t", block)) is { } closed) Put(closed with { Text = Str(delta, "content") is { Length: > 0 } final ? final : closed.Text, Streaming = false });
                break;
            case "toolcall_end" when delta.TryGetProperty("toolCall", out var call):
                AddCall(call);
                break;
        }
    }

    private string Block(string prefix, int block) => $"{prefix}{messages}.{block}";

    private void AddCall(JsonElement call)
    {
        var id = Str(call, "id");
        if (id.Length == 0) return;
        var input = call.TryGetProperty("arguments", out var arguments) ? arguments.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        var existing = Get<ChatToolItem>(id);
        var item = new ChatToolItem(id, existing?.Seq ?? Next(), Str(call, "name"), input, existing?.State ?? ChatToolState.Pending)
        {
            Summary = Summary(input),
            FilePath = Str(call, "name") is "edit" or "write" ? Path(input) : null,
            Output = existing?.Output,
            OutputTruncated = existing?.OutputTruncated ?? false
        };
        Put(item);
    }

    private void Tool(JsonElement line, ChatToolState state, string? output)
    {
        var id = Str(line, "toolCallId");
        if (id.Length == 0) return;
        var existing = Get<ChatToolItem>(id);
        if (existing is null)
        {
            var started = new JsonObject
            {
                ["id"] = id,
                ["name"] = Str(line, "toolName"),
                ["arguments"] = line.TryGetProperty("args", out var args) ? JsonNode.Parse(args.GetRawText()) : new JsonObject()
            };
            AddCall(JsonSerializer.SerializeToElement(started, PiLocalJson.Default.JsonObject));
            existing = Get<ChatToolItem>(id);
            if (existing is null) return;
        }
        var cut = output is { Length: > ChatContract.OutputLimit };
        if (output is not null && cut) full[id] = output;
        Put(existing with { State = state, Output = cut ? output![..ChatContract.OutputLimit] : output ?? existing.Output, OutputTruncated = cut });
    }

    private void End(JsonElement message)
    {
        switch (Str(message, "role"))
        {
            case "assistant":
                CloseStreaming();
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var at = 0;
                    foreach (var part in content.EnumerateArray())
                    {
                        // A replayed message has no deltas: its blocks are laid down whole.
                        switch (Str(part, "type"))
                        {
                            case "text" when Get<ChatAssistantItem>(Block("a", at)) is null:
                                Put(new ChatAssistantItem(Block("a", at), Next(), Str(part, "text")));
                                break;
                            case "thinking" when Get<ChatThinkingItem>(Block("t", at)) is null:
                                Put(new ChatThinkingItem(Block("t", at), Next(), Str(part, "thinking")));
                                break;
                            case "toolCall":
                                AddCall(part);
                                break;
                        }
                        at++;
                    }
                }
                if (Str(message, "errorMessage") is { Length: > 0 } error && Str(message, "stopReason") == "error") Notice(ChatNoticeLevel.Error, error);
                if (message.TryGetProperty("usage", out var usage)) Account(usage, message);
                break;
            case "toolResult":
                var id = Str(message, "toolCallId");
                if (Get<ChatToolItem>(id) is { } tool && tool.State is ChatToolState.Pending or ChatToolState.Running)
                {
                    var output = ResultText(message);
                    Put(tool with { State = Bool(message, "isError") ? ChatToolState.Failed : ChatToolState.Done, Output = output.Length > ChatContract.OutputLimit ? output[..ChatContract.OutputLimit] : output, OutputTruncated = output.Length > ChatContract.OutputLimit });
                    if (output.Length > ChatContract.OutputLimit) full[id] = output;
                }
                break;
        }
    }

    private void Account(JsonElement usage, JsonElement message)
    {
        long input = Long(usage, "input"), output = Long(usage, "output"), read = Long(usage, "cacheRead"), write = Long(usage, "cacheWrite");
        var cost = usage.TryGetProperty("cost", out var priced) && priced.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetDouble() : 0;
        Update(header => header with
        {
            Usage = header.Usage with
            {
                Input = header.Usage.Input + input,
                Output = header.Usage.Output + output,
                CacheRead = header.Usage.CacheRead + read,
                CacheWrite = header.Usage.CacheWrite + write,
                ContextTokens = input + read + write + output,
                CostUsd = header.Usage.CostUsd + cost
            }
        });
    }

    private void CloseStreaming()
    {
        foreach (var item in items.ToArray())
        {
            if (item is ChatAssistantItem { Streaming: true } text) Put(text with { Streaming = false });
            else if (item is ChatThinkingItem { Streaming: true } thought) Put(thought with { Streaming = false });
        }
    }

    internal static string UserText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
            if (Str(part, "type") == "text") text.Append(Str(part, "text"));
        return text.ToString();
    }

    private static string ResultText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
            if (Str(part, "type") == "text") text.Append(Str(part, "text"));
        return text.ToString();
    }

    private static string? Summary(JsonElement input)
    {
        foreach (var name in new[] { "command", "path", "file_path", "pattern", "query", "url" })
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                return text.Length > 200 ? text[..200] : text;
        return null;
    }

    private static string? Path(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object && (input.TryGetProperty("path", out var path) || input.TryGetProperty("file_path", out path)) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;

    internal static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    internal static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    internal static int Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}