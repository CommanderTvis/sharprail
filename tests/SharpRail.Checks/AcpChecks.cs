using System.Text.Json.Nodes;

using SharpRail.Plugins.Acp;
using SharpRail.Plugins.Acp.Host;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Chat;

namespace SharpRail.Checks;

// The ACP plugin. Protocol core: framing, version negotiation and a session flow against the in-process fake agent (AcpFakeAgent).
internal static class AcpChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("acp: " + message);
    }

    private sealed class Recorder : IAcpClientHandler
    {
        public readonly List<(string Session, JsonObject Update)> Updates = [];

        public void Update(string sessionId, JsonObject update)
        {
            lock (Updates) Updates.Add((sessionId, update));
        }

        public Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken) =>
            throw new AcpException(AcpException.MethodNotFound, method);

        public string[] Kinds()
        {
            lock (Updates) return [.. Updates.Select(update => update.Update["sessionUpdate"]!.GetValue<string>())];
        }
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("acp: " + message);
            await Task.Delay(10);
        }
    }

    internal static void Manifest()
    {
        var manifest = AcpManifest.Manifest;
        Require(manifest.Id == "acp" && !manifest.EnabledByDefault, "ACP is a builtin that is off by default.");
        Require(manifest.Support == PluginSupportLevels.Early && manifest.Label.Contains("Early support", StringComparison.Ordinal) && manifest.Description!.StartsWith("Early support", StringComparison.Ordinal), "ACP carries the Early support label in its name and description.");
        Require(manifest.UnavailableOn.SequenceEqual([HostKind.PureWeb]), "A host without processes does not offer ACP.");
        Require(ChatContract.Methods.All(method => AcpContract.Contract.Methods.Contains(method)), "ACP serves every chat method.");
    }

    internal static async Task Protocol()
    {
        // Framing and the session flow.
        var handler = new Recorder();
        await using var agent = new AcpFakeAgent();
        await using var client = new AcpClient(agent.ClientInput, agent.ClientOutput, handler);
        var info = await client.InitializeAsync("SharpRail", "0.1", CancellationToken.None);
        Require(info is { ProtocolVersion: 1, LoadSession: true, Image: true, EmbeddedContext: true, Audio: false, Name: "Fake agent", Version: "1.0.0" }, "initialize reports the agent's version, capabilities and identity.");
        Require(info.AuthMethods.Single() is { Id: "login", Name: "Log in" }, "Authentication methods are listed, never asked for here.");
        Require(agent.ClientCapabilities!["fs"]!["readTextFile"]!.GetValue<bool>() && agent.ClientCapabilities["terminal"]!.GetValue<bool>(), "The client advertises the file and terminal methods it serves.");

        var session = await client.NewSessionAsync("/work", CancellationToken.None);
        var sessionId = session["sessionId"]!.GetValue<string>();
        Require(sessionId == "sess-1" && session["modes"]!["currentModeId"]!.GetValue<string>() == "ask", "session/new returns the id and the modes.");

        var stop = await client.PromptAsync(sessionId, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hello" }), CancellationToken.None);
        await Until(() => handler.Kinds().Length >= 6, "The streamed updates arrive.");
        Require(stop == "end_turn", "The turn ends with its stop reason.");
        Require(handler.Kinds().SequenceEqual(["agent_thought_chunk", "agent_message_chunk", "agent_message_chunk", "tool_call", "tool_call_update", "plan"]), "Updates arrive in the order the agent sent them.");

        // A message with a line break inside a string is still one frame.
        var multi = await client.PromptAsync(sessionId, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "line one\nline two" }), CancellationToken.None);
        await Until(() => handler.Kinds().Length >= 7, "The multi-line echo arrives.");
        Require(multi == "end_turn" && handler.Updates[^1].Update["content"]!["text"]!.GetValue<string>() == "echo line one\nline two", "A line break inside a string does not split the frame.");

        // Cancel: a notification that ends the pending prompt with `cancelled`.
        var waiting = client.PromptAsync(sessionId, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "wait" }), CancellationToken.None);
        await Until(() => handler.Kinds().Length >= 8, "The long turn starts.");
        await client.CancelAsync(sessionId);
        Require(await waiting == "cancelled", "session/cancel ends the turn with the cancelled stop reason.");

        // A request the client does not serve is answered with method-not-found rather than hanging the agent.
        var loaded = await client.LoadSessionAsync("old", "/work", CancellationToken.None);
        Require(loaded is not null && handler.Updates.Any(update => update.Session == "old" && update.Update["sessionUpdate"]!.GetValue<string>() == "user_message_chunk"), "session/load replays the stored conversation as updates.");

        // The agent dies: pending requests fail instead of hanging.
        var hung = client.PromptAsync(sessionId, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "wait" }), CancellationToken.None);
        await Until(() => handler.Kinds().Count(kind => kind == "agent_message_chunk") >= 6, "The second long turn starts.");
        await agent.KillAsync();
        var failed = false;
        try { await hung; } catch (AcpClosedException) { failed = true; }
        Require(failed, "A pending request fails when the agent's output ends.");
        await client.Closed;

        // Version negotiation.
        await using var future = new AcpFakeAgent(protocolVersion: 2);
        await using var other = new AcpClient(future.ClientInput, future.ClientOutput, new Recorder());
        AcpVersionException? mismatch = null;
        try { await other.InitializeAsync("SharpRail", "0.1", CancellationToken.None); } catch (AcpVersionException error) { mismatch = error; }
        Require(mismatch is { AgentVersion: 2 }, "An agent on another protocol version is refused by name.");
    }

    internal static async Task Run(string root)
    {
        Manifest();
        await Protocol();
    }
}