using System.IO.Pipelines;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Acp.Host;

namespace Tereditor.Checks;

// An in-process ACP agent for the checks: it speaks the real framing over a pipe pair and plays scripted turns chosen by the prompt text.
//   "hello"   thought, two message chunks, a tool call that completes with a diff, a plan; ends the turn.
//   "ask"     asks permission for a tool call and reports the chosen option as a message.
//   "read X"  calls fs/read_text_file for path X and reports the content (or the error).
//   "write X" calls fs/write_text_file for path X.
//   "run"     creates a terminal (echo hi), waits for exit and reports its output.
//   "wait"    streams a chunk and then waits for session/cancel; stops with "cancelled".
internal sealed class AcpFakeAgent : IAsyncDisposable
{
    private readonly Pipe toAgent = new(), fromAgent = new();
    private readonly AcpConnection connection;
    private readonly Dictionary<string, TaskCompletionSource> cancels = [];
    public int ProtocolVersion { get; init; } = 1;
    public bool LoadSession { get; init; } = true;
    public List<string> Methods { get; } = [];
    public JsonObject? ClientCapabilities { get; private set; }
    private int sessions;

    /// <summary>The client's side of the pipes: read what the agent writes, write what the client sends.</summary>
    public Stream ClientInput => fromAgent.Reader.AsStream();
    public Stream ClientOutput => toAgent.Writer.AsStream();

    public AcpFakeAgent(int protocolVersion = 1, bool loadSession = true)
    {
        ProtocolVersion = protocolVersion;
        LoadSession = loadSession;
        connection = new AcpConnection(toAgent.Reader.AsStream(), fromAgent.Writer.AsStream(), HandleAsync, Notified);
    }

    /// <summary>Ends the agent as a crashed process would: the client's input ends.</summary>
    public async Task KillAsync()
    {
        await fromAgent.Writer.CompleteAsync();
        await toAgent.Reader.CompleteAsync();
    }

    private void Notified(string method, JsonNode? parameters)
    {
        lock (Methods) Methods.Add(method);
        if (method == "session/cancel" && parameters?["sessionId"]?.GetValue<string>() is { } id)
            lock (cancels) { if (cancels.TryGetValue(id, out var waiting)) waiting.TrySetResult(); }
    }

    private Task Update(string session, JsonObject update) =>
        connection.NotifyAsync("session/update", new JsonObject { ["sessionId"] = session, ["update"] = update });

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private async Task<JsonNode?> HandleAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        lock (Methods) Methods.Add(method);
        switch (method)
        {
            case "initialize":
                ClientCapabilities = parameters?["clientCapabilities"] as JsonObject;
                return new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["agentCapabilities"] = new JsonObject { ["loadSession"] = LoadSession, ["promptCapabilities"] = new JsonObject { ["image"] = true, ["embeddedContext"] = true } },
                    ["authMethods"] = new JsonArray(new JsonObject { ["id"] = "login", ["name"] = "Log in", ["description"] = "Sign in with the agent's own flow." }),
                    ["agentInfo"] = new JsonObject { ["name"] = "fake-agent", ["title"] = "Fake agent", ["version"] = "1.0.0" }
                };
            case "authenticate": return new JsonObject();
            case "session/new":
                return new JsonObject
                {
                    ["sessionId"] = "sess-" + Interlocked.Increment(ref sessions),
                    ["modes"] = new JsonObject
                    {
                        ["currentModeId"] = "ask",
                        ["availableModes"] = new JsonArray(new JsonObject { ["id"] = "ask", ["name"] = "Ask" }, new JsonObject { ["id"] = "code", ["name"] = "Code" })
                    }
                };
            case "session/load":
                {
                    var id = parameters!["sessionId"]!.GetValue<string>();
                    await Update(id, new JsonObject { ["sessionUpdate"] = "user_message_chunk", ["content"] = Text("earlier question") });
                    await Update(id, new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = Text("earlier answer") });
                    return new JsonObject();
                }
            case "session/set_mode": return new JsonObject();
            case "session/prompt": return await PromptAsync(parameters!, ct);
            default: throw new AcpException(AcpException.MethodNotFound, "Unknown method " + method);
        }
    }

    private async Task<JsonNode?> PromptAsync(JsonNode parameters, CancellationToken ct)
    {
        var id = parameters["sessionId"]!.GetValue<string>();
        var text = string.Concat((parameters["prompt"] as JsonArray ?? []).Select(block => block?["text"]?.GetValue<string>()));
        JsonObject Stop(string reason) => new() { ["stopReason"] = reason };
        async Task Say(string message) => await Update(id, new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = Text(message) });
        var word = text.Split(' ', 2);
        switch (word[0])
        {
            case "hello":
                await Update(id, new JsonObject { ["sessionUpdate"] = "agent_thought_chunk", ["content"] = Text("Thinking.") });
                await Say("Hi ");
                await Say("there.");
                await Update(id, new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "t1", ["title"] = "Edit a.txt", ["kind"] = "edit", ["status"] = "in_progress", ["locations"] = new JsonArray(new JsonObject { ["path"] = "a.txt" }) });
                await Update(id, new JsonObject
                {
                    ["sessionUpdate"] = "tool_call_update",
                    ["toolCallId"] = "t1",
                    ["status"] = "completed",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = "a.txt", ["oldText"] = "old", ["newText"] = "new" })
                });
                await Update(id, new JsonObject
                {
                    ["sessionUpdate"] = "plan",
                    ["entries"] = new JsonArray(new JsonObject { ["content"] = "Step one", ["priority"] = "high", ["status"] = "completed" })
                });
                return Stop("end_turn");
            case "ask":
                {
                    var reply = await connection.RequestAsync("session/request_permission", new JsonObject
                    {
                        ["sessionId"] = id,
                        ["toolCall"] = new JsonObject { ["toolCallId"] = "t2", ["title"] = "Run rm", ["kind"] = "execute", ["status"] = "pending" },
                        ["options"] = new JsonArray(
                            new JsonObject { ["optionId"] = "once", ["name"] = "Allow once", ["kind"] = "allow_once" },
                            new JsonObject { ["optionId"] = "always", ["name"] = "Always allow", ["kind"] = "allow_always" },
                            new JsonObject { ["optionId"] = "no", ["name"] = "Reject", ["kind"] = "reject_once" })
                    }, ct);
                    var outcome = reply?["outcome"];
                    await Say("outcome " + (outcome?["outcome"]?.GetValue<string>() == "selected" ? outcome["optionId"]!.GetValue<string>() : "cancelled"));
                    return Stop("end_turn");
                }
            case "read":
                {
                    try
                    {
                        var result = await connection.RequestAsync("fs/read_text_file", new JsonObject { ["sessionId"] = id, ["path"] = word[1] }, ct);
                        await Say("content " + result?["content"]?.GetValue<string>());
                    }
                    catch (AcpException error) { await Say("error " + error.Message); }
                    return Stop("end_turn");
                }
            case "write":
                {
                    try
                    {
                        await connection.RequestAsync("fs/write_text_file", new JsonObject { ["sessionId"] = id, ["path"] = word[1], ["content"] = "written" }, ct);
                        await Say("written");
                    }
                    catch (AcpException error) { await Say("error " + error.Message); }
                    return Stop("end_turn");
                }
            case "run":
                {
                    var created = await connection.RequestAsync("terminal/create", new JsonObject { ["sessionId"] = id, ["command"] = "echo", ["args"] = new JsonArray("hi") }, ct);
                    var terminal = created!["terminalId"]!.GetValue<string>();
                    var exit = await connection.RequestAsync("terminal/wait_for_exit", new JsonObject { ["sessionId"] = id, ["terminalId"] = terminal }, ct);
                    var output = await connection.RequestAsync("terminal/output", new JsonObject { ["sessionId"] = id, ["terminalId"] = terminal }, ct);
                    await connection.RequestAsync("terminal/release", new JsonObject { ["sessionId"] = id, ["terminalId"] = terminal }, ct);
                    await Say($"exit {exit?["exitCode"]} output {output?["output"]?.GetValue<string>()?.Trim()}");
                    return Stop("end_turn");
                }
            case "wait":
                {
                    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (cancels) cancels[id] = cancelled;
                    await Say("working");
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    return Stop("cancelled");
                }
            default:
                await Say("echo " + text);
                return Stop("end_turn");
        }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}