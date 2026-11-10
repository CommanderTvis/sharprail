using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Checks;

/// <summary>
/// The headless host as a real child process: no window is involved. Stdout carries JSON-RPC lines only, stderr the logs,
/// end of input and SIGTERM exit 0, a configuration error exits 2, and the project reaches no UI package.
/// </summary>
internal static partial class HeadlessChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    [GeneratedRegex(@"Avalonia|SkiaSharp|HarfBuzz|SharpRail\.UI|\.UI/|Ghostty|Scintilla|Plugins\.UI\.Kit|Host\.Remote|Host\.Client|Microsoft\.AspNetCore", RegexOptions.CultureInvariant)]
    private static partial Regex Forbidden();

    internal static async Task Run(string fixture)
    {
        Dependencies();
        Languages();
        await Consent(fixture);
        var root = Path.Combine(fixture, "headless");
        var workspace = Path.Combine(root, "workspace");
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(Path.Combine(state, "language"));
        Directory.CreateDirectory(workspace);
        await GitRepository.RunAsync(workspace, default, "init");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".gitignore"), ".tools/\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "code.ts"), "😀 target");
        await File.WriteAllTextAsync(Path.Combine(workspace, "SPEC.md"), "---\nid: goal\ntitle: Project goal\ntype: product-goal\n---\n# Goal\n");
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(HeadlessChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        await File.WriteAllTextAsync(Path.Combine(state, "language", "servers.json"), JsonSerializer.Serialize(new { typescript = new { command = executable, arguments } }));

        await Conversation(workspace, state);
        await Terminate(workspace, state);
        await Configuration(root, workspace, state);
        await using (var listing = Host.Start(["languages"], state))
        {
            Require(await listing.ExitAsync(TimeSpan.FromSeconds(30)) == 0 && listing.Lines.Any(line => line.StartsWith("typescript\tJavaScript / TypeScript", StringComparison.Ordinal)) && listing.Lines.All(line => line.Split('\t').Length == 4), "The languages command did not list the served servers: " + string.Join('|', listing.Lines));
        }
        Require(!Directory.Exists(Path.Combine(workspace, ".sharprail")), "A default run must create nothing in .sharprail.");
        await Switches(root, state);
        Console.WriteLine("PASS headless host: JSON-RPC lines only on stdout, logs on stderr, EOF and SIGTERM exit 0, bad configuration exits 2, no UI reference");
    }

    /// <summary>The restore graph, transitive packages and projects included, holds no UI, Skia or web server package.</summary>
    private static void Dependencies()
    {
        if (BoundaryChecks.Checkout() is not { } checkout) { Console.WriteLine("SKIP headless dependency graph: no source checkout"); return; }
        var assets = Path.Combine(checkout, "src", "SharpRail.Host.Headless", "obj", "project.assets.json");
        Require(File.Exists(assets), "Restore the headless project before checking its dependencies.");
        var libraries = JsonNode.Parse(File.ReadAllText(assets))!["libraries"]!.AsObject().Select(pair => pair.Key).ToArray();
        Require(libraries.Any(name => name.StartsWith("SharpRail.Host.Core/", StringComparison.Ordinal)) && libraries.Length > 20, "The headless dependency graph is empty or lost the host core.");
        var offending = libraries.Where(name => Forbidden().IsMatch(name)).ToArray();
        Require(offending.Length == 0, "The headless host reaches UI or web server packages: " + string.Join(", ", offending));
    }

    /// <summary>
    /// The served languages, offline: the roster follows what can be provisioned with no preinstalled toolchain, every served server pins its download
    /// (checksum, exact version, a pinned runtime), and the ones left out say why. Runs against the shipped catalog and pins.
    /// </summary>
    private static readonly HashSet<string> OptIn = ["lsp-intellij"];

    private static void Languages()
    {
        var declared = BuiltinPlugins.Headless;
        string Served(string platform, bool intellij = false) => string.Join(' ', HeadlessLanguages.Servers(HeadlessLanguages.Roster(declared, platform, intellij ? OptIn : null)).Select(spec => spec.Id).Order());
        Require(Served("linux-x86_64") == "clangd csharp css kotlin php python shell typescript", "The headless languages on linux-x86_64 changed: " + Served("linux-x86_64"));
        Require(Served("macos-aarch64") == "clangd csharp css kotlin php python shell typescript", "The headless languages on macos-aarch64 changed: " + Served("macos-aarch64"));
        // clangd publishes no linux-arm64 build, so the arm64 image leaves it out instead of failing at the first call.
        Require(Served("linux-aarch64") == "csharp css kotlin php python shell typescript", "The headless languages on linux-aarch64 changed: " + Served("linux-aarch64"));

        // IntelliJ is opt-in: not in the roster unless the environment names it, then served on every platform that pins its archive.
        Require(HeadlessLanguages.OptedIn(name => null).Count == 0 && HeadlessLanguages.OptedIn(name => name == "SHARPRAIL_PREFETCH" ? "intellij" : null).SequenceEqual(["lsp-intellij"])
            && HeadlessLanguages.OptedIn(name => name == "SHARPRAIL_ACCEPT_TERMS" ? "csharp, intellij:" + new string('a', 64) : null).SequenceEqual(["lsp-intellij"]) && HeadlessLanguages.OptedIn(name => "csharp").Count == 0, "The opt-in rule for IntelliJ is wrong.");
        Require(Served("linux-x86_64", true).Contains("intellij", StringComparison.Ordinal) && Served("linux-aarch64", true).Contains("intellij", StringComparison.Ordinal) && Served("macos-aarch64", true).Contains("intellij", StringComparison.Ordinal), "IntelliJ is missing when asked for.");

        // The manifests are the source of truth: the only server a platform drops from the declared set is clangd on linux-aarch64.
        Require(HeadlessLanguages.Left(declared, "linux-x86_64").Count == 0 && HeadlessLanguages.Left(declared, "macos-aarch64").Count == 0, "A manifest declares a language Headless = Yes that the pins cannot provision.");
        Require(HeadlessLanguages.Left(declared, "linux-aarch64").Select(item => item.Spec.Id).SequenceEqual(["clangd"]), "The platform exceptions to the declared headless languages changed.");
        // Every language server that declares No still needs a toolchain without a pin, so flipping it requires the pin first.
        var desktop = BuiltinPlugins.Create(null).Where(plugin => plugin.Manifest.Headless == HeadlessSupport.No).Select(plugin => plugin.Host).OfType<LanguageServerPlugin>().Where(plugin => plugin.Spec.Id != "intellij").ToArray();
        Require(desktop.Select(plugin => plugin.Spec.Id).Order().SequenceEqual(["dart", "go", "java", "powershell", "r", "ruby", "rust", "scala", "swift"]), "The language servers declared desktop-only changed: " + string.Join(", ", desktop.Select(plugin => plugin.Spec.Id)));
        var reasons = desktop.ToDictionary(plugin => plugin.Spec.Id, plugin => HeadlessLanguages.Refusal(plugin.Spec, "linux-x86_64"));
        Require(reasons.Values.All(reason => reason is { Length: > 10 }), "A server declared desktop-only could now be served headless; declare it Yes: " + string.Join(", ", reasons.Where(item => item.Value is null).Select(item => item.Key)));
        Require(reasons["java"]!.Contains("JDK", StringComparison.Ordinal) && reasons["swift"]!.Contains("toolchain", StringComparison.Ordinal) && reasons["rust"]!.Contains("cargo", StringComparison.Ordinal), "A reason does not name the missing toolchain.");

        foreach (var platform in new[] { "linux-x86_64", "linux-aarch64", "macos-aarch64" })
            foreach (var spec in HeadlessLanguages.Servers(HeadlessLanguages.Roster(declared, platform, OptIn)))
            {
                var name = $"{spec.Id} on {platform}";
                Require(System.Text.RegularExpressions.Regex.IsMatch(spec.Version, @"^\d+(\.\d+)+"), $"{name} has no exact version: {spec.Version}");
                // A server with terms is installed by its provider from pinned archives, and its terms text is pinned too, so consent can come before the download.
                if (spec.TermsAsync is not null)
                {
                    var bundle = spec.Artifacts.SingleOrDefault(item => item.Platform == platform);
                    Require(bundle is not null && bundle.Url.StartsWith("https://download.jetbrains.com/", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(bundle.Sha256, "^[0-9a-f]{64}$")
                        && System.Text.RegularExpressions.Regex.IsMatch(spec.TermsSha256, "^[0-9a-f]{64}$") && spec.TermsTitle.Length > 0 && spec.TermsSummary.Length > 0 && spec.TermsQuestions.Count > 0, $"{name} does not pin its archive and its terms.");
                    continue;
                }
                switch (spec.Pinning)
                {
                    case LanguageServerPinning.Archive:
                        var artifact = spec.Artifacts.First(item => item.Platform == platform || item.Platform == "any");
                        Require(artifact.Url.StartsWith("https://", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(artifact.Sha256, "^[0-9a-f]{64}$"), $"{name} has no https URL with a SHA-256.");
                        break;
                    case LanguageServerPinning.Lockfile:
                        Require(spec.LockFiles.ContainsKey("package-lock.json") && spec.NpmPackages.Split(' ').All(package => System.Text.RegularExpressions.Regex.IsMatch(package, @"@\d+\.\d+\.\d+$")), $"{name} does not pin its npm graph: {spec.NpmPackages}");
                        break;
                    case LanguageServerPinning.NuGet:
                        Require(spec.NuGetPackages.Where(item => item.Platform == platform || item.Platform == "any").All(item => item.Sha512.Length == 128), $"{name} has a NuGet package without SHA-512.");
                        break;
                    default:
                        throw new InvalidOperationException($"{name} is served headless with pinning {spec.Pinning}, which is not a verified download.");
                }
                foreach (var dependency in spec.Dependencies)
                {
                    var pins = ManagedRuntimes.For(ManagedRuntimes.KindOf(dependency.Executable)!.Id, platform);
                    Require(pins.Count > 0 && pins.All(pin => pin.Sha256.Length == 64 && pin.Url.StartsWith("https://", StringComparison.Ordinal)), $"{name} needs {dependency.Name}, which has no checksummed pin.");
                }
            }
        // Bun runs the Node-based servers: a Bun pin per Linux architecture, from GitHub releases.
        Require(new[] { "linux-x86_64", "linux-aarch64" }.All(platform => ManagedRuntimes.For("bun", platform).Count > 0), "Bun is not pinned for both Linux architectures.");
        Console.WriteLine("PASS headless languages: the roster is the servers that provision without a preinstalled toolchain, each pinned with a checksum, the rest named with the reason");
    }

    private static async Task Conversation(string workspace, string state)
    {
        await using var host = Host.Start(["mcp", "--workspace", workspace], state);
        var big = new string('x', 2_000_000);
        host.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}""");
        host.Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        host.Send("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        host.Send("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");
        host.Send("""{"jsonrpc":"2.0","id":4,"method":"no/such"}""");
        host.Send("this is not json");
        host.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":999}}""");
        host.Send("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\",\"params\":{\"pad\":\"" + big + "\"}}");
        host.Send("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"lsp_status","arguments":{"workspace":"/somewhere/else"}}}""");
        host.Send("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"spec_validate","arguments":{}}}""");
        host.Send("""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"lsp_diagnostics","arguments":{"paths":["code.ts"]}}}""");
        host.Send("""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"set_title","arguments":{"title":"x"}}}""");
        // Overlapping calls: every answer arrives once, in any order.
        for (var id = 20; id < 36; id++)
            host.Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = id % 2 == 0 ? "spec_get" : "lsp_status", ["arguments"] = id % 2 == 0 ? new JsonObject { ["id"] = "goal" } : new JsonObject() }
            }.ToJsonString());
        var ids = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }.Concat(Enumerable.Range(20, 16)).ToArray();
        var replies = await host.ReplyAsync(ids, TimeSpan.FromSeconds(90));
        var parseError = host.Lines.Select(line => JsonNode.Parse(line)!).Single(node => node["error"]?["code"]?.GetValue<int>() == -32700);
        Require(parseError["id"] is null, "A parse error must answer with a null id.");

        var initialize = replies[1]["result"]!;
        var instructions = initialize["instructions"]!.GetValue<string>();
        Require(initialize["protocolVersion"]!.GetValue<string>() == "2025-06-18" && !instructions.Contains("set_title", StringComparison.Ordinal) && !instructions.Contains("workspace_delete", StringComparison.Ordinal),
            "The headless initialize text mentions terminal-only tools.");
        Require(instructions.Contains("Language servers here:", StringComparison.Ordinal) && instructions.Contains("TypeScript", StringComparison.Ordinal) && instructions.Contains("Python", StringComparison.Ordinal)
            && instructions.Contains("downloads on first use", StringComparison.Ordinal) && !instructions.Contains("Java (", StringComparison.Ordinal), "The headless initialize text does not list the served languages: " + instructions);
        Require(replies[2]["result"] is JsonObject { Count: 0 }, "ping did not answer an empty result.");
        Require(replies[4]["error"]?["code"]?.GetValue<int>() == -32601, "An unknown method must answer -32601.");
        Require(replies[5]["result"] is JsonObject, "A 2 MB request line was not answered.");

        var tools = replies[3]["result"]!["tools"]!.AsArray().ToDictionary(tool => tool!["name"]!.GetValue<string>(), tool => tool!);
        string[] required = ["lsp_diagnostics", "lsp_navigate", "lsp_status", "lsp_code_actions", "lsp_rename", "lsp_format", "spec_grep", "spec_get", "spec_graph", "spec_create", "spec_update", "spec_delete", "spec_validate", "blueprint_check"];
        Require(required.All(tools.ContainsKey) && tools.Keys.All(name => name.StartsWith("lsp_", StringComparison.Ordinal) || name.StartsWith("spec_", StringComparison.Ordinal) || name == "blueprint_check") && tools.Count == 14,
            "The headless tool table is not exactly the 14 language, spec and Blueprint tools (accept_terms exists only when IntelliJ is asked for): " + string.Join(", ", tools.Keys));
        var desktop = LanguageMcpTools.Create(new HostStateStore(Path.Combine(Path.GetDirectoryName(state)!, "desktop-state")), workspace);
        Require(desktop.Count == 6 && desktop.All(tool => tools[tool.Name]["inputSchema"]!.ToJsonString() == tool.InputSchema.ToJsonString()), "A language tool's schema differs from the desktop's.");
        Require(replies[9]["error"]?["code"]?.GetValue<int>() == -32602, "A terminal-only tool must not exist headless.");

        string Text(int id) => replies[id]["result"]!["content"]![0]!["text"]!.GetValue<string>();
        bool Failed(int id) => replies[id]["result"]!["isError"]?.GetValue<bool>() ?? false;
        Require(!Failed(6) && Text(6).Contains("\"state\"", StringComparison.Ordinal), "lsp_status failed or did not ignore a foreign workspace argument: " + Text(6));
        Require(!Failed(7) && Text(7).Contains("valid", StringComparison.Ordinal), "spec_validate failed: " + Text(7));
        Require(!Failed(8) && Text(8).Contains("code.ts", StringComparison.Ordinal), "lsp_diagnostics did not report the fixture file through the fake server: " + Text(8));
        for (var id = 20; id < 36; id++)
            Require(!Failed(id) && (id % 2 == 0 ? Text(id).Contains("Project goal", StringComparison.Ordinal) : Text(id).Contains("\"state\"", StringComparison.Ordinal)), $"Overlapping call {id} was lost or failed: {Text(id)}");

        host.CloseInput();
        var exit = await host.ExitAsync(TimeSpan.FromSeconds(30));
        Require(exit == 0, $"End of input must exit 0, got {exit}. stderr:\n{host.Errors}");
        Require(host.Lines.Count == ids.Length + 1, $"Expected {ids.Length + 1} output lines, got {host.Lines.Count}.");
        foreach (var line in host.Lines)
        {
            var node = JsonNode.Parse(line);
            Require(node is JsonObject frame && frame["jsonrpc"]?.GetValue<string>() == "2.0" && (frame.ContainsKey("result") || frame.ContainsKey("error")), "stdout carried something besides a JSON-RPC response: " + line[..Math.Min(line.Length, 200)]);
        }
        Require(host.Errors.Contains("sharprail-headless: serving", StringComparison.Ordinal) && host.Errors.Contains("sharprail-headless: stopping", StringComparison.Ordinal), "stderr lost the host's log lines.");
    }

    private static async Task Terminate(string workspace, string state)
    {
        if (OperatingSystem.IsWindows()) return;
        await using var host = Host.Start(["mcp", "--workspace", workspace], state);
        host.Send("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
        using (var kill = Process.Start("kill", ["-TERM", host.Id.ToString()])!) await kill.WaitForExitAsync();
        var exit = await host.ExitAsync(TimeSpan.FromSeconds(30));
        Require(exit == 0, $"SIGTERM must exit 0, got {exit}. stderr:\n{host.Errors}");
    }

    private static async Task Configuration(string root, string workspace, string state)
    {
        foreach (var arguments in new[] { new[] { "mcp", "--workspace", Path.Combine(root, "missing") }, ["mcp", "--workspace", Path.Combine(workspace, "code.ts")], ["frobnicate"], ["mcp", "--nope"] })
        {
            await using var host = Host.Start(arguments, state);
            var exit = await host.ExitAsync(TimeSpan.FromSeconds(30));
            Require(exit == 2 && host.Lines.Count == 0 && host.Errors.Contains("sharprail-headless:", StringComparison.Ordinal), $"`{string.Join(' ', arguments)}` must exit 2 with a message on stderr only, got {exit}, stdout {host.Lines.Count} lines, stderr: {host.Errors}");
        }
        var file = Path.Combine(root, "state-is-a-file");
        await File.WriteAllTextAsync(file, "x");
        await using var blocked = Host.Start(["mcp", "--workspace", workspace], file);
        var blockedExit = await blocked.ExitAsync(TimeSpan.FromSeconds(30));
        Require(blockedExit == 2 && blocked.Lines.Count == 0, $"An unusable state directory must exit 2, got {blockedExit}. stderr: {blocked.Errors}");
    }

    private const string ProjectFile = """
        version = 1

        [tools]
        disable = ["blueprint_check"]

        [tools.lsp_navigate]
        operations = ["definition", "references", "hover"]

        [log]
        level = "args"

        [arms.nolsp]
        tools.disable = ["lsp_navigate", "lsp_diagnostics", "lsp_status", "lsp_code_actions", "lsp_rename", "lsp_format"]

        [arms.narrow]
        tools.lsp_diagnostics.enabled = false
        tools.lsp_navigate.operations = ["references", "hover"]
        tools.lsp_code_actions.operations = ["list", "preview"]
        log.enabled = true
        log.level = "full"
        log.max_result_chars = 50
        """;

    private static async Task<JsonObject> CheckConfig(string workspace, string state, string[] extra, Dictionary<string, string>? environment = null)
    {
        await using var host = Host.Start(["check-config", "--workspace", workspace, .. extra], state, environment);
        var exit = await host.ExitAsync(TimeSpan.FromSeconds(60));
        Require(exit == 0, $"check-config {string.Join(' ', extra)} exited {exit}. stderr:\n{host.Errors}");
        return (JsonObject)JsonNode.Parse(string.Join('\n', host.Lines))!;
    }

    private static string[] Offered(JsonObject effective) => [.. effective["toolsOffered"]!.AsArray().Select(name => name!.GetValue<string>())];

    /// <summary>Tool switches, arms and overrides, the strict file, the opt-in call log, and the generated instructions.</summary>
    private static async Task Switches(string root, string state)
    {
        var project = Path.Combine(root, "switches");
        Directory.CreateDirectory(Path.Combine(project, ".sharprail"));
        var file = Path.Combine(project, ".sharprail", "mcp.toml");
        await File.WriteAllTextAsync(file, ProjectFile);
        await File.WriteAllTextAsync(Path.Combine(project, "SPEC.md"), "---\nid: goal\ntitle: Project goal\ntype: product-goal\n---\n# Goal\n");

        // Precedence: defaults, file, arm, environment, flags.
        var plain = await CheckConfig(project, state, []);
        Require(plain["arm"] is null && !Offered(plain).Contains("blueprint_check") && Offered(plain).Contains("lsp_navigate") && plain["log"]!["enabled"]!.GetValue<bool>() == false && plain["log"]!["level"]!.GetValue<string>() == "args", "The top level of mcp.toml did not apply: " + plain.ToJsonString());
        var nolsp = await CheckConfig(project, state, [], new() { ["SHARPRAIL_ARM"] = "nolsp" });
        Require(Offered(nolsp).All(name => !name.StartsWith("lsp_", StringComparison.Ordinal)) && Offered(nolsp).Contains("spec_get") && Offered(nolsp).Contains("blueprint_check"),
            "An arm must replace the top-level disable list: " + nolsp.ToJsonString());
        var byEnvironment = await CheckConfig(project, state, [], new() { ["SHARPRAIL_ARM"] = "nolsp", ["SHARPRAIL_TOOLS_DISABLE"] = "spec_get, spec_grep", ["SHARPRAIL_LOG"] = "1", ["SHARPRAIL_LOG_LEVEL"] = "meta", ["SHARPRAIL_RUNS_DIR"] = "elsewhere" });
        Require(Offered(byEnvironment).Contains("lsp_navigate") && !Offered(byEnvironment).Contains("spec_get") && byEnvironment["log"]!["enabled"]!.GetValue<bool>() && byEnvironment["log"]!["level"]!.GetValue<string>() == "meta"
            && byEnvironment["log"]!["dir"]!.GetValue<string>() == Path.Combine(project, "elsewhere"), "Environment variables must beat the arm: " + byEnvironment.ToJsonString());
        var byFlag = await CheckConfig(project, state, ["--arm", "narrow", "--disable-tools=spec_graph"], new() { ["SHARPRAIL_ARM"] = "nolsp", ["SHARPRAIL_TOOLS_DISABLE"] = "spec_get" });
        Require(byFlag["arm"]!.GetValue<string>() == "narrow" && Offered(byFlag).Contains("spec_get") && !Offered(byFlag).Contains("spec_graph") && !Offered(byFlag).Contains("lsp_diagnostics") && Offered(byFlag).Contains("lsp_navigate"),
            "Flags must beat the environment: " + byFlag.ToJsonString());
        Require(!Directory.Exists(Path.Combine(project, ".sharprail", "runs")), "check-config must not create the run directory.");

        // Invalid configuration exits 2 and names the file, line and key.
        async Task Invalid(string text, string expected, Dictionary<string, string>? environment = null, string[]? extra = null)
        {
            var bad = Path.Combine(root, "invalid");
            Directory.CreateDirectory(Path.Combine(bad, ".sharprail"));
            await File.WriteAllTextAsync(Path.Combine(bad, ".sharprail", "mcp.toml"), text);
            foreach (var command in new[] { "check-config", "mcp" })
            {
                await using var host = Host.Start([command, "--workspace", bad, .. extra ?? []], state, environment);
                var exit = await host.ExitAsync(TimeSpan.FromSeconds(60));
                Require(exit == 2 && host.Lines.Count == 0 && host.Errors.Contains(expected, StringComparison.Ordinal), $"`{command}` with `{text.ReplaceLineEndings("|")}` must exit 2 saying `{expected}`, got {exit}: {host.Errors}");
            }
        }
        await Invalid("version = 1\n\n[tools]\ndisabel = []\n", ".sharprail/mcp.toml:4: tools.disabel: unknown key");
        await Invalid("version = 1\n[tools.lsp_navigate]\noperations = [\"definition\", \"teleport\"]\n", ".sharprail/mcp.toml:3: tools.lsp_navigate.operations: unknown operation \"teleport\"");
        await Invalid("version = 1\n[tools.lsp_rename]\noperations = [\"apply\", \"teleport\"]\n", ".sharprail/mcp.toml:3: tools.lsp_rename.operations: unknown operation \"teleport\"");
        await Invalid("version = 2\n", ".sharprail/mcp.toml:1: version:");
        await Invalid("[tools]\n", ".sharprail/mcp.toml:1: version:");
        await Invalid("version = 1\n[log]\nenabled = \"yes\"\n", ".sharprail/mcp.toml:3: log.enabled: must be true or false");
        await Invalid("version = 1\n[arms.a]\nlog.level = \"loud\"\n", ".sharprail/mcp.toml:3: arms.a.log.level:");
        await Invalid("version = 1\n[tools]\nallow = []\n", ".sharprail/mcp.toml:3: tools.allow: must not be empty");
        await Invalid("version = 1\nnonsense\n", ".sharprail/mcp.toml:");
        await Invalid("version = 1\n[arms.a]\n", "arm \"b\" is not defined", new() { ["SHARPRAIL_ARM"] = "b" });
        await Invalid("version = 1\n", "SHARPRAIL_LOG: expected 0 or 1", new() { ["SHARPRAIL_LOG"] = "yes" });
        await Invalid("version = 1\n", "SHARPRAIL_LOG_LEVEL: expected meta, args or full", new() { ["SHARPRAIL_LOG_LEVEL"] = "chatty" });

        // The arm without the language tools: nothing about them in the list, the instructions or a call.
        await using (var host = Host.Start(["mcp", "--workspace", project, "--arm", "nolsp"], state))
        {
            host.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
            host.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            host.Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"lsp_status","arguments":{}}}""");
            host.Send("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"spec_validate","arguments":{}}}""");
            var replies = await host.ReplyAsync([1, 2, 3, 4], TimeSpan.FromSeconds(90));
            var listed = replies[2]["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
            Require(listed.Length > 0 && listed.All(name => !name.StartsWith("lsp_", StringComparison.Ordinal)) && listed.Contains("blueprint_check"), "tools/list still shows the language tools: " + string.Join(", ", listed));
            Require(!replies[2].ToJsonString().Contains("lsp_", StringComparison.Ordinal), "A tool description still mentions a language tool.");
            Require(!replies[1]["result"]!["instructions"]!.GetValue<string>().Contains("lsp", StringComparison.OrdinalIgnoreCase) && !replies[1]["result"]!["instructions"]!.GetValue<string>().Contains("language", StringComparison.OrdinalIgnoreCase), "The instructions mention the language tools.");
            Require(replies[3]["error"]?["code"]?.GetValue<int>() == -32602 && replies[3]["error"]!["message"]!.GetValue<string>().StartsWith("Unknown tool", StringComparison.Ordinal), "A call to a disabled tool must be an unknown tool: " + replies[3].ToJsonString());
            Require(replies[4]["result"]?["isError"] is null, "An offered tool failed in the nolsp arm.");
            host.CloseInput();
            Require(await host.ExitAsync(TimeSpan.FromSeconds(30)) == 0, "nolsp arm did not exit 0.");
        }
        Require(!Directory.Exists(Path.Combine(project, ".sharprail", "runs")), "A run with logging off must not create the run directory.");
        Require(Directory.GetFileSystemEntries(Path.Combine(project, ".sharprail")).Length == 1, "A run with logging off must leave .sharprail as it was.");

        // The narrow arm: diagnostics gone, navigation limited, the log on at full level with a tiny size limit.
        var runs = Path.Combine(root, "runs");
        if (Directory.Exists(runs)) Directory.Delete(runs, true);
        await File.WriteAllTextAsync(file, ProjectFile + "\nlog.max_file_kb = 16\n");
        await using (var host = Host.Start(["mcp", "--workspace", project, "--arm", "narrow"], state, new() { ["SHARPRAIL_RUNS_DIR"] = runs }))
        {
            host.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
            host.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            host.Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"lsp_navigate","arguments":{"operation":"definition","query":"x"}}}""");
            host.Send("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"lsp_diagnostics","arguments":{"paths":["code.ts"]}}}""");
            host.Send("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"lsp_navigate","arguments":{"query":"x"}}}""");
            host.Send("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"spec_get","arguments":{"id":"goal","workspace":"/not/logged"}}}""");
            host.Send("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"lsp_code_actions","arguments":{"operation":"apply","id":"ca1.0"}}}""");
            var early = await host.ReplyAsync([1, 2, 3, 4, 5, 6, 7], TimeSpan.FromSeconds(90));
            var tools = early[2]["result"]!["tools"]!.AsArray().ToDictionary(tool => tool!["name"]!.GetValue<string>(), tool => tool!);
            Require(!tools.ContainsKey("lsp_diagnostics") && tools.ContainsKey("lsp_navigate"), "The narrow arm lists the wrong tools: " + string.Join(", ", tools.Keys));
            var operation = tools["lsp_navigate"]["inputSchema"]!["properties"]!["operation"]!;
            Require(string.Join(',', operation["enum"]!.AsArray().Select(item => item!.GetValue<string>())) == "hover,references" && operation["default"]!.GetValue<string>() == "references", "The operation enum was not narrowed: " + operation.ToJsonString());
            var instructions = early[1]["result"]!["instructions"]!.GetValue<string>();
            Require(instructions.Contains("lsp_navigate", StringComparison.Ordinal) && !instructions.Contains("lsp_diagnostics", StringComparison.Ordinal), "The instructions must name only offered tools: " + instructions);
            Require(early[3]["result"]!["isError"]?.GetValue<bool>() == true && early[3]["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("hover, references", StringComparison.Ordinal), "A removed operation must be refused with the allowed ones: " + early[3].ToJsonString());
            var actions = tools["lsp_code_actions"]["inputSchema"]!["properties"]!["operation"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>());
            Require(string.Join(',', actions) == "list,preview" && early[7]["result"]!["isError"]?.GetValue<bool>() == true && early[7]["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("list, preview", StringComparison.Ordinal),
                "Apply must be removable from lsp_code_actions: " + early[7].ToJsonString());
            Require(early[4]["error"]?["code"]?.GetValue<int>() == -32602, "A switched-off tool must be unknown.");
            Require(early[5]["result"]?["isError"]?.GetValue<bool>() != true, "The default operation, references, was refused: " + early[5].ToJsonString());
            // Enough calls to pass 16 KB: the log stops once, the calls keep working.
            for (var id = 100; id < 300; id++) host.Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"spec_get\",\"arguments\":{\"id\":\"goal\"}}}");
            var later = await host.ReplyAsync([.. Enumerable.Range(100, 200)], TimeSpan.FromSeconds(90));
            Require(later.Where(pair => pair.Key >= 100).All(pair => pair.Value["result"]?["isError"] is null), "A call failed while the log hit its limit.");
            host.CloseInput();
            Require(await host.ExitAsync(TimeSpan.FromSeconds(30)) == 0, "narrow arm did not exit 0.");
            Require(host.Errors.Contains("sharprail-headless: call log ", StringComparison.Ordinal) && host.Errors.Split("reached 16 KB").Length == 2, "The log path or the single size warning is missing from stderr:\n" + host.Errors);
        }
        var logFile = Directory.GetFiles(runs, "*.jsonl").Single();
        var entries = (await File.ReadAllLinesAsync(logFile)).Select(line => (JsonObject)JsonNode.Parse(line)!).ToArray();
        var session = entries[0]["session"]!.GetValue<string>();
        Require(Path.GetFileName(logFile) == session + ".jsonl" && Regex.IsMatch(session, @"^\d{8}T\d{6}Z-[0-9a-f]{6}$"), "The session id has the wrong shape: " + session);
        Require(entries[0]["ev"]!.GetValue<string>() == "start" && entries[0]["arm"]!.GetValue<string>() == "narrow" && entries[0]["level"]!.GetValue<string>() == "full" && entries[0]["configHash"]!.GetValue<string>().Length == 64 && entries[0]["version"] is not null
            && entries[0]["toolsOffered"]!.AsArray().Count > 0, "The start line is incomplete: " + entries[0].ToJsonString());
        Require(entries[^1]["ev"]!.GetValue<string>() == "end" && entries[^1]["calls"]!.GetValue<int>() >= 3, "The log has no end line: " + entries[^1].ToJsonString());
        var calls = entries.Where(entry => entry["ev"]!.GetValue<string>() == "call").ToArray();
        Require(calls.Length is >= 3 and < 200, $"The size limit did not stop the log: {calls.Length} calls.");
        Require(new FileInfo(logFile).Length < 16 * 1024 + 1024, "The log grew past its limit: " + new FileInfo(logFile).Length);
        foreach (var call in calls)
            Require(call["session"]!.GetValue<string>() == session && call["arm"]!.GetValue<string>() == "narrow" && call["ts"] is not null && call["seq"] is not null && call["tool"] is not null && call["durationMs"] is not null
                && call["isError"] is not null && call["resultChars"] is not null && call["resultLines"] is not null && call["args"] is JsonObject, "A call line lacks a documented field: " + call.ToJsonString());
        var first = calls.Single(call => call["tool"]!.GetValue<string>() == "lsp_navigate" && call["isError"]!.GetValue<bool>());
        Require(first["tool"]!.GetValue<string>() == "lsp_navigate" && first["operation"]!.GetValue<string>() == "definition" && first["isError"]!.GetValue<bool>() && first["error"]!.GetValue<string>().Contains("not available", StringComparison.Ordinal), "The refused call is not in the log: " + first.ToJsonString());
        var spec = calls.First(call => call["tool"]!.GetValue<string>() == "spec_get");
        Require(spec["operation"] is null && spec["args"]!["workspace"] is null && spec["args"]!["id"]!.GetValue<string>() == "goal" && spec["result"]!.GetValue<string>().Length <= 51 && spec["resultChars"]!.GetValue<int>() > 0, "A spec_get line is wrong: " + spec.ToJsonString());
        Require(!Directory.Exists(Path.Combine(project, ".sharprail", "runs")), "SHARPRAIL_RUNS_DIR was ignored.");
    }

    /// <summary>One child process of the headless executable (or its dll under dotnet) with its three streams captured.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly Process process;
        private readonly object gate = new();
        private readonly List<string> lines = [];
        private readonly StringBuilder errors = new();
        private readonly Task pumps;

        private Host(Process process)
        {
            this.process = process;
            var output = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line) lock (gate) lines.Add(line);
            });
            var error = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line) lock (gate) errors.AppendLine(line);
            });
            pumps = Task.WhenAll(output, error);
        }

        internal static Host Start(string[] arguments, string state, Dictionary<string, string>? environment = null)
        {
            var apphost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "sharprail-headless.exe" : "sharprail-headless");
            var dll = Path.Combine(AppContext.BaseDirectory, "sharprail-headless.dll");
            Require(File.Exists(dll), "The checks output has no headless host: " + dll);
            var start = new ProcessStartInfo(File.Exists(apphost) ? apphost : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!File.Exists(apphost)) start.ArgumentList.Add(dll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            // The checks switch language services off for themselves; the host under test must run them.
            start.Environment.Remove("SHARPRAIL_LSP_DISABLE");
            start.Environment.Remove("SHARPRAIL_ROOT");
            foreach (var name in new[] { "SHARPRAIL_ACCEPT_TERMS", "INTELLIJ_REGION", "INTELLIJ_DATA_SHARING" }) start.Environment.Remove(name);
            start.Environment["SHARPRAIL_STATE_DIR"] = state;
            foreach (var name in start.Environment.Keys.Where(name => name is "SHARPRAIL_ARM" or "SHARPRAIL_LOG" or "SHARPRAIL_LOG_LEVEL" or "SHARPRAIL_RUNS_DIR" || name.StartsWith("SHARPRAIL_TOOLS_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(name);
            foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
            return new(Process.Start(start)!);
        }

        internal int Id => process.Id;
        internal string Errors { get { lock (gate) return errors.ToString(); } }
        internal IReadOnlyList<string> Lines { get { lock (gate) return [.. lines]; } }
        internal void Send(string line) { process.StandardInput.Write(line + "\n"); process.StandardInput.Flush(); }
        internal void CloseInput() => process.StandardInput.Close();

        /// <summary>Waits until every id has a response and returns them; a response with an unexpected id fails.</summary>
        internal async Task<Dictionary<int, JsonNode>> ReplyAsync(int[] ids, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var found = Lines.Select(line => JsonNode.Parse(line)!).Where(node => node["method"] is null && node["id"] is JsonValue { } id && id.TryGetValue<int>(out _)).ToDictionary(node => node["id"]!.GetValue<int>());
                if (ids.All(found.ContainsKey)) return found;
                if (process.HasExited && DateTime.UtcNow > deadline - timeout + TimeSpan.FromSeconds(5)) throw new InvalidOperationException("The headless host exited early. stderr:\n" + Errors);
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"No answer for ids {string.Join(',', ids.Where(id => !found.ContainsKey(id)))}. stderr:\n{Errors}");
                await Task.Delay(50);
            }
        }

        internal async Task<int> ExitAsync(TimeSpan timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(cancel.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw new InvalidOperationException("The headless host did not exit. stderr:\n" + Errors); }
            await pumps;
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }
}