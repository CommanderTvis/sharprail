using System.Text.Json.Nodes;

using SharpRail.Plugins.Chat;

namespace SharpRail.Plugins.Acp.Host;

/// <summary>
/// One chat on one ACP agent: the transport, the client, the conversation it folds into and the permission requests waiting for the user.
/// The agent is untrusted: everything it asks the client for goes through <see cref="IAcpClientMethods"/>.
/// </summary>
internal sealed class AcpChatSession : IAsyncDisposable, IAcpClientHandler
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(16);

    private readonly Lock gate = new();
    private readonly AcpFolder folder = new();
    private readonly string workspaceId, chatId, cwd;
    private readonly Action<ChatPush> publish;
    private readonly Action<string> log;
    private readonly Func<Task<IAcpTransport>> connect;
    private readonly Dictionary<string, (TaskCompletionSource<string?> Answer, IReadOnlyList<AcpPermissionOption> Options)> permissions = [];
    private readonly CancellationTokenSource lifetime = new();
    private AcpClient? client;
    private IAcpTransport? transport;
    private AcpAgentInfo? agent;
    private string? sessionId;
    private int closing;
    private int requests;
    private bool flushing;

    public AcpChatSession(string workspaceId, string chatId, string cwd, Func<Task<IAcpTransport>> connect, Action<ChatPush> publish, Action<string> log, IAcpClientMethods? methods = null)
    {
        this.workspaceId = workspaceId;
        this.chatId = chatId;
        this.cwd = cwd;
        this.connect = connect;
        this.publish = publish;
        this.log = log;
        Methods = methods;
    }

    public IAcpClientMethods? Methods { get; }

    public bool Active => client is not null && Volatile.Read(ref closing) == 0 && Header.Status is not (ChatStatus.Exited or ChatStatus.Failed);

    public string? SessionId => sessionId;

    public AcpAgentInfo? Agent => agent;

    public ChatHeader Header
    {
        get { lock (gate) return folder.Header; }
    }

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

    public ChatHeader Begin()
    {
        lock (gate)
        {
            folder.Update(header => header with { Status = ChatStatus.Starting, Cwd = cwd, Error = null });
            FlushNow();
            return folder.Header;
        }
    }

    /// <summary>Starts the agent, negotiates the protocol and opens (or, with an id the agent can load, resumes) a session.</summary>
    public async Task StartAsync(string? resume)
    {
        try
        {
            transport = await connect();
            client = new AcpClient(transport.FromAgent, transport.ToAgent, this);
            _ = WatchAsync(client, transport);
            agent = await client.InitializeAsync("SharpRail", "0.1", lifetime.Token);
            JsonObject session;
            if (resume is { Length: > 0 } && agent.LoadSession)
            {
                lock (gate) { folder.Clear(); FlushNow(); }
                session = await client.LoadSessionAsync(resume, cwd, lifetime.Token);
                sessionId = resume;
            }
            else
            {
                session = await client.NewSessionAsync(cwd, lifetime.Token);
                sessionId = AcpFolder.Str(session, "sessionId");
            }
            lock (gate)
            {
                folder.ApplyModes(session["modes"] as JsonObject);
                folder.ApplyModels(session["models"] as JsonObject);
                folder.ApplyConfig(session["configOptions"] as JsonArray);
                folder.Update(header => header with { SessionId = sessionId, Version = agent.Version, Title = header.Title ?? agent.Name });
                if (folder.Header.Status == ChatStatus.Starting) folder.SetStatus(ChatStatus.Idle);
                FlushNow();
            }
        }
        catch (Exception error) when (error is AcpException or AcpVersionException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            lock (gate)
            {
                if (error is AcpException { Code: AcpException.AuthRequired } && agent is { AuthMethods.Count: > 0 })
                    folder.Notice(ChatNoticeLevel.Warning, "The agent needs you to sign in: " + string.Join("; ", agent.AuthMethods.Select(method => method.Name + (method.Description is { Length: > 0 } d ? " (" + d + ")" : ""))) + ". Sign in with the agent's own tool, then reopen this chat.", ChatNoticeKinds.Failure);
                folder.SetStatus(ChatStatus.Failed, error is System.ComponentModel.Win32Exception ? "The agent program was not found." : error.Message);
                FlushNow();
            }
        }
    }

    private async Task WatchAsync(AcpClient watched, IAcpTransport owner)
    {
        await watched.Closed;
        if (Volatile.Read(ref closing) == 1) return;
        SettlePermissions(null);
        (Methods as IDisposable)?.Dispose();
        lock (gate)
        {
            if (folder.Header.Status is ChatStatus.Failed) return;
            var detail = owner.ErrorTail;
            folder.SetStatus(ChatStatus.Exited, detail.Length > 0 ? detail : "The agent exited.");
            folder.Notice(ChatNoticeLevel.Error, "The agent exited" + (detail.Length > 0 ? ": " + detail : "."), ChatNoticeKinds.Failure, true);
            FlushNow();
        }
    }

    public void Update(string session, JsonObject update)
    {
        lock (gate)
        {
            if (!folder.Apply(update)) log("The agent sent an update this host does not fold: " + AcpFolder.Str(update, "sessionUpdate"));
            ScheduleFlush();
        }
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        if (method == "session/request_permission") return await PermissionAsync(parameters, cancellationToken);
        if (Methods is { } served) return await served.HandleAsync(method, parameters, cancellationToken);
        throw new AcpException(AcpException.MethodNotFound, method);
    }

    private async Task<JsonNode?> PermissionAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var options = (parameters["options"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(option => new AcpPermissionOption(AcpFolder.Str(option, "optionId"), AcpFolder.Str(option, "name"), AcpFolder.Str(option, "kind"))).ToArray();
        var requestId = "perm" + Interlocked.Increment(ref requests);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            permissions[requestId] = (answer, options);
            folder.AddPermission(requestId, parameters["toolCall"] as JsonObject ?? new JsonObject(), options);
            folder.SetStatus(ChatStatus.AwaitingPermission);
            FlushNow();
        }
        string? chosen;
        try { chosen = await answer.Task.WaitAsync(cancellationToken); }
        finally { lock (gate) permissions.Remove(requestId); }
        lock (gate)
        {
            folder.Settle(requestId, chosen is not null && options.FirstOrDefault(o => o.Id == chosen)?.Kind.StartsWith("allow", StringComparison.Ordinal) == true ? ChatPermissionDecision.Allowed : ChatPermissionDecision.Denied);
            if (folder.Header.Status == ChatStatus.AwaitingPermission && permissions.Count == 0) folder.SetStatus(ChatStatus.Running);
            FlushNow();
        }
        return new JsonObject { ["outcome"] = chosen is null ? new JsonObject { ["outcome"] = "cancelled" } : new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen } };
    }

    /// <summary>Resolves every waiting permission request as cancelled (the turn was cancelled or the agent ended).</summary>
    private void SettlePermissions(string? optionId)
    {
        List<TaskCompletionSource<string?>> waiting;
        lock (gate) waiting = [.. permissions.Values.Select(p => p.Answer)];
        foreach (var answer in waiting) answer.TrySetResult(optionId);
    }

    /// <summary>Answers a permission card: an allow picks allow_always when the user asked to remember and the agent offered it, else allow_once; a deny picks reject_once.</summary>
    public void Answer(string requestId, bool allow, bool remember)
    {
        (TaskCompletionSource<string?> Answer, IReadOnlyList<AcpPermissionOption> Options) entry;
        lock (gate)
        {
            if (!permissions.TryGetValue(requestId, out entry)) throw new InvalidOperationException("That permission request is no longer waiting.");
        }
        string[] preference = allow ? (remember ? ["allow_always", "allow_once"] : ["allow_once", "allow_always"]) : ["reject_once", "reject_always"];
        var option = preference.Select(kind => entry.Options.FirstOrDefault(o => o.Kind == kind)).FirstOrDefault(o => o is not null);
        // An agent that offers no option of the wanted kind is answered as cancelled rather than guessed at.
        entry.Answer.TrySetResult(option?.Id);
    }

    public async Task SendAsync(string text, IReadOnlyList<ChatAttachment>? attachments)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var attachment in attachments ?? [])
        {
            if (attachment.Data is { Length: > 0 } data && attachment.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true && agent is { Image: true })
                content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = attachment.MediaType });
            else if (attachment.Path.Length > 0)
                content.Add(new JsonObject { ["type"] = "resource_link", ["name"] = attachment.Name, ["uri"] = new Uri(Path.GetFullPath(attachment.Path, cwd)).AbsoluteUri });
        }
        var active = client ?? throw new InvalidOperationException("The agent is not running.");
        var id = sessionId ?? throw new InvalidOperationException("The agent has no session yet.");
        lock (gate)
        {
            if (folder.Header.Status is ChatStatus.Running or ChatStatus.AwaitingPermission) throw new InvalidOperationException("Wait for the agent to finish its turn, or interrupt it.");
            folder.AddUser(text, attachments);
            folder.SetStatus(ChatStatus.Running);
            FlushNow();
        }
        _ = Task.Run(() => TurnAsync(active, id, content));
    }

    private async Task TurnAsync(AcpClient active, string id, JsonArray content)
    {
        string? stop = null;
        string? failure = null;
        try { stop = await active.PromptAsync(id, content, lifetime.Token); }
        catch (AcpClosedException) { return; }
        catch (AcpException error) { failure = error.Message; }
        catch (Exception error) when (error is IOException or OperationCanceledException) { if (Volatile.Read(ref closing) == 1) return; failure = error.Message; }
        lock (gate)
        {
            folder.CloseStreaming();
            if (failure is not null) folder.Notice(ChatNoticeLevel.Error, "The turn failed: " + failure, ChatNoticeKinds.Failure, true);
            else if (stop == "refusal") folder.Notice(ChatNoticeLevel.Error, "The agent refused to continue.", ChatNoticeKinds.Failure, false);
            else if (stop == "cancelled") folder.Notice(ChatNoticeLevel.Info, "Interrupted", ChatNoticeKinds.Interrupt);
            else if (stop == "max_tokens") folder.Notice(ChatNoticeLevel.Warning, "The turn stopped at the token limit.");
            else if (stop == "max_turn_requests") folder.Notice(ChatNoticeLevel.Warning, "The turn stopped at the agent's request limit.");
            if (folder.Header.Status is not (ChatStatus.Exited or ChatStatus.Failed)) folder.SetStatus(ChatStatus.Idle);
            FlushNow();
        }
    }

    /// <summary>session/cancel; waiting permission requests are answered as cancelled, as the protocol requires.</summary>
    public async Task InterruptAsync()
    {
        if (client is null || sessionId is null) return;
        SettlePermissions(null);
        try { await client.CancelAsync(sessionId); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public async Task<ChatHeader> SetModeAsync(string value)
    {
        var active = client ?? throw new InvalidOperationException("The agent is not running.");
        try { await active.SetModeAsync(sessionId!, value, lifetime.Token); }
        catch (AcpException error) { throw new InvalidOperationException("The agent did not change mode: " + error.Message); }
        lock (gate) { folder.Update(header => header with { PermissionMode = value }); FlushNow(); return folder.Header; }
    }

    public async Task<ChatHeader> SetModelAsync(string value)
    {
        var active = client ?? throw new InvalidOperationException("The agent is not running.");
        try { await active.SetModelAsync(sessionId!, value, lifetime.Token); }
        catch (AcpException error) { throw new InvalidOperationException("The agent did not change model: " + error.Message); }
        lock (gate) { folder.Update(header => header with { Model = value }); FlushNow(); return folder.Header; }
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
        var (items, reset, header) = folder.Drain();
        if (reset) publish(new ChatResetPush(workspaceId, chatId));
        foreach (var item in items) publish(new ChatItemPush(workspaceId, chatId, item));
        if (header || reset) publish(new ChatHeaderPush(workspaceId, chatId, folder.Header));
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref closing, 1) == 1) return;
        SettlePermissions(null);
        if (client is { } active) await active.DisposeAsync();
        if (transport is { } owner) await owner.DisposeAsync();
        (Methods as IDisposable)?.Dispose();
        lock (gate) { folder.SetStatus(ChatStatus.Exited); FlushNow(); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        await lifetime.CancelAsync();
    }
}

/// <summary>The client methods an agent may call (fs/*, terminal/*), served by the host from the workspace.</summary>
internal interface IAcpClientMethods
{
    Task<JsonNode?> HandleAsync(string method, JsonObject parameters, CancellationToken cancellationToken);
}