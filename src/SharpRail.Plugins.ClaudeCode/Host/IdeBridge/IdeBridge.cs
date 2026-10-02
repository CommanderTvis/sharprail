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
/// selection from it, takes the file references the user sends to its prompt, and asks it to open, check, save and
/// close files; the plugin relays those requests to the client that owns the workspace and its replies back. A
/// socket hears about a workspace, and acts in it, only once its agent says which process it is, so a CLI SharpRail
/// did not start learns nothing.
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

        /// <returns>Whether the message was written to an open socket.</returns>
        public async Task<bool> SendAsync(string message)
        {
            await Sending.WaitAsync();
            try
            {
                if (Socket.State != WebSocketState.Open) return false;
                await Socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                return true;
            }
            catch (Exception error) when (error is WebSocketException or ObjectDisposedException or IOException) { return false; }
            finally { Sending.Release(); }
        }
    }

    private readonly ConcurrentDictionary<Client, byte> clients = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> pending = new();
    private readonly Lock gate = new();
    // Selections by workspace, so a session reads its own workspace's editor whichever one the user touched last.
    private readonly Dictionary<string, IdeSelectionChanged> latestSelection = [];
    private readonly Dictionary<string, IdeSelectionChanged> currentSelection = [];
    private string? recentWorkspace;
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

    /// <returns>How many of the workspace's sockets the message was written to.</returns>
    private async Task<int> Broadcast(string workspaceId, string message) =>
        (await Task.WhenAll(clients.Keys.Where(client => client.Workspace == workspaceId).Select(client => client.SendAsync(message)))).Count(sent => sent);

    // The plugin's contract counts lines and columns from one; the protocol, like the editors it was written for, from zero.
    private static JsonObject SelectionRange(IdeSelectionChanged value) => new()
    {
        ["start"] = new JsonObject { ["line"] = value.Selection.StartLine - 1, ["character"] = value.Selection.StartColumn - 1 },
        ["end"] = new JsonObject { ["line"] = value.Selection.EndLine - 1, ["character"] = value.Selection.EndColumn - 1 },
        ["isEmpty"] = value.Text.Length == 0
    };

    public void SelectionChanged(IdeSelectionChanged payload)
    {
        lock (gate)
        {
            currentSelection[payload.WorkspaceId] = payload;
            if (payload.Text.Length > 0) latestSelection[payload.WorkspaceId] = payload;
            recentWorkspace = payload.WorkspaceId;
        }
        _ = Broadcast(payload.WorkspaceId, IdeMcp.Notification("selection_changed", new JsonObject
        {
            ["text"] = payload.Text,
            ["filePath"] = payload.Path,
            ["fileUrl"] = "file://" + payload.Path,
            ["selection"] = SelectionRange(payload)
        }));
    }

    /// <summary>Forgets the selection a closed tab held. The protocol has no notification for a closed tab.</summary>
    public void DocumentClosed(IdeDocumentClosed payload)
    {
        lock (gate)
            if (currentSelection.GetValueOrDefault(payload.WorkspaceId)?.Path == payload.Path) currentSelection.Remove(payload.WorkspaceId);
    }

    /// <summary>
    /// Has every Claude session of the workspace type a reference to a file, or to lines of it, into its prompt, as
    /// the CLI does for <c>at_mentioned</c>; nothing is sent to the model until the user submits.
    /// </summary>
    /// <returns>How many sessions took it.</returns>
    public Task<int> AtMentioned(IdeAtMention mention)
    {
        var parameters = new JsonObject { ["filePath"] = mention.Path };
        if (mention is { StartLine: { } start, EndLine: { } end }) { parameters["lineStart"] = start - 1; parameters["lineEnd"] = end - 1; }
        return Broadcast(mention.WorkspaceId, IdeMcp.Notification("at_mentioned", parameters));
    }

    private static JsonObject SelectionPayload(IdeSelectionChanged? value, string missing) => value is null
        ? new JsonObject { ["success"] = false, ["message"] = missing }
        : new JsonObject
        {
            ["success"] = true,
            ["text"] = value.Text,
            ["filePath"] = value.Path,
            ["fileUrl"] = "file://" + value.Path,
            ["selection"] = SelectionRange(value)
        };

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string Plain(JsonObject arguments, string name) => arguments[name]?.ToString() ?? "";

    private static bool? Flag(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;

    private static int Closed(JsonElement? reply) =>
        reply is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("closed", out var closed) && closed.TryGetInt32(out var count) ? count : 0;

    /// <summary>
    /// Runs one IDE tool and returns its reply's text items. It acts in the calling session's workspace; a session
    /// SharpRail did not start has none and takes the workspace of the last selection.
    /// </summary>
    public async Task<IReadOnlyList<string>> CallToolAsync(string name, JsonObject arguments, string? session = null)
    {
        string? workspaceId;
        IdeSelectionChanged? current, latest;
        lock (gate)
        {
            workspaceId = session ?? recentWorkspace;
            current = workspaceId is null ? null : currentSelection.GetValueOrDefault(workspaceId);
            latest = workspaceId is null ? null : latestSelection.GetValueOrDefault(workspaceId);
        }
        Task<JsonElement?> Action(IdeActionKind kind, IdeActionParams parameters) =>
            RequestAction(workspaceId ?? throw new InvalidOperationException("No active SharpRail workspace for this request"), kind, parameters);
        static string[] Json(JsonNode value) => [value.ToJsonString()];
        static string[] Raw(JsonElement? value) => [value?.GetRawText() ?? "null"];
        switch (name)
        {
            case "getCurrentSelection": return Json(SelectionPayload(current, "No active editor found"));
            case "getLatestSelection": return Json(SelectionPayload(latest, "No selection available"));
            case "getWorkspaceFolders":
                var folders = deps.WorkspaceFolders();
                return Json(new JsonObject
                {
                    ["success"] = true,
                    ["folders"] = new JsonArray([.. folders.Select(path => (JsonNode?)new JsonObject
                        { ["name"] = path.Split('/').LastOrDefault(part => part.Length > 0) ?? path, ["uri"] = "file://" + path, ["path"] = path })]),
                    ["rootPath"] = workspaceId ?? folders.FirstOrDefault()
                });
            case "openFile":
                var opened = Plain(arguments, "filePath");
                await Action(IdeActionKind.OpenFile, new IdeActionParams
                {
                    Path = opened,
                    Preview = Flag(arguments, "preview"),
                    StartText = Text(arguments, "startText"),
                    EndText = Text(arguments, "endText")
                });
                return Flag(arguments, "makeFrontmost") == false ? Json(new JsonObject { ["success"] = true, ["filePath"] = opened }) : [$"Opened file: {opened}"];
            case "openDiff":
                return Raw(await Action(IdeActionKind.OpenDiff, new IdeActionParams
                {
                    OldPath = Text(arguments, "old_file_path"),
                    NewPath = Text(arguments, "new_file_path"),
                    NewContent = Text(arguments, "new_file_contents")
                }));
            case "getOpenEditors": return Raw(await Action(IdeActionKind.GetOpenEditors, new IdeActionParams()));
            case "checkDocumentDirty": return Raw(await Action(IdeActionKind.CheckDocumentDirty, new IdeActionParams { Path = Plain(arguments, "filePath") }));
            case "saveDocument": return Raw(await Action(IdeActionKind.SaveDocument, new IdeActionParams { Path = Plain(arguments, "filePath") }));
            // The CLI compares these two replies as words; it sends close_tab after every openDiff, whatever became of it.
            case "close_tab":
                return [Closed(await Action(IdeActionKind.CloseTab, new IdeActionParams { TabName = Plain(arguments, "tab_name") })) > 0 ? "TAB_CLOSED" : "Tab not found"];
            case "closeAllDiffTabs": return [$"CLOSED_{Closed(await Action(IdeActionKind.CloseAllDiffTabs, new IdeActionParams()))}_DIFF_TABS"];
            default: throw new InvalidOperationException($"Unknown tool: {name}");
        }
    }

    /// <summary>Answers one JSON-RPC message from a socket, or nothing for a notification.</summary>
    private async Task<string?> HandleAsync(Client client, string raw)
    {
        if (IdeMcp.Parse(raw) is not { } message) return null;
        // The one notification worth reading: it names the agent's own process, which is how a socket learns its workspace.
        if (message.Method == "ide_connected")
        {
            var pid = message.Params is JsonObject parameters && parameters["pid"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
            if (deps.WorkspaceForProcess(pid) is { } workspace) client.Workspace = workspace;
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
                    return IdeMcp.Success(id, IdeMcp.ToolContent(await CallToolAsync(name, call["arguments"] as JsonObject ?? [], client.Workspace)));
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
        // The CLI asks for the mcp subprotocol and, as RFC 6455 requires, drops a handshake that does not confirm it.
        var protocol = headers.GetValueOrDefault("sec-websocket-protocol", "").Split(',', StringSplitOptions.TrimEntries).Contains("mcp") ? "mcp" : null;
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n" +
            (protocol is null ? "" : $"Sec-WebSocket-Protocol: {protocol}\r\n") + "\r\n";
        try { await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { return; }
        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, SubProtocol = protocol, KeepAliveInterval = TimeSpan.FromSeconds(30) });
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
                    if (await HandleAsync(client, text) is { } answer) await client.SendAsync(answer);
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
        lock (gate) { currentSelection.Clear(); latestSelection.Clear(); recentWorkspace = null; }
    }

    /// <summary>Drops the selections and pending actions, as a fresh activation would have them; for the checks.</summary>
    internal void Reset()
    {
        foreach (var (id, entry) in pending.ToArray())
            if (pending.TryRemove(id, out _)) entry.TrySetCanceled();
        lock (gate) { currentSelection.Clear(); latestSelection.Clear(); recentWorkspace = null; }
    }
}