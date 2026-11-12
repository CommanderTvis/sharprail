using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Chat;
using SharpRail.Plugins.Pi;
using SharpRail.Plugins.Pi.Host;

namespace SharpRail.Checks;

// The Pi plugin: its manifest (off by default, unavailable on a pure web host), the folding of Pi's RPC events and stored
// messages into chat items, and a session driven end to end against a fixture `pi` that speaks the RPC protocol.
internal static class PiChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("pi: " + message);
    }

    private static JsonElement Line(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal static void Manifest()
    {
        var manifest = PiManifest.Manifest;
        Require(manifest.Id == "pi" && !manifest.EnabledByDefault, "Pi is a builtin that is off by default.");
        Require(manifest.UnavailableOn.Count == 0, "Pi runs in a browser tab too, through PiLinks.InTab, so no host kind refuses it.");
        Require(PiContract.Contract.Id == manifest.Id, "The contract is the plugin's own.");
        Require(ChatContract.Methods.All(method => PiContract.Contract.Methods.Contains(method)), "Pi serves every chat method.");
    }

    internal static void Fold()
    {
        var folder = new PiFolder();
        string[] events =
        [
            """{"type":"agent_start"}""",
            """{"type":"message_start","message":{"role":"user","content":"list files"}}""",
            """{"type":"message_start","message":{"role":"assistant","content":[]}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_start","contentIndex":0}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_delta","contentIndex":0,"delta":"Let me look."}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_end","contentIndex":0,"content":"Let me look."}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"text_start","contentIndex":1}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":1,"delta":"Running "}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":1,"delta":"ls."}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"toolcall_end","contentIndex":2,"toolCall":{"id":"call1","name":"bash","arguments":{"command":"ls -la"}}}}""",
            """{"type":"tool_execution_start","toolCallId":"call1","toolName":"bash","args":{"command":"ls -la"}}""",
            """{"type":"tool_execution_update","toolCallId":"call1","toolName":"bash","partialResult":{"content":[{"type":"text","text":"total 1"}]}}""",
            """{"type":"tool_execution_end","toolCallId":"call1","toolName":"bash","result":{"content":[{"type":"text","text":"total 1\nfile.txt"}]},"isError":false}""",
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Let me look."},{"type":"text","text":"Running ls."},{"type":"toolCall","id":"call1","name":"bash","arguments":{"command":"ls -la"}}],"usage":{"input":100,"output":20,"cacheRead":0,"cacheWrite":0,"cost":{"total":0.002}},"stopReason":"toolUse"}}""",
            """{"type":"agent_end","messages":[]}"""
        ];
        foreach (var json in events) Require(folder.Apply(Line(json)), "A documented event is folded: " + json[..Math.Min(60, json.Length)]);
        Require(folder.Items.Select(item => item.GetType()).SequenceEqual([typeof(ChatUserItem), typeof(ChatThinkingItem), typeof(ChatAssistantItem), typeof(ChatToolItem)]),
            "A turn folds to the prompt, the thinking, the reply and the tool call, in order.");
        Require(folder.Items.OfType<ChatAssistantItem>().Single() is { Text: "Running ls.", Streaming: false }, "Streamed deltas meet in one finished reply.");
        var tool = folder.Items.OfType<ChatToolItem>().Single();
        Require(tool is { Name: "bash", State: ChatToolState.Done, Summary: "ls -la" } && tool.Output == "total 1\nfile.txt", "The tool card carries its command, state and final output.");
        Require(folder.Header is { Status: ChatStatus.Idle } && folder.Header.Usage is { Input: 100, Output: 20 } && Math.Abs(folder.Header.Usage.CostUsd - 0.002) < 1e-9, "The turn's usage lands in the header and the chat is idle again.");

        var failing = new PiFolder();
        failing.Apply(Line("""{"type":"tool_execution_start","toolCallId":"c2","toolName":"edit","args":{"path":"a.cs"}}"""));
        failing.Apply(Line("""{"type":"tool_execution_end","toolCallId":"c2","toolName":"edit","result":{"content":[{"type":"text","text":"no match"}]},"isError":true}"""));
        Require(failing.Items.OfType<ChatToolItem>().Single() is { State: ChatToolState.Failed, FilePath: "a.cs" }, "A failed edit is marked failed and keeps its file for the diff.");

        var queued = new PiFolder();
        var waiting = queued.AddQueued("also tests");
        queued.Apply(Line("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"also tests"}]}}"""));
        Require(queued.Items.OfType<ChatUserItem>().Single() is { Queued: false } released && released.Id == waiting.Id, "Pi's echo of a queued message releases that row instead of adding another.");

        var replay = new PiFolder();
        replay.Replay([
            Line("""{"role":"user","content":"hi"}"""),
            Line("""{"role":"assistant","content":[{"type":"text","text":"hello"},{"type":"toolCall","id":"t1","name":"read","arguments":{"path":"a.txt"}}],"usage":{"input":1,"output":1}}"""),
            Line("""{"role":"toolResult","toolCallId":"t1","toolName":"read","content":[{"type":"text","text":"contents"}],"isError":false}""")]);
        Require(replay.Items.Select(item => item.GetType()).SequenceEqual([typeof(ChatUserItem), typeof(ChatAssistantItem), typeof(ChatToolItem)])
            && replay.Items.OfType<ChatToolItem>().Single() is { State: ChatToolState.Done, Output: "contents" }, "A stored session replays as the same items a live one folds to.");

        var long_ = new PiFolder();
        var big = new string('x', ChatContract.OutputLimit + 10);
        long_.Apply(Line("{\"type\":\"tool_execution_start\",\"toolCallId\":\"c3\",\"toolName\":\"bash\",\"args\":{\"command\":\"cat big\"}}"));
        long_.Apply(Line("{\"type\":\"tool_execution_end\",\"toolCallId\":\"c3\",\"toolName\":\"bash\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"" + big + "\"}]},\"isError\":false}"));
        Require(long_.Items.OfType<ChatToolItem>().Single() is { OutputTruncated: true } cut && cut.Output!.Length == ChatContract.OutputLimit && long_.Detail("c3") == big, "Output over the wire limit is cut and served whole as the item's detail.");
    }

    // A fixture pi: answers the RPC commands the session sends and plays one short turn for a prompt.
    private static string FakePi(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pi");
        File.WriteAllText(path, """
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "0.99.1"; exit 0; fi
            printf '%s\n' "$@" > "$(dirname "$0")/argv.txt"
            while IFS= read -r line; do
              id=$(printf '%s' "$line" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
              case "$line" in
                *'"type":"get_state"'*) printf '{"id":"%s","type":"response","command":"get_state","success":true,"data":{"model":{"provider":"fake","id":"m1","contextWindow":1000},"thinkingLevel":"medium","sessionFile":"/nonexistent/s.jsonl","sessionId":"s1"}}\n' "$id" ;;
                *'"type":"get_available_models"'*) printf '{"id":"%s","type":"response","command":"get_available_models","success":true,"data":{"models":[{"provider":"fake","id":"m1","name":"Model One","reasoning":true},{"provider":"fake","id":"m2","name":"Model Two","reasoning":false}]}}\n' "$id" ;;
                *'"type":"get_commands"'*) printf '{"id":"%s","type":"response","command":"get_commands","success":true,"data":{"commands":[{"name":"skill:x","description":"A skill","source":"skill"}]}}\n' "$id" ;;
                *'"type":"set_model"'*) printf '{"id":"%s","type":"response","command":"set_model","success":true,"data":{}}\n' "$id" ;;
                *'"type":"set_thinking_level"'*) printf '{"id":"%s","type":"response","command":"set_thinking_level","success":true}\n' "$id" ;;
                *'"type":"prompt"'*)
                  printf '{"id":"%s","type":"response","command":"prompt","success":true}\n' "$id"
                  printf '{"type":"agent_start"}\n'
                  printf '{"type":"message_start","message":{"role":"user","content":"hello pi"}}\n'
                  printf '{"type":"message_start","message":{"role":"assistant","content":[]}}\n'
                  printf '{"type":"message_update","assistantMessageEvent":{"type":"text_start","contentIndex":0}}\n'
                  printf '{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"Hi there."}}\n'
                  printf '{"type":"message_update","assistantMessageEvent":{"type":"text_end","contentIndex":0,"content":"Hi there."}}\n'
                  printf '{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Hi there."}],"usage":{"input":5,"output":3},"stopReason":"stop"}}\n'
                  printf '{"type":"agent_end","messages":[]}\n' ;;
                *'"type":"abort"'*) printf '{"id":"%s","type":"response","command":"abort","success":true}\n' "$id" ;;
              esac
            done
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static async Task Session(string root)
    {
        var directory = Path.Combine(root, "pi");
        var fake = FakePi(directory);
        var pushes = new List<ChatPush>();
        var remembered = new List<string?>();
        await using var session = new PiChatSession("ws", "chat1", push => { lock (pushes) pushes.Add(push); }, _ => { }, (_, file) => remembered.Add(file));
        var spawn = new PiSpawn(fake, directory) { Model = "fake/m1", Thinking = "high" };
        Require(session.Begin(spawn) is { Status: ChatStatus.Starting, Cwd: { } cwd } && cwd == directory, "Begin reports a starting chat in its directory.");
        await session.StartAsync(spawn);
        await Until(() => session.Header.Status == ChatStatus.Idle, "The session starts, reads Pi's state and goes idle.");
        var header = session.Header;
        Require(header.Model == "fake/m1" && header.Effort == "high" && header.SessionId == "s1", "The header carries the model, thinking level and session id Pi reported.");
        Require(header.Models.Select(model => model.Id).SequenceEqual(["fake/m1", "fake/m2"]) && header.Models[0].Efforts.Count > 0 && header.Models[1].Efforts.Count == 0, "Models come from Pi, with thinking levels only for reasoning ones.");
        Require(header.Commands.Any(command => command.Name == "skill:x"), "Pi's commands feed the slash menu.");
        Require(File.ReadAllLines(Path.Combine(directory, "argv.txt")).Take(4).SequenceEqual(["--mode", "rpc", "--model", "fake/m1"]), "Pi starts in RPC mode with the chosen model.");

        await session.SendAsync("hello pi", null);
        await Until(() => session.Header.Status == ChatStatus.Idle && session.Snapshot().OfType<ChatItemPush>().Any(push => push.Item is ChatAssistantItem { Text: "Hi there." }), "A prompt streams a reply and the chat returns to idle.");
        var items = session.Snapshot().OfType<ChatItemPush>().Select(push => push.Item).ToArray();
        Require(items.Length == 2 && items[0] is ChatUserItem { Text: "hello pi" }, "The conversation is the prompt and the reply.");
        Require(session.Header.Usage is { Input: 5, Output: 3 }, "Usage is counted.");

        header = await session.SetModelAsync("fake/m2");
        Require(header.Model == "fake/m2", "A model change is confirmed by Pi before the header shows it.");
        header = await session.SetEffortAsync("low");
        Require(header.Effort == "low", "A thinking-level change is reflected.");
        await session.CloseAsync();
        Require(session.Header.Status == ChatStatus.Exited, "Closing ends the child.");

        var missing = new PiChatSession("ws", "chat2", _ => { }, _ => { }, (_, _) => { });
        var absent = new PiSpawn(Path.Combine(directory, "no-such-pi"), directory);
        missing.Begin(absent);
        await missing.StartAsync(absent);
        Require(missing.Header is { Status: ChatStatus.Failed, Error: { } error } && error.Contains("not found", StringComparison.Ordinal), "A missing pi fails the chat with an install hint.");

        var status = await PiHost.ReadStatusAsync(fake);
        Require(status is { Installed: true, Version: "0.99.1" }, "The version probe reads pi --version.");
        Require(!(await PiHost.ReadStatusAsync(Path.Combine(directory, "none"))).Installed, "A missing pi is reported as not installed.");
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("pi: " + message);
            await Task.Delay(25);
        }
    }

    internal static void Sessions(string root)
    {
        var sessions = Path.Combine(root, "pi-sessions");
        var cwd = "/work/project";
        var directory = Path.Combine(sessions, "--work-project--");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.jsonl"), "{\"type\":\"session\",\"id\":\"a\"}\n{\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":\"fix the build\"}}\n");
        File.WriteAllText(Path.Combine(directory, "b.jsonl"), "{\"type\":\"session\",\"id\":\"b\"}\n{\"type\":\"session_info\",\"name\":\"Named\"}\n");
        File.SetLastWriteTimeUtc(Path.Combine(directory, "a.jsonl"), DateTime.UtcNow.AddMinutes(-5));
        var host = new PiChatHost(null!, () => sessions);
        var list = host.ListSessions(cwd).Sessions;
        Require(list.Select(session => session.Title).SequenceEqual(["Named", "fix the build"]), "Past sessions list newest first, titled by name or first prompt.");
        Require(host.ListSessions("/elsewhere").Sessions.Count == 0, "A directory with no sessions lists none.");
    }

    internal static async Task Run(string root)
    {
        Manifest();
        Fold();
        Sessions(root);
        await Session(root);
    }
}