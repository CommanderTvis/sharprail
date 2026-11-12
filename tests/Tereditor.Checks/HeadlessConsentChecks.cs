using System.Text.Json.Nodes;

using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Checks;

/// <summary>
/// Consent to the terms of a language server in the headless host, with no window and no network: the refusal text, accept_terms with a wrong and the right
/// hash, the environment variable (plain and strict), the elicitation paths, restarts with and without a state volume, and that nothing is accepted by default.
/// The terms are the shipped ones (the IntelliJ server); an installation fixture stands in for the download, so nothing is fetched or started.
/// </summary>
internal static partial class HeadlessChecks
{
    private const string Answers = """{"region":"europe","dataSharing":"none"}""";

    private static LanguageServer[] Catalog() =>
        [.. LanguageServers.Wrap(HeadlessLanguages.Servers(HeadlessLanguages.Roster(BuiltinPlugins.Headless, ManagedArtifacts.Platform(), OptIn)))];

    private const string Fixture = "FIXTURE TERMS: the user may use the server under these words only.\n";

    /// <summary>A reader that stands in for the download: the terms text at once, with the hash the spec pins.</summary>
    private static Func<LanguageServer, bool, CancellationToken, Task<LanguageServerTerms>> Instant(string text = Fixture) =>
        (server, _, _) => Task.FromResult(new LanguageServerTerms("/fixture", server.Spec.Version, "artifact", text, server.Spec.TermsSha256));

    private static LanguageConsent NewConsent(string directory, Dictionary<string, string>? environment = null, Func<LanguageServer, bool, CancellationToken, Task<LanguageServerTerms>>? reader = null)
    {
        var catalog = Catalog();
        return new(directory, () => catalog, name => environment is not null && environment.TryGetValue(name, out var value) ? value : null, reader ?? Instant());
    }

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json)!;

    internal static async Task Consent(string fixture)
    {
        var root = Path.Combine(fixture, "headless-consent");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var server = Catalog().Single(item => item.Id == "intellij");
        var hash = server.Spec.TermsSha256;
        Require(hash.Length == 64 && server.Spec.TermsTitle.Length > 0 && server.Spec.TermsQuestions.Count == 2 && server.Spec.Version.Length > 0, "The IntelliJ spec does not pin its terms.");

        // Nothing is accepted by default: no grant, no files, and the shipped image sets no acceptance.
        var plain = Path.Combine(root, "plain");
        var none = NewConsent(plain);
        Require(none.GrantFor(server) is null && !Directory.Exists(plain), "A new host held a grant or wrote a record.");
        if (BoundaryChecks.Checkout() is { } checkout)
        {
            var dockerfile = File.ReadAllText(Path.Combine(checkout, "docker", "headless", "Dockerfile"));
            Require(!dockerfile.Contains("SHARPRAIL_ACCEPT_TERMS", StringComparison.Ordinal) && !dockerfile.Contains("INTELLIJ_REGION", StringComparison.Ordinal), "The image must not bake in any acceptance.");
        }

        // Before the download has finished: the summary, the hash and where things stand, and no way to accept yet.
        var early = none.Refusal(server);
        Require(early.Contains(hash, StringComparison.Ordinal) && early.Contains(server.Spec.Version, StringComparison.Ordinal) && early.Contains("full terms text appears here once the download has finished", StringComparison.Ordinal)
            && early.Contains("Nothing was started", StringComparison.Ordinal) && !early.Contains("call the tool accept_terms", StringComparison.Ordinal), "The refusal before the download is wrong:\n" + early);
        var tooEarly = await none.AcceptAsync(server, hash, null, new Dictionary<string, string> { ["region"] = "europe", ["dataSharing"] = "none" }, "tool", default);
        Require(!tooEarly.Accepted && tooEarly.Message.Contains("has not finished", StringComparison.Ordinal) && !Directory.Exists(plain), "Consent was accepted before the full terms could be shown.");
        // After it: the FULL terms text, the hash and version, the exact next step.
        await none.StartPrefetch(server);
        var refusal = none.Refusal(server);
        Require(refusal.Contains(hash, StringComparison.Ordinal) && refusal.Contains(server.Spec.Version, StringComparison.Ordinal) && refusal.Contains("accept_terms", StringComparison.Ordinal) && refusal.Contains(Fixture.Trim(), StringComparison.Ordinal)
            && refusal.Contains("Nothing was started", StringComparison.Ordinal) && refusal.Contains("region", StringComparison.Ordinal) && refusal.Contains("dataSharing", StringComparison.Ordinal)
            && refusal.Contains("SHARPRAIL_ACCEPT_TERMS=intellij", StringComparison.Ordinal) && refusal.Contains("only after the user clearly said yes", StringComparison.OrdinalIgnoreCase), "The refusal text is incomplete:\n" + refusal);
        await Prefetching(root, server);

        // Which calls need the terms: Java does, Kotlin keeps its own server until the terms are accepted, other languages never do.
        Require(none.NeededBy(Args("""{"path":"src/Main.java"}"""))?.Id == "intellij" && none.NeededBy(Args("""{"paths":["a.cs","src/**/*.java"]}"""))?.Id == "intellij"
            && none.NeededBy(Args("""{"path":"src/Main.kt"}""")) is null && none.NeededBy(Args("""{"path":"a.cs"}""")) is null && none.NeededBy(Args("""{"operation":"servers"}""")) is null
            && none.NeededBy(Args("""{"operation":"retry","server":"intellij"}"""))?.Id == "intellij", "The calls that need the terms are wrong.");
        Require(none.Provider() == "standard", "Before acceptance Java and Kotlin must stay with the servers without terms: " + none.Provider());

        // accept_terms: wrong hash, wrong version and missing or unknown answers are refused and record nothing; the current hash with every answer is recorded.
        var started = new List<string>();
        var tools = new LanguageConsentTools(none, null, (started1, _) => { started.Add(started1.Id); return Task.CompletedTask; });
        var tool = tools.AcceptTool();
        Require(tool.Description.Contains("ONLY after the user explicitly said yes", StringComparison.Ordinal), "The accept_terms description must say it needs the user's yes.");
        var wrongHash = await tool.Call(Args($$$"""{"server":"intellij","hash":"{{{new string('0', 64)}}}","answers":{{{Answers}}}}"""), default);
        var wrongVersion = await tool.Call(Args($$$"""{"server":"intellij","hash":"{{{hash}}}","version":"1.0","answers":{{{Answers}}}}"""), default);
        var noAnswers = await tool.Call(Args($$"""{"server":"intellij","hash":"{{hash}}"}"""), default);
        var badAnswer = await tool.Call(Args($$$"""{"server":"intellij","hash":"{{{hash}}}","answers":{"region":"mars","dataSharing":"none"}}"""), default);
        Require(wrongHash.Error && wrongVersion.Error && noAnswers.Error && badAnswer.Error && none.GrantFor(server) is null && !Directory.Exists(plain) && started.Count == 0,
            "A refused acceptance recorded something or started the server.");
        Require(wrongHash.Text.Contains("not the current terms hash", StringComparison.Ordinal) && noAnswers.Text.Contains("Never choose for the user", StringComparison.Ordinal), "The refusals do not explain themselves.");
        var review = await tool.Call(Args("""{"server":"intellij"}"""), default);
        Require(!review.Error && review.Text.StartsWith("Not accepted.", StringComparison.Ordinal) && review.Text.Contains(hash, StringComparison.Ordinal) && started.Count == 0, "Reading the terms must change nothing.");
        var accepted = await tool.Call(Args($$$"""{"server":"intellij","hash":"{{{hash.ToUpperInvariant()}}}","version":"{{{server.Spec.Version}}}","answers":{{{Answers}}}}"""), default);
        Require(!accepted.Error && accepted.Text.StartsWith("Recorded:", StringComparison.Ordinal) && none.GrantFor(server) is { Source: "recorded" } grant && grant.Answers["region"] == "europe" && grant.Answers["dataSharing"] == "none", "The right hash was not accepted: " + accepted.Text);
        await Until(() => started.Count == 1);
        Require(none.Provider() == "intellij" && none.NeededBy(Args("""{"path":"src/Main.java"}""")) is null, "Accepted terms must move Java and Kotlin to the server and lift the refusal.");
        Require(File.Exists(Path.Combine(plain, "consents", "intellij.json")), "The consent was not recorded in the state directory.");

        // A restart on the same state directory (a mounted volume) keeps it; a fresh one (no volume) starts with nothing.
        Require(NewConsent(plain).GrantFor(server) is not null, "A restart with the state volume lost the consent.");
        Require(NewConsent(Path.Combine(root, "other")).GrantFor(server) is null, "A restart without the state volume must hold no consent.");
        // A record for other terms or another version is not consent.
        var stale = Path.Combine(root, "stale");
        Directory.CreateDirectory(Path.Combine(stale, "consents"));
        await File.WriteAllTextAsync(Path.Combine(stale, "consents", "intellij.json"), $$"""{"Version":"1.0","TextSha256":"{{hash}}","Answers":{"region":"europe","dataSharing":"none"},"AcceptedAtUtc":"2026-01-01T00:00:00Z","Via":"tool"}""");
        Require(NewConsent(stale).GrantFor(server) is null, "A record for another version counted as consent.");

        // The environment variable: plain, strict, answers required; nothing is written, and removing it revokes.
        var answers = new Dictionary<string, string> { ["INTELLIJ_REGION"] = "europe", ["INTELLIJ_DATA_SHARING"] = "none" };
        Dictionary<string, string> With(string value) => new(answers) { ["SHARPRAIL_ACCEPT_TERMS"] = value };
        var environmentDirectory = Path.Combine(root, "environment");
        Require(NewConsent(environmentDirectory, With("intellij")).GrantFor(server) is { Source: "environment" } && NewConsent(environmentDirectory, With("other, intellij")).GrantFor(server) is not null
            && NewConsent(environmentDirectory, With("intellij:" + hash)).GrantFor(server) is not null, "The environment variable did not accept.");
        Require(NewConsent(environmentDirectory, With("intellij:" + new string('a', 64))).GrantFor(server) is null && NewConsent(environmentDirectory, With("intellij:" + new string('a', 64))).EnvironmentWarnings().Any(text => text.Contains("another terms hash", StringComparison.Ordinal)),
            "Strict mode accepted a hash that is not this build's, or said nothing.");
        Require(NewConsent(environmentDirectory, new() { ["SHARPRAIL_ACCEPT_TERMS"] = "intellij" }).GrantFor(server) is null
            && NewConsent(environmentDirectory, new() { ["SHARPRAIL_ACCEPT_TERMS"] = "intellij" }).EnvironmentWarnings().Any(text => text.Contains("INTELLIJ_REGION", StringComparison.Ordinal)), "The environment accepted without the user's answers.");
        Require(NewConsent(environmentDirectory, new(answers) { ["SHARPRAIL_ACCEPT_TERMS"] = "intellij", ["INTELLIJ_REGION"] = "mars" }).GrantFor(server) is null
            && NewConsent(environmentDirectory, With("nosuch")).EnvironmentWarnings().Any(text => text.Contains("nosuch", StringComparison.Ordinal)), "A bad answer or an unknown server was not refused.");
        Require(NewConsent(environmentDirectory, answers).GrantFor(server) is null && !Directory.Exists(environmentDirectory), "Without the variable there must be no consent, and the variable must write nothing.");

        // Not accepted, Kotlin files get a hint saying why IntelliJ is not in use; Java files get the terms. Accepted, nothing of the sort.
        var waiting = NewConsent(Path.Combine(root, "waiting"));
        Require(waiting.WaitingFor(Args("""{"path":"src/Main.kt"}""")) is { Id: "intellij" } && waiting.WaitingFor(Args("""{"path":"src/Main.java"}""")) is null && waiting.WaitingFor(Args("""{"path":"a.cs"}""")) is null, "The files served by the previous server are not recognised.");
        var hinted = await new LanguageConsentTools(waiting, null, null).Guard(new McpServer.McpTool("lsp_navigate", "t", "d", [], (_, _) => Task.FromResult(("""{"state":"ready"}""", false)))).Call(Args("""{"path":"src/Main.kt"}"""), default);
        Require(!hinted.Error && hinted.Text.Contains("not in use", StringComparison.Ordinal) && hinted.Text.Contains("accept_terms", StringComparison.Ordinal), "A Kotlin answer must say why IntelliJ is not in use: " + hinted.Text);
        // The unlicensed message says plainly that a window-less host needs a license; no trial is ever started or switchable there.
        var held = await new LanguageConsentTools(waiting, null, null).Guard(new McpServer.McpTool("lsp_navigate", "t", "d", [], (_, _) => Task.FromResult(("{\"state\":\"actionRequired\",\"detail\":\"No active license. " + LanguageConsentTools.DesktopLicenceSentence + "\"}", false)))).Call(Args("""{"path":"a.cs"}"""), default);
        Require(!held.Text.Contains("Open Settings", StringComparison.Ordinal) && held.Text.Contains("INTELLIJ_SERVER_LICENSE_FILE", StringComparison.Ordinal) && held.Text.Contains("JETBRAINS_LICENSE_SERVER", StringComparison.Ordinal) && held.Text.Contains("no trial", StringComparison.Ordinal), "The unlicensed message must name the license routes: " + held.Text);
        if (BoundaryChecks.Checkout() is { } source)
        {
            var everything = string.Join('\n', new[] { "src/SharpRail.Host.Headless", "src/SharpRail.Host.Core/Language", "src/SharpRail.Plugins.Lsp.IntelliJ", "docker", "scripts" }
                .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(source, folder), "*", SearchOption.AllDirectories).Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".sh", StringComparison.Ordinal) || Path.GetFileName(file) == "Dockerfile"))
                .Where(file => !file.Contains("/obj/", StringComparison.Ordinal) && !file.Contains("/bin/", StringComparison.Ordinal)).Select(File.ReadAllText));
            Require(!everything.Contains("AUTO_TRIAL", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.Matches(everything, "trial/startAnonymous").Count == 1,
                "Only the desktop's trial action may start a trial, and no variable may exist for it.");
            Require(!File.ReadAllText(Path.Combine(source, "src", "SharpRail.Host.Headless", "Program.cs")).Contains("\"trial\"", StringComparison.Ordinal), "The headless host must never invoke the trial action.");
        }
        await Supersession(root, server);
        await Prompts(root, server);
        await Elicitation(root, server);
        await Absent(root, server);
        await ChildProcesses(root, server);
        Console.WriteLine("PASS headless consent: refusal text with the hash, accept_terms refuses a wrong hash, version or answer and records the right one, SHARPRAIL_ACCEPT_TERMS plain and strict, elicitation accept, decline, unsupported and failed, state kept with a volume and not without, nothing accepted by default");
    }

    /// <summary>A chosen IntelliJ that fails to start or is unlicensed stops serving: the previous server answers, and the answer says why. No server is started.</summary>
    private static async Task Supersession(string root, LanguageServer server)
    {
        var directory = Path.Combine(root, "supersession");
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Main.java"), "class Main {}");
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE");
        Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        try { await SupersessionCore(directory, workspace, server); }
        finally { Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled); }
    }

    private static async Task SupersessionCore(string directory, string workspace, LanguageServer server)
    {
        var state = new HostStateStore(directory) { LanguageServerSource = () => [.. Catalog().Select(item => item.Spec)] };
        Directory.CreateDirectory(Path.Combine(directory, "language", "consents"));
        await File.WriteAllTextAsync(Path.Combine(directory, "language", "consents", "intellij.json"), $$$"""{"Version":"{{{server.Spec.Version}}}","TextSha256":"{{{server.Spec.TermsSha256}}}","Answers":{"region":"europe","dataSharing":"none"},"AcceptedAtUtc":"2026-01-01T00:00:00Z","Via":"tool"}""");
        var consent = state.EnableLanguageConsent(name => null, workspace);
        Require(consent.GrantFor(server) is not null && consent.Provider() == "intellij", "The grant did not choose IntelliJ.");
        state.Languages.MarkNotInUse(workspace, "intellij", "the license has expired.");
        var java = await state.Languages.ExecuteAsync(new(workspace, "Main.java", "definition"));
        Require(java.State == "unavailable" && java.Detail.Contains("not in use", StringComparison.Ordinal) && java.Detail.Contains("expired", StringComparison.Ordinal), "A Java call must say why IntelliJ is not in use: " + java.State + " " + java.Detail);
        var listed = await state.Languages.ExecuteAsync(new(workspace, Operation: "servers"));
        Require(listed.Servers.Single(item => item.Id == "intellij") is { State: "error" } row && row.Detail.Contains("replaces answer instead", StringComparison.Ordinal), "The server list does not show IntelliJ as not in use.");
        await state.StopLanguagesAsync();
    }

    /// <summary>The download as a state: while it runs the call answers "downloading" and nothing can be accepted; when it finishes the FULL terms are shown, paged when long; a text that is not the pinned one fails and nothing runs.</summary>
    private static async Task Prefetching(string root, LanguageServer server)
    {
        var hash = server.Spec.TermsSha256;
        var release = new TaskCompletionSource<LanguageServerTerms>();
        var reads = 0;
        var directory = Path.Combine(root, "prefetch");
        var consent = NewConsent(directory, null, (_, prepare, _) => { Interlocked.Increment(ref reads); Require(prepare, "The prefetch must be the only one that downloads."); return release.Task; });
        var started = new List<string>();
        var tools = new LanguageConsentTools(consent, null, (item, _) => { started.Add(item.Id); return Task.CompletedTask; });
        var inner = 0;
        var language = new McpServer.McpTool("lsp_diagnostics", "t", "d", [], (_, _) => { inner++; return Task.FromResult(("""{"state":"ready"}""", false)); });
        var previous = LanguageConsentTools.PrefetchWait;
        LanguageConsentTools.PrefetchWait = TimeSpan.FromMilliseconds(100);
        try
        {
            // A Java call while the download runs: one download, a "call again" text, nothing accepted, nothing run.
            var first = await tools.Guard(language).Call(Args("""{"paths":["Main.java"]}"""), default);
            var second = await tools.Guard(language).Call(Args("""{"paths":["Main.java"]}"""), default);
            Require(first.Error && second.Error && reads == 1 && inner == 0 && first.Text.Contains("being downloaded", StringComparison.Ordinal) && first.Text.Contains("call again in a few minutes", StringComparison.Ordinal)
                && !first.Text.Contains("call the tool accept_terms", StringComparison.Ordinal), "A call during the download must say it is downloading, once: " + first.Text);
            var reading = await tools.AcceptTool().Call(Args("""{"server":"intellij"}"""), default);
            var accepting = await tools.AcceptTool().Call(Args($$$"""{"server":"intellij","hash":"{{{hash}}}","answers":{"region":"europe","dataSharing":"none"}}"""), default);
            Require(!reading.Error && reading.Text.Contains("being downloaded", StringComparison.Ordinal) && accepting.Error && accepting.Text.Contains("has not finished", StringComparison.Ordinal) && started.Count == 0
                && consent.GrantFor(server) is null && !Directory.Exists(directory), "Nothing may be accepted while the download runs.");

            // Finished: the full text, in pages when it is long.
            var longText = string.Concat(Enumerable.Range(0, 3000).Select(index => $"Clause {index:0000} of the long agreement.\n"));
            release.SetResult(new LanguageServerTerms("/fixture", server.Spec.Version, "artifact", longText, hash));
            await consent.StartPrefetch(server);
            var shown = await tools.Guard(language).Call(Args("""{"paths":["Main.java"]}"""), default);
            Require(shown.Error && inner == 0 && shown.Text.Contains("Clause 0000", StringComparison.Ordinal) && shown.Text.Contains("[page 1 of", StringComparison.Ordinal) && shown.Text.Contains(hash, StringComparison.Ordinal)
                && shown.Text.Contains("call the tool accept_terms", StringComparison.Ordinal), "A finished download must show the terms and the next step: " + shown.Text[..Math.Min(400, shown.Text.Length)]);
            var pages = longText.Length / 12_000 + 1;
            var last = await tools.AcceptTool().Call(Args($$$"""{"server":"intellij","page":{{{pages}}}}"""), default);
            Require(last.Text.Contains("Clause 2999", StringComparison.Ordinal) && last.Text.Contains($"[page {pages} of {pages}]", StringComparison.Ordinal), "The last page of the terms is missing.");
            Require(reads == 1 && started.Count == 0 && inner == 0, "Reading the terms must not download again or run anything.");
        }
        finally { LanguageConsentTools.PrefetchWait = previous; }

        // A short text is shown whole; a download whose terms are not the pinned text fails, shows no text, and cannot be accepted: nothing runs.
        var wrong = NewConsent(Path.Combine(root, "prefetch-wrong"), null, (item, _, _) => Task.FromResult(new LanguageServerTerms("/fixture", item.Spec.Version, "artifact", "OTHER TERMS", new string('e', 64))));
        await wrong.StartPrefetch(server);
        var failed = wrong.Refusal(server);
        var refused = await wrong.AcceptAsync(server, hash, null, new Dictionary<string, string> { ["region"] = "europe", ["dataSharing"] = "none" }, "tool", default);
        Require(wrong.Prefetched(server).State == LanguageConsent.PrefetchState.Failed && failed.Contains("not the pinned text", StringComparison.Ordinal) && !failed.Contains("OTHER TERMS", StringComparison.Ordinal) && !refused.Accepted && wrong.GrantFor(server) is null,
            "A terms file that is not the pinned text must fail the download: " + failed);
        var broken = NewConsent(Path.Combine(root, "prefetch-broken"), null, (_, _, _) => throw new IOException("SHA-256 mismatch."));
        await broken.StartPrefetch(server);
        Require(broken.Refusal(server).Contains("The download failed: SHA-256 mismatch.", StringComparison.Ordinal), "A failed download must say why.");

        // The variable names what to download; a name without terms is reported.
        var (named, warnings) = NewConsent(Path.Combine(root, "prefetch-env"), new() { ["SHARPRAIL_PREFETCH"] = "intellij, csharp" }).PrefetchRequests();
        Require(named.Select(item => item.Id).SequenceEqual(["intellij"]) && warnings.Count == 1 && warnings[0].Contains("csharp", StringComparison.Ordinal), "SHARPRAIL_PREFETCH was not read.");
        Require(NewConsent(Path.Combine(root, "prefetch-none")).PrefetchRequests().Servers.Count == 0, "Nothing may be downloaded unless asked.");
    }

    /// <summary>The real stdio server and channel in one process: the prompt carries the full text and Accept records consent, Decline and a client without elicitation get the text.</summary>
    private static async Task Elicitation(string root, LanguageServer server)
    {
        foreach (var (name, capabilities, reply) in new[]
        {
            ("accept", """{"elicitation":{}}""", """{"action":"accept","content":{"region":"europe","dataSharing":"none"}}"""),
            ("decline", """{"elicitation":{}}""", """{"action":"decline"}"""),
            ("unsupported", "{}", "")
        })
        {
            var directory = Path.Combine(root, "stdio-" + name);
            var consent = NewConsent(directory);
            await consent.StartPrefetch(server);
            var client = new SharpRail.Host.Headless.ClientChannel();
            var tools = new LanguageConsentTools(consent, client, null);
            var inner = 0;
            IReadOnlyList<McpServer.McpTool> offered = [tools.Guard(new McpServer.McpTool("lsp_diagnostics", "t", "d", [], (_, _) => { inner++; return Task.FromResult(("ran", false)); })), tools.AcceptTool()];
            using var toServer = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
            using var serverInput = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, toServer.ClientSafePipeHandle);
            using var fromServer = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
            using var clientInput = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, fromServer.ClientSafePipeHandle);
            var stdio = new SharpRail.Host.Headless.StdioMcp(serverInput, fromServer, () => offered, "x", TextWriter.Null, client);
            using var stop = new CancellationTokenSource();
            var running = Task.Run(() => stdio.RunAsync(stop));
            using var writer = new StreamWriter(toServer, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(clientInput, new System.Text.UTF8Encoding(false));
            async Task<JsonNode> Next() => JsonNode.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)))!)!;
            await writer.WriteLineAsync($$$$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{{{{capabilities}}}},"clientInfo":{"name":"check","version":"1"}}}""");
            await Next();
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"lsp_diagnostics","arguments":{"paths":["Main.java"]}}}""");
            var message = await Next();
            if (reply.Length > 0)
            {
                Require(message["method"]?.GetValue<string>() == "elicitation/create" && message["params"]!["message"]!.GetValue<string>().Contains(Fixture.Trim(), StringComparison.Ordinal)
                    && message["params"]!["message"]!.GetValue<string>().Contains(server.Spec.TermsSha256, StringComparison.Ordinal) && message["params"]!["requestedSchema"]!["properties"]!["region"] is not null, "The prompt lacks the full terms.");
                await writer.WriteLineAsync($$$"""{"jsonrpc":"2.0","id":{{{message["id"]!.ToJsonString()}}},"result":{{{reply}}}}""");
                message = await Next();
            }
            var text = TextOf(message);
            var accepted = name == "accept";
            Require(message["id"]!.GetValue<int>() == 2 && accepted == (inner == 1) && accepted == (consent.GrantFor(server) is not null) && accepted == !text.Contains("needs the user's agreement", StringComparison.Ordinal)
                && accepted == File.Exists(Path.Combine(directory, "consents", "intellij.json")), $"The {name} path through the real stdio server is wrong: {text}");
            await stop.CancelAsync();
            writer.Close();
            // The abandoned read of the closed input may fault; the server itself has to stop.
            try { await running.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(25);
        Require(condition(), "A condition did not become true in time.");
    }

    private sealed class FakePrompt(Func<JsonObject, Task<JsonNode?>>? reply) : IConsentPrompt
    {
        internal List<JsonObject> Asked { get; } = [];
        public bool Supported => reply is not null;
        public async Task<JsonNode?> AskAsync(JsonObject request, CancellationToken token)
        {
            Asked.Add(request);
            return await reply!(request);
        }
    }

    /// <summary>Elicitation: only an explicit Accept with every answer records consent; Decline, a failure, a missing answer and an unsupported client fall back to the text.</summary>
    private static async Task Prompts(string root, LanguageServer server)
    {
        var inner = 0;
        var language = new McpServer.McpTool("lsp_navigate", "t", "d", [], (_, _) => { inner++; return Task.FromResult(("""{"state":"ready"}""", false)); });
        var call = Args("""{"path":"src/Main.java"}""");

        var accept = new FakePrompt(_ => Task.FromResult<JsonNode?>(Args("""{"action":"accept","content":{"region":"apac","dataSharing":"anonymous"}}""")));
        var acceptDirectory = Path.Combine(root, "prompt-accept");
        var acceptConsent = NewConsent(acceptDirectory);
        await acceptConsent.StartPrefetch(server);
        var guarded = new LanguageConsentTools(acceptConsent, accept, null).Guard(language);
        var answered = await guarded.Call(call, default);
        Require(!answered.Error && inner == 1 && accept.Asked.Count == 1 && acceptConsent.GrantFor(server) is { Source: "recorded" } grant && grant.Answers["region"] == "apac", "An Accept did not record consent and let the call run.");
        var message = accept.Asked[0]["message"]!.GetValue<string>();
        var properties = accept.Asked[0]["requestedSchema"]!["properties"]!.AsObject();
        Require(message.Contains(server.Spec.TermsSha256, StringComparison.Ordinal) && properties.ContainsKey("region") && properties.ContainsKey("dataSharing") && properties["region"]!["enum"]!.AsArray().Count == 7, "The prompt lacks the terms or the questions.");
        await guarded.Call(call, default);
        Require(accept.Asked.Count == 1 && inner == 2, "A granted server must not be asked again.");

        var decline = new FakePrompt(_ => Task.FromResult<JsonNode?>(Args("""{"action":"decline"}""")));
        var declineDirectory = Path.Combine(root, "prompt-decline");
        var declineConsent = NewConsent(declineDirectory);
        await declineConsent.StartPrefetch(server);
        inner = 0;
        var declined = await new LanguageConsentTools(declineConsent, decline, null).Guard(language).Call(call, default);
        Require(declined.Error && inner == 0 && declined.Text.Contains(server.Spec.TermsSha256, StringComparison.Ordinal) && declined.Text.Contains("declined or did not answer", StringComparison.Ordinal) && declineConsent.GrantFor(server) is null && !Directory.Exists(declineDirectory),
            "A Decline must give the text and record nothing.");
        var again = new LanguageConsentTools(declineConsent, decline, null);
        await again.Guard(language).Call(call, default);
        await again.Guard(language).Call(call, default);
        Require(decline.Asked.Count == 2, "A declined server must be asked once per process, not on every call: " + decline.Asked.Count);

        foreach (var (name, reply) in new (string, Func<JsonObject, Task<JsonNode?>>)[]
        {
            ("cancel", _ => Task.FromResult<JsonNode?>(Args("""{"action":"cancel"}"""))),
            ("missing answer", _ => Task.FromResult<JsonNode?>(Args("""{"action":"accept","content":{"region":"apac"}}"""))),
            ("unknown answer", _ => Task.FromResult<JsonNode?>(Args("""{"action":"accept","content":{"region":"apac","dataSharing":"all"}}"""))),
            ("failure", _ => throw new IOException("The client refused the request."))
        })
        {
            var directory = Path.Combine(root, "prompt-" + name.Replace(' ', '-'));
            var consent = NewConsent(directory);
            await consent.StartPrefetch(server);
            inner = 0;
            var result = await new LanguageConsentTools(consent, new FakePrompt(reply), null).Guard(language).Call(call, default);
            Require(result.Error && inner == 0 && result.Text.Contains("accept_terms", StringComparison.Ordinal) && consent.GrantFor(server) is null && !Directory.Exists(directory), $"The {name} reply must fall back to the text and record nothing.");
        }

        var unsupported = new FakePrompt(null);
        var unsupportedConsent = NewConsent(Path.Combine(root, "prompt-unsupported"));
        await unsupportedConsent.StartPrefetch(server);
        inner = 0;
        var none = await new LanguageConsentTools(unsupportedConsent, unsupported, null).Guard(language).Call(call, default);
        Require(none.Error && inner == 0 && unsupported.Asked.Count == 0 && !none.Text.Contains("declined or did not answer", StringComparison.Ordinal) && unsupportedConsent.GrantFor(server) is null, "A client without elicitation must get the text and no prompt.");
        // A call that does not need the terms never asks.
        var other = await new LanguageConsentTools(unsupportedConsent, accept, null).Guard(language).Call(Args("""{"path":"a.cs"}"""), default);
        Require(!other.Error && accept.Asked.Count == 1, "A call for another language asked for terms.");
    }

    /// <summary>An installation that stands in for the download: the pinned archive hash in its marker, and a terms file that is not the pinned one, so a start can never succeed.</summary>
    private static void SeedInstallation(string state, LanguageServer server)
    {
        var platform = ManagedArtifacts.Platform();
        var artifact = server.Spec.Artifacts.Single(item => item.Platform == platform);
        var directory = Path.Combine(state, "language", "servers", "intellij", server.Spec.Version + "-" + platform);
        Directory.CreateDirectory(Path.Combine(directory, "bin"));
        var launcher = Path.Combine(directory, "bin", OperatingSystem.IsWindows() ? "intellij-server.exe" : "intellij-server");
        File.WriteAllText(launcher, "#!/bin/sh\necho ran > \"$(dirname \"$0\")/ran\"\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var eula = "Fixture terms, not the pinned ones.";
        File.WriteAllText(Path.Combine(directory, "EULA.txt"), eula);
        File.WriteAllText(Path.Combine(directory, "product-info.json"), $$"""{"name":"intellij-server","buildNumber":"{{server.Spec.Version}}","productCode":"ILS"}""");
        var eulaHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(eula)));
        File.WriteAllText(Path.Combine(directory, ".sharprail-artifact.json"), $$"""{"ArchiveSha256":"{{artifact.Sha256}}","EulaSha256":"{{eulaHash}}"}""");
    }

    /// <summary>A host that asked for IntelliJ (SHARPRAIL_PREFETCH), over a state directory that holds the installation fixture, so no download is attempted.</summary>
    private static Host ChildHost(string[] arguments, string state, Dictionary<string, string>? environment = null) =>
        Host.Start(arguments, state, new(environment ?? []) { ["SHARPRAIL_PREFETCH"] = "intellij" });

    /// <summary>Without the flag IntelliJ does not exist: not a language, not a tool, not a word in the texts, no state, and Java and Kotlin route as if the plugin were absent.</summary>
    private static async Task Absent(string root, LanguageServer server)
    {
        var state = Path.Combine(root, "absent-state");
        var workspace = Path.Combine(root, "absent-workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Main.java"), "class Main {}");
        await using (var listing = Host.Start(["languages"], state))
        {
            Require(await listing.ExitAsync(TimeSpan.FromSeconds(30)) == 0 && listing.Lines.All(line => !line.Contains("intellij", StringComparison.OrdinalIgnoreCase) && line.Split('\t')[3].Length == 0) && listing.Lines.Any(line => line.StartsWith("kotlin\t", StringComparison.Ordinal)),
                "Without the flag the languages command must not know IntelliJ: " + string.Join('|', listing.Lines));
        }
        await using var host = Host.Start(["mcp", "--workspace", workspace], state);
        Initialize(host, "{}");
        var initialize = (await host.ReplyAsync([1], TimeSpan.FromSeconds(60)))[1]["result"]!["instructions"]!.GetValue<string>();
        host.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = (await host.ReplyAsync([2], TimeSpan.FromSeconds(60)))[2]["result"]!["tools"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()).ToArray();
        var status = await CallAsync(host, 3, "lsp_status", """{"operation":"servers"}""");
        var java = await CallAsync(host, 4, "lsp_diagnostics", """{"paths":["Main.java"]}""");
        var kotlin = await CallAsync(host, 5, "lsp_status", """{"operation":"servers"}""");
        Require(!initialize.Contains("IntelliJ", StringComparison.OrdinalIgnoreCase) && !initialize.Contains("accept_terms", StringComparison.Ordinal) && tools.Length == 14 && !tools.Contains("accept_terms"), "Without the flag the initialize text or the tool list knows IntelliJ.");
        Require(!TextOf(status).Contains("intellij", StringComparison.OrdinalIgnoreCase) && TextOf(status).Contains("kotlin", StringComparison.Ordinal), "Without the flag the server list shows IntelliJ or lost Kotlin: " + TextOf(status));
        Require(!TextOf(java).Contains("terms", StringComparison.OrdinalIgnoreCase) && !TextOf(java).Contains("intellij", StringComparison.OrdinalIgnoreCase), "Without the flag a Java file must get the ordinary answer: " + TextOf(java));
        Require(!Directory.Exists(Path.Combine(state, "language", "consents")) && !Directory.Exists(Path.Combine(state, "language", "agreements")) && !Directory.Exists(Path.Combine(state, "language", "servers", "intellij")), "Without the flag nothing about IntelliJ may be written.");
        host.CloseInput();
        await host.ExitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task<JsonNode> CallAsync(Host host, int id, string tool, string arguments)
    {
        host.Send($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""");
        return (await host.ReplyAsync([id], TimeSpan.FromSeconds(90)))[id];
    }

    private static string TextOf(JsonNode reply) => reply["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static void Initialize(Host host, string capabilities, string version = "2025-06-18") =>
        host.Send($$$$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"{{{{version}}}}","capabilities":{{{{capabilities}}}},"clientInfo":{"name":"check","version":"1"}}}""");

    /// <summary>The real host as a child process with the tools over stdio: default refusal, accept_terms, restarts, the environment variable and a real elicitation round trip.</summary>
    private static async Task ChildProcesses(string root, LanguageServer server)
    {
        var hash = server.Spec.TermsSha256;
        var workspace = Path.Combine(root, "child-workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Main.java"), "class Main {}");
        var state = Path.Combine(root, "child-state");
        SeedInstallation(state, server);
        var consents = Path.Combine(state, "language", "consents");
        var agreements = Path.Combine(state, "language", "agreements");

        await using (var host = ChildHost(["mcp", "--workspace", workspace], state))
        {
            Initialize(host, "{}");
            var initialize = (await host.ReplyAsync([1], TimeSpan.FromSeconds(60)))[1];
            Require(initialize["result"]!["instructions"]!.GetValue<string>().Contains("accept_terms", StringComparison.Ordinal), "The initialize text does not mention the terms.");
            host.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            var listed = (await host.ReplyAsync([2], TimeSpan.FromSeconds(60)))[2]["result"]!["tools"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()).ToArray();
            Require(listed.Contains("accept_terms"), "accept_terms is not offered: " + string.Join(", ", listed));
            var refused = await CallAsync(host, 3, "lsp_diagnostics", """{"paths":["Main.java"]}""");
            Require(refused["result"]!["isError"]?.GetValue<bool>() == true && TextOf(refused).Contains(hash, StringComparison.Ordinal) && TextOf(refused).Contains("not the pinned text", StringComparison.Ordinal), "A Java call must answer with the terms state: " + refused.ToJsonString());
            Require(!Directory.Exists(consents) && !Directory.Exists(agreements), "A default run must record no acceptance.");
            // The installed terms file is not the pinned text, so no hash can be accepted: nothing is recorded and nothing runs.
            var wrong = await CallAsync(host, 4, "accept_terms", $$$"""{"server":"intellij","hash":"{{{new string('1', 64)}}}","answers":{{{Answers}}}}""");
            var right = await CallAsync(host, 5, "accept_terms", $$$"""{"server":"intellij","hash":"{{{hash}}}","answers":{{{Answers}}}}""");
            Require(wrong["result"]!["isError"]?.GetValue<bool>() == true && right["result"]!["isError"]?.GetValue<bool>() == true && TextOf(right).Contains("has not finished", StringComparison.Ordinal) && !Directory.Exists(consents) && !Directory.Exists(agreements),
                "A terms file that is not the pinned text was accepted: " + TextOf(right));
            host.CloseInput();
            Require(await host.ExitAsync(TimeSpan.FromSeconds(30)) == 0, "The host did not exit 0.");
        }

        // An earlier yes (recorded by a host whose download matched) stays with the state volume.
        Directory.CreateDirectory(consents);
        await File.WriteAllTextAsync(Path.Combine(consents, "intellij.json"), $$$"""{"Version":"{{{server.Spec.Version}}}","TextSha256":"{{{hash}}}","Answers":{"region":"europe","dataSharing":"none"},"AcceptedAtUtc":"2026-01-01T00:00:00Z","Via":"tool"}""");
        // Restart with the state volume: the consent is still there.
        await using (var host = ChildHost(["mcp", "--workspace", workspace], state))
        {
            Initialize(host, "{}");
            await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
            var shown = await CallAsync(host, 2, "accept_terms", """{"server":"intellij"}""");
            Require(TextOf(shown).StartsWith("Accepted (recorded)", StringComparison.Ordinal), "A restart with the state lost the consent: " + TextOf(shown));
            host.CloseInput();
            await host.ExitAsync(TimeSpan.FromSeconds(30));
        }

        // Restart without the volume: nothing is accepted.
        var fresh = Path.Combine(root, "child-fresh");
        SeedInstallation(fresh, server);
        await using (var host = ChildHost(["mcp", "--workspace", workspace], fresh))
        {
            Initialize(host, "{}");
            await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
            var shown = await CallAsync(host, 2, "accept_terms", """{"server":"intellij"}""");
            var refused = await CallAsync(host, 3, "lsp_diagnostics", """{"paths":["Main.java"]}""");
            Require(TextOf(shown).StartsWith("Not accepted.", StringComparison.Ordinal) && TextOf(refused).Contains(hash, StringComparison.Ordinal), "A restart without the state volume must start with no consent.");
            host.CloseInput();
            await host.ExitAsync(TimeSpan.FromSeconds(30));
        }

        // The environment variable, plain and strict; it writes nothing.
        var viaEnvironment = Path.Combine(root, "child-environment");
        SeedInstallation(viaEnvironment, server);
        var variables = new Dictionary<string, string> { ["SHARPRAIL_ACCEPT_TERMS"] = "intellij", ["INTELLIJ_REGION"] = "europe", ["INTELLIJ_DATA_SHARING"] = "none" };
        await using (var host = Host.Start(["mcp", "--workspace", workspace], viaEnvironment, variables))
        {
            Initialize(host, "{}");
            await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
            var shown = await CallAsync(host, 2, "accept_terms", """{"server":"intellij"}""");
            Require(TextOf(shown).StartsWith("Accepted (environment)", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(viaEnvironment, "language", "consents")), "The environment variable did not accept, or wrote a record: " + TextOf(shown));
            host.CloseInput();
            await host.ExitAsync(TimeSpan.FromSeconds(30));
        }
        var strict = new Dictionary<string, string>(variables) { ["SHARPRAIL_ACCEPT_TERMS"] = "intellij:" + new string('b', 64) };
        await using (var host = Host.Start(["mcp", "--workspace", workspace], viaEnvironment, strict))
        {
            Initialize(host, "{}");
            await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
            var refused = await CallAsync(host, 2, "lsp_diagnostics", """{"paths":["Main.java"]}""");
            Require(TextOf(refused).Contains(hash, StringComparison.Ordinal) && host.Errors.Contains("another terms hash", StringComparison.Ordinal), "Strict mode accepted another hash, or did not say why on stderr:\n" + host.Errors);
            host.CloseInput();
            await host.ExitAsync(TimeSpan.FromSeconds(30));
        }

        // SHARPRAIL_PREFETCH with the consent variable: the host downloads, finds the terms are not the pinned text and starts nothing.
        var unattended = Path.Combine(root, "child-unattended");
        SeedInstallation(unattended, server);
        await using (var host = Host.Start(["mcp", "--workspace", workspace], unattended, new(variables) { ["SHARPRAIL_PREFETCH"] = "intellij" }))
        {
            Initialize(host, "{}");
            await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
            await Until(() => host.Errors.Contains("intellij download: Failed", StringComparison.Ordinal));
            Require(host.Errors.Contains("downloading intellij in the background", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(unattended, "language", "agreements")), "The unattended start did the wrong thing:\n" + host.Errors);
            host.CloseInput();
            await host.ExitAsync(TimeSpan.FromSeconds(30));
        }
        // No launcher ever ran: nothing executes without consent and a matching pinned hash.
        foreach (var directory in new[] { state, viaEnvironment, unattended })
            Require(!File.Exists(Path.Combine(directory, "language", "servers", "intellij", server.Spec.Version + "-" + ManagedArtifacts.Platform(), "bin", "ran")), "The server was executed without a matching, accepted terms text.");
        SeedInstallation(Path.Combine(root, "child-no-prompt"), server);
        // A client that did not advertise elicitation is never asked.
        await using (var host = ChildHost(["mcp", "--workspace", workspace], Path.Combine(root, "child-no-prompt")))
        {
            Initialize(host, "{}");
            await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
            var refused = await CallAsync(host, 2, "lsp_diagnostics", """{"paths":["Main.java"]}""");
            Require(TextOf(refused).Contains(hash, StringComparison.Ordinal) && !host.Lines.Any(line => line.Contains("elicitation/create", StringComparison.Ordinal)), "A client without elicitation was asked.");
            host.CloseInput();
            await host.ExitAsync(TimeSpan.FromSeconds(30));
        }
        await using (var listing = Host.Start(["languages"], Path.Combine(root, "child-languages"), new() { ["SHARPRAIL_PREFETCH"] = "intellij" }))
        {
            Require(await listing.ExitAsync(TimeSpan.FromSeconds(30)) == 0 && listing.Lines.Single(line => line.StartsWith("intellij\t", StringComparison.Ordinal)).Split('\t')[3].Contains("agreement", StringComparison.Ordinal)
                && listing.Lines.Where(line => !line.StartsWith("intellij\t", StringComparison.Ordinal)).All(line => line.Split('\t')[3].Length == 0), "The languages command does not say which server needs the user's agreement.");
        }
    }
}