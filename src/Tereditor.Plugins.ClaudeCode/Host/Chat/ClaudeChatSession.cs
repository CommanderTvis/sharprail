using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tereditor.Plugins.ClaudeCode.Host.Chat;

/// <summary>What survives an app restart about a chat: enough to respawn it under its session with the choices it had.</summary>
internal sealed record ChatRecord(string WorkspaceId, string ChatId)
{
    public string? SessionId { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? PermissionMode { get; init; }
    public bool? Chrome { get; init; }
    public string? Advisor { get; init; }
    public string? Title { get; init; }
    /// <summary>A fork that has not started yet: the session it continues from and the prompt it branches before.</summary>
    public string? ForkOf { get; init; }
    public string? ForkBefore { get; init; }
}

/// <summary>How one spawn continues the conversation.</summary>
internal sealed record ChatSpawn(string? SessionId, string? Resume, bool Fork, string? ResumeAt);

/// <summary>The command line, environment and directory of one spawn, as the host's launch rules compose them.</summary>
internal sealed record ChatLaunch(string Executable, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string Cwd);

/// <summary>What a session needs from the host that owns it.</summary>
internal sealed record ChatDeps(
    Func<ChatRecord, ChatSpawn, ChatLaunch> Launch,
    Action<ChatPush> Push,
    Action<ClaudeCodeStatusPush> Status,
    Action<ChatRecord> Save,
    Action<string> Warn,
    Func<ChatRecord, CancellationToken, Task<(IReadOnlyDictionary<string, string> Environment, string Cwd)>> Shell);

/// <summary>
/// One chat: the headless child, the folder that turns its messages into items, the control requests in flight and the
/// pushes that tell the view what changed. Everything the child says is folded under one lock; I/O happens outside it.
/// </summary>
internal sealed class ClaudeChatSession(ChatRecord initial, ChatDeps deps)
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(16);

    private readonly Lock gate = new();
    private readonly ChatFolder folder = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> controls = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<ShellEntry> shells = [];
    private ChatRecord record = initial;
    private ChatProcess? process;
    private int generation;
    private bool awaitingNewSession;
    private long controlSequence;
    private int flushing;
    private bool closed;

    public string ChatId => record.ChatId;

    public ChatHeader Header
    {
        get { lock (gate) return folder.Header; }
    }

    public ChatRecord Record
    {
        get { lock (gate) return record; }
    }

    /// <summary>The conversation so far as pushes, the same list a late subscriber replays.</summary>
    public IReadOnlyList<ChatPush> Snapshot()
    {
        lock (gate)
            return [.. folder.Items.Select(item => (ChatPush)new ChatItemPush(record.WorkspaceId, record.ChatId, item)), new ChatHeaderPush(record.WorkspaceId, record.ChatId, folder.Header)];
    }

    /// <summary>The running child's process id, or null when none runs.</summary>
    public int? Pid
    {
        get { lock (gate) return process is { } running && !closed ? running.Pid : null; }
    }

    public bool Active
    {
        get { lock (gate) return !closed && folder.Header.Status is not (ChatStatus.Exited or ChatStatus.Failed); }
    }

    /// <summary>Marks the chat as starting at once, so the header an open returns is never the stale one of a chat that exited.</summary>
    public ChatHeader Begin()
    {
        lock (gate)
        {
            folder.SetStatus(ChatStatus.Starting);
            FlushNow();
            return folder.Header;
        }
    }

    /// <summary>
    /// Brings the chat up: replays the transcript of the session it resumes into the items first, so the view has the
    /// conversation before the child is ready, then spawns. Runs off the caller's thread.
    /// </summary>
    public Task StartAsync(ChatSpawn spawn, string? transcript, string? stopBefore = null) => Task.Run(async () =>
    {
        try
        {
            lock (gate)
                folder.SetHeader(header => header with
                {
                    SessionId = record.SessionId ?? header.SessionId,
                    Cwd = record.Cwd ?? header.Cwd ?? record.WorkspaceId,
                    Model = record.Model ?? header.Model,
                    Effort = record.Effort ?? header.Effort,
                    PermissionMode = record.PermissionMode ?? header.PermissionMode,
                    Title = record.Title ?? header.Title
                });
            if (transcript is not null)
            {
                lock (gate)
                {
                    folder.Clear();
                    deps.Push(new ChatResetPush(record.WorkspaceId, record.ChatId));
                    var replayed = ChatTranscript.Replay(folder, transcript, stopBefore, lifetime.Token);
                    // Branching before a prompt continues from the message that preceded it; with none, the branch starts afresh.
                    if (stopBefore is not null)
                        spawn = replayed.LastUuid is { } at ? spawn with { ResumeAt = at } : new ChatSpawn(Guid.NewGuid().ToString(), null, false, null);
                    folder.SetStatus(ChatStatus.Starting);
                    FlushNow();
                }
            }
            else lock (gate) { folder.SetStatus(ChatStatus.Starting); FlushNow(); }
            await SpawnAsync(spawn);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Fail("Could not start Claude Code: " + error.Message);
        }
    });

    private async Task SpawnAsync(ChatSpawn spawn)
    {
        ChatRecord current;
        int mine;
        lock (gate)
        {
            current = record;
            mine = ++generation;
            awaitingNewSession = false;
            stderrTail.Clear();
        }
        var launch = deps.Launch(current, spawn);
        var advisorAt = launch.Arguments.ToList().IndexOf("--advisor");
        lock (gate) folder.SetHeader(header => header with { Advisor = advisorAt >= 0 && advisorAt + 1 < launch.Arguments.Count ? launch.Arguments[advisorAt + 1] : null, Chrome = launch.Arguments.Contains("--chrome"), CanSendQueuedNow = true, CanRunShell = true });
        lock (gate) folder.AllowBypass = current.PermissionMode == "bypassPermissions" || launch.Arguments.Any(argument => argument is "--dangerously-skip-permissions" or "--allow-dangerously-skip-permissions");
        var started = ChatProcess.Start(launch.Executable, launch.Arguments, launch.Environment, launch.Cwd,
            line => OnLineAsync(mine, line), text => OnError(mine, text));
        lock (gate)
        {
            if (closed || mine != generation) { _ = started.StopAsync(); return; }
            process = started;
        }
        _ = started.Exited.ContinueWith(task => OnExit(mine, task.IsCompletedSuccessfully ? task.Result : -1), TaskScheduler.Default);
        // The initialize request returns the models and commands; the chat is ready once it, or the init message, says so.
        _ = Task.Run(async () =>
        {
            try
            {
                var response = await ControlAsync(mine, new JsonObject { ["subtype"] = "initialize" });
                lock (gate)
                {
                    if (mine != generation) return;
                    folder.ApplyInitialize(response);
                    if (folder.Header.Status == ChatStatus.Starting) folder.SetStatus(ChatStatus.Idle);
                    ScheduleFlush();
                }
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException or OperationCanceledException or IOException)
            {
                lock (gate) if (mine == generation && folder.Header.Status == ChatStatus.Starting && process is not null) { folder.SetStatus(ChatStatus.Idle); ScheduleFlush(); }
            }
        });
    }

    private const int StderrTailLines = 20;
    private const int StderrTailChars = 4096;
    private readonly Queue<string> stderrTail = new();

    private void OnError(int mine, string text)
    {
        lock (gate)
        {
            if (mine != generation) return;
            var clean = ChatFolder.Sanitise(text).TrimEnd();
            if (clean.Length > 0)
            {
                stderrTail.Enqueue(clean);
                while (stderrTail.Count > StderrTailLines) stderrTail.Dequeue();
            }
            folder.AddNotice(ChatNoticeLevel.Warning, text);
            ScheduleFlush();
        }
    }

    private void OnExit(int mine, int code)
    {
        lock (gate)
        {
            if (mine != generation || closed) return;
            process = null;
            foreach (var pending in controls.Values) pending.TrySetException(new IOException("Claude Code exited."));
            controls.Clear();
            folder.Settle(ChatToolState.Failed);
            if (code == 0 || folder.Header.Status == ChatStatus.Exited) folder.SetStatus(ChatStatus.Exited);
            else
            {
                var detail = string.Join('\n', stderrTail);
                if (detail.Length > StderrTailChars) detail = detail[^StderrTailChars..];
                folder.AddNotice(ChatNoticeLevel.Error, $"Claude Code exited with code {code}." + (detail.Length > 0 ? "\n" + detail : ""));
                folder.SetStatus(ChatStatus.Failed, $"Claude Code exited with code {code}.");
                ReportStatus("stop_failure");
            }
            FlushNow();
        }
    }

    private void Fail(string text)
    {
        lock (gate)
        {
            folder.AddNotice(ChatNoticeLevel.Error, text);
            folder.SetStatus(ChatStatus.Failed, text);
            ReportStatus("stop_failure");
            FlushNow();
        }
    }

    private async Task OnLineAsync(int mine, string line)
    {
        JsonElement message;
        try { message = JsonElement.Parse(line); }
        catch (JsonException) { return; }
        if (message.ValueKind != JsonValueKind.Object) return;
        string? refusal = null;
        string? refusedId = null;
        lock (gate)
        {
            if (mine != generation) return;
            switch (ChatFolder.Str(message, "type"))
            {
                case "control_response":
                    ControlResponse(message);
                    return;
                case "control_request":
                    (refusedId, refusal) = ControlRequest(message);
                    break;
                case "control_cancel_request":
                    if (ChatFolder.Str(message, "request_id") is { } cancelled)
                    {
                        folder.Decide(cancelled, ChatPermissionDecision.Denied);
                        folder.DecideQuestion(cancelled, ChatPermissionDecision.Denied, null);
                        AfterPermission();
                    }
                    break;
                default:
                    Fold(message);
                    break;
            }
        }
        if (refusedId is not null)
            await WriteAsync(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = refusedId, ["error"] = refusal } });
    }

    private void Fold(JsonElement message)
    {
        var type = ChatFolder.Str(message, "type");
        var folded = folder.Apply(message);
        if (folder.TakeCleared())
        {
            // /clear: the CLI starts a new conversation, so the rows go, the counters restart and the next message names the new session.
            folder.ResetConversation();
            deps.Push(new ChatResetPush(record.WorkspaceId, record.ChatId));
            folder.AddNotice(ChatNoticeLevel.Info, "Conversation cleared");
            awaitingNewSession = true;
            if (folder.ResetConversationId is { } named)
            {
                awaitingNewSession = false;
                record = record with { SessionId = named };
                deps.Save(record);
                folder.SetHeader(current => current with { SessionId = named });
            }
        }
        if (awaitingNewSession && ChatFolder.Str(message, "parent_tool_use_id") is null && ChatFolder.Str(message, "session_id") is { } fresh && fresh != record.SessionId)
        {
            awaitingNewSession = false;
            record = record with { SessionId = fresh };
            deps.Save(record);
            folder.SetHeader(current => current with { SessionId = fresh });
            ReportStatus("session_start");
        }
        if (folded.Signal == ChatFolder.Signal.Init)
        {
            var header = folder.Header;
            record = record with { SessionId = header.SessionId ?? record.SessionId, Cwd = header.Cwd ?? record.Cwd, Model = header.Model ?? record.Model, PermissionMode = header.PermissionMode ?? record.PermissionMode, ForkOf = null, ForkBefore = null };
            deps.Save(record);
            folder.SetHeader(current => current with { Pending = null });
            awaitingNewSession = false;
            if (header.Status == ChatStatus.Starting) folder.SetStatus(ChatStatus.Idle);
            ReportStatus("session_start");
        }
        else if (folded.Signal == ChatFolder.Signal.Result)
        {
            var failed = folded.Failed;
            folder.Settle(ChatToolState.Failed);
            folder.SetStatus(ChatStatus.Idle);
            ReportStatus(failed ? "stop_failure" : "stop");
        }
        else if (type is "stream_event" or "assistant" && folder.Header.Status == ChatStatus.Idle)
        {
            // A turn the CLI started on its own, such as one a background task's notification woke.
            folder.SetStatus(ChatStatus.Running);
            ReportStatus("prompt_submit");
        }
        ScheduleFlush();
    }

    private void ControlResponse(JsonElement message)
    {
        if (!message.TryGetProperty("response", out var body) || ChatFolder.Str(body, "request_id") is not { } id || !controls.Remove(id, out var pending)) return;
        if (ChatFolder.Str(body, "subtype") == "error") pending.TrySetException(new InvalidOperationException(ChatFolder.Str(body, "error") ?? "Claude Code refused the request."));
        else pending.TrySetResult(body.TryGetProperty("response", out var result) ? result.Clone() : ClaudeJson.EmptyObject());
    }

    // A request from the CLI. Only can_use_tool is ours to answer; anything else is refused so the CLI does not wait.
    private (string? RefusedId, string? Reason) ControlRequest(JsonElement message)
    {
        var id = ChatFolder.Str(message, "request_id");
        if (id is null || !message.TryGetProperty("request", out var request)) return (null, null);
        if (ChatFolder.Str(request, "subtype") != "can_use_tool") return (id, "Not supported by tereditor's chat.");
        var tool = ChatFolder.Str(request, "tool_name") ?? "tool";
        var input = request.TryGetProperty("input", out var raw) ? raw.Clone() : ClaudeJson.EmptyObject();
        if (tool == "AskUserQuestion")
        {
            folder.AddQuestion(id, input, ChatFolder.Str(request, "tool_use_id") is { } askUse ? folder.Item(askUse)?.ParentId : null, ChatFolder.Str(request, "tool_use_id"));
            folder.SetStatus(ChatStatus.AwaitingPermission);
            ReportStatus("permission_request");
            ScheduleFlush();
            return (null, null);
        }
        JsonElement? suggestions = request.TryGetProperty("permission_suggestions", out var found) && found.ValueKind == JsonValueKind.Array ? found.Clone() : null;
        var toolUse = ChatFolder.Str(request, "tool_use_id");
        var parent = toolUse is not null && folder.Item(toolUse)?.ParentId is { } nested ? nested : null;
        folder.AddPermission(id, tool, input, toolUse, ChatFolder.Str(request, "title") ?? ChatFolder.Str(request, "display_name"), ChatFolder.Str(request, "description"), suggestions, parent);
        folder.SetStatus(ChatStatus.AwaitingPermission);
        ReportStatus("permission_request");
        ScheduleFlush();
        return (null, null);
    }

    private void AfterPermission()
    {
        if (folder.Header.Status == ChatStatus.AwaitingPermission && !folder.HasPendingPermission())
        {
            folder.SetStatus(ChatStatus.Running);
            ReportStatus("prompt_submit");
        }
        ScheduleFlush();
    }

    private void ReportStatus(string @event)
    {
        var header = folder.Header;
        deps.Status(new ClaudeCodeStatusPush(record.WorkspaceId, record.ChatId,
            new AgentStatusReport(@event) { SessionId = header.SessionId ?? record.SessionId, Cwd = header.Cwd ?? record.Cwd, Model = header.Model, Effort = record.Effort, ToolName = null },
            ClaudeValues.StatusForAgentEvent(@event)));
    }

    // At most one push per changed item per interval: the first change arms a short timer and everything that lands before
    // it fires goes out together.
    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref flushing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(FlushInterval, lifetime.Token); }
            catch (OperationCanceledException) { }
            Interlocked.Exchange(ref flushing, 0);
            lock (gate) FlushNow();
        });
    }

    // Callers hold the gate.
    private void FlushNow()
    {
        var (items, header, removed) = folder.Drain();
        foreach (var id in removed) deps.Push(new ChatRemovePush(record.WorkspaceId, record.ChatId, id));
        foreach (var item in items) deps.Push(new ChatItemPush(record.WorkspaceId, record.ChatId, item));
        if (header is not null) deps.Push(new ChatHeaderPush(record.WorkspaceId, record.ChatId, header));
    }

    private Task WriteAsync(JsonObject message)
    {
        ChatProcess? target;
        lock (gate) target = process;
        return target is null ? Task.FromException(new InvalidOperationException("Claude Code is not running.")) : target.WriteAsync(message.ToJsonString(), lifetime.Token);
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private async Task<JsonElement> ControlAsync(int mine, JsonObject request)
    {
        var id = "sr-" + Interlocked.Increment(ref controlSequence);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (mine != generation || process is null) throw new InvalidOperationException("Claude Code is not running.");
            controls[id] = pending;
        }
        try
        {
            await WriteAsync(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
            return await pending.Task.WaitAsync(ControlTimeout, lifetime.Token);
        }
        finally { lock (gate) controls.Remove(id); }
    }

    private Task<JsonElement> ControlAsync(JsonObject request)
    {
        int mine;
        lock (gate) mine = generation;
        return ControlAsync(mine, request);
    }

    public async Task SendAsync(string text, IReadOnlyList<ChatAttachment>? attachments)
    {
        var uuid = Guid.NewGuid().ToString();
        if (attachments?.FirstOrDefault(attachment => attachment.Data is { Length: > ChatAttachment.MaxDataBytes }) is { } tooBig)
            throw new InvalidOperationException($"{tooBig.Name} is larger than {ChatAttachment.MaxDataBytes / (1024 * 1024)} MB, which is the most one attachment may be.");
        // Images and PDFs the UI sent as bytes go out as content blocks; anything else is a path the CLI reads, mentioned as @path.
        var blocks = new List<JsonNode>();
        var mentions = new List<string>();
        foreach (var attachment in attachments ?? [])
        {
            if (attachment.Data is { Length: > 0 } data)
            {
                var type = attachment.MediaType is { Length: > 0 } declared ? declared : "application/octet-stream";
                blocks.Add(new JsonObject
                {
                    ["type"] = type.StartsWith("image/", StringComparison.Ordinal) ? "image" : "document",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = type, ["data"] = Convert.ToBase64String(data) }
                });
            }
            else if (attachment.Path.Length > 0) mentions.Add("@" + attachment.Path);
        }
        var prompt = mentions.Count > 0 ? text + "\n\n" + string.Join("\n", mentions) : text;
        List<ShellEntry> attached;
        // The transcript keeps the names, not the bytes.
        var shown = attachments?.Select(attachment => attachment with { Data = null }).ToList();
        lock (gate)
        {
            if (process is null) throw new InvalidOperationException("The chat is not running.");
            attached = TakeShells();
            var running = folder.Header.Status is ChatStatus.Running or ChatStatus.AwaitingPermission;
            // The finished bash-mode cards take the ids their echo will have, so the echo replaces them in place.
            for (var at = 0; at < attached.Count; at++) folder.RekeyShell(attached[at].Id, $"m:{uuid}:{at * 2}");
            folder.AddUser(text, shown, running, "u:" + uuid, uuid);
            if (!running)
            {
                folder.SetStatus(ChatStatus.Running);
                ReportStatus("prompt_submit");
            }
            ScheduleFlush();
        }
        var content = new List<JsonNode>(blocks);
        if (prompt.Length > 0 || content.Count == 0) content.Insert(0, TextBlock(prompt));
        // Each command is its input and its output as blocks of their own, ahead of the prompt, as the CLI writes them.
        content.InsertRange(0, attached.SelectMany(entry => new[] { TextBlock($"<bash-input>{entry.Command}</bash-input>"), TextBlock(entry.Block()) }));
        try { await WriteAsync(new JsonObject { ["type"] = "user", ["uuid"] = uuid, ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray([.. content]) }, ["parent_tool_use_id"] = null }); }
        catch
        {
            // The model never got them: they ride the next send instead.
            lock (gate) shells.InsertRange(0, attached);
            throw;
        }
    }

    /// <summary>One bash-mode command: its card, and once finished what the next send carries.</summary>
    private sealed class ShellEntry(string id, string command)
    {
        public string Id { get; } = id;
        public string Command { get; } = command;
        public CancellationTokenSource Stop { get; } = new();
        public bool Done { get; set; }
        public string Stdout { get; set; } = "";
        public string Stderr { get; set; } = "";
        public string Exit { get; set; } = "";

        /// <summary>The output as the CLI writes it, and the exit code after it when the command did not succeed.</summary>
        public string Block() => $"<bash-stdout>{Stdout}</bash-stdout><bash-stderr>{Stderr}</bash-stderr>" + (Exit.Length > 0 ? $"<bash-exit-code>{Exit}</bash-exit-code>" : "");
    }

    // Callers hold the gate.
    private List<ShellEntry> TakeShells()
    {
        var finished = shells.Where(entry => entry.Done).ToList();
        shells.RemoveAll(entry => entry.Done);
        return finished;
    }

    /// <summary>Starts a bash-mode command and returns; the card streams and the finished one rides the next send.</summary>
    public Task RunShellAsync(string command)
    {
        command = command.Trim();
        if (command.Length == 0) throw new InvalidOperationException("Type a command to run.");
        if (command.Length > 20_000) throw new InvalidOperationException("That command is too long.");
        ShellEntry entry;
        lock (gate)
        {
            if (closed || process is null) throw new InvalidOperationException("The chat is not running.");
            if (shells.Any(other => !other.Done)) throw new InvalidOperationException("A shell command is already running.");
            entry = new ShellEntry("b:" + Guid.NewGuid(), command);
            shells.Add(entry);
            folder.PutShell(entry.Id, command, "", "", "", running: true);
            ScheduleFlush();
        }
        _ = Task.Run(async () =>
        {
            ChatShellResult result;
            try
            {
                var (environment, cwd) = await deps.Shell(Record, entry.Stop.Token);
                result = await ChatShell.RunAsync(command, environment, cwd, (stdout, stderr) =>
                {
                    lock (gate)
                    {
                        if (entry.Done) return;
                        folder.PutShell(entry.Id, command, stdout, stderr, "", running: true);
                        ScheduleFlush();
                    }
                }, entry.Stop.Token);
            }
            catch (OperationCanceledException) { result = new ChatShellResult("", "", null, true); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                result = new ChatShellResult("", error.Message, 127, false);
            }
            lock (gate)
            {
                entry.Stdout = ChatFolder.Sanitise(result.Stdout).Trim();
                entry.Stderr = ChatFolder.Sanitise(result.Stderr).Trim();
                entry.Exit = result.Stopped ? "stopped" : result.ExitCode is { } code and not 0 ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                entry.Done = true;
                folder.PutShell(entry.Id, command, entry.Stdout, entry.Stderr, entry.Exit, running: false);
                ScheduleFlush();
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Ends the running bash-mode command; its card settles with what it printed.</summary>
    public Task StopShellAsync()
    {
        lock (gate) foreach (var entry in shells.Where(entry => !entry.Done)) entry.Stop.Cancel();
        return Task.CompletedTask;
    }

    public Task InterruptAsync()
    {
        lock (gate)
        {
            if (folder.Header.Status is ChatStatus.Running or ChatStatus.AwaitingPermission) folder.MarkInterrupted();
            foreach (var permission in folder.Items.OfType<ChatPermissionItem>().Where(item => item.Decision == ChatPermissionDecision.Pending))
                folder.Decide(permission.RequestId, ChatPermissionDecision.Denied);
            foreach (var question in folder.Items.OfType<ChatQuestionItem>().Where(item => item.Decision == ChatPermissionDecision.Pending))
                folder.DecideQuestion(question.RequestId, ChatPermissionDecision.Denied, null);
        }
        return ControlAsync(new JsonObject { ["subtype"] = "interrupt" }).ContinueWith(task =>
        {
            lock (gate)
            {
                if (folder.Header.Status is ChatStatus.Running or ChatStatus.AwaitingPermission)
                {
                    folder.Settle(ChatToolState.Failed);
                    folder.SetStatus(ChatStatus.Idle);
                    ReportStatus("interrupted");
                }
                ScheduleFlush();
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Switches the model; the header changes only when the CLI's answer arrives.</summary>
    public async Task<ChatHeader> SetModelAsync(string model)
    {
        await ControlAsync(new JsonObject { ["subtype"] = "set_model", ["model"] = model });
        lock (gate)
        {
            record = record with { Model = model };
            deps.Save(record);
            folder.SetHeader(header => header with { Model = model });
            FlushNow();
            return folder.Header;
        }
    }

    public async Task<ChatHeader> SetPermissionModeAsync(string mode)
    {
        await ControlAsync(new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = mode });
        lock (gate)
        {
            record = record with { PermissionMode = mode };
            deps.Save(record);
            folder.SetHeader(header => header with { PermissionMode = mode });
            FlushNow();
            return folder.Header;
        }
    }

    /// <summary>
    /// The effort level changes live, as the extension does it: <c>apply_flag_settings</c> with <c>effortLevel</c>, confirmed by the
    /// <c>applied.effort</c> that <c>get_settings</c> reports; the header changes only then. A CLI that rejects the request gets the
    /// older way instead: the child is restarted on the same session with the new flag, and the header says so.
    /// </summary>
    public async Task<ChatHeader> SetEffortAsync(string effort)
    {
        try
        {
            await ControlAsync(new JsonObject { ["subtype"] = "apply_flag_settings", ["settings"] = new JsonObject { ["effortLevel"] = effort } });
            string? applied = effort;
            try
            {
                var settings = await ControlAsync(new JsonObject { ["subtype"] = "get_settings" });
                applied = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("applied", out var found) ? ChatFolder.Str(found, "effort") : effort;
            }
            catch (InvalidOperationException) { }
            lock (gate)
            {
                if (applied == effort)
                {
                    record = record with { Effort = effort };
                    deps.Save(record);
                    folder.SetHeader(header => header with { Effort = effort });
                    FlushNow();
                }
                return folder.Header;
            }
        }
        catch (InvalidOperationException) when (process is not null) { }
        ChatSpawn spawn;
        lock (gate)
        {
            record = record with { Effort = effort };
            deps.Save(record);
            folder.AddNotice(ChatNoticeLevel.Info, $"Restarting Claude Code to apply effort {effort}.", ChatNoticeKinds.Restart);
            folder.SetHeader(header => header with { Pending = "effort " + effort });
            spawn = ResumeSpawn();
            FlushNow();
        }
        await RestartAsync(spawn, null, null);
        lock (gate)
        {
            folder.SetHeader(header => header with { Effort = effort });
            FlushNow();
            return folder.Header;
        }
    }

    // Callers hold the gate. A session that never saved a transcript cannot be resumed, so it starts again under its id.
    private ChatSpawn ResumeSpawn()
    {
        var id = record.SessionId ?? record.ChatId;
        return AgentResume.SessionExists(record.Cwd ?? record.WorkspaceId, id) ? new(null, id, false, null) : new(id, null, false, null);
    }

    private async Task RestartAsync(ChatSpawn spawn, string? transcript, string? stopBefore)
    {
        ChatProcess? old;
        lock (gate)
        {
            old = process;
            process = null;
            generation++;
            foreach (var pending in controls.Values) pending.TrySetException(new IOException("Claude Code is restarting."));
            controls.Clear();
            folder.Settle(ChatToolState.Failed);
        }
        if (old is not null) await old.StopAsync();
        await StartAsync(spawn, transcript, stopBefore);
    }

    /// <summary>A dry run of rewinding the code to a prompt: whether it can, and what would change.</summary>
    public async Task<ChatRewindPreview> RewindPreviewAsync(string uuid)
    {
        JsonElement answer;
        try { answer = await ControlAsync(new JsonObject { ["subtype"] = "rewind_files", ["user_message_id"] = uuid, ["dry_run"] = true }); }
        catch (InvalidOperationException error) { return new ChatRewindPreview(false) { Error = error.Message }; }
        var can = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("canRewind", out var found) && found.ValueKind == JsonValueKind.True;
        var files = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("filesChanged", out var changed) && changed.ValueKind == JsonValueKind.Array
            ? changed.EnumerateArray().Select(entry => entry.GetString()).OfType<string>().ToList() : [];
        return new ChatRewindPreview(can)
        {
            Error = can ? null : ChatFolder.Str(answer, "error") ?? "No file checkpoint found",
            FilesChanged = files,
            Insertions = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("insertions", out var ins) && ins.TryGetInt32(out var added) ? added : null,
            Deletions = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("deletions", out var del) && del.TryGetInt32(out var removed) ? removed : null
        };
    }

    /// <summary>The text of the prompt with this uuid, which a fork seeds the new composer with.</summary>
    public string? PromptTextOf(string uuid)
    {
        lock (gate) return folder.Items.OfType<ChatUserItem>().FirstOrDefault(item => item.Uuid == uuid)?.Text;
    }

    public async Task RewindAsync(string uuid, bool files)
    {
        if (files)
        {
            JsonElement rewound;
            try { rewound = await ControlAsync(new JsonObject { ["subtype"] = "rewind_files", ["user_message_id"] = uuid }); }
            catch (InvalidOperationException error) { throw new InvalidOperationException("Failed to rewind code: " + error.Message); }
            if (rewound.ValueKind == JsonValueKind.Object && rewound.TryGetProperty("canRewind", out var can) && can.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("Failed to rewind code: " + (ChatFolder.Str(rewound, "error") ?? "No file checkpoint found"));
            var links = rewound.ValueKind == JsonValueKind.Object && rewound.TryGetProperty("skippedLinks", out var found) && found.ValueKind == JsonValueKind.Array ? found.EnumerateArray().ToList() : [];
            // What the checkpoint reports for each skipped file is shown as it came: the extension's own explanation was not recovered.
            if (links.Count > 0)
                lock (gate) folder.AddNotice(ChatNoticeLevel.Warning, $"Code rewind completed, but {links.Count} {(links.Count == 1 ? "file was" : "files were")} skipped: {string.Join(", ", links.Select(link => link.ValueKind == JsonValueKind.String ? link.GetString() : link.ToString()))}", ChatNoticeKinds.Meta);
        }
        string? transcript;
        lock (gate) transcript = AgentResume.TranscriptPath(record.Cwd ?? record.WorkspaceId, record.SessionId ?? record.ChatId);
        if (transcript is null) throw new InvalidOperationException("The conversation has no saved transcript to rewind.");
        // The conversation up to the message before the prompt stays; the CLI continues from there under a new session.
        var spawn = new ChatSpawn(null, record.SessionId ?? record.ChatId, true, null);
        await RestartAsync(spawn, transcript, uuid);
    }

    public Task StopTaskAsync(string taskId) => ControlAsync(new JsonObject { ["subtype"] = "stop_task", ["task_id"] = taskId });

    public async Task AnswerAsync(ChatAnswerParams answer)
    {
        JsonObject body;
        lock (gate)
        {
            if (folder.Item("p:" + answer.RequestId) is not ChatPermissionItem { Decision: ChatPermissionDecision.Pending } permission) return;
            if (answer.Allow)
            {
                var updated = answer.UpdatedInput ?? permission.Input;
                if (answer.Remember == true && permission.Suggestions is { } suggestions)
                    body = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = ClaudeJson.Node(updated), ["updatedPermissions"] = ClaudeJson.Node(ChatPermissions.WithDestination(suggestions, answer.Destination)) };
                else if (permission.ToolName == "ExitPlanMode" && answer.PlanMode is { Length: > 0 } planMode)
                    body = new JsonObject
                    {
                        ["behavior"] = "allow",
                        ["updatedInput"] = ClaudeJson.Node(updated),
                        ["updatedPermissions"] = new JsonArray(new JsonObject { ["type"] = "setMode", ["mode"] = planMode, ["destination"] = "session" })
                    };
                else body = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = ClaudeJson.Node(updated) };
            }
            else body = new JsonObject { ["behavior"] = "deny", ["message"] = ChatPermissions.DenyMessage(answer, permission.ToolName) };
            folder.Decide(answer.RequestId, answer.Allow ? ChatPermissionDecision.Allowed : ChatPermissionDecision.Denied);
            AfterPermission();
        }
        await WriteAsync(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = answer.RequestId, ["response"] = body } });
    }

    public async Task AnswerQuestionAsync(ChatAnswerQuestionParams answer)
    {
        JsonObject body;
        lock (gate)
        {
            if (folder.Item("q:" + answer.RequestId) is not ChatQuestionItem { Decision: ChatPermissionDecision.Pending } || folder.QuestionInput(answer.RequestId) is not { } input) return;
            if (answer.Cancelled == true || answer.Answers is null)
            {
                body = new JsonObject { ["behavior"] = "deny", ["message"] = ChatPermissions.Rejected };
                folder.DecideQuestion(answer.RequestId, ChatPermissionDecision.Denied, null);
            }
            else
            {
                // The CLI hands the tool its own questions back with the answers keyed by question text.
                var questions = input.TryGetProperty("questions", out var original) ? original : ClaudeJson.EmptyArray();
                var answers = new JsonObject();
                foreach (var (question, chosen) in answer.Answers) answers[question] = chosen;
                body = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = new JsonObject { ["questions"] = ClaudeJson.Node(questions), ["answers"] = answers } };
                folder.DecideQuestion(answer.RequestId, ChatPermissionDecision.Allowed, answer.Answers);
            }
            AfterPermission();
        }
        await WriteAsync(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = answer.RequestId, ["response"] = body } });
    }

    /// <summary>Cancels a queued message in the CLI and drops its item; refuses when the CLI has already taken it up.</summary>
    public async Task DequeueAsync(string itemId) => _ = await TakeQueuedAsync(itemId);

    private async Task<ChatUserItem> TakeQueuedAsync(string itemId)
    {
        ChatUserItem item;
        lock (gate)
            item = folder.Item(itemId) as ChatUserItem is { Queued: true, Uuid: not null } queued ? queued : throw new InvalidOperationException("That message is not queued.");
        var response = await ControlAsync(new JsonObject { ["subtype"] = "cancel_async_message", ["message_uuid"] = item.Uuid });
        if (!(response.ValueKind == JsonValueKind.Object && response.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True))
            throw new InvalidOperationException("Claude Code has already taken that message up.");
        lock (gate)
        {
            folder.Remove(itemId);
            ScheduleFlush();
        }
        return item;
    }

    /// <summary>Replaces a queued message: cancels the old prompt, then queues the new text, so it never runs twice.</summary>
    public async Task EditQueuedAsync(string itemId, string text)
    {
        var old = await TakeQueuedAsync(itemId);
        await SendAsync(text, old.Attachments);
    }

    /// <summary>
    /// Sends a queued message at once: every queued prompt is cancelled (in order), the turn is interrupted when one runs, then the chosen
    /// prompt goes first and the others are queued again behind it in their old order. A prompt the CLI took up meanwhile stays where it is.
    /// </summary>
    public async Task SendQueuedNowAsync(string itemId)
    {
        List<string> order;
        lock (gate) order = [.. folder.Items.OfType<ChatUserItem>().Where(item => item.Queued && item.Uuid is not null).Select(item => item.Id)];
        if (!order.Contains(itemId)) throw new InvalidOperationException("That message is not queued.");
        var chosen = await TakeQueuedAsync(itemId);
        var others = new List<ChatUserItem>();
        foreach (var id in order.Where(id => id != itemId))
        {
            try { others.Add(await TakeQueuedAsync(id)); }
            catch (InvalidOperationException) { }
        }
        bool running;
        lock (gate) running = folder.Header.Status is ChatStatus.Running or ChatStatus.AwaitingPermission;
        if (running) await InterruptAsync();
        await SendAsync(chosen.Text, chosen.Attachments);
        foreach (var other in others) await SendAsync(other.Text, other.Attachments);
    }

    public string? ItemDetail(string itemId)
    {
        lock (gate) return folder.FullText(itemId) ?? (folder.Item(itemId) switch
        {
            ChatToolItem tool => tool.Output,
            ChatNoticeItem notice => notice.Text,
            ChatResultItem result => result.Text,
            _ => null
        });
    }

    public void SetTitle(string title)
    {
        lock (gate)
        {
            record = record with { Title = title };
            deps.Save(record);
            folder.SetHeader(header => header with { Title = title });
            ScheduleFlush();
        }
    }

    /// <summary>Ends the child; the transcript stays on disk and a later open resumes it.</summary>
    public async Task CloseAsync()
    {
        ChatProcess? old;
        lock (gate)
        {
            old = process;
            process = null;
            foreach (var entry in shells.Where(entry => !entry.Done)) entry.Stop.Cancel();
            folder.SetStatus(ChatStatus.Exited);
            folder.Settle(ChatToolState.Failed);
            foreach (var pending in controls.Values) pending.TrySetException(new IOException("The chat was closed."));
            controls.Clear();
            ReportStatus("interrupted");
            FlushNow();
        }
        if (old is not null) await old.StopAsync();
    }

    /// <summary>Final teardown: the child goes, and nothing is pushed afterwards.</summary>
    public async Task DisposeAsync()
    {
        ChatProcess? old;
        lock (gate)
        {
            closed = true;
            foreach (var entry in shells.Where(entry => !entry.Done)) entry.Stop.Cancel();
            old = process;
            process = null;
        }
        lifetime.Cancel();
        if (old is not null) await old.StopAsync();
    }
}