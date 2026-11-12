using System.Text.Json.Nodes;

namespace SharpRail.Plugins.Acp.Host;

/// <summary>The agent's answer to <c>initialize</c>, reduced to what the client uses.</summary>
internal sealed record AcpAgentInfo(int ProtocolVersion, bool LoadSession, bool Image, bool Audio, bool EmbeddedContext, IReadOnlyList<AcpAuthMethod> AuthMethods, string? Name, string? Version, bool CanListSessions, JsonObject Raw);

internal sealed record AcpAuthMethod(string Id, string Name, string? Description);

/// <summary>Raised when the agent speaks a protocol version this client does not.</summary>
public sealed class AcpVersionException(int agent) : InvalidOperationException($"The agent speaks ACP version {agent}; SharpRail supports version {AcpClient.ProtocolVersion}.")
{
    public int AgentVersion { get; } = agent;
}

/// <summary>What the client side serves to the agent: the methods the agent calls and the updates it streams.</summary>
internal interface IAcpClientHandler
{
    /// <summary>A <c>session/update</c> notification's <c>update</c> object.</summary>
    void Update(string sessionId, JsonObject update);
    /// <summary>An agent-to-client request other than session/update (fs/*, terminal/*, session/request_permission).</summary>
    Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken);
}

/// <summary>The client end of the Agent Client Protocol (stable version 1) over one <see cref="AcpConnection"/>.</summary>
internal sealed class AcpClient : IAsyncDisposable
{
    public const int ProtocolVersion = 1;

    private readonly AcpConnection connection;

    public AcpClient(Stream fromAgent, Stream toAgent, IAcpClientHandler handler)
    {
        connection = new AcpConnection(fromAgent, toAgent,
            (method, parameters, ct) => handler.RequestAsync(method, parameters as JsonObject ?? new JsonObject(), ct),
            (method, parameters) =>
            {
                if (method == "session/update" && parameters is JsonObject p && p["sessionId"]?.GetValue<string>() is { } id && p["update"] is JsonObject update) handler.Update(id, update);
            });
    }

    public Task Closed => connection.Closed;

    /// <summary>
    /// Advertises the client capabilities (file and terminal methods) and negotiates the version: an agent that answers with a
    /// higher version than asked is not understood, so the client refuses it rather than guessing.
    /// </summary>
    public async Task<AcpAgentInfo> InitializeAsync(string clientName, string clientVersion, CancellationToken cancellationToken)
    {
        var result = await connection.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = true, ["writeTextFile"] = true },
                ["terminal"] = true
            },
            ["clientInfo"] = new JsonObject { ["name"] = clientName.ToLowerInvariant(), ["title"] = clientName, ["version"] = clientVersion }
        }, cancellationToken) as JsonObject ?? throw new AcpException(AcpException.Internal, "The agent answered initialize with no result.");
        var version = result["protocolVersion"]?.GetValue<int>() ?? 0;
        if (version != ProtocolVersion) throw new AcpVersionException(version);
        var capabilities = result["agentCapabilities"] as JsonObject;
        var prompt = capabilities?["promptCapabilities"] as JsonObject;
        var info = result["agentInfo"] as JsonObject;
        return new AcpAgentInfo(version,
            capabilities?["loadSession"]?.GetValue<bool>() == true,
            prompt?["image"]?.GetValue<bool>() == true, prompt?["audio"]?.GetValue<bool>() == true, prompt?["embeddedContext"]?.GetValue<bool>() == true,
            [.. (result["authMethods"] as JsonArray ?? []).OfType<JsonObject>().Select(method => new AcpAuthMethod(method["id"]?.GetValue<string>() ?? "", method["name"]?.GetValue<string>() ?? "", method["description"]?.GetValue<string>()))],
            info?["title"]?.GetValue<string>() ?? info?["name"]?.GetValue<string>(), info?["version"]?.GetValue<string>(),
            (capabilities?["sessionCapabilities"] as JsonObject)?.ContainsKey("list") == true, result);
    }

    public Task<JsonNode?> AuthenticateAsync(string methodId, CancellationToken cancellationToken) =>
        connection.RequestAsync("authenticate", new JsonObject { ["methodId"] = methodId }, cancellationToken);

    /// <summary>Starts a session; the result carries <c>sessionId</c> and, when offered, <c>modes</c>, <c>models</c> and <c>configOptions</c>.</summary>
    public async Task<JsonObject> NewSessionAsync(string cwd, CancellationToken cancellationToken) =>
        await connection.RequestAsync("session/new", new JsonObject { ["cwd"] = cwd, ["mcpServers"] = new JsonArray() }, cancellationToken) as JsonObject
        ?? throw new AcpException(AcpException.Internal, "The agent answered session/new with no result.");

    /// <summary>Resumes a stored session; the agent replays the conversation as updates before this returns.</summary>
    public async Task<JsonObject> LoadSessionAsync(string sessionId, string cwd, CancellationToken cancellationToken) =>
        await connection.RequestAsync("session/load", new JsonObject { ["sessionId"] = sessionId, ["cwd"] = cwd, ["mcpServers"] = new JsonArray() }, cancellationToken) as JsonObject ?? new JsonObject();

    /// <summary>Runs one turn. Returns the stop reason (<c>end_turn</c>, <c>max_tokens</c>, <c>max_turn_requests</c>, <c>refusal</c>, <c>cancelled</c>).</summary>
    public async Task<string> PromptAsync(string sessionId, JsonArray content, CancellationToken cancellationToken) =>
        (await connection.RequestAsync("session/prompt", new JsonObject { ["sessionId"] = sessionId, ["prompt"] = content }, cancellationToken) as JsonObject)?["stopReason"]?.GetValue<string>() ?? "end_turn";

    /// <summary>session/set_model, an unstable method some agents serve for the models they list; an agent without it answers method-not-found.</summary>
    public Task<JsonNode?> SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken) =>
        connection.RequestAsync("session/set_model", new JsonObject { ["sessionId"] = sessionId, ["modelId"] = modelId }, cancellationToken);

    public Task CancelAsync(string sessionId) => connection.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId });

    public Task<JsonNode?> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken) =>
        connection.RequestAsync("session/set_mode", new JsonObject { ["sessionId"] = sessionId, ["modeId"] = modeId }, cancellationToken);

    public Task<JsonNode?> SetConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken) =>
        connection.RequestAsync("session/set_config_option", new JsonObject { ["sessionId"] = sessionId, ["configId"] = configId, ["value"] = value }, cancellationToken);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}