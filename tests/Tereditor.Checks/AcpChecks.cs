using System.Text.Json.Nodes;

using Tereditor.Plugins.Acp;
using Tereditor.Plugins.Acp.Host;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Chat;

namespace Tereditor.Checks;

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
        Require(manifest.Support == PluginSupportLevels.Early && manifest.Label == "ACP" && manifest.Description!.StartsWith("Early support", StringComparison.Ordinal), "ACP is named ACP and carries the Early support level and description.");
        Require(manifest.UnavailableOn.SequenceEqual([HostKind.PureWeb]), "A host without processes does not offer ACP.");
        Require(ChatContract.Methods.All(method => AcpContract.Contract.Methods.Contains(method)), "ACP serves every chat method.");
    }

    // The logomark: the glyph and the plugin's asset are one drawing, a square monochrome document that parses and draws something.
    internal static void Logo()
    {
        var svg = System.Text.Encoding.UTF8.GetString(Tereditor.Plugins.Acp.UI.AcpGlyph.Svg);
        Require(svg.Contains("viewBox=\"0 -178 576 576\"", StringComparison.Ordinal) && svg.Contains("currentColor", StringComparison.Ordinal) && !svg.Contains("<script", StringComparison.OrdinalIgnoreCase) && !svg.Contains("href", StringComparison.OrdinalIgnoreCase), "The ACP logomark is a square, currentColor SVG with no script or reference.");
        using var stream = new MemoryStream(Tereditor.Plugins.Acp.UI.AcpGlyph.Svg);
        using var source = Avalonia.Svg.Skia.SvgSource.LoadFromStream(stream);
        Require(source.Picture is { CullRect: { Width: > 0, Height: > 0 } }, "The ACP logomark parses and draws a non-empty picture.");
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var asset = Path.Combine(directory.FullName, "src", "Tereditor.Plugins.Acp", "assets", "acp.svg");
            if (!File.Exists(asset)) continue;
            Require(File.ReadAllText(asset).Trim() == svg.Trim(), "The plugin's acp.svg is the same drawing as the built-in glyph.");
            break;
        }
    }

    internal static async Task Protocol()
    {
        // Framing and the session flow.
        var handler = new Recorder();
        await using var agent = new AcpFakeAgent();
        await using var client = new AcpClient(agent.ClientInput, agent.ClientOutput, handler);
        var info = await client.InitializeAsync("tereditor", "0.1", CancellationToken.None);
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
        try { await other.InitializeAsync("tereditor", "0.1", CancellationToken.None); } catch (AcpVersionException error) { mismatch = error; }
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
        Require(view.SchemaVersion == "1.0.0" && view.Agents.Select(agent => agent.Id).SequenceEqual(["codex-acp", "fast-agent", "goose", "claude-acp", "pi-acp"]), "The captured registry lists its agents by name.");
        var goose = view.Agents.Single(agent => agent.Id == "goose");
        Require(goose is { Kinds: ["binary"], Installable: true, Version: "1.57.0", License: "Apache-2.0" } && view.Agents.Single(agent => agent.Id == "pi-acp").Kinds.SequenceEqual(["npx"]) && view.Agents.Single(agent => agent.Id == "fast-agent").Kinds.SequenceEqual(["uvx"]), "Each agent shows its distribution kinds.");
        Require(view.Agents.Single(agent => agent.Id == "pi-acp").Icon!.EndsWith("pi-acp.svg", StringComparison.Ordinal), "The registry's icon url is kept.");
        Require(!AcpRegistry.View(fixture, "x", null, null, "plan9-riscv").Agents.Single(agent => agent.Id == "goose").Installable, "A binary-only agent is not installable on a platform it has no archive for.");

        Require(view.Agents.Where(agent => agent.NotRecommended is not null).Select(agent => agent.Id).SequenceEqual(["claude-acp", "pi-acp"]) && view.Agents.Single(agent => agent.Id == "goose").NotRecommended is null && view.Agents.Single(agent => agent.Id == "codex-acp").NotRecommended is null, "Only the Claude and Pi adapters are flagged, and they sort last.");
        Require(AcpRegistry.Plan(fixture, "claude-acp", null, mac)!.NotRecommended is { Length: > 0 } && AcpRegistry.Plan(fixture, "pi-acp", null, mac)!.NotRecommended!.StartsWith("Pi ", StringComparison.Ordinal) && AcpRegistry.Plan(fixture, "goose", null, mac)!.NotRecommended is null, "The install confirmation carries the not-recommended flag.");
        var custom = AcpRegistry.View(new JsonObject { ["agents"] = new JsonArray(new JsonObject { ["id"] = "my-claude", ["name"] = "Claude", ["version"] = "1", ["description"] = "mine", ["distribution"] = new JsonObject { ["npx"] = new JsonObject { ["package"] = "x" } } }) }, "x", null, null, mac);
        Require(custom.Agents.Single().NotRecommended is null, "Matching is by exact id, so another agent named Claude is not flagged.");
        var npx = AcpRegistry.Plan(fixture, "pi-acp", null, mac)!;
        Require(npx is { Kind: "npx", CommandLine: "bun x pi-acp@0.0.34", Version: "0.0.34" }, "An npx plan shows the exact command that will run.");
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
        Require(offline is { Source: "cache", Note: { } note } && note.Contains("earlier", StringComparison.Ordinal) && offline.Agents.Count == 5, "Offline, the cached copy is shown with a note.");
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

    // The settings lists mirror Code Intelligence: one row model per registry entry, a search over name, description and id, and an install status per row.
    internal static void Rows()
    {
        var view = AcpRegistry.View(AcpRegistry.Parse(FixtureText())!, "network", null, null, "darwin-aarch64");
        var none = new List<AcpAgentEntry>();
        Require(Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, none, null).Count == 5 && Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, none, "  ").Count == 5, "A blank search lists every registry agent.");
        Require(Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, none, "GOOSE").Select(row => row.Agent.Id).SequenceEqual(["goose"]) && Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, none, "nothing like it").Count == 0, "The search matches name, description and id without case.");
        var rows = Tereditor.Plugins.Acp.UI.AcpRows.Registry(view, [new AcpAgentEntry("goose", "goose", "x") { Origin = "registry", Version = "1.57.0" }, new AcpAgentEntry("pi-acp", "Pi", "npx") { Origin = "registry", Version = "0.0.1" }, new AcpAgentEntry("codex-acp", "mine", "x")], null);
        Require(rows.Single(row => row.Agent.Id == "goose") is { Status: "installed", CanInstall: false, ActionLabel: "Installed" }, "An installed agent of the same version says Installed and offers no button.");
        Require(rows.Single(row => row.Agent.Id == "pi-acp") is { Status: "update", CanInstall: true, ActionLabel: "Update ▷" }, "Another installed version offers an update.");
        Require(rows.Single(row => row.Agent.Id == "codex-acp") is { Status: "available", ActionLabel: "Install ▷" }, "A custom agent with the same id is not the registry install.");
        var plan9 = AcpRegistry.View(AcpRegistry.Parse(FixtureText())!, "network", null, null, "plan9-riscv");
        Require(Tereditor.Plugins.Acp.UI.AcpRows.Registry(plan9, none, "goose").Single() is { Status: "unavailable", CanInstall: false }, "An agent with no distribution for this machine cannot be installed.");
    }

    // Settings > ACP shows one list: installed agents pinned on top, then the registry agents that are not installed.
    internal static void CombinedList()
    {
        var view = AcpRegistry.View(AcpRegistry.Parse(FixtureText())!, "network", null, null, "darwin-aarch64");
        var entries = new List<AcpAgentEntry>
        {
            new("pi-acp", "Pi", "npx") { Origin = "registry", Version = "0.0.1" },
            new("goose", "goose", "/x/goose") { Origin = "registry", Version = "1.57.0" },
            new("custom-zed", "Zed", "/bin/zed")
        };
        var none = new Dictionary<string, string>();
        var all = Tereditor.Plugins.Acp.UI.AcpRows.Combined(entries, view, none, "", null);
        Require(all.Installed.Select(row => row.Entry.Id).SequenceEqual(["goose", "pi-acp", "custom-zed"]), "Installed agents come first, by name, registry installs and custom ones together.");
        Require(all.Available.Count == 3 && all.Available.All(row => row.Agent.Id is not ("goose" or "pi-acp")), "An installed registry agent is not repeated in the registry group.");
        Require(all.Installed.Select(row => row.Entry.Id).Concat(all.Available.Select(row => row.Agent.Id)).Distinct().Count() == all.Installed.Count + all.Available.Count, "No agent appears twice in the list.");
        Require(all.Installed.Single(row => row.Entry.Id == "custom-zed").Tags[0] == "Custom", "A custom agent is marked Custom.");
        var filtered = Tereditor.Plugins.Acp.UI.AcpRows.Combined(entries, view, none, "", "GOOSE");
        Require(filtered.Installed.Select(row => row.Entry.Id).SequenceEqual(["goose"]) && filtered.Available.Count == 0, "The search filters the installed group and the registry group, without bringing an installed agent back below.");
        var zed = Tereditor.Plugins.Acp.UI.AcpRows.Combined(entries, view, none, "", "zed");
        Require(zed.Installed.Select(row => row.Entry.Id).SequenceEqual(["custom-zed"]), "The search matches a custom agent by name.");
        var offline = Tereditor.Plugins.Acp.UI.AcpRows.Combined(entries, null, none, "", null);
        Require(offline.Installed.Count == 3 && offline.Available.Count == 0, "Without the registry list the installed agents still show.");
    }

    // Settings > ACP is a list of every agent (registry installs and custom ones together) with a pane for the selected one.
    internal static void AgentList()
    {
        var view = AcpRegistry.View(AcpRegistry.Parse(FixtureText())!, "network", null, null, "darwin-aarch64");
        var entries = new List<AcpAgentEntry>
        {
            new("pi-acp", "Pi", "npx") { Origin = "registry", Version = "0.0.1", Args = ["-y", "pi-acp@0.0.1"], Description = "pi" },
            new("goose", "goose", "/x/goose") { Origin = "registry", Version = "1.57.0", Source = "https://example.invalid/goose.tar.bz2", License = "Apache-2.0", Website = "https://goose.example/" },
            new("custom-zed", "Zed", "/bin/zed") { Enabled = false },
            new("codex-acp", "Codex", "/x/codex") { Origin = "registry", Version = "2.5.0" }
        };
        var problems = new Dictionary<string, string> { ["goose"] = "The program is missing: /x/goose" };
        var rows = Tereditor.Plugins.Acp.UI.AcpRows.Agents(entries, view, problems, "goose");
        Require(rows.Select(row => row.Entry.Id).SequenceEqual(new[] { "codex-acp", "goose", "pi-acp", "custom-zed" }), "Registry installs and custom agents are listed together by name.");
        Require(rows.Single(row => row.Entry.Id == "pi-acp").Tags.SequenceEqual(["Registry", "Not recommended", "Update available"]), "A registry install shows Registry, Not recommended and Update available.");
        Require(rows.Single(row => row.Entry.Id == "custom-zed").Tags.SequenceEqual(["Custom", "Off"]) && rows.Single(row => row.Entry.Id == "goose").Tags.SequenceEqual(["Registry", "Default"]) && rows.Single(row => row.Entry.Id == "codex-acp").Tags.SequenceEqual(["Registry"]), "Custom agents say Custom, a turned-off agent says Off, the default says Default, a current install has no extra tag.");
        Require(rows.Where(row => row.HasDot).Select(row => row.Entry.Id).SequenceEqual(["goose"]) && rows.Single(row => row.HasDot).Problem!.Contains("missing", StringComparison.Ordinal), "Only an agent with a problem gets a status dot, and the problem is its tooltip.");
        Require(Tereditor.Plugins.Acp.UI.AcpRows.Agents(entries, null, problems, "").Single(row => row.Entry.Id == "pi-acp").Tags.SequenceEqual(["Registry"]), "Without the registry list there is no Not recommended or Update available tag.");

        var detail = new Tereditor.Plugins.Acp.UI.AcpAgentDetail(entries[1], Tereditor.Plugins.Acp.UI.AcpRows.RegistryAgent(entries[1], view), problems["goose"]);
        Require(detail is { IsRegistry: true, License: "Apache-2.0", Website: "https://goose.example/", CanReinstall: true, CanUpdate: false, UpdateTo: null } && detail.Version == "Version 1.57.0" && detail.SourceLine.Contains("example.invalid", StringComparison.Ordinal) && detail.InstallStatus.Contains("missing", StringComparison.Ordinal) && detail.Location == "/x/goose", "The detail of a registry agent shows its version, source, links, license, status and where the program is.");
        var pi = new Tereditor.Plugins.Acp.UI.AcpAgentDetail(entries[0], Tereditor.Plugins.Acp.UI.AcpRows.RegistryAgent(entries[0], view), null);
        Require(pi is { CanUpdate: true, CanReinstall: false, UpdateTo: "0.0.34" } && pi.InstallStatus.Contains("0.0.34", StringComparison.Ordinal) && pi.Location.StartsWith("bun x ", StringComparison.Ordinal), "An older install offers an update, and an npx agent says what runs it.");
        var custom = new Tereditor.Plugins.Acp.UI.AcpAgentDetail(entries[2], null, null);
        Require(custom is { IsRegistry: false, CanUpdate: false, CanReinstall: false, InstallStatus: "Custom" } && custom.SourceLine == "Added by you" && custom.CommandLine == "/bin/zed", "A custom agent has no update or reinstall.");

        var overridden = entries[1] with { CommandOverride = "/other/goose", UseDefaultArgs = false, ArgsOverride = ["acp", "--fast"], Args = ["acp"] };
        Require(overridden.LaunchCommand() == "/other/goose" && overridden.LaunchArgs().SequenceEqual(["acp", "--fast"]) && entries[1].LaunchCommand() == "/x/goose" && entries[1].LaunchArgs().Count == 0, "An executable override and own arguments replace the registry's; without them the registry's are used.");
        Require((entries[2] with { UseDefaultArgs = false, ArgsOverride = ["x"], Args = ["own"] }).LaunchArgs().SequenceEqual(["own"]), "A custom agent's own arguments are always used.");
        Require(AcpEntryText.ParseLines("a\r\n\n b c \n").SequenceEqual(["a", " b c "]), "Arguments are one per line, blank lines dropped.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("custom-zed", [entries[2], entries[3]]) is { Kind: "start", Agent.Id: "codex-acp" } && Tereditor.Plugins.Acp.UI.AcpLauncher.MenuEntries("", entries).All(entry => entry.Agent.Enabled), "A turned-off agent is not started from the button, even as the default.");
    }

    // The Add custom agent popup validates its form before anything is saved.
    internal static void CustomForm()
    {
        var existing = new List<AcpAgentEntry> { new("custom-mine", "Mine", "/bin/mine") };
        var ok = Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate(" My Agent ", " /usr/bin/agent ", "--flag \"two words\"", "A=1\nB=x=y", "/work", existing);
        Require(ok.Entry is { Id: "custom-my-agent", Name: "My Agent", Command: "/usr/bin/agent", Cwd: "/work", Origin: "custom", Enabled: true } entry && entry.Args.SequenceEqual(["--flag", "two words"]) && entry.Env.Count == 2 && ok.Errors.Count == 0, "A valid form becomes a custom entry with the same fields as before.");
        var none = Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("", "  ", null, null, null, existing);
        Require(none.Entry is null && none.Errors.Count == 2, "A name and a command are needed.");
        Require(Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("mine", "x", "", "", "", existing).Errors.Single().Contains("already", StringComparison.Ordinal) && Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("MINE!", "x", "", "", "", existing).Errors.Count == 1, "A name already in the list is refused, so adding never replaces another agent.");
        var bad = Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("n", "x", "", "A=1\nnot a pair\n=x", "relative/dir", existing);
        Require(bad.Entry is null && bad.Errors.Count == 3 && bad.Errors[0].Contains("line 2", StringComparison.Ordinal) && bad.Errors[1].Contains("line 3", StringComparison.Ordinal), "Environment lines that are not NAME=value and a relative working directory are refused, each with its reason.");
        Require(Tereditor.Plugins.Acp.UI.AcpCustomForm.Validate("n", "x\ny", "", "", "", existing).Errors.Count == 1, "The command is one line.");
    }

    // What the registry ships to verify (checked against the live registry on 2026-11-06: an optional sha256 per binary target, no signature of any kind, every npx and uvx package pinned), and what is done with it.
    internal static async Task Verification(string root)
    {
        Directory.CreateDirectory(root);
        const string mac = "darwin-aarch64";
        Require(AcpRegistry.IsPinned("npx", "pi-acp@0.0.34") && AcpRegistry.IsPinned("npx", "@scope/name@1.2.3-beta.1") && AcpRegistry.IsPinned("uvx", "fast-agent-acp==0.10.1"), "Exact versions are pinned.");
        Require(!AcpRegistry.IsPinned("npx", "pi-acp") && !AcpRegistry.IsPinned("npx", "@scope/name") && !AcpRegistry.IsPinned("npx", "pi-acp@latest") && !AcpRegistry.IsPinned("npx", "pi-acp@^1.2.0") && !AcpRegistry.IsPinned("uvx", "x>=1") && !AcpRegistry.IsPinned("uvx", "x"), "A tag, a range or no version is not pinned.");
        Require(AcpRegistry.IsSha256(new string('a', 64)) && !AcpRegistry.IsSha256(new string('a', 63)) && !AcpRegistry.IsSha256(new string('g', 64)) && !AcpRegistry.IsSha256(null), "Only 64 hex digits are a SHA-256.");

        var fixture = AcpRegistry.Parse(FixtureText())!;
        Require(AcpRegistry.Plan(fixture, "goose", null, mac)!.Verification == AcpRegistry.Sha256Verified && AcpRegistry.Plan(fixture, "pi-acp", null, mac)!.Verification == AcpRegistry.Pinned && AcpRegistry.Plan(fixture, "fast-agent", null, mac)!.Verification == AcpRegistry.Pinned, "The plan says what will be verified: SHA-256, or a pinned version.");
        var runPlan = AcpRegistry.Plan(fixture, "pi-acp", null, mac)!;
        Require(runPlan.CommandLine.StartsWith("bun x pi-acp@0.0.34", StringComparison.Ordinal) && runPlan.Verification == AcpRegistry.Pinned && !runPlan.CommandLine.Contains("npx", StringComparison.Ordinal), "An npx agent's plan runs with Bun and keeps its pin verification.");
        var (runner, runnerArgs) = Tereditor.Plugins.Acp.Host.AcpRunner.Bun("/opt/bun", ["-y", "@scope/pi-acp@0.0.34", "--acp"]);
        Require(runner == "/opt/bun" && runnerArgs.SequenceEqual(["x", "@scope/pi-acp@0.0.34", "--acp"]) && Tereditor.Plugins.Acp.Host.AcpRunner.Bun("b", ["pi@1", "-y"]).Args.SequenceEqual(["x", "pi@1", "-y"]), "The npx arguments did not become the same package run by bun x.");
        var guardHome = Path.Combine(root, "bun-guard");
        var guarded = Tereditor.Plugins.Acp.Host.AcpRunner.GuardedEnvironment(guardHome, new Dictionary<string, string> { ["TOKEN"] = "t", ["DO_NOT_TRACK"] = "0" });
        Require(guarded["BUN_OPTIONS"].Contains("--no-env-file", StringComparison.Ordinal) && guarded["BUN_OPTIONS"].Contains(Path.Combine(guardHome, "empty-bunfig.toml"), StringComparison.Ordinal) && new FileInfo(Path.Combine(guardHome, "empty-bunfig.toml")).Length == 0 && guarded["BUN_INSTALL"] == guardHome && guarded["TOKEN"] == "t" && guarded["DO_NOT_TRACK"] == "0", "Bun was not kept from the workspace's bunfig and .env, or the agent's own environment was lost.");
        var loose = (JsonObject)fixture.DeepClone();
        loose["agents"]!.AsArray().OfType<JsonObject>().First(agent => agent["id"]!.GetValue<string>() == "pi-acp")["distribution"]!["npx"]!["package"] = "pi-acp";
        var goose = (JsonObject)loose["agents"]!.AsArray().OfType<JsonObject>().First(agent => agent["id"]!.GetValue<string>() == "goose")["distribution"]!["binary"]![mac]!;
        Require(AcpRegistry.Plan(loose, "pi-acp", null, mac)!.Verification == AcpRegistry.NotPinned, "A package without an exact version says Not pinned.");
        var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3])).ToLowerInvariant();
        goose["archive"] = "https://example.invalid/goose";
        goose["cmd"] = "goose";
        goose["sha256"] = new string('0', 64);
        var downloads = 0;
        var installer = new AcpInstaller(Path.Combine(root, "agents"), (_, _) => { downloads++; return Task.FromResult(new byte[] { 1, 2, 3 }); });
        async Task<string> Try()
        {
            try { await installer.InstallAsync(loose, new("goose", "binary", "1.57.0", true), mac, CancellationToken.None); return ""; }
            catch (InvalidOperationException error) { return error.Message; }
        }
        var mismatch = await Try();
        Require(mismatch.Contains("does not match", StringComparison.Ordinal) && mismatch.Contains("nothing was installed", StringComparison.Ordinal) && downloads == 1 && !Directory.Exists(Path.Combine(root, "agents", "goose")), "A checksum mismatch refuses the install after the download and before anything is written.");
        goose["sha256"] = "not-a-checksum";
        downloads = 0;
        Require((await Try()).Contains("not a valid SHA-256", StringComparison.Ordinal) && downloads == 0, "A checksum that is not a SHA-256 is refused before the download, not skipped.");
        goose["sha256"] = good.ToUpperInvariant();
        goose["archive"] = "http://example.invalid/goose";
        Require((await Try()).Contains("not https", StringComparison.Ordinal) && downloads == 0, "A download that is not https is refused.");
        goose["archive"] = "https://example.invalid/goose";
        var verified = await installer.InstallAsync(loose, new("goose", "binary", "1.57.0", true), mac, CancellationToken.None);
        Require(verified.Verification == AcpRegistry.Sha256Verified && verified.Source == "https://example.invalid/goose", "A matching checksum (in either letter case) installs and the entry records SHA-256 verified.");
        goose.Remove("sha256");
        var unchecked_ = await installer.InstallAsync(loose, new("goose", "binary", "1.57.0", true), mac, CancellationToken.None);
        Require(unchecked_.Verification == AcpRegistry.NoChecksum && AcpRegistry.Plan(loose, "goose", null, mac)!.Verification == AcpRegistry.NoChecksum, "With no checksum the install goes ahead as before and says so, in the plan and on the entry.");
        var pinned = await installer.InstallAsync(fixture, new("pi-acp", "npx", "0.0.34", true), mac, CancellationToken.None);
        Require(pinned.Verification == AcpRegistry.Pinned, "A pinned package records Version pinned.");

        var plan = AcpRegistry.Plan(fixture, "goose", null, mac)!;
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Explanation(plan).Contains(AcpInstallPlan.TrustNote, StringComparison.Ordinal) && AcpInstallPlan.TrustNote.Contains("only install ones you trust", StringComparison.Ordinal) && Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Facts(plan).Single(fact => fact.Label == "Checked").Value == AcpRegistry.Sha256Verified, "The install confirmation carries the trust notice and what is verified.");
        Require(AcpInstallPlan.TrustNote.Contains("third-party programs", StringComparison.OrdinalIgnoreCase) && !AcpInstallPlan.TrustNote.Contains("checksum", StringComparison.OrdinalIgnoreCase) && !AcpInstallPlan.TrustNote.Contains('—'), "The trust notice says registry agents are third-party programs to trust, without explaining checksums.");
        var detail = new Tereditor.Plugins.Acp.UI.AcpAgentDetail(verified, null, null);
        Require(detail.Verification == AcpRegistry.Sha256Verified && new Tereditor.Plugins.Acp.UI.AcpAgentDetail(new AcpAgentEntry("a", "A", "x") { Origin = "registry" }, null, null).Verification.StartsWith("Not recorded", StringComparison.Ordinal), "The detail pane shows what was verified, or that an older install has no record.");
    }

    private const string GoodIcon = """<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16"><defs><linearGradient id="g"><stop offset="0" stop-color="#fff"/></linearGradient></defs><path fill="currentColor" d="M0 0h16v16H0z"/><circle r="3" fill="url(#g)" stroke="#123456"/></svg>""";

    // Registry icons: untrusted remote SVGs. Only the registry's own https origin is fetched, small plain documents are kept, anything active or external is refused, and a copy on disk is shown offline.
    internal static async Task Icons(string root)
    {
        Directory.CreateDirectory(root);
        const string registry = AcpRegistry.Url;
        const string own = "https://cdn.agentclientprotocol.com/registry/v1/latest/goose.svg";
        Require(AcpIconRules.IsAllowedUrl(own, registry) && AcpIconRules.IsAllowedUrl("https://cdn.agentclientprotocol.com/other/x.svg", registry), "An icon on the registry's own https host is allowed.");
        foreach (var url in new[] { "http://cdn.agentclientprotocol.com/x.svg", "https://evil.example/x.svg", "https://cdn.agentclientprotocol.com.evil.example/x.svg", "https://user:pw@cdn.agentclientprotocol.com/x.svg", "https://cdn.agentclientprotocol.com:8443/x.svg", "//cdn.agentclientprotocol.com/x.svg", "data:image/svg+xml,<svg/>", "goose.svg", "file:///etc/passwd" })
            Require(!AcpIconRules.IsAllowedUrl(url, registry), "The icon url " + url + " is refused.");
        Require(AcpIconRules.IsSafeId("goose") && AcpIconRules.IsSafeId("pi-acp") && !AcpIconRules.IsSafeId("../x") && !AcpIconRules.IsSafeId("a/b") && !AcpIconRules.IsSafeId("") && !AcpIconRules.IsSafeId(".hidden"), "An agent id becomes a file name only when it is plain.");

        Require(AcpIconRules.Reject(GoodIcon) is null, "A plain SVG with currentColor, a fixed colour and a local gradient is accepted.");
        var unsafeIcons = new Dictionary<string, string>
        {
            ["script element"] = """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""",
            ["foreignObject"] = """<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div/></foreignObject></svg>""",
            ["image"] = """<svg xmlns="http://www.w3.org/2000/svg"><image href="https://evil.example/a.png"/></svg>""",
            ["href"] = """<svg xmlns="http://www.w3.org/2000/svg"><use href="https://evil.example/a.svg#x"/></svg>""",
            ["xlink href"] = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><use xlink:href="data:image/svg+xml;base64,AAAA"/></svg>""",
            ["style url"] = """<svg xmlns="http://www.w3.org/2000/svg"><style>path{fill:url(https://evil.example/a.svg#x)}</style><path/></svg>""",
            ["style import"] = """<svg xmlns="http://www.w3.org/2000/svg"><style>@import 'https://evil.example/a.css';</style></svg>""",
            ["attribute url"] = """<svg xmlns="http://www.w3.org/2000/svg"><path style="fill:url( http://evil.example/a)"/></svg>""",
            ["event handler"] = """<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"/>""",
            ["doctype"] = """<!DOCTYPE svg [<!ENTITY x "y">]><svg xmlns="http://www.w3.org/2000/svg"/>""",
            ["not svg"] = """<html><body>captive portal</body></html>""",
            ["not xml"] = "<svg",
            ["too large"] = """<svg xmlns="http://www.w3.org/2000/svg"><!--""" + new string('x', AcpIconRules.MaxBytes) + """--></svg>"""
        };
        foreach (var (name, svg) in unsafeIcons) Require(AcpIconRules.Reject(svg) is not null, "The icon is refused: " + name + ".");

        var directory = Path.Combine(root, "icons");
        var calls = new List<(string Url, string? ETag)>();
        var online = true;
        var body = GoodIcon;
        var clock = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        AcpFetch fetch = (url, etag, modified, _) =>
        {
            calls.Add((url, etag));
            if (!online) throw new HttpRequestException("no network");
            return Task.FromResult(etag == "\"i1\"" ? new AcpFetchResult(304, null, etag, modified) : new AcpFetchResult(200, body, "\"i1\"", null));
        };
        var cache = new AcpIconCache(directory, fetch, () => clock);
        var wanted = new[] { ("goose", own), ("evil", "https://evil.example/e.svg"), ("../x", own) };
        var first = await cache.LoadAsync(wanted, false, false, CancellationToken.None);
        Require(first.Keys.Single() == "goose" && first["goose"] == GoodIcon && calls.Count == 1, "Only the icon on the registry's host is fetched, under a plain id.");
        Require((await cache.LoadAsync(wanted, false, true, CancellationToken.None)).ContainsKey("goose") && calls.Count == 1, "A cache-only read never touches the network.");
        clock = clock.AddHours(1);
        await cache.LoadAsync(wanted, false, false, CancellationToken.None);
        Require(calls.Count == 1, "A fresh icon is not fetched again.");
        clock = clock.AddDays(1);
        await cache.LoadAsync(wanted, false, false, CancellationToken.None);
        Require(calls.Count == 2 && calls[1].ETag == "\"i1\"", "A stale icon is revalidated with its ETag.");
        online = false;
        clock = clock.AddDays(3);
        var offline = await new AcpIconCache(directory, fetch, () => clock).LoadAsync(wanted, true, false, CancellationToken.None);
        Require(offline.ContainsKey("goose"), "Offline, the icon fetched before is still shown.");
        Require((await new AcpIconCache(Path.Combine(root, "icons-empty"), fetch, () => clock).LoadAsync(wanted, false, false, CancellationToken.None)).Count == 0, "Offline with no copy there is no icon, and the generic glyph stands in.");
        online = true;
        body = """<svg xmlns="http://www.w3.org/2000/svg"><script/></svg>""";
        clock = clock.AddDays(3);
        Require((await new AcpIconCache(directory, (u, _, m, c) => fetch(u, null, m, c), () => clock).LoadAsync(wanted, true, false, CancellationToken.None)).Count == 0 && !File.Exists(Path.Combine(directory, "goose.svg")), "An icon that turns unsafe is dropped with its old copy.");
        body = new string('x', AcpIconRules.MaxBytes + 1);
        Require((await new AcpIconCache(Path.Combine(root, "icons-big"), fetch, () => clock).LoadAsync(wanted, false, false, CancellationToken.None)).Count == 0, "An oversized answer is not kept.");

        Require(!Tereditor.Plugins.Acp.UI.AcpGlyph.HasIcon(null) && !Tereditor.Plugins.Acp.UI.AcpGlyph.HasIcon(" ") && Tereditor.Plugins.Acp.UI.AcpGlyph.HasIcon(GoodIcon), "Without an icon the generic glyph is drawn: custom agents, rejected, missing or not yet fetched icons.");
    }

    // The install confirmation shows the same facts as before in the overlay dialog: name and version, source, command, checksum, license, repository, the plain explanation and the not-recommended line.
    internal static void Confirmation()
    {
        var fixture = AcpRegistry.Parse(FixtureText())!;
        var goose = AcpRegistry.Plan(fixture, "goose", null, "darwin-aarch64")!;
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Title(goose) == "Install goose 1.57.0?", "The dialog is titled with the agent and version.");
        var facts = Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Facts(goose);
        Require(facts.Select(fact => fact.Label).SequenceEqual(["Source", "Runs", "Checked", "SHA-256", "License", "Repository"]) && facts[1].Value == "./goose acp" && facts[2].Value == AcpRegistry.Sha256Verified, "A binary install lists source, command, checksum, license and repository.");
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Facts(AcpRegistry.Plan(fixture, "pi-acp", null, "darwin-aarch64")!).Select(fact => fact.Label).SequenceEqual(["Source", "Runs", "Checked", "License", "Repository"]), "Facts the registry does not give are left out.");
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Explanation(goose).Contains("SHA-256", StringComparison.Ordinal) && !Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Explanation(goose).Contains(AcpInstallPlan.NotRecommendedLine, StringComparison.Ordinal), "The explanation says what is verified, with no warning for a normal entry.");
        Require(Tereditor.Plugins.Acp.UI.AcpInstallConfirm.Explanation(AcpRegistry.Plan(fixture, "pi-acp", null, "darwin-aarch64")!).EndsWith(AcpInstallPlan.NotRecommendedLine, StringComparison.Ordinal), "A not-recommended agent adds the warning line.");
    }

    // The launcher: a left click starts the default agent, the right-click menu lists every agent with the default marked.
    internal static void Launcher()
    {
        var one = new AcpAgentEntry("a", "A", "a");
        var two = new AcpAgentEntry("b", "B", "b");
        var three = new AcpAgentEntry("c", "C", "c");
        var launcher = typeof(Tereditor.Plugins.Acp.UI.AcpLauncher);
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("b", [one, two, three]) is { Kind: "start", Agent.Id: "b" }, "A default that is configured starts at once, without the picker.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("", [one, two, three]) is { Kind: "ask", Agent: null }, "With no default and several agents the picker opens.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("", [one]) is { Kind: "start", Agent.Id: "a" }, "With no default the only agent starts.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("gone", [one, two]) is { Kind: "ask" } && Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("gone", [one]) is { Kind: "start", Agent.Id: "a" }, "A removed default falls back to today's behaviour.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.Resolve("a", []) is { Kind: "none" }, "With no agent at all there is nothing to start.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.DefaultIsMissing("gone", [one]) && !Tereditor.Plugins.Acp.UI.AcpLauncher.DefaultIsMissing("a", [one]) && !Tereditor.Plugins.Acp.UI.AcpLauncher.DefaultIsMissing("", [one]), "Only a set default that no agent has counts as removed.");
        var menu = Tereditor.Plugins.Acp.UI.AcpLauncher.MenuEntries("b", [one, two, three]);
        Require(menu.Select(entry => entry.Agent.Id).SequenceEqual(["a", "b", "c"]) && menu.Where(entry => entry.IsDefault).Select(entry => entry.Agent.Id).SequenceEqual(["b"]), "The right-click menu lists every agent in order with the default marked.");
        Require(Tereditor.Plugins.Acp.UI.AcpLauncher.MenuEntries("gone", [one, two]).All(entry => !entry.IsDefault) && new Tereditor.Plugins.Acp.AcpSettings().DefaultAgent == "", "A removed default marks nothing, and no default is the empty string.");
        Require(launcher.IsSealed || launcher.IsAbstract, "The launcher decisions are static.");
    }

    internal static async Task Run(string root)
    {
        Manifest();
        Rows();
        CombinedList();
        AgentList();
        CustomForm();
        Confirmation();
        Launcher();
        EntryText();
        Logo();
        await Protocol();
        await Session();
        Directory.CreateDirectory(root);
        await ClientMethods(root);
        await Registry(Path.Combine(root, "registry"));
        await Icons(Path.Combine(root, "icons"));
        await Verification(Path.Combine(root, "verify"));
    }
}