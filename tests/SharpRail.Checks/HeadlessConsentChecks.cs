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
        [.. LanguageServers.Wrap(HeadlessLanguages.Servers(HeadlessLanguages.Roster(BuiltinPlugins.Headless, ManagedArtifacts.Platform())))];

    private static LanguageConsent NewConsent(string directory, Dictionary<string, string>? environment = null)
    {
        var catalog = Catalog();
        return new(directory, () => catalog, name => environment is not null && environment.TryGetValue(name, out var value) ? value : null);
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

        // The refusal: what it is, the terms and their hash and version, the exact next step. It starts and downloads nothing.
        var refusal = none.Refusal(server);
        Require(refusal.Contains(hash, StringComparison.Ordinal) && refusal.Contains(server.Spec.Version, StringComparison.Ordinal) && refusal.Contains("accept_terms", StringComparison.Ordinal)
            && refusal.Contains("Nothing was downloaded or started", StringComparison.Ordinal) && refusal.Contains("region", StringComparison.Ordinal) && refusal.Contains("dataSharing", StringComparison.Ordinal)
            && refusal.Contains("SHARPRAIL_ACCEPT_TERMS=intellij", StringComparison.Ordinal) && refusal.Contains("only after the user clearly said yes", StringComparison.OrdinalIgnoreCase), "The refusal text is incomplete:\n" + refusal);

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

        await Prompts(root, server);
        await ChildProcesses(root, server);
        Console.WriteLine("PASS headless consent: refusal text with the hash, accept_terms refuses a wrong hash, version or answer and records the right one, SHARPRAIL_ACCEPT_TERMS plain and strict, elicitation accept, decline, unsupported and failed, state kept with a volume and not without, nothing accepted by default");
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
            inner = 0;
            var result = await new LanguageConsentTools(consent, new FakePrompt(reply), null).Guard(language).Call(call, default);
            Require(result.Error && inner == 0 && result.Text.Contains("accept_terms", StringComparison.Ordinal) && consent.GrantFor(server) is null && !Directory.Exists(directory), $"The {name} reply must fall back to the text and record nothing.");
        }

        var unsupported = new FakePrompt(null);
        var unsupportedConsent = NewConsent(Path.Combine(root, "prompt-unsupported"));
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
        File.WriteAllText(launcher, "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var eula = "Fixture terms, not the pinned ones.";
        File.WriteAllText(Path.Combine(directory, "EULA.txt"), eula);
        File.WriteAllText(Path.Combine(directory, "product-info.json"), $$"""{"name":"intellij-server","buildNumber":"{{server.Spec.Version}}","productCode":"ILS"}""");
        var eulaHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(eula)));
        File.WriteAllText(Path.Combine(directory, ".sharprail-artifact.json"), $$"""{"ArchiveSha256":"{{artifact.Sha256}}","EulaSha256":"{{eulaHash}}"}""");
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

        await using (var host = Host.Start(["mcp", "--workspace", workspace], state))
        {
            Initialize(host, "{}");
            var initialize = (await host.ReplyAsync([1], TimeSpan.FromSeconds(60)))[1];
            Require(initialize["result"]!["instructions"]!.GetValue<string>().Contains("accept_terms", StringComparison.Ordinal), "The initialize text does not mention the terms.");
            host.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            var listed = (await host.ReplyAsync([2], TimeSpan.FromSeconds(60)))[2]["result"]!["tools"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()).ToArray();
            Require(listed.Contains("accept_terms"), "accept_terms is not offered: " + string.Join(", ", listed));
            var refused = await CallAsync(host, 3, "lsp_diagnostics", """{"paths":["Main.java"]}""");
            Require(refused["result"]!["isError"]?.GetValue<bool>() == true && TextOf(refused).Contains(hash, StringComparison.Ordinal) && TextOf(refused).Contains("accept_terms", StringComparison.Ordinal), "A Java call must answer with the terms: " + refused.ToJsonString());
            Require(!Directory.Exists(consents) && !Directory.Exists(agreements), "A default run must record no acceptance.");
            var wrong = await CallAsync(host, 4, "accept_terms", $$$"""{"server":"intellij","hash":"{{{new string('1', 64)}}}","answers":{{{Answers}}}}""");
            Require(wrong["result"]!["isError"]?.GetValue<bool>() == true && !Directory.Exists(consents), "A wrong hash was accepted by the host.");
            var right = await CallAsync(host, 5, "accept_terms", $$$"""{"server":"intellij","hash":"{{{hash}}}","answers":{{{Answers}}}}""");
            Require(right["result"]!["isError"] is null && TextOf(right).StartsWith("Recorded:", StringComparison.Ordinal) && File.Exists(Path.Combine(consents, "intellij.json")), "The right hash was not recorded: " + right.ToJsonString());
            // The fixture's terms are not the pinned text, so the start that acceptance triggers must refuse and write no acceptance of its own.
            await Task.Delay(500);
            var after = await CallAsync(host, 6, "lsp_diagnostics", """{"paths":["Main.java"]}""");
            Require(!TextOf(after).Contains("needs the user's agreement", StringComparison.Ordinal) && !Directory.Exists(agreements), "After acceptance the call must go to the server, and a mismatching installation must not be accepted: " + TextOf(after));
            host.CloseInput();
            Require(await host.ExitAsync(TimeSpan.FromSeconds(30)) == 0, "The host did not exit 0.");
        }

        // Restart with the state volume: the consent is still there.
        await using (var host = Host.Start(["mcp", "--workspace", workspace], state))
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
        await using (var host = Host.Start(["mcp", "--workspace", workspace], fresh))
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

        // Elicitation over the real protocol: Decline gives the text, Accept records and lets the call run.
        foreach (var (name, reply, expectAccepted) in new[] { ("decline", """{"action":"decline"}""", false), ("accept", """{"action":"accept","content":{"region":"europe","dataSharing":"none"}}""", true) })
        {
            var prompted = Path.Combine(root, "child-prompt-" + name);
            SeedInstallation(prompted, server);
            await using var host = Host.Start(["mcp", "--workspace", workspace], prompted);
            Initialize(host, """{"elicitation":{}}""");
            await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
            host.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"lsp_diagnostics","arguments":{"paths":["Main.java"]}}}""");
            JsonNode? request = null;
            await Until(() => (request = host.Lines.Select(line => JsonNode.Parse(line)!).FirstOrDefault(node => node["method"]?.GetValue<string>() == "elicitation/create")) is not null);
            Require(request!["params"]!["message"]!.GetValue<string>().Contains(hash, StringComparison.Ordinal) && request["params"]!["requestedSchema"]!["properties"]!["region"] is not null, "The elicitation request lacks the terms.");
            host.Send($$$"""{"jsonrpc":"2.0","id":{{{request["id"]!.ToJsonString()}}},"result":{{{reply}}}}""");
            var final = (await host.ReplyAsync([2], TimeSpan.FromSeconds(90)))[2];
            Require(expectAccepted == File.Exists(Path.Combine(prompted, "language", "consents", "intellij.json")), $"The {name} reply recorded the wrong thing.");
            Require(expectAccepted != TextOf(final).Contains("needs the user's agreement", StringComparison.Ordinal), $"The {name} reply gave the wrong answer: " + TextOf(final));
            host.CloseInput();
            await host.ExitAsync(TimeSpan.FromSeconds(30));
        }
        // A client that did not advertise elicitation is never asked.
        await using (var host = Host.Start(["mcp", "--workspace", workspace], Path.Combine(root, "child-no-prompt")))
        {
            Initialize(host, "{}");
            await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
            var refused = await CallAsync(host, 2, "lsp_diagnostics", """{"paths":["Main.java"]}""");
            Require(TextOf(refused).Contains(hash, StringComparison.Ordinal) && !host.Lines.Any(line => line.Contains("elicitation/create", StringComparison.Ordinal)), "A client without elicitation was asked.");
            host.CloseInput();
            await host.ExitAsync(TimeSpan.FromSeconds(30));
        }
        await using (var listing = Host.Start(["languages"], Path.Combine(root, "child-languages")))
        {
            Require(await listing.ExitAsync(TimeSpan.FromSeconds(30)) == 0 && listing.Lines.Single(line => line.StartsWith("intellij\t", StringComparison.Ordinal)).Split('\t')[3].Contains("agreement", StringComparison.Ordinal)
                && listing.Lines.Where(line => !line.StartsWith("intellij\t", StringComparison.Ordinal)).All(line => line.Split('\t')[3].Length == 0), "The languages command does not say which server needs the user's agreement.");
        }
    }
}