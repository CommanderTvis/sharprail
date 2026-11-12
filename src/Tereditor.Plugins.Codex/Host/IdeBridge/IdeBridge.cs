using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.Codex.Host.IdeBridge;

/// <summary>
/// Codex's private, version-0 <c>ide-context</c> provider: it joins (or owns) the router at <paramref name="path"/>,
/// answers discovery only for the workspaces <paramref name="canHandle"/> accepts, and replies with what
/// <paramref name="context"/> reads. Unknown versions and methods are refused explicitly. Dispose to leave.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class CodexIdeBridge : IDisposable
{
    private readonly Connection connection;

    public CodexIdeBridge(string path, Func<string, Task<bool>> canHandle, Func<string, Task<JsonNode?>> context, Action<Exception> warn)
    {
        connection = new Connection(path,
            peer => peer.Send(new JsonObject
            {
                ["type"] = "request",
                ["requestId"] = Guid.NewGuid().ToString(),
                ["sourceClientId"] = "sharprail",
                ["method"] = "initialize",
                ["version"] = 0,
                // The router's editor-client vocabulary; wire compatibility, not a claim to run VS Code.
                ["params"] = new JsonObject { ["clientType"] = "vscode" }
            }),
            (peer, message) => Receive(peer, message, canHandle, context),
            warn);
    }

    private static int Version(JsonObject? message) => message?["version"] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;

    private static void Receive(Peer peer, JsonObject message, Func<string, Task<bool>> canHandle, Func<string, Task<JsonNode?>> context)
    {
        var type = Peer.Text(message["type"]);
        var requestId = Peer.Text(message["requestId"]);
        if (requestId is null || type is not ("client-discovery-request" or "request")) return;
        var request = type == "client-discovery-request" ? Peer.Record(message["request"]) : message;
        var root = Peer.Text(Peer.Record(request?["params"])?["workspaceRoot"]);
        var supported = Peer.Text(request?["method"]) == "ide-context" && Version(request) == 0;
        _ = Task.Run(async () =>
        {
            bool eligible;
            try { eligible = supported && root is not null && await canHandle(root); }
            catch (Exception) { eligible = false; }
            if (type == "client-discovery-request")
            {
                peer.Send(new JsonObject { ["type"] = "client-discovery-response", ["requestId"] = requestId, ["response"] = new JsonObject { ["canHandle"] = eligible } });
                return;
            }
            if (Version(message) != 0) peer.Failure(requestId, "request-version-mismatch");
            else if (!eligible) peer.Failure(requestId, "no-handler-for-request");
            else
                try
                {
                    var ideContext = await context(root!);
                    peer.Send(new JsonObject { ["type"] = "response", ["requestId"] = requestId, ["resultType"] = "success", ["result"] = new JsonObject { ["ideContext"] = ideContext } });
                }
                catch (Exception) { peer.Failure(requestId, "no-client-found"); }
        });
    }

    public void Dispose() => connection.Dispose();
}