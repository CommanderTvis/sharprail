#!/usr/bin/env dotnet
// The opt-in real language-server matrix: `scripts/mise run lsp:integration -- --all | --server <id>[,<id>...]`
// (dotnet run --file tests/Tereditor.Language.Integration/run.cs -- ...). Launches the built Release host with
// TEREDITOR_HEADLESS_MCP=1 for each corpus.json case and sends its prewritten MCP calls. Repositories and managed servers
// are cached in $TEREDITOR_CACHE_DIR/lsp-validation, reports go to artifacts/lsp-validation. Never accepts agreements:
// a missing prerequisite or a pending acceptance is BLOCKED, and any case that did not pass makes the exit code 1.
#:include ../../scripts/lib/Tasks.cs
#:include Integration.cs
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Tasks;

return Suite.Entry(() => RunSuite(args).GetAwaiter().GetResult());

static async Task<int> RunSuite(string[] args)
{
    if (!args.Contains("--all") && !args.Contains("--server")) throw new TaskFailure("Explicitly select --all or --server <id>; this expensive suite is opt-in.");
    var serverIndex = Array.IndexOf(args, "--server") + 1;
    string[]? selected = args.Contains("--all") ? null
        : serverIndex < args.Length ? args[serverIndex].Split(',') : throw new TaskFailure("--server needs a provider id, or a comma-separated list.");
    var corpus = Suite.ReadJson("corpus.json");
    Suite.ValidateCorpus(corpus);
    var cases = Suite.Items(corpus, "cases")!.Select(item => item!.AsObject()).Where(item => selected is null || Suite.Text(item, "server") is { } id && selected.Contains(id)).ToList();
    if (cases.Count == 0) throw new TaskFailure("No matching scenario");
    var cache = Suite.CacheDir("lsp-validation");
    var reports = At("artifacts", "lsp-validation");
    Directory.CreateDirectory(cache);
    Directory.CreateDirectory(reports);
    var dotnet = Dotnet;
    // DOTNET's directory, or "." for a bare `dotnet` (which names no SDK, so the C# provider goes on to its managed SDK and PATH), as before.
    var dotnetRoot = Path.GetDirectoryName(dotnet) is { Length: > 0 } dotnetDirectory ? dotnetDirectory : ".";
    // The JVM cases run with the repository's pinned JDK (`scripts/tool jdk`), or JAVA_HOME when set. The host's project-JDK
    // lookup prefers a machine SDKMAN install, so the child environment points it at an empty candidates directory: the
    // selection never depends on what the machine has installed.
    var jdk = Env("JAVA_HOME") ?? Tool("jdk");
    var java = Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
    var jvmEnvironment = new Dictionary<string, string?>
    {
        ["JAVA_HOME"] = jdk,
        ["PATH"] = Path.Combine(jdk, "bin") + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
        ["SDKMAN_CANDIDATES_DIR"] = Path.Combine(cache, "no-sdkman"),
    };
    if (!File.Exists(Suite.HostAssembly)) throw new TaskFailure($"Build Release first: {Suite.HostAssembly} is missing.");
    var outcomes = new JsonArray();
    Suite.TrapInterrupt();
    foreach (var scenario in cases)
    {
        if (Suite.Interrupted) break;
        var started = Environment.TickCount64;
        var server = Suite.Text(scenario, "server")!;
        var report = new JsonObject
        {
            ["server"] = server, ["repository"] = Suite.Text(scenario, "repository"), ["revision"] = Suite.Text(scenario, "revision"), ["state"] = "failed",
        };
        Suite.Put(report, "coverage", scenario["coverage"]?.DeepClone());
        var calls = new JsonArray();
        report["calls"] = calls;
        HostProcess? host = null;
        Func<Task>? captureLogs = null;
        Action? restoreProbe = null;
        try
        {
            var runtimeName = Suite.Text(scenario, "runtime") ?? "";
            var runtime = runtimeName == "dotnet" ? dotnet : runtimeName == "java" ? java : runtimeName;
            var versionArguments = runtimeName == "java" ? new[] { "-version" } : runtimeName is "go" or "cs" ? new[] { "version" } : new[] { "--version" };
            try { report["runtime"] = Suite.Command(runtime, versionArguments, Root).Trim(); }
            // The host downloads its own pinned Bun when none is on PATH (ManagedRuntimes), so a missing bun blocks nothing.
            catch (Exception) when (runtimeName == "bun") { report["runtime"] = "no bun on PATH; the host provisions its pinned Bun"; }
            catch (Exception error)
            {
                report["state"] = "blocked";
                throw new InvalidOperationException($"Runtime {runtimeName} unavailable: {error.Message}");
            }
            if (Suite.Text(scenario, "blocked") is { Length: > 0 } blocked)
            {
                report["state"] = "blocked";
                throw new InvalidOperationException(blocked);
            }
            var repository = Suite.Text(scenario, "repository")!;
            var revision = Suite.Text(scenario, "revision")!;
            var project = Path.Combine(cache, "projects", repository.Replace('/', '-'), revision);
            Directory.CreateDirectory(project);
            if (!Path.Exists(Path.Combine(project, ".git")))
            {
                Suite.Command("git", ["init", "-q"], project);
                Suite.Command("git", ["remote", "add", "origin", $"https://github.com/{repository}.git"], project);
            }
            var head = "";
            try { head = Suite.Command("git", ["rev-parse", "--verify", "--quiet", "HEAD"], project).Trim(); }
            catch (InvalidOperationException) { }
            if (head != revision)
            {
                Suite.Command("git", ["fetch", "--depth=1", "origin", revision], project);
                Suite.Command("git", ["checkout", "--detach", revision], project);
            }
            if (Suite.Command("git", ["rev-parse", "HEAD"], project).Trim() != revision) throw new InvalidOperationException("Project revision mismatch");
            var projectEnvironment = new Dictionary<string, string?>(jvmEnvironment)
            {
                ["BUNDLE_PATH"] = Path.Combine(project, ".bundle", "packages"),
                ["BUNDLE_USER_HOME"] = Path.Combine(cache, "bundle"),
                ["GEM_SPEC_CACHE"] = Path.Combine(cache, "gem-specs"),
            };
            foreach (var preparation in Suite.Items(scenario, "prepare") ?? new JsonArray())
            {
                var command = preparation as JsonArray ?? Suite.Items(preparation, "command") ?? throw new InvalidDataException("A preparation needs a command");
                var directory = preparation is JsonArray ? project : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(project, Suite.Text(preparation, "cwd") ?? "")));
                if (directory != project && !directory.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Preparation directory escapes project");
                var words = command.Select(word => word!.GetValue<string>()).ToArray();
                Suite.Command(words[0], words[1..], directory, projectEnvironment);
            }
            report["workspace"] = project;
            var extensions = Suite.Items(scenario, "extensions")!.Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var sourceFiles = Suite.Command("git", ["ls-files"], project).Split('\n').Count(file => extensions.Contains(Extension(file)));
            report["sourceFiles"] = sourceFiles;
            if (sourceFiles < Suite.Number(scenario, "minimumSourceFiles")) throw new InvalidOperationException($"Corpus too small: {sourceFiles} source files");
            var probePath = Suite.Text(scenario, "path")!;
            if (!Path.Exists(Path.Combine(project, probePath))) throw new InvalidOperationException($"ENOENT: no such file or directory, access '{Path.Combine(project, probePath)}'");
            var state = Path.Combine(cache, "state", server);
            Directory.CreateDirectory(Path.Combine(state, "language"));
            File.WriteAllText(Path.Combine(state, "language", "servers.json"),
                new JsonObject { [server] = new JsonObject { ["managed"] = server is not ("swift" or "dart") } }.ToJsonString(Suite.Compact));
            var token = McpClient.NewToken();
            var environment = new Dictionary<string, string?>(projectEnvironment)
            {
                ["TEREDITOR_ROOT"] = project, ["TEREDITOR_STATE_DIR"] = state, ["TEREDITOR_BIND"] = "127.0.0.1", ["TEREDITOR_PORT"] = "0", ["TEREDITOR_TOKEN"] = token,
                ["TEREDITOR_HEADLESS_MCP"] = "1", ["TEREDITOR_LSP_DISABLE"] = "0", ["DOTNET_ROOT"] = dotnetRoot,
            };
            host = HostProcess.Start(dotnet, environment, 200_000);
            var mcp = new McpClient(await host.Endpoint(30_000, 100, "Headless host failed to start", stopOnInterrupt: false), token, numberRequests: false, errorPrefix: "");

            async Task<JsonNode?> Call(string tool, JsonObject arguments, int retry = 0)
            {
                var result = await mcp.Tool(tool, project, arguments);
                var value = JsonNode.Parse(Suite.Text(Suite.Items(result, "content")?[0], "text") ?? throw new InvalidDataException($"{tool} returned no text content"));
                calls.Add(new JsonObject { ["tool"] = tool, ["arguments"] = arguments.DeepClone(), ["result"] = value?.DeepClone() });
                if (server == "scala" && tool == "lsp_navigate" && Suite.Text(value, "state") == "error" && Regex.IsMatch(Suite.Text(value, "detail") ?? "", "request .*has been cancelled", RegexOptions.IgnoreCase)
                    && retry < 3 && !Suite.Interrupted)
                {
                    await Task.Delay(1000);
                    return await Call(tool, arguments, retry + 1);
                }
                return value;
            }

            captureLogs = () => Call("lsp_status", new JsonObject { ["operation"] = "logs", ["server"] = server });
            await mcp.Rpc("initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "tereditor-functional-tests", ["version"] = "1" },
            });
            var tools = await mcp.Rpc("tools/list");
            if (!(Suite.Items(tools, "tools")?.Any(tool => Suite.Text(tool, "name") == "lsp_navigate") ?? false)) throw new InvalidOperationException("Production language MCP tools missing");
            var catalog = await Call("lsp_status", new JsonObject { ["operation"] = "catalog" });
            Suite.Put(report, "provider", Suite.Items(catalog, "servers")?.FirstOrDefault(item => Suite.Text(item, "id") == server)?.DeepClone());
            if (scenario["configuration"] is { } configuration)
                await Call("lsp_status", new JsonObject { ["operation"] = "configure", ["server"] = server, ["configuration"] = configuration.DeepClone() });
            foreach (var stepNode in Suite.Items(scenario, "calls")!)
            {
                var step = stepNode!.AsObject();
                var tool = Suite.Text(step, "tool")!;
                var assert = Suite.Text(step, "assert");
                var arguments = step["arguments"]?.AsObject() ?? new JsonObject();
                if (assert == "checked" && Suite.Text(scenario, "diagnosticProbe") is { Length: > 0 } probe && restoreProbe is null)
                {
                    var file = Path.Combine(project, probePath);
                    var original = File.ReadAllBytes(file);
                    restoreProbe = () => File.WriteAllBytes(file, original);
                    File.WriteAllBytes(file, original.Concat(Encoding.UTF8.GetBytes(probe)).ToArray());
                }
                if (step["anchor"] is JsonObject anchor)
                {
                    var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(project, Suite.Text(arguments, "path")!)));
                    var anchorText = Suite.Text(anchor, "text")!;
                    var start = text.IndexOf(anchorText, StringComparison.Ordinal);
                    if (start < 0 || text.IndexOf(anchorText, start + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException("Anchor must occur exactly once");
                    var prefix = text[..(start + anchorText.IndexOf(Suite.Text(anchor, "identifier")!, StringComparison.Ordinal))];
                    arguments["line"] = prefix.Count(character => character == '\n');
                    arguments["column"] = prefix.Length - prefix.LastIndexOf('\n') - 1;
                }
                var result = await Call(tool, arguments);
                var deadline = Environment.TickCount64 + 900_000;
                while (Suite.Text(result, "state") is "installing" or "initializing")
                {
                    if (Suite.Interrupted) throw new InvalidOperationException("Interrupted");
                    if (Environment.TickCount64 > deadline) throw new InvalidOperationException("Import/readiness deadline exceeded");
                    await Task.Delay(1000);
                    result = await Call(tool, Suite.Text(arguments, "operation") == "retry" ? new JsonObject { ["operation"] = "status", ["server"] = server } : arguments);
                }
                var queried = Suite.Text(arguments, "path");
                bool ElsewhereToo(JsonNode? value) => Suite.Items(value, "locations")?.Any(item => Suite.Text(item, "path") != queried) ?? false;
                static bool Published(JsonNode? value) => Suite.Items(value, "files")?.Any(file => Suite.Text(file, "freshness") is "current" or "unversioned") ?? false;
                if (assert == "crossFile" && server is "swift" or "scala")
                {
                    var indexDeadline = Environment.TickCount64 + 900_000;
                    while (Suite.Text(result, "state") is "ready" or "initializing" && !ElsewhereToo(result) && Environment.TickCount64 < indexDeadline && !Suite.Interrupted)
                    {
                        await Task.Delay(1000);
                        result = await Call(tool, arguments);
                    }
                }
                if (assert == "checked" && Suite.Text(result, "state") == "ready")
                {
                    var diagnosticDeadline = Environment.TickCount64 + 30_000;
                    while (!Published(result) && Environment.TickCount64 < diagnosticDeadline && !Suite.Interrupted)
                    {
                        await Task.Delay(1000);
                        result = await Call(tool, arguments);
                        if (Suite.Text(result, "state") != "ready") break;
                    }
                }
                var resultState = Suite.Text(result, "state");
                if (resultState is "requiresAcceptance" or "disabled" or "expired")
                {
                    report["state"] = "blocked";
                    throw new InvalidOperationException(Suite.Text(result, "detail") ?? "");
                }
                if (resultState != "ready") throw new InvalidOperationException($"{tool}: {Suite.Js(resultState)}: {Suite.Js(Suite.Text(result, "detail"))}");
                if (assert == "locations" && Suite.Items(result, "locations") is not { Count: > 0 }) throw new InvalidOperationException("Expected semantic locations, received none");
                if (assert == "checked" && !Published(result)) throw new InvalidOperationException("No current or explicitly unversioned diagnostic publication was received");
                if (assert == "crossFile" && !ElsewhereToo(result)) throw new InvalidOperationException("No references outside the queried file");
            }
            report["state"] = "passed";
            await Call("lsp_status", new JsonObject { ["operation"] = "stop", ["server"] = server });
        }
        catch (Exception error) { report["detail"] = error.Message; }
        finally
        {
            restoreProbe?.Invoke();
            if (captureLogs is not null && !Suite.Interrupted)
            {
                try { await captureLogs(); }
                catch (Exception) { }
            }
            if (host is not null) await host.Stop();
            report["durationMs"] = Environment.TickCount64 - started;
            Suite.WriteJson(Path.Combine(reports, $"{server}.json"), report);
            File.WriteAllText(Path.Combine(reports, $"{server}.log"), host?.Log ?? "");
            var outcome = new JsonObject { ["server"] = server, ["state"] = Suite.Text(report, "state") };
            foreach (var key in new[] { "detail", "sourceFiles", "coverage" }) Suite.Put(outcome, key, report[key]?.DeepClone());
            outcomes.Add(outcome);
            Console.WriteLine($"{Suite.Text(report, "state")!.ToUpperInvariant()} {server} {Suite.Text(report, "detail") ?? ""}");
        }
    }
    Suite.WriteJson(Path.Combine(reports, "summary.json"), outcomes);
    if (Suite.Interrupted) return 130;
    return outcomes.Any(item => Suite.Text(item, "state") != "passed") ? 1 : 0;
}

// path.extname of a Git path: the name's last dot onwards, nothing for a name that only starts with a dot.
static string Extension(string file)
{
    var name = file[(file.LastIndexOf('/') + 1)..];
    var dot = name.LastIndexOf('.');
    return dot <= 0 ? "" : name[dot..];
}
