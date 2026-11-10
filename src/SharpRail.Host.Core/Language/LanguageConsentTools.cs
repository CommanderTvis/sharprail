using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Host.Core.Language;

/// <summary>The way a host with a client asks its user a question: MCP elicitation. Unsupported when the client did not advertise it.</summary>
internal interface IConsentPrompt
{
    bool Supported { get; }

    /// <summary>Sends the request and returns the client's reply (<c>action</c> and <c>content</c>); throws when the client fails or does not answer.</summary>
    Task<JsonNode?> AskAsync(JsonObject request, CancellationToken token);
}

/// <summary>
/// The tools of headless consent: <c>accept_terms</c>, and a wrapper for the language tools that answers a call needing unaccepted terms with the terms
/// instead of starting anything. Consent is only ever recorded for an explicit Accept of a prompt, a call of <c>accept_terms</c> with the current hash and
/// every answer, or the environment variable; a Decline, a failed prompt or a client without elicitation leaves the text for the agent to relay.
/// </summary>
internal sealed class LanguageConsentTools(LanguageConsent consent, IConsentPrompt? prompt, Func<LanguageServer, CancellationToken, Task>? start)
{
    internal const string ToolName = "accept_terms";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> asking = new();
    private readonly ConcurrentDictionary<string, bool> declined = new();

    internal McpServer.McpTool AcceptTool()
    {
        var ids = new JsonArray(consent.Gated.Select(server => (JsonNode?)JsonValue.Create(server.Id)).ToArray());
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["server"] = new JsonObject { ["type"] = "string", ["enum"] = ids, ["description"] = "The language server whose terms these are." },
                ["hash"] = new JsonObject { ["type"] = "string", ["description"] = "The terms hash from the text a refused call returned. Leave it out to only read the terms." },
                ["version"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: the server version the text named." },
                ["answers"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" }, ["description"] = "The user's own answers to the questions in the text, by question name." }
            },
            ["required"] = new JsonArray("server"),
            ["additionalProperties"] = false
        };
        return new(ToolName, "accept terms",
            "Records that the USER accepted the terms of a language server that has them (the text comes with a refused Java or Kotlin call). " +
            "Call it ONLY after the user explicitly said yes in this chat to these terms, and send the user's own answers. Never call it on your own, never to try a server out, never with a guessed answer. " +
            "The hash must be the current terms hash from that text. With only server it shows the terms and whether they are accepted. Acceptance lasts as long as the host's state directory.",
            schema, CallAsync);
    }

    private async Task<(string Text, bool Error)> CallAsync(JsonObject arguments, CancellationToken token)
    {
        var gated = consent.Gated;
        var id = arguments["server"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : gated.Count == 1 ? gated[0].Id : "";
        if (gated.FirstOrDefault(server => server.Id == id) is not { } server)
            return ($"Name the server: one of {(gated.Count == 0 ? "none (no server here has terms)" : string.Join(", ", gated.Select(item => item.Id)))}.", true);
        var hash = arguments["hash"] is JsonValue hashed && hashed.TryGetValue<string>(out var h) ? h : "";
        if (hash.Length == 0) return (consent.Review(server), false);
        var answers = new Dictionary<string, string>();
        if (arguments["answers"] is JsonObject given)
            foreach (var pair in given)
                if (pair.Value is JsonValue answer && answer.TryGetValue<string>(out var chosen)) answers[pair.Key] = chosen;
        var version = arguments["version"] is JsonValue shown && shown.TryGetValue<string>(out var v) ? v : null;
        var (accepted, message) = await consent.AcceptAsync(server, hash, version, answers, "tool", token);
        if (!accepted) return (message, true);
        if (start is not null) _ = StartQuietlyAsync(server);
        return (message + $" {LanguageConsent.Name(server)} is downloaded (about 380 MB) and started by the next Java or Kotlin call, which answers initializing until it is ready; call again then.", false);
    }

    private async Task StartQuietlyAsync(LanguageServer server)
    {
        try { await start!(server, CancellationToken.None); }
        catch (Exception error) when (error is not OperationCanceledException) { Console.Error.WriteLine($"sharprail-headless: starting {server.Id} failed: {error.Message}"); }
    }

    /// <summary>A language tool that refuses, with the terms, a call needing a server whose terms are not accepted.</summary>
    internal McpServer.McpTool Guard(McpServer.McpTool tool) => tool with
    {
        Call = async (arguments, token) =>
        {
            if (consent.NeededBy(arguments) is { } needed)
            {
                if (await AskAsync(needed, token)) return await tool.Call(arguments, token);
                return (consent.Refusal(needed, declined.ContainsKey(needed.Id)), true);
            }
            var reply = await tool.Call(arguments, token);
            // A scan over a whole workspace found a file the server with unaccepted terms serves.
            if (!reply.Error && StateOf(reply.Text) == "requiresAcceptance" && consent.Gated.FirstOrDefault(server => consent.GrantFor(server) is null) is { } found)
                return await AskAsync(found, token) ? await tool.Call(arguments, token) : (consent.Refusal(found, declined.ContainsKey(found.Id)), true);
            return reply;
        }
    };

    private static string StateOf(string text)
    {
        try { return JsonNode.Parse(text) is JsonObject { } body && body["state"] is JsonValue state && state.TryGetValue<string>(out var name) ? name : ""; }
        catch (JsonException) { return ""; }
    }

    /// <summary>Asks the user through the client, once per server and process. True only when the user explicitly accepted and consent was recorded.</summary>
    private async Task<bool> AskAsync(LanguageServer server, CancellationToken token)
    {
        if (prompt is not { Supported: true } || declined.ContainsKey(server.Id)) return false;
        var gate = asking.GetOrAdd(server.Id, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (consent.GrantFor(server) is not null) return true;
            if (declined.ContainsKey(server.Id)) return false;
            declined[server.Id] = true;
            JsonNode? reply;
            try { reply = await prompt.AskAsync(consent.ElicitationRequest(server), token); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.Error.WriteLine($"sharprail-headless: the terms prompt for {server.Id} failed: {error.Message}");
                return false;
            }
            if (LanguageConsent.ElicitationAnswers(server, reply) is not { } answers) return false;
            var (accepted, message) = await consent.AcceptAsync(server, server.Spec.TermsSha256, server.Spec.Version, answers, "prompt", token);
            if (!accepted) { Console.Error.WriteLine("sharprail-headless: " + message); return false; }
            declined.TryRemove(server.Id, out _);
            return true;
        }
        finally { gate.Release(); }
    }
}
