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

    private sealed class FakeTransport(AcpFakeAgent agent) : IAcpTransport
    {
        public Stream FromAgent => agent.ClientInput;
        public Stream ToAgent => agent.ClientOutput;
        public string ErrorTail => "";
        public ValueTask DisposeAsync() => agent.DisposeAsync();
    }

    private static ChatItem[] Items(AcpChatSession session) => [.. session.Snapshot().OfType<ChatItemPush>().Select(push => push.Item)];

    internal static async Task Session()
    {
        var agents = new List<AcpFakeAgent>();
        Func<Task<IAcpTransport>> connect = () =>
        {
            var agent = new AcpFakeAgent();
            agents.Add(agent);
            return Task.FromResult<IAcpTransport>(new FakeTransport(agent));
        };
        await using var session = new AcpChatSession("ws", "chat1", "/work", connect, _ => { }, _ => { });
        Require(session.Begin() is { Status: ChatStatus.Starting, Cwd: "/work" }, "Begin reports a starting chat in its directory.");
        await session.StartAsync(null);
        Require(session.Header is { Status: ChatStatus.Idle, SessionId: "sess-1", PermissionMode: "ask", Title: "Fake agent" } && session.Header.PermissionModes.Select(mode => mode.Key).SequenceEqual(["ask", "code"]), "The session starts idle with the agent's modes and identity.");

        await session.SendAsync("hello", null);
        await Until(() => session.Header.Status == ChatStatus.Idle && Items(session).OfType<ChatAssistantItem>().Any(item => item.Id == "plan"), "A prompt streams a reply and the chat returns to idle.");
        var items = Items(session);
        Require(items.Select(item => item.GetType()).SequenceEqual([typeof(ChatUserItem), typeof(ChatThinkingItem), typeof(ChatAssistantItem), typeof(ChatToolItem), typeof(ChatAssistantItem)]), "A turn folds to the prompt, thought, reply, tool call and plan in order.");
        Require(items.OfType<ChatAssistantItem>().First() is { Text: "Hi there.", Streaming: false }, "Message chunks meet in one finished reply.");
        Require(items.OfType<ChatToolItem>().Single() is { State: ChatToolState.Done, FilePath: "a.txt", Name: "Edit a.txt" } tool && tool.Output!.Contains("-old", StringComparison.Ordinal) && tool.Output.Contains("+new", StringComparison.Ordinal), "The tool card keeps its file and shows the diff.");
        Require(items.OfType<ChatAssistantItem>().Last().Text.Contains("[x] Step one", StringComparison.Ordinal), "The plan shows its entries and their status.");

        await session.SendAsync("ask", null);
        await Until(() => session.Header.Status == ChatStatus.AwaitingPermission, "A permission request waits for the user.");
        var request = Items(session).OfType<ChatPermissionItem>().Single();
        Require(request is { Decision: ChatPermissionDecision.Pending, AlwaysLabel: "Always allow", Title: "Run rm" }, "The request is shown with the agent's own title and its always option.");
        session.Answer(request.RequestId, true, remember: false);
        await Until(() => session.Header.Status == ChatStatus.Idle && Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "outcome once"), "Allow once answers with the allow_once option.");
        Require(Items(session).OfType<ChatPermissionItem>().Single().Decision == ChatPermissionDecision.Allowed, "The card records the decision.");

        await session.SendAsync("ask", null);
        await Until(() => session.Header.Status == ChatStatus.AwaitingPermission, "A second request waits.");
        session.Answer(Items(session).OfType<ChatPermissionItem>().Last().RequestId, false, false);
        await Until(() => Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "outcome no"), "A rejection answers with the reject option.");
        await Until(() => session.Header.Status == ChatStatus.Idle, "The chat is idle after the rejection.");

        await session.SendAsync("ask", null);
        await Until(() => session.Header.Status == ChatStatus.AwaitingPermission, "A third request waits.");
        await session.InterruptAsync();
        await Until(() => Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "outcome cancelled"), "Cancelling answers a waiting permission as cancelled.");
        await Until(() => session.Header.Status == ChatStatus.Idle, "The chat is idle after cancelling a request.");

        await session.SendAsync("wait", null);
        await Until(() => Items(session).OfType<ChatAssistantItem>().Any(item => item.Text == "working"), "A long turn starts.");
        await session.InterruptAsync();
        await Until(() => session.Header.Status == ChatStatus.Idle && Items(session).OfType<ChatNoticeItem>().Any(n => n.Category == ChatNoticeKinds.Interrupt), "Interrupt ends the turn with an interrupt marker.");

        // The agent crashes mid-turn: the chat shows it exited, and a new session starts a fresh agent.
        await session.SendAsync("wait", null);
        await Until(() => Items(session).OfType<ChatAssistantItem>().Count(item => item.Text == "working") == 2, "The second long turn starts.");
        await agents[0].KillAsync();
        await Until(() => session.Header.Status == ChatStatus.Exited, "An agent that ends shows the chat as exited.");
        Require(Items(session).OfType<ChatNoticeItem>().Any(n => n.Category == ChatNoticeKinds.Failure), "The exit is explained in a failure notice.");
        Require(!session.Active, "An exited session is not active, so opening the chat again restarts it.");
        await using var restarted = new AcpChatSession("ws", "chat1", "/work", connect, _ => { }, _ => { });
        restarted.Begin();
        await restarted.StartAsync("old-session");
        Require(restarted.Header.Status == ChatStatus.Idle && restarted.SessionId == "old-session" && Items(restarted).OfType<ChatUserItem>().Single().Text == "earlier question" && Items(restarted).OfType<ChatAssistantItem>().Single().Text == "earlier answer", "A restart resumes the stored session through session/load and shows the replayed conversation.");

        // An agent without loadSession starts a new session instead.
        await using var plain = new AcpChatSession("ws", "chat2", "/work", () => Task.FromResult<IAcpTransport>(new FakeTransport(new AcpFakeAgent(loadSession: false))), _ => { }, _ => { });
        plain.Begin();
        await plain.StartAsync("old-session");
        Require(plain.SessionId == "sess-1", "Resume falls back to a new session when the agent cannot load one.");

        // An agent on another protocol version fails the chat with its reason.
        await using var future = new AcpChatSession("ws", "chat3", "/work", () => Task.FromResult<IAcpTransport>(new FakeTransport(new AcpFakeAgent(protocolVersion: 2))), _ => { }, _ => { });
        future.Begin();
        await future.StartAsync(null);
        Require(future.Header is { Status: ChatStatus.Failed, Error: { } reason } && reason.Contains("version 2", StringComparison.Ordinal), "A protocol mismatch fails the chat and says which version the agent speaks.");
    }

    // The agent's file and terminal calls through a session whose workspace is a temporary directory: reads and writes stay inside it.
    internal static async Task ClientMethods(string root)
    {
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "one\ntwo\nthree");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(workspace, "escape");
        if (!OperatingSystem.IsWindows() && !Directory.Exists(link)) Directory.CreateSymbolicLink(link, outside);

        var methods = new AcpClientMethods(workspace);
        await using var session = new AcpChatSession("ws", "chat1", workspace, () => Task.FromResult<IAcpTransport>(new FakeTransport(new AcpFakeAgent())), _ => { }, _ => { }, methods);
        session.Begin();
        await session.StartAsync(null);

        async Task<string> Ask(string prompt, string expected)
        {
            var before = Items(session).OfType<ChatAssistantItem>().Count();
            await session.SendAsync(prompt, null);
            await Until(() => session.Header.Status == ChatStatus.Idle && Items(session).OfType<ChatAssistantItem>().Count() > before, expected);
            return Items(session).OfType<ChatAssistantItem>().Last().Text;
        }

        Require(await Ask("read " + Path.Combine(workspace, "a.txt"), "The read answers.") == "content one\ntwo\nthree", "The agent reads a workspace file.");
        Require(await Ask("read a.txt", "The relative read answers.") == "content one\ntwo\nthree", "A relative path is taken against the workspace.");
        Require((await Ask("read " + Path.Combine(outside, "secret.txt"), "The outside read answers.")).StartsWith("error", StringComparison.Ordinal), "A file outside the workspace is refused.");
        Require((await Ask("read ../outside/secret.txt", "The dotdot read answers.")).Contains("outside the workspace", StringComparison.Ordinal), "A path that climbs out of the workspace is refused.");
        if (!OperatingSystem.IsWindows())
            Require((await Ask("read " + Path.Combine(link, "secret.txt"), "The link read answers.")).Contains("outside the workspace", StringComparison.Ordinal), "A symbolic link inside the workspace cannot lead out of it.");
        Require(await Ask("write " + Path.Combine(workspace, "new", "b.txt"), "The write answers.") == "written" && File.ReadAllText(Path.Combine(workspace, "new", "b.txt")) == "written", "The agent writes a file inside the workspace.");
        Require((await Ask("write " + Path.Combine(outside, "evil.txt"), "The outside write answers.")).StartsWith("error", StringComparison.Ordinal) && !File.Exists(Path.Combine(outside, "evil.txt")), "A write outside the workspace is refused and writes nothing.");
        if (!OperatingSystem.IsWindows())
            Require(await Ask("run", "The terminal answers.") == "exit 0 output hi", "A terminal runs the command and reports its exit and output.");

        var direct = new JsonObject { ["path"] = "a.txt", ["line"] = 2, ["limit"] = 1 };
        Require((await methods.HandleAsync("fs/read_text_file", direct, CancellationToken.None))!["content"]!.GetValue<string>() == "two", "line and limit select part of the file.");
        AcpException? refused = null;
        try { await methods.HandleAsync("terminal/create", new JsonObject { ["command"] = "echo", ["cwd"] = outside }, CancellationToken.None); } catch (AcpException error) { refused = error; }
        Require(refused is not null, "A terminal cannot start outside the workspace.");
        try { await methods.HandleAsync("session/unknown", new JsonObject(), CancellationToken.None); refused = null; } catch (AcpException error) { refused = error; }
        Require(refused is { Code: AcpException.MethodNotFound }, "An unknown client method is answered as not found.");
    }

    private static string FixtureText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp-registry.json"));

    // The registry: the captured real JSON (trimmed), its cache with ETag revalidation and offline fallback, and the install confirmation.
    internal static async Task Registry(string root)
    {
        const string mac = "darwin-aarch64";
        Directory.CreateDirectory(root);
        var fixture = AcpRegistry.Parse(FixtureText()) ?? throw new InvalidOperationException("acp: The captured registry does not parse.");
        var view = AcpRegistry.View(fixture, "network", null, null, mac);
        Require(view.SchemaVersion == "1.0.0" && view.Agents.Select(agent => agent.Id).SequenceEqual(["codex-acp", "fast-agent", "goose", "pi-acp"]), "The captured registry lists its agents by name.");
        var goose = view.Agents.Single(agent => agent.Id == "goose");
        Require(goose is { Kinds: ["binary"], Installable: true, Version: "1.57.0", License: "Apache-2.0" } && view.Agents.Single(agent => agent.Id == "pi-acp").Kinds.SequenceEqual(["npx"]) && view.Agents.Single(agent => agent.Id == "fast-agent").Kinds.SequenceEqual(["uvx"]), "Each agent shows its distribution kinds.");
        Require(view.Agents.Single(agent => agent.Id == "pi-acp").Icon!.EndsWith("pi-acp.svg", StringComparison.Ordinal), "The registry's icon url is kept.");
        Require(!AcpRegistry.View(fixture, "x", null, null, "plan9-riscv").Agents.Single(agent => agent.Id == "goose").Installable, "A binary-only agent is not installable on a platform it has no archive for.");

        var npx = AcpRegistry.Plan(fixture, "pi-acp", null, mac)!;
        Require(npx is { Kind: "npx", CommandLine: "npx -y pi-acp@0.0.34", Version: "0.0.34" }, "An npx plan shows the exact command that will run.");
        var uvx = AcpRegistry.Plan(fixture, "fast-agent", null, mac)!;
        Require(uvx.CommandLine == "uvx fast-agent-acp==0.10.1 -x", "A uvx plan shows its package pin and arguments.");
        var binary = AcpRegistry.Plan(fixture, "goose", null, mac)!;
        Require(binary is { Kind: "binary", Sha256.Length: 64, CommandLine: "./goose acp" } && binary.Source.EndsWith("goose-aarch64-apple-darwin.tar.bz2", StringComparison.Ordinal), "A binary plan shows the archive url, checksum and command.");
        Require(AcpRegistry.Plan(fixture, "goose", null, "plan9-riscv") is null && AcpRegistry.Plan(fixture, "nope", null, mac) is null, "No plan for an agent or platform the registry does not have.");

        // Cache: first run fetches, a fresh copy is not refetched, a refresh revalidates with the ETag, offline falls back to the copy.
        var directory = Path.Combine(root, "registry-cache");
        var calls = new List<(string? ETag, string? Modified)>();
        var online = true;
        var clock = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        AcpFetch fetch = (url, etag, modified, _) =>
        {
            calls.Add((etag, modified));
            if (!online) throw new HttpRequestException("no network");
            return Task.FromResult(etag == "\"v1\"" ? new AcpFetchResult(304, null, etag, modified) : new AcpFetchResult(200, FixtureText(), "\"v1\"", "Wed, 04 Nov 2026 12:58:12 GMT"));
        };
        var cache = new AcpRegistryCache(directory, fetch, () => clock);
        var (_, first) = await cache.LoadAsync(false, CancellationToken.None);
        Require(first is { Source: "network", Note: null } && calls.Count == 1 && calls[0] == (null, null), "The first run fetches the registry without conditions.");
        clock = clock.AddMinutes(1);
        var (_, again) = await cache.LoadAsync(false, CancellationToken.None);
        Require(again.Source == "cache" && calls.Count == 1, "A copy younger than the registry's cache lifetime is not fetched again.");
        clock = clock.AddMinutes(10);
        var (_, revalidated) = await cache.LoadAsync(false, CancellationToken.None);
        Require(revalidated.Source == "network" && calls.Count == 2 && calls[1] == ("\"v1\"", "Wed, 04 Nov 2026 12:58:12 GMT"), "A stale copy is revalidated with If-None-Match and If-Modified-Since, and a 304 keeps it.");
        online = false;
        clock = clock.AddHours(1);
        var (_, offline) = await cache.LoadAsync(false, CancellationToken.None);
        Require(offline is { Source: "cache", Note: { } note } && note.Contains("earlier", StringComparison.Ordinal) && offline.Agents.Count == 4, "Offline, the cached copy is shown with a note.");
        var (_, seeded) = await new AcpRegistryCache(Path.Combine(root, "registry-empty"), fetch, () => clock).LoadAsync(false, CancellationToken.None);
        Require(seeded is { Source: "seed", Note: { } } && seeded.Agents.Count == 2, "With no copy and no network a built-in minimal list stands in, and says so.");
        online = true;
        var (_, garbage) = await new AcpRegistryCache(Path.Combine(root, "registry-bad"), (_, _, _, _) => Task.FromResult(new AcpFetchResult(200, "<html>captive portal</html>", null, null)), () => clock).LoadAsync(false, CancellationToken.None);
        Require(garbage.Source == "seed", "An answer that is not a registry is never cached or shown.");

        // Install: nothing happens without the confirmation of exactly this version; a bad checksum discards the download.
        var downloads = new List<string>();
        var archive = Path.Combine(root, "pkg.tar.gz");
        var source = Path.Combine(root, "pkg");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "goose"), "#!/bin/sh\nexit 0\n");
        System.Formats.Tar.TarFile.CreateFromDirectory(source, Path.Combine(root, "pkg.tar"), false);
        await using (var raw = File.OpenRead(Path.Combine(root, "pkg.tar")))
        await using (var zipped = File.Create(archive))
        await using (var gzip = new System.IO.Compression.GZipStream(zipped, System.IO.Compression.CompressionLevel.Fastest)) await raw.CopyToAsync(gzip);
        var bytes = File.ReadAllBytes(archive);
        var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var local = (JsonObject)fixture.DeepClone();
        var target = (JsonObject)local["agents"]!.AsArray().OfType<JsonObject>().First(agent => agent["id"]!.GetValue<string>() == "goose")["distribution"]!["binary"]![mac]!;
        target["archive"] = "https://example.invalid/goose.tar.gz";
        target["sha256"] = good;
        var installer = new AcpInstaller(Path.Combine(root, "agents"), (url, _) => { downloads.Add(url); return Task.FromResult(bytes); });

        async Task<string> Try(AcpInstallRequest request)
        {
            try { await installer.InstallAsync(local, request, mac, CancellationToken.None); return ""; }
            catch (InvalidOperationException error) { return error.Message; }
        }

        Require((await Try(new("goose", "binary", "1.57.0", false))).Contains("confirmation", StringComparison.Ordinal) && downloads.Count == 0, "Without a confirmation nothing is downloaded.");
        Require((await Try(new("goose", "binary", "1.0.0", true))).Contains("review", StringComparison.Ordinal) && downloads.Count == 0, "A confirmation for another version than the registry offers installs nothing.");
        target["sha256"] = new string('0', 64);
        Require((await Try(new("goose", "binary", "1.57.0", true))).Contains("checksum", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(root, "agents", "goose", "1.57.0")), "A download that fails its checksum is discarded and nothing is unpacked.");
        target["sha256"] = good;
        if (!OperatingSystem.IsWindows())
        {
            var entry = await installer.InstallAsync(local, new("goose", "binary", "1.57.0", true), mac, CancellationToken.None);
            Require(entry is { Origin: "registry", Version: "1.57.0" } && File.Exists(entry.Command) && entry.Command.StartsWith(Path.Combine(root, "agents"), StringComparison.Ordinal) && entry.Args.SequenceEqual(["acp"]), "A confirmed, verified binary is unpacked under the agents directory and becomes an entry.");
        }
        var package = await installer.InstallAsync(local, new("pi-acp", "npx", "0.0.34", true), mac, CancellationToken.None);
        Require(package is { Command: "npx", Version: "0.0.34" } && package.Args.SequenceEqual(["-y", "pi-acp@0.0.34"]), "An npx install becomes an entry that runs the pinned package.");
    }

    // The settings page's plain-text forms for arguments and environment.
    internal static void EntryText()
    {
        Require(AcpEntryText.SplitArguments("--flag \"two words\" 'x y' plain").SequenceEqual(["--flag", "two words", "x y", "plain"]), "Quotes keep a space inside one argument.");
        var env = AcpEntryText.ParseEnvironment("A=1\n\nB=x=y\n=bad\n  C = 3 ");
        Require(env.Count == 3 && env["A"] == "1" && env["B"] == "x=y" && env["C"] == " 3", "Environment lines are NAME=value; the value keeps everything after the first equals sign.");
        Require(AcpEntryText.FormatEnvironment(env).Split('\n').Length == 3, "Environment round-trips to lines.");
        Require(AcpEntryText.CommandLine(new AcpAgentEntry("x", "X", "/a b/agent") { Args = ["--c", "d e"] }) == "\"/a b/agent\" --c \"d e\"", "The shown command line quotes parts with spaces.");
        Require(AcpEntryText.IdFor("My Agent!") == "custom-my-agent", "A custom agent gets a stable id from its name.");
    }

    internal static async Task Run(string root)
    {
        Manifest();
        EntryText();
        await Protocol();
        await Session();
        Directory.CreateDirectory(root);
        await ClientMethods(root);
        await Registry(Path.Combine(root, "registry"));
    }
}