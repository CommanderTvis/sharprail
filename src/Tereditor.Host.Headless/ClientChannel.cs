using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Tereditor.Host.Core.Language;

namespace Tereditor.Host.Headless;

/// <summary>
/// Requests the server sends to its MCP client: today only elicitation, which asks the user a question with Accept and Decline. Used when the client
/// advertised it in <c>initialize</c> on a protocol version that has it; otherwise <see cref="Supported"/> is false and the text flow is the only one.
/// </summary>
internal sealed class ClientChannel : IConsentPrompt
{
    private const string FirstWithElicitation = "2025-06-18";
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
    private Func<JsonNode, Task>? send;
    private long counter;
    private volatile bool elicitation;

    public bool Supported => elicitation && send is not null;

    /// <summary>Connects the channel to the output stream.</summary>
    internal void Attach(Func<JsonNode, Task> output) => send = output;

    /// <summary>Reads what the client can do from its <c>initialize</c> parameters; <paramref name="negotiated"/> is the protocol version the server answered with.</summary>
    internal void Initialized(JsonObject parameters, string negotiated) =>
        elicitation = string.CompareOrdinal(negotiated, FirstWithElicitation) >= 0 && parameters["capabilities"] is JsonObject { } capabilities && capabilities["elicitation"] is JsonObject;

    /// <summary>The version <c>initialize</c> answers with, as <see cref="McpServer"/> picks it.</summary>
    internal static string Negotiated(JsonObject parameters) =>
        parameters["protocolVersion"] is JsonValue value && value.TryGetValue<string>(out var requested) && requested is "2024-11-05" or "2025-03-26" or "2025-06-18" ? requested : "2025-06-18";

    /// <summary>Delivers the client's answer to a request of this channel; false for a frame that is not one.</summary>
    internal bool TryComplete(JsonObject frame)
    {
        if (frame["id"] is not JsonValue id || !id.TryGetValue<string>(out var key) || !pending.TryRemove(key, out var waiting)) return false;
        if (frame["error"] is JsonObject error) waiting.TrySetException(new IOException("The client refused the request: " + (error["message"]?.ToString() ?? "error")));
        else waiting.TrySetResult(frame["result"]?.DeepClone());
        return true;
    }

    public async Task<JsonNode?> AskAsync(JsonObject request, CancellationToken token)
    {
        if (!Supported) throw new NotSupportedException("The client does not support elicitation.");
        var id = "tereditor-" + Interlocked.Increment(ref counter);
        var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiting;
        try
        {
            await send!(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "elicitation/create", ["params"] = request.DeepClone() });
            return await waiting.Task.WaitAsync(Wait, token);
        }
        finally { pending.TryRemove(id, out _); }
    }
}