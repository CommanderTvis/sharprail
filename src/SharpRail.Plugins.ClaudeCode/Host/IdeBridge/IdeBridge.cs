using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.ClaudeCode.Host.IdeBridge;

/// <summary>What the bridge needs from the plugin: dispatching an action to a client, the workspace folders, and process ownership.</summary>
/// <param name="Dispatch">Pushes a host-initiated action to the workspace's client; the reply arrives through <see cref="IdeBridge.Settle"/>.</param>
/// <param name="WorkspaceFolders">The worktrees this IDE has open.</param>
/// <param name="WorkspaceForProcess">The workspace a connecting agent's process belongs to, or null when SharpRail did not start it.</param>
internal sealed record IdeBridgeDeps(Action<IdeActionRequest> Dispatch, Func<IReadOnlyList<string>> WorkspaceFolders, Func<int, string?> WorkspaceForProcess);

/// <summary>
/// Claude Code's IDE protocol: a loopback WebSocket MCP server advertised by a lock file. The CLI reads the editor's
/// selection from it and asks it to open, check, save and close files; the plugin relays those requests to the client
/// that owns the workspace and its replies back. A socket hears about a workspace only once its agent says which
/// process it is, so a CLI SharpRail did not start learns nothing.
/// </summary>
internal sealed class IdeBridge(IdeBridgeDeps deps)
{
    public const string IdeName = "SharpRail";

    /// <summary>The variable the CLI reads to skip lock-file discovery when SharpRail started its terminal.</summary>
    public const string SsePortVariable = "CLAUDE_CODE_SSE_PORT";

    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(15);
    private const int PortScanStart = 10_100;
    private const int PortScanAttempts = 20;

    private sealed class Client(WebSocket socket)
    {
        public readonly WebSocket Socket = socket;
        public readonly SemaphoreSlim Sending = new(1, 1);
        public string? Workspace;

        public async Task SendAsync(string message)
        {
            await Sending.WaitAsync();
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception error) when (error is WebSocketException or ObjectDisposedException or IOException) { }
            finally { Sending.Release(); }
        }
    }

    private readonly ConcurrentDictionary<Client, byte> clients = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> pending = new();
    private readonly Lock gate = new();
    private IdeSelectionChanged? latestSelection;
    private IdeSelectionChanged? currentSelection;
    private TcpListener? listener;
    private CancellationTokenSource? lifetime;
    private string authToken = "";

    public int? Port { get; private set; }

    private Task<JsonElement?> RequestAction(string workspaceId, IdeActionKind kind, IdeActionParams parameters)
    {
        var id = Guid.NewGuid().ToString();
        var reply = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        var timeout = new CancellationTokenSource(ActionTimeout);
        timeout.Token.Register(() =>
        {
            if (pending.TryRemove(id, out var late)) late.TrySetException(new InvalidOperationException($"The editor did not answer {Kind(kind)} in time"));
            timeout.Dispose();
        });
        try { deps.Dispatch(new(id, workspaceId, kind, parameters)); }
        catch (Exception error)
        {
            pending.TryRemove(id, out _);
            reply.TrySetException(error);
        }
        return reply.Task;
    }

    private static string Kind(IdeActionKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    /// <summary>Settles a dispatched action with the client's answer. Unknown and late ids are dropped.</summary>
    public void Settle(IdeActionReply reply)
    {
        if (!pending.TryRemove(reply.Id, out var entry)) return;
        if (reply.Result.Ok) entry.TrySetResult(reply.Result.Value);
        else entry.TrySetException(new InvalidOperationException(reply.Result.Error ?? "The editor refused the action"));
    }

    private void Broadcast(string workspaceId, string message)
    {
        foreach (var client in clients.Keys)
            if (client.Workspace == workspaceId) _ = client.SendAsync(message);
    }

    private static JsonObject SelectionRange(IdeSelection selection) => new()
    {
        ["start"] = new JsonObject { ["line"] = selection.StartLine, ["character"] = selection.StartColumn },
        ["end"] = new JsonObject { ["line"] = selection.EndLine, ["character"] = selection.EndColumn }
    };

    public void SelectionChanged(IdeSelectionChanged payload)
    {
        lock (gate)
        {
            currentSelection = payload;
            if (payload.Text.Length > 0) latestSelection = payload;
        }
        Broadcast(payload.WorkspaceId, IdeMcp.Notification("selection_changed", new JsonObject
        {
            ["text"] = payload.Text,
            ["filePath"] = payload.Path,
            ["fileUrl"] = "file://" + payload.Path,
            ["selection"] = SelectionRange(payload.Selection)
        }));
    }

    public void DocumentClosed(IdeDocumentClosed payload)
    {
        lock (gate) if (currentSelection?.Path == payload.Path) currentSelection = null;
        Broadcast(payload.WorkspaceId, IdeMcp.Notification("document_closed", new JsonObject { ["filePath"] = payload.Path, ["uri"] = "file://" + payload.Path }));
    }

    private static JsonObject SelectionPayload(IdeSelectionChanged? value) => value is null
        ? new JsonObject { ["success"] = false, ["message"] = "No selection" }
        : new JsonObject
        {
            ["success"] = true,
            ["text"] = value.Text,
            ["filePath"] = value.Path,
            ["fileUrl"] = "file://" + value.Path,
            ["selection"] = SelectionRange(value.Selection)
        };

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string Plain(JsonObject arguments, string name) => arguments[name]?.ToString() ?? "";

    /// <summary>Runs one IDE tool. Every write-side tool needs a workspace; the CLI never sends one, so it is the last selection's.</summary>
    public async Task<JsonNode?> CallToolAsync(string name, JsonObject arguments)
    {
        string? workspaceId;
        IdeSelectionChanged? current, latest;
        lock (gate) { current = currentSelection; latest = latestSelection; workspaceId = (current ?? latest)?.WorkspaceId; }
        string Workspace() => workspaceId ?? throw new InvalidOperationException("No active SharpRail workspace for this request");
        async Task<JsonNode?> Action(IdeActionKind kind, IdeActionParams parameters) =>
            await RequestAction(Workspace(), kind, parameters) is { } value ? JsonNode.Parse(value.GetRawText()) : null;
        switch (name)
        {
            case "getCurrentSelection": return SelectionPayload(current);
            case "getLatestSelection": return SelectionPayload(latest);
            case "getWorkspaceFolders":
                return new JsonObject
                {
                    ["folders"] = new JsonArray([.. deps.WorkspaceFolders().Select(path => (JsonNode?)new JsonObject
                        { ["name"] = path.Split('/').LastOrDefault(part => part.Length > 0) ?? path, ["path"] = path })])
                };
            case "openFile":
                return await Action(IdeActionKind.OpenFile, new IdeActionParams
                {
                    Path = Plain(arguments, "filePath"),
                    Preview = arguments["preview"] is null ? null : arguments["preview"]!.GetValueKind() == JsonValueKind.True,
                    StartText = Text(arguments, "startText"),
                    EndText = Text(arguments, "endText")
                });
            case "openDiff":
                return await Action(IdeActionKind.OpenDiff, new IdeActionParams
                {
                    OldPath = Text(arguments, "old_file_path"),
                    NewPath = Text(arguments, "new_file_path"),
                    NewContent = Text(arguments, "new_file_contents")
                });
            case "getOpenEditors": return await Action(IdeActionKind.GetOpenEditors, new IdeActionParams());
            case "checkDocumentDirty": return await Action(IdeActionKind.CheckDocumentDirty, new IdeActionParams { Path = Plain(arguments, "filePath") });
            case "saveDocument": return await Action(IdeActionKind.SaveDocument, new IdeActionParams { Path = Plain(arguments, "filePath") });
            case "close_tab": return await Action(IdeActionKind.CloseTab, new IdeActionParams { TabName = Plain(arguments, "tab_name") });
            case "closeAllDiffTabs": return await Action(IdeActionKind.CloseAllDiffTabs, new IdeActionParams());
            default: throw new InvalidOperationException($"Unknown tool: {name}");
        }
    }

    /// <summary>Answers one JSON-RPC message from a socket, or nothing for a notification.</summary>
    internal async Task<string?> HandleAsync(Action<string> tag, string raw)
    {
        if (IdeMcp.Parse(raw) is not { } message) return null;
        // The one notification worth reading: it names the agent's own process, which is how a socket learns its workspace.
        if (message.Method == "ide_connected")
        {
            var pid = message.Params is JsonObject parameters && parameters["pid"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
            if (deps.WorkspaceForProcess(pid) is { } workspace) tag(workspace);
            return null;
        }
        // A notification (no id) is never answered, even when it is not recognised.
        if (message.Id is not { } id) return null;
        try
        {
            switch (message.Method)
            {
                case "initialize":
                    return IdeMcp.Success(id, new JsonObject
                    {
                        ["protocolVersion"] = IdeMcp.ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "sharprail-ide", ["version"] = "1.0.0" }
                    });
                case "tools/list": return IdeMcp.Success(id, new JsonObject { ["tools"] = IdeMcp.Tools() });
                case "ping": return IdeMcp.Success(id, new JsonObject());
                case "tools/call":
                    var call = message.Params as JsonObject ?? [];
                    var name = call["name"] is JsonValue tool && tool.GetValueKind() == JsonValueKind.String ? tool.GetValue<string>() : "";
                    return IdeMcp.Success(id, IdeMcp.ToolContent(await CallToolAsync(name, call["arguments"] as JsonObject ?? [])));
                default:
                    return IdeMcp.Failure(id, IdeMcp.MethodNotFound, $"Unknown method: {message.Method}");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return IdeMcp.Failure(id, IdeMcp.InternalError, error.Message);
        }
    }

    public int Start()
    {
        if (Port is { } running) return running;
        LockFile.RemoveOwnStale(IdeName);
        authToken = Guid.NewGuid().ToString();
        listener = Listen();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        lifetime = new CancellationTokenSource();
        WriteLock();
        _ = AcceptAsync(listener, lifetime.Token);
        return Port.Value;
    }

    private static TcpListener Listen()
    {
        for (var port = PortScanStart; port < PortScanStart + PortScanAttempts; port++)
        {
            var candidate = new TcpListener(IPAddress.Loopback, port);
            try { candidate.Start(); return candidate; }
            catch (SocketException) { candidate.Dispose(); }
        }
        var assigned = new TcpListener(IPAddress.Loopback, 0);
        assigned.Start();
        return assigned;
    }

    private void WriteLock()
    {
        if (Port is not { } port) return;
        LockFile.Write(port, new(Environment.ProcessId, deps.WorkspaceFolders(), IdeName, authToken));
    }

    /// <summary>Re-publishes the lock file's workspace list after a workspace is opened or removed.</summary>
    public void RefreshWorkspaces() => WriteLock();

    private async Task AcceptAsync(TcpListener server, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient connection;
            try { connection = await server.AcceptTcpClientAsync(cancellationToken); }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            _ = ServeAsync(connection, cancellationToken);
        }
    }

    // A minimal HTTP/1.1 upgrade: the token header is the authorization, anything else is refused before a socket exists.
    private async Task ServeAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        using var owned = connection;
        var stream = connection.GetStream();
        Dictionary<string, string> headers;
        try { headers = await ReadHeadersAsync(stream, cancellationToken); }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { return; }
        if (headers.GetValueOrDefault(IdeMcp.AuthHeader) != authToken)
        {
            await Respond(stream, "401 Unauthorized", "unauthorized");
            return;
        }
        if (!headers.TryGetValue("sec-websocket-key", out var key) || !headers.GetValueOrDefault("upgrade", "").Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            await Respond(stream, "400 Bad Request", "ws upgrade failed");
            return;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        try { await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { return; }
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
        var client = new Client(socket);
        clients[client] = 0;
        try
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                _ = Task.Run(async () =>
                {
                    if (await HandleAsync(workspace => client.Workspace = workspace, text) is { } answer) await client.SendAsync(answer);
                }, CancellationToken.None);
            }
        }
        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { clients.TryRemove(client, out _); }
    }

    private static async Task<Dictionary<string, string>> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (buffer.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, deadline.Token) == 0) throw new IOException("The connection closed during the handshake.");
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal)) throw new InvalidDataException("Not a WebSocket upgrade.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    private static async Task Respond(NetworkStream stream, string status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(bytes);
        }
        catch (IOException) { }
    }

    public void Stop()
    {
        if (Port is { } port) LockFile.Remove(port);
        Port = null;
        lifetime?.Cancel();
        listener?.Stop();
        listener = null;
        foreach (var client in clients.Keys)
        {
            try { client.Socket.Abort(); } catch (ObjectDisposedException) { }
        }
        clients.Clear();
        foreach (var (id, entry) in pending.ToArray())
            if (pending.TryRemove(id, out _)) entry.TrySetException(new InvalidOperationException("The IDE bridge stopped"));
        lock (gate) { currentSelection = null; latestSelection = null; }
    }

    /// <summary>Drops the selections and pending actions, as a fresh activation would have them; for the checks.</summary>
    internal void Reset()
    {
        foreach (var (id, entry) in pending.ToArray())
            if (pending.TryRemove(id, out _)) entry.TrySetCanceled();
        lock (gate) { currentSelection = null; latestSelection = null; }
    }
}