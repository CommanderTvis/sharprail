using System.Text.Json;

namespace SharpRail.Plugins.ClaudeCode.Host.Chat;

/// <summary>What survives an app restart about a chat: enough to respawn it under its session with the choices it had.</summary>
internal sealed record ChatRecord(string WorkspaceId, string ChatId)
{
    public string? SessionId { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? PermissionMode { get; init; }
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
    Action<string> Warn);

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
    private ChatRecord record = initial;
    private ChatProcess? process;
    private int generation;
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
        }
        var launch = deps.Launch(current, spawn);
        var advisorAt = launch.Arguments.ToList().IndexOf("--advisor");
        lock (gate) folder.SetHeader(header => header with { Advisor = advisorAt >= 0 && advisorAt + 1 < launch.Arguments.Count ? launch.Arguments[advisorAt + 1] : null, Chrome = launch.Arguments.Contains("--chrome"), CanSendQueuedNow = true });
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
                var response = await ControlAsync(mine, new { subtype = "initialize" });
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

    private void OnError(int mine, string text)
    {
        lock (gate)
        {
            if (mine != generation) return;
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
                folder.AddNotice(ChatNoticeLevel.Error, $"Claude Code exited with code {code}.");
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
        try { message = JsonSerializer.Deserialize<JsonElement>(line); }
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
            await WriteAsync(new { type = "control_response", response = new { subtype = "error", request_id = refusedId, error = refusal } });
    }

    private void Fold(JsonElement message)
    {
        var type = ChatFolder.Str(message, "type");
        var folded = folder.Apply(message);
        if (folded.Signal == ChatFolder.Signal.Init)
        {
            var header = folder.Header;
            record = record with { SessionId = header.SessionId ?? record.SessionId, Cwd = header.Cwd ?? record.Cwd, Model = header.Model ?? record.Model, PermissionMode = header.PermissionMode ?? record.PermissionMode, ForkOf = null, ForkBefore = null };
            deps.Save(record);
            folder.SetHeader(current => current with { Pending = null });
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
        else pending.TrySetResult(body.TryGetProperty("response", out var result) ? result.Clone() : JsonSerializer.SerializeToElement(new { }));
    }

    // A request from the CLI. Only can_use_tool is ours to answer; anything else is refused so the CLI does not wait.
    private (string? RefusedId, string? Reason) ControlRequest(JsonElement message)
    {
        var id = ChatFolder.Str(message, "request_id");
        if (id is null || !message.TryGetProperty("request", out var request)) return (null, null);
        if (ChatFolder.Str(request, "subtype") != "can_use_tool") return (id, "Not supported by SharpRail's chat.");
        var tool = ChatFolder.Str(request, "tool_name") ?? "tool";
        var input = request.TryGetProperty("input", out var raw) ? raw.Clone() : JsonSerializer.SerializeToElement(new { });
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

    private Task WriteAsync(object message)
    {
        ChatProcess? target;
        lock (gate) target = process;
        return target is null ? Task.FromException(new InvalidOperationException("Claude Code is not running.")) : target.WriteAsync(JsonSerializer.Serialize(message), lifetime.Token);
    }

    private async Task<JsonElement> ControlAsync(int mine, object request)
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
            await WriteAsync(new { type = "control_request", request_id = id, request });
            return await pending.Task.WaitAsync(ControlTimeout, lifetime.Token);
        }
        finally { lock (gate) controls.Remove(id); }
    }

    private Task<JsonElement> ControlAsync(object request)
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
        var blocks = new List<object>();
        var mentions = new List<string>();
        foreach (var attachment in attachments ?? [])
        {
            if (attachment.Data is { Length: > 0 } data)
            {
                var type = attachment.MediaType is { Length: > 0 } declared ? declared : "application/octet-stream";
                blocks.Add(new { type = type.StartsWith("image/", StringComparison.Ordinal) ? "image" : "document", source = new { type = "base64", media_type = type, data = Convert.ToBase64String(data) } });
            }
            else if (attachment.Path.Length > 0) mentions.Add("@" + attachment.Path);
        }
        var prompt = mentions.Count > 0 ? text + "\n\n" + string.Join("\n", mentions) : text;
        // The transcript keeps the names, not the bytes.
        var shown = attachments?.Select(attachment => attachment with { Data = null }).ToList();
        lock (gate)
        {
            if (process is null) throw new InvalidOperationException("The chat is not running.");
            var running = folder.Header.Status is ChatStatus.Running or ChatStatus.AwaitingPermission;
            folder.AddUser(text, shown, running, "u:" + uuid, uuid);
            if (!running)
            {
                folder.SetStatus(ChatStatus.Running);
                ReportStatus("prompt_submit");
            }
            ScheduleFlush();
        }
        var content = new List<object>(blocks);
        if (prompt.Length > 0 || content.Count == 0) content.Insert(0, new { type = "text", text = prompt });
        await WriteAsync(new { type = "user", uuid, message = new { role = "user", content }, parent_tool_use_id = (string?)null });
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
        return ControlAsync(new { subtype = "interrupt" }).ContinueWith(task =>
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
        await ControlAsync(new { subtype = "set_model", model });
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
        await ControlAsync(new { subtype = "set_permission_mode", mode });
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
            await ControlAsync(new { subtype = "apply_flag_settings", settings = new { effortLevel = effort } });
            string? applied = effort;
            try
            {
                var settings = await ControlAsync(new { subtype = "get_settings" });
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
        try { answer = await ControlAsync(new { subtype = "rewind_files", user_message_id = uuid, dry_run = true }); }
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
            try { rewound = await ControlAsync(new { subtype = "rewind_files", user_message_id = uuid }); }
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

    public Task StopTaskAsync(string taskId) => ControlAsync(new { subtype = "stop_task", task_id = taskId });

    public async Task AnswerAsync(ChatAnswerParams answer)
    {
        object body;
        lock (gate)
        {
            if (folder.Item("p:" + answer.RequestId) is not ChatPermissionItem { Decision: ChatPermissionDecision.Pending } permission) return;
            if (answer.Allow)
            {
                var updated = answer.UpdatedInput ?? permission.Input;
                if (answer.Remember == true && permission.Suggestions is { } suggestions)
                    body = new { behavior = "allow", updatedInput = updated, updatedPermissions = ChatPermissions.WithDestination(suggestions, answer.Destination) };
                else if (permission.ToolName == "ExitPlanMode" && answer.PlanMode is { Length: > 0 } planMode)
                    body = new { behavior = "allow", updatedInput = updated, updatedPermissions = new[] { new { type = "setMode", mode = planMode, destination = "session" } } };
                else body = new { behavior = "allow", updatedInput = updated };
            }
            else body = new { behavior = "deny", message = ChatPermissions.DenyMessage(answer, permission.ToolName) };
            folder.Decide(answer.RequestId, answer.Allow ? ChatPermissionDecision.Allowed : ChatPermissionDecision.Denied);
            AfterPermission();
        }
        await WriteAsync(new { type = "control_response", response = new { subtype = "success", request_id = answer.RequestId, response = body } });
    }

    public async Task AnswerQuestionAsync(ChatAnswerQuestionParams answer)
    {
        object body;
        lock (gate)
        {
            if (folder.Item("q:" + answer.RequestId) is not ChatQuestionItem { Decision: ChatPermissionDecision.Pending } || folder.QuestionInput(answer.RequestId) is not { } input) return;
            if (answer.Cancelled == true || answer.Answers is null)
            {
                body = new { behavior = "deny", message = ChatPermissions.Rejected };
                folder.DecideQuestion(answer.RequestId, ChatPermissionDecision.Denied, null);
            }
            else
            {
                // The CLI hands the tool its own questions back with the answers keyed by question text.
                var questions = input.TryGetProperty("questions", out var original) ? original : JsonSerializer.SerializeToElement(Array.Empty<object>());
                body = new { behavior = "allow", updatedInput = new { questions, answers = answer.Answers } };
                folder.DecideQuestion(answer.RequestId, ChatPermissionDecision.Allowed, answer.Answers);
            }
            AfterPermission();
        }
        await WriteAsync(new { type = "control_response", response = new { subtype = "success", request_id = answer.RequestId, response = body } });
    }

    /// <summary>Cancels a queued message in the CLI and drops its item; refuses when the CLI has already taken it up.</summary>
    public async Task DequeueAsync(string itemId) => _ = await TakeQueuedAsync(itemId);

    private async Task<ChatUserItem> TakeQueuedAsync(string itemId)
    {
        ChatUserItem item;
        lock (gate)
            item = folder.Item(itemId) as ChatUserItem is { Queued: true, Uuid: not null } queued ? queued : throw new InvalidOperationException("That message is not queued.");
        var response = await ControlAsync(new { subtype = "cancel_async_message", message_uuid = item.Uuid });
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
            old = process;
            process = null;
        }
        lifetime.Cancel();
        if (old is not null) await old.StopAsync();
    }
}