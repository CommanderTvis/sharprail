using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>What starts a session: the executable, the directory, and how it continues a conversation.</summary>
internal sealed record PiSpawn(string Command, string Cwd)
{
    public string? Model { get; init; }
    public string? Thinking { get; init; }
    /// <summary>A stored session file (or id) to continue; null starts a new session.</summary>
    public string? Session { get; init; }
}

/// <summary>
/// One <c>pi --mode rpc</c> child and the conversation it folds into. Commands go to stdin as LF-delimited JSON, events and
/// responses come back on stdout (https://github.com/badlogic/pi-mono, docs/rpc.md); everything is asynchronous.
/// </summary>
internal sealed class PiChatSession : IAsyncDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock gate = new();
    private readonly PiFolder folder = new();
    private readonly string workspaceId, chatId;
    private readonly Action<ChatPush> publish;
    private readonly Action<string> log;
    private readonly Action<string, string?> remember;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> pending = [];
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<string> errors = new();
    private IPiLink? child;
    private int requests;
    private int closing;
    private bool flushing;
    private string? sessionFile;

    public PiChatSession(string workspaceId, string chatId, Action<ChatPush> publish, Action<string> log, Action<string, string?> remember)
    {
        this.workspaceId = workspaceId;
        this.chatId = chatId;
        this.publish = publish;
        this.log = log;
        this.remember = remember;
    }

    public bool Active => child is { Alive: true };

    public ChatHeader Header
    {
        get { lock (gate) return folder.Header; }
    }

    public string? SessionFile => sessionFile;

    public IReadOnlyList<ChatPush> Snapshot()
    {
        lock (gate)
        {
            var pushes = new List<ChatPush>(folder.Items.Count + 1);
            pushes.AddRange(folder.Items.Select(item => new ChatItemPush(workspaceId, chatId, item)));
            pushes.Add(new ChatHeaderPush(workspaceId, chatId, folder.Header));
            return pushes;
        }
    }

    public ChatHeader Begin(PiSpawn spawn)
    {
        lock (gate)
        {
            folder.Update(header => header with
            {
                Status = ChatStatus.Starting,
                Cwd = spawn.Cwd,
                Model = spawn.Model,
                Effort = spawn.Thinking,
                Error = null,
                PermissionMode = "default",
                PermissionModes = [new ChatMode("default", "Runs tools freely", "Pi has no permission prompts: its tools run without asking.")]
            });
            FlushNow();
            return folder.Header;
        }
    }

    public async Task StartAsync(PiSpawn spawn)
    {
        try
        {
            var link = PiLinks.InTab is { } inTab
                ? await inTab(spawn.Cwd, spawn.Session, spawn.Model, spawn.Thinking, lifetime.Token)
                : await ProcessPiLink.StartAsync(spawn, text =>
                {
                    lock (errors)
                    {
                        errors.Enqueue(text);
                        while (errors.Count > 8) errors.Dequeue();
                    }
                });
            child = link;
            _ = Task.Run(() => ReadAsync(link));
            await InitializeAsync(spawn);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            lock (gate)
            {
                folder.SetStatus(ChatStatus.Failed, error is System.ComponentModel.Win32Exception ? $"{spawn.Command} was not found. Install Pi, or set its command in Settings." : error.Message);
                FlushNow();
            }
        }
    }

    private async Task InitializeAsync(PiSpawn spawn)
    {
        var state = await RequestAsync(new JsonObject { ["type"] = "get_state" });
        ApplyState(state);
        if (spawn.Thinking is { Length: > 0 } thinking)
        {
            try
            {
                await RequestAsync(new JsonObject { ["type"] = "set_thinking_level", ["level"] = thinking });
                lock (gate) folder.Update(header => header with { Effort = thinking });
            }
            catch (Exception error) when (error is TimeoutException or InvalidOperationException) { log("Pi did not take the thinking level: " + error.Message); }
        }
        try
        {
            var models = await RequestAsync(new JsonObject { ["type"] = "get_available_models" });
            lock (gate) { folder.Update(header => header with { Models = ModelsOf(models) }); ScheduleFlush(); }
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException) { log("Pi models were not listed: " + error.Message); }
        try
        {
            var commands = await RequestAsync(new JsonObject { ["type"] = "get_commands" });
            lock (gate) { folder.Update(header => header with { Commands = CommandsOf(commands) }); ScheduleFlush(); }
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException) { log("Pi commands were not listed: " + error.Message); }
        if (spawn.Session is { Length: > 0 })
        {
            var messages = await RequestAsync(new JsonObject { ["type"] = "get_messages" });
            lock (gate)
            {
                if (messages.TryGetProperty("messages", out var stored) && stored.ValueKind == JsonValueKind.Array) folder.Replay(stored.EnumerateArray().Select(message => message.Clone()));
                ScheduleFlush();
            }
        }
        lock (gate)
        {
            if (folder.Header.Status == ChatStatus.Starting) folder.SetStatus(ChatStatus.Idle);
            FlushNow();
        }
    }

    private void ApplyState(JsonElement state)
    {
        lock (gate)
        {
            var model = state.TryGetProperty("model", out var current) && current.ValueKind == JsonValueKind.Object ? $"{PiFolder.Str(current, "provider")}/{PiFolder.Str(current, "id")}" : null;
            var window = state.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("contextWindow", out var w) && w.TryGetInt64(out var tokens) ? tokens : (long?)null;
            sessionFile = PiFolder.Str(state, "sessionFile") is { Length: > 0 } file ? file : sessionFile;
            folder.Update(header => header with
            {
                Model = model,
                Effort = PiFolder.Str(state, "thinkingLevel") is { Length: > 0 } level ? level : header.Effort,
                SessionId = PiFolder.Str(state, "sessionId") is { Length: > 0 } id ? id : header.SessionId,
                Title = PiFolder.Str(state, "sessionName") is { Length: > 0 } name ? name : header.Title,
                Usage = header.Usage with { ContextWindow = window ?? header.Usage.ContextWindow }
            });
        }
        remember(chatId, sessionFile);
    }

    private static IReadOnlyList<ChatModel> ModelsOf(JsonElement response)
    {
        if (!response.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
        return [.. models.EnumerateArray().Select(model => new ChatModel($"{PiFolder.Str(model, "provider")}/{PiFolder.Str(model, "id")}", PiFolder.Str(model, "name") is { Length: > 0 } name ? name : PiFolder.Str(model, "id"))
        {
            Provider = PiFolder.Str(model, "provider"),
            // The picker leads with the first model of each family, so a Claude model needs its family named.
            Family = new[] { "opus", "sonnet", "haiku" }.FirstOrDefault(family => PiFolder.Str(model, "id").Contains(family, StringComparison.OrdinalIgnoreCase)),
            // A model that lists its own levels (the provider's say) wins over the one list every reasoning model gets.
            Efforts = model.TryGetProperty("efforts", out var efforts) && efforts.ValueKind == JsonValueKind.Array ? [.. efforts.EnumerateArray().Select(level => level.GetString() ?? "")]
                : PiFolder.Bool(model, "reasoning") ? PiThinking.Levels : []
        })];
    }

    private static IReadOnlyList<ChatCommand> CommandsOf(JsonElement response)
    {
        if (!response.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array) return [];
        return [.. commands.EnumerateArray().Select(command => new ChatCommand(PiFolder.Str(command, "name"), PiFolder.Str(command, "description") is { Length: > 0 } text ? text : null) { Source = PiFolder.Str(command, "source") })];
    }

    private async Task ReadAsync(IPiLink link)
    {
        try
        {
            while (await link.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length == 0) continue;
                JsonElement element;
                try { element = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { continue; }
                Handle(element);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        if (Volatile.Read(ref closing) == 0)
        {
            string detail;
            lock (errors) detail = string.Join(" ", errors);
            lock (gate)
            {
                folder.SetStatus(ChatStatus.Exited, detail.Length > 0 ? detail : null);
                FlushNow();
            }
        }
        foreach (var waiting in pending.Values.ToArray()) waiting.TrySetException(new InvalidOperationException("Pi exited."));
    }

    private void Handle(JsonElement line)
    {
        var type = PiFolder.Str(line, "type");
        if (type == "response")
        {
            if (PiFolder.Str(line, "id") is { Length: > 0 } id)
            {
                TaskCompletionSource<JsonElement>? waiting;
                lock (pending) pending.Remove(id, out waiting);
                if (waiting is null) return;
                if (PiFolder.Bool(line, "success")) waiting.TrySetResult(line.TryGetProperty("data", out var data) ? data.Clone() : default);
                else waiting.TrySetException(new InvalidOperationException(PiFolder.Str(line, "error") is { Length: > 0 } text ? text : "Pi refused the command."));
            }
            return;
        }
        if (type == "extension_ui_request")
        {
            ExtensionRequest(line);
            return;
        }
        lock (gate)
        {
            if (!folder.Apply(line)) log("Pi sent an event this host does not fold: " + type);
            ScheduleFlush();
        }
    }

    // A dialog an extension opens cannot be shown in a chat yet: it is answered as cancelled so Pi does not wait, and said so.
    private void ExtensionRequest(JsonElement request)
    {
        var method = PiFolder.Str(request, "method");
        if (method == "notify")
        {
            lock (gate) { folder.Notice(ChatNoticeLevel.Info, PiFolder.Str(request, "message")); ScheduleFlush(); }
            return;
        }
        if (method is not ("select" or "confirm" or "input" or "editor")) return;
        lock (gate) { folder.Notice(ChatNoticeLevel.Warning, $"An extension asked for input ({PiFolder.Str(request, "title")}) which a chat cannot show yet; it was cancelled."); ScheduleFlush(); }
        _ = TrySendAsync(new JsonObject { ["type"] = "extension_ui_response", ["id"] = PiFolder.Str(request, "id"), ["cancelled"] = true });
    }

    private async Task<JsonElement> RequestAsync(JsonObject command)
    {
        var id = "r" + Interlocked.Increment(ref requests);
        command["id"] = id;
        var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending) pending[id] = waiting;
        try
        {
            await WriteAsync(command.ToJsonString());
            return await waiting.Task.WaitAsync(ResponseTimeout, lifetime.Token);
        }
        catch
        {
            lock (pending) pending.Remove(id);
            throw;
        }
    }

    private async Task TrySendAsync(JsonObject command)
    {
        try { await WriteAsync(command.ToJsonString()); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task WriteAsync(string line)
    {
        var link = child ?? throw new InvalidOperationException("Pi is not running.");
        await writer.WaitAsync(lifetime.Token);
        try { await link.WriteLineAsync(line, lifetime.Token); }
        finally { writer.Release(); }
    }

    public async Task SendAsync(string text, IReadOnlyList<ChatAttachment>? attachments)
    {
        var message = new StringBuilder(text);
        var images = new JsonArray();
        foreach (var attachment in attachments ?? [])
        {
            if (attachment.Data is { Length: > 0 } data && attachment.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true)
                images.Add((JsonNode)new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = attachment.MediaType });
            else if (attachment.Path.Length > 0) message.Append("\n\nAttached: ").Append(attachment.Path);
        }
        var command = new JsonObject { ["type"] = "prompt", ["message"] = message.ToString() };
        if (images.Count > 0) command["images"] = images;
        bool busy;
        lock (gate)
        {
            busy = folder.Header.Status == ChatStatus.Running;
            if (busy)
            {
                folder.AddQueued(text);
                ScheduleFlush();
            }
        }
        if (busy) command["streamingBehavior"] = "steer";
        try { await RequestAsync(command); }
        catch (InvalidOperationException error)
        {
            lock (gate)
            {
                folder.Notice(ChatNoticeLevel.Error, "Pi did not take the message: " + error.Message);
                ScheduleFlush();
            }
            throw;
        }
    }

    public async Task InterruptAsync() => await TrySendAsync(new JsonObject { ["type"] = "abort" });

    public async Task<ChatHeader> SetModelAsync(string value)
    {
        var slash = value.IndexOf('/');
        if (slash <= 0) throw new InvalidOperationException("A Pi model is named provider/id.");
        await RequestAsync(new JsonObject { ["type"] = "set_model", ["provider"] = value[..slash], ["modelId"] = value[(slash + 1)..] });
        lock (gate) { folder.Update(header => header with { Model = value, Pending = null }); FlushNow(); return folder.Header; }
    }

    public async Task<ChatHeader> SetEffortAsync(string value)
    {
        await RequestAsync(new JsonObject { ["type"] = "set_thinking_level", ["level"] = value });
        lock (gate) { folder.Update(header => header with { Effort = value, Pending = null }); FlushNow(); return folder.Header; }
    }

    public string? ItemDetail(string id)
    {
        lock (gate) return folder.Detail(id);
    }

    private void ScheduleFlush()
    {
        if (flushing) return;
        flushing = true;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(FlushInterval, lifetime.Token); }
            catch (OperationCanceledException) { return; }
            lock (gate) FlushNow();
        });
    }

    private void FlushNow()
    {
        flushing = false;
        var (items, removed, reset, header) = folder.Drain();
        if (reset) publish(new ChatResetPush(workspaceId, chatId));
        foreach (var id in removed) publish(new ChatRemovePush(workspaceId, chatId, id));
        foreach (var item in items) publish(new ChatItemPush(workspaceId, chatId, item));
        if (header || reset) publish(new ChatHeaderPush(workspaceId, chatId, folder.Header));
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref closing, 1) == 1) return;
        if (child is not { } link) return;
        await link.CloseAsync();
        lock (gate) { folder.SetStatus(ChatStatus.Exited); FlushNow(); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        await lifetime.CancelAsync();
        child?.Dispose();
    }
}