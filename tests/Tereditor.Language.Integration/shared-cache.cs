#!/usr/bin/env dotnet
// Shared semantic caches: `--inventory` classifies every catalog server; `--server go,dart [--scratch <dir>]` runs the opt-in
// native probes (two Git worktrees on different branches, warm, restart in the other, compare against a cold run).
// dotnet run --file tests/Tereditor.Language.Integration/shared-cache.cs -- ... Needs the built Release host; Dart also
// TEREDITOR_LSP_COMMAND_DART. Scratch defaults to $TEREDITOR_CACHE_DIR/lsp-shared-cache/run, reports go to
// artifacts/lsp-shared-cache, and any probe that did not pass makes the exit code 1.
#:include ../../scripts/lib/Tasks.cs
#:include Integration.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Tasks;

string? Option(string name) => Array.IndexOf(args, name) is var index and >= 0 && index + 1 < args.Length ? args[index + 1] : null;

// Resolved against the directory the script was started in, before the run moves to the repository root.
var scratch = Option("--scratch") is { } given ? Path.GetFullPath(given) : null;
return Suite.Entry(() => RunProbes().GetAwaiter().GetResult());

async Task<int> RunProbes()
{
    var inventory = Suite.ReadJson("shared-cache-inventory.json");
    string[] catalog = ["typescript", "python", "php", "css", "shell", "csharp", "clangd", "java", "kotlin", "intellij", "ruby", "go", "rust", "scala", "r", "powershell", "swift", "dart"];
    var native = new HashSet<string>(["go", "dart"], StringComparer.Ordinal);
    var servers = Suite.Items(inventory, "servers");
    if (servers is null || servers.Count != catalog.Length || servers.Select(item => Suite.Text(item, "id")).Distinct().Count() != catalog.Length)
        throw new TaskFailure("The shared-cache inventory must classify every catalog server exactly once.");
    if (!catalog.All(id => servers.Any(item => Suite.Text(item, "id") == id))) throw new TaskFailure("The shared-cache inventory is missing a catalog server.");
    if (!servers.Where(item => Suite.Text(item, "probe") == "native").All(item => Suite.Text(item, "id") is { } id && native.Contains(id)))
        throw new TaskFailure("Every native shared-cache claim needs a runnable probe.");
    if (args.Contains("--inventory"))
    {
        foreach (var item in servers)
            Console.WriteLine($"{Suite.Js(Suite.Text(item, "id"))}: {Suite.Js(Suite.Text(item, "kind"))}{(Suite.Text(item, "probe") is { Length: > 0 } probe ? $" ({probe})" : "")}");
        return 0;
    }
    if (Option("--server") is not { } option) throw new TaskFailure("Explicitly select --server go,dart or use --inventory. Real integration is opt-in.");
    var selected = option.Split(',');
    var root = scratch ?? Suite.CacheDir("lsp-shared-cache", "run");
    var reportDirectory = At("artifacts", "lsp-shared-cache");
    var dotnet = Dotnet;
    if (!File.Exists(Suite.HostAssembly)) throw new TaskFailure($"Build Release first: {Suite.HostAssembly} is missing.");
    Directory.CreateDirectory(reportDirectory);
    Suite.TrapInterrupt();
    var outcomes = new List<JsonObject>();
    foreach (var server in selected)
    {
        var directory = Path.Combine(root, server + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5)));
        var outcome = new JsonObject { ["server"] = server, ["state"] = "blocked", ["detail"] = "No native cache reuse scenario is implemented for this provider" };
        try
        {
            Directory.CreateDirectory(directory);
            if (native.Contains(server))
            {
                var result = await NativeCase(directory, server, dotnet);
                outcome = new JsonObject { ["server"] = server };
                foreach (var (key, value) in result) outcome[key] = value?.DeepClone();
            }
        }
        catch (Exception error) { outcome = new JsonObject { ["server"] = server, ["state"] = "failed", ["detail"] = error.Message }; }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        outcomes.Add(outcome);
        Console.WriteLine($"{Suite.Text(outcome, "state")!.ToUpperInvariant()} {server}{(Suite.Text(outcome, "detail") is { Length: > 0 } detail ? ": " + detail : "")}");
    }
    foreach (var outcome in outcomes)
    {
        var file = new JsonObject { ["measuredAt"] = Suite.IsoNow() };
        foreach (var (key, value) in outcome) file[key] = value?.DeepClone();
        Suite.WriteJson(Path.Combine(reportDirectory, Suite.Text(outcome, "server") + ".json"), file);
    }
    Suite.WriteJson(Path.Combine(reportDirectory, "latest.json"),
        new JsonObject { ["measuredAt"] = Suite.IsoNow(), ["outcomes"] = new JsonArray(outcomes.Select(item => (JsonNode?)item.DeepClone()).ToArray()) });
    return outcomes.Any(item => Suite.Text(item, "state") != "passed") || Suite.Interrupted ? 1 : 0;
}

static async Task<JsonObject> NativeCase(string directory, string server, string dotnet)
{
    var scenario = server switch
    {
        "go" => new NativeScenario("fixture.go", "go.mod", "module example.test/sharedcache\n\ngo 1.25\n",
            "package fixture\nimport \"fmt\"\nfunc Shared() string { return fmt.Sprint(\"shared\") }\nvar Branch int = 1\n",
            "var Branch int = 1", "var Branch int = \"branch-b\"", "var Branch int = 2",
            new Dictionary<string, string?>
            {
                ["GOCACHE"] = Path.Combine(directory, "go-build"), ["GOMODCACHE"] = Path.Combine(directory, "go-modules"), ["GOPROXY"] = "off",
                ["GOTOOLCHAIN"] = "local", ["GOMAXPROCS"] = "2",
            },
            state => Path.Combine(state, "language", "cache", "go", "v0.23.0"), null,
            name => Regex.IsMatch(name, @"-(export|xrefs|typerefs|methodsets|analysis)\z")),
        "dart" => new NativeScenario("fixture.dart", "pubspec.yaml", "name: shared_cache_fixture\nenvironment:\n  sdk: \">=3.13.0 <4.0.0\"\n",
            "import \"dart:collection\";\nString Shared() => Queue<int>().toString();\nint branch = 1;\n",
            "int branch = 1", "int branch = \"branch-b\"", "int branch = 2",
            new Dictionary<string, string?> { ["DART_SUPPRESS_ANALYTICS"] = "true" },
            state => Path.Combine(state, "native-dart-cache"),
            state => ["language-server", "--protocol=lsp", "--cache", Path.Combine(state, "native-dart-cache")],
            name => !name.Contains("-temp-", StringComparison.Ordinal)),
        _ => throw new InvalidOperationException($"No native cache fixture for {server}"),
    };
    string[] workspaces = [Path.Combine(directory, "branch-a"), Path.Combine(directory, "branch-b")];
    var valid = scenario.Valid;
    var invalid = valid.Replace(scenario.Line, scenario.Broken, StringComparison.Ordinal);
    Directory.CreateDirectory(workspaces[0]);
    File.WriteAllText(Path.Combine(workspaces[0], scenario.Manifest), scenario.ManifestText);
    File.WriteAllText(Path.Combine(workspaces[0], scenario.Source), valid);
    var configuration = Path.Combine(directory, "fixture.gitconfig");
    File.WriteAllText(configuration, "[user]\nname = tereditor fixture\nemail = fixture@example.test\n[commit]\ngpgsign = false\n");
    var gitEnvironment = new Dictionary<string, string?> { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = configuration };
    void Git(params string[] arguments) => Suite.Command("git", arguments, workspaces[0], gitEnvironment, timeout: -1);
    Git("init", "-q", "-b", "branch-a");
    Git("add", ".");
    Git("commit", "-qm", "Fixture");
    Git("worktree", "add", "-qb", "branch-b", workspaces[1]);
    File.WriteAllText(Path.Combine(workspaces[1], scenario.Source), invalid);
    if (server == "dart")
    {
        var dart = Env("TEREDITOR_LSP_COMMAND_DART") ?? throw new InvalidOperationException("Set TEREDITOR_LSP_COMMAND_DART to the actual provider executable for the isolated cache probe");
        foreach (var workspace in workspaces)
            Suite.Command(dart, ["pub", "get", "--offline"], workspace, new Dictionary<string, string?> { ["DART_SUPPRESS_ANALYTICS"] = "true", ["PUB_CACHE"] = Path.Combine(directory, "pub-cache") }, timeout: -1, quiet: true);
    }
    var warmState = Path.Combine(directory, "warm-state");
    var coldState = Path.Combine(directory, "cold-state");
    CacheHost? host = null;
    JsonObject Symbols() => new() { ["operation"] = "symbols", ["path"] = scenario.Source };
    Task<CacheHost> Start(string state, string workspace) => CacheHost.Start(dotnet, state, workspace, server, scenario.Environment, scenario.Arguments);
    async Task<JsonObject> PrimeClean(string workspace)
    {
        if (server == "dart")
        {
            File.WriteAllText(Path.Combine(workspace, scenario.Source), invalid);
            await host!.Call(workspace, "lsp_navigate", Symbols());
            await host.Diagnostics(workspace, scenario.Source, true);
            File.WriteAllText(Path.Combine(workspace, scenario.Source), valid);
        }
        return await host!.Diagnostics(workspace, scenario.Source, false);
    }
    try
    {
        host = await Start(warmState, workspaces[0]);
        await host.Ready(workspaces[0]);
        var clean = await PrimeClean(workspaces[0]);
        var symbols = await host.Call(workspaces[0], "lsp_navigate", Symbols());
        Suite.Require(Suite.Items(symbols, "locations")?.Any(item => Suite.Text(item, "text")?.Contains("Shared", StringComparison.Ordinal) ?? false) ?? false,
            $"Real {server} returned no fixture symbol: {Suite.Head(symbols?.ToJsonString(Suite.Compact) ?? "null", 1500)}");
        await host.Stop(); host = null;
        var afterA = Snapshot(scenario.Cache(warmState));
        Suite.Require(afterA.Count > 0, $"Workspace A produced no native {server} cache");
        host = await Start(warmState, workspaces[1]);
        await host.Ready(workspaces[1]);
        var branchError = await host.Diagnostics(workspaces[1], scenario.Source, true);
        await host.Stop(); host = null;
        var afterWarmB = Snapshot(scenario.Cache(warmState));
        host = await Start(coldState, workspaces[1]);
        await host.Ready(workspaces[1]);
        await host.Diagnostics(workspaces[1], scenario.Source, true);
        await host.Stop(); host = null;
        var afterColdB = Snapshot(scenario.Cache(coldState));
        var reused = afterA.Where(pair => scenario.Semantic(pair.Key)
            && afterColdB.TryGetValue(pair.Key, out var cold) && cold.Hash == pair.Value.Hash
            && afterWarmB.TryGetValue(pair.Key, out var warm) && warm.Hash == pair.Value.Hash && warm.Modified == pair.Value.Modified).ToList();
        Suite.Require(reused.Count > 0, "No A-produced semantic cache records stayed unmodified during warm B while independently regenerated by cold B");
        host = await Start(warmState, workspaces[0]);
        await host.Ready(workspaces[0]); await PrimeClean(workspaces[0]); await host.Ready(workspaces[1]);
        await host.Diagnostics(workspaces[0], scenario.Source, false);
        await host.Diagnostics(workspaces[1], scenario.Source, true);
        File.WriteAllText(Path.Combine(workspaces[1], scenario.Source), valid.Replace(scenario.Line, scenario.Fixed, StringComparison.Ordinal));
        await host.Diagnostics(workspaces[1], scenario.Source, false);
        await host.Diagnostics(workspaces[0], scenario.Source, false);
        return new JsonObject
        {
            ["state"] = "passed",
            ["evidence"] = new JsonObject
            {
                ["workspaces"] = new JsonArray(workspaces.Select(item => (JsonNode?)JsonValue.Create(Path.GetFileName(item))).ToArray()),
                ["nativeRecordsAfterA"] = afterA.Count, ["nativeRecordsAfterWarmB"] = afterWarmB.Count, ["nativeRecordsAfterColdB"] = afterColdB.Count,
                ["reusedRecords"] = new JsonArray(reused.Select(pair => (JsonNode?)new JsonObject { ["name"] = pair.Key, ["sha256"] = pair.Value.Hash }).ToArray()),
                ["diagnostics"] = new JsonObject { ["clean"] = clean, ["branchError"] = branchError },
                ["concurrentBranchIsolation"] = true, ["invalidationAfterEdit"] = true,
            },
        };
    }
    finally
    {
        if (host is not null) await host.Stop();
    }
}

// Every file under a cache directory (none when it is missing): its SHA-256 and modification time, by relative path. Links are skipped.
static Dictionary<string, (string Hash, string Modified)> Snapshot(string directory)
{
    var result = new Dictionary<string, (string Hash, string Modified)>(StringComparer.Ordinal);
    void Visit(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) continue;
            if (entry is DirectoryInfo child) Visit(child.FullName);
            else if (entry is FileInfo file)
                result[Path.GetRelativePath(directory, file.FullName)] = (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.FullName))), file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
    }
    Visit(directory);
    return result;
}

/// <summary>A native cache probe: the fixture file and manifest, the line that branch B breaks and the edit then fixes, and where the provider keeps its cache.</summary>
sealed record NativeScenario(string Source, string Manifest, string ManifestText, string Valid, string Line, string Broken, string Fixed,
    IReadOnlyDictionary<string, string?> Environment, Func<string, string> Cache, Func<string, string[]>? Arguments, Func<string, bool> Semantic);

/// <summary>A headless host for one probe step: one server, isolated state, numbered MCP requests.</summary>
sealed class CacheHost
{
    readonly HostProcess process;
    readonly McpClient mcp;
    readonly string server;

    CacheHost(HostProcess process, McpClient mcp, string server)
    {
        this.process = process;
        this.mcp = mcp;
        this.server = server;
    }

    public static async Task<CacheHost> Start(string dotnet, string state, string workspace, string server, IReadOnlyDictionary<string, string?> environment, Func<string, string[]>? launchArguments)
    {
        Directory.CreateDirectory(Path.Combine(state, "language"));
        var variable = $"TEREDITOR_LSP_COMMAND_{server.ToUpperInvariant()}";
        var command = Tasks.Env(variable);
        Suite.Require(launchArguments is null || command is not null, $"Set {variable} to the actual provider executable for the isolated cache probe");
        var entry = command is not null ? new JsonObject { ["command"] = command } : new JsonObject { ["managed"] = server != "dart" };
        if (launchArguments is not null) entry["arguments"] = new JsonArray(launchArguments(state).Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
        File.WriteAllText(Path.Combine(state, "language", "servers.json"), new JsonObject { [server] = entry }.ToJsonString(Suite.Compact));
        var token = McpClient.NewToken();
        var variables = new Dictionary<string, string?>(environment)
        {
            ["TEREDITOR_ROOT"] = workspace, ["TEREDITOR_STATE_DIR"] = state, ["TEREDITOR_BIND"] = "127.0.0.1", ["TEREDITOR_PORT"] = "0", ["TEREDITOR_TOKEN"] = token,
            ["TEREDITOR_HEADLESS_MCP"] = "1", ["TEREDITOR_LSP_DISABLE"] = "0",
        };
        var process = HostProcess.Start(dotnet, variables, 100_000);
        try
        {
            var mcp = new McpClient(await process.Endpoint(30_000, 50, "Headless host startup failed", stopOnInterrupt: true), token, numberRequests: true, errorPrefix: "MCP error: ");
            await mcp.Rpc("initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "tereditor-shared-cache-checks", ["version"] = "1" },
            });
            return new CacheHost(process, mcp, server);
        }
        catch
        {
            await process.Stop();
            throw;
        }
    }

    public Task Stop() => process.Stop();

    public async Task<JsonNode?> Call(string directory, string name, JsonObject arguments)
    {
        var result = await mcp.Tool(name, directory, arguments);
        var parts = Suite.Items(result, "content")?.Where(item => Suite.Text(item, "type") == "text").Select(item => Suite.Text(item, "text"));
        var text = parts is null ? "" : string.Join("\n", parts);
        Suite.Require(text.Length > 0, $"Missing MCP result for {name}");
        try { return JsonNode.Parse(text); }
        catch (JsonException) { throw new InvalidOperationException($"{name}: {Suite.Head(text, 1800)}"); }
    }

    public async Task<JsonNode?> Ready(string directory)
    {
        var result = await Call(directory, "lsp_status", new JsonObject { ["operation"] = "retry", ["server"] = server });
        var deadline = Environment.TickCount64 + 180_000;
        while (Suite.Text(result, "state") is "installing" or "initializing" && Environment.TickCount64 < deadline && !Suite.Interrupted)
        {
            await Task.Delay(200);
            result = await Call(directory, "lsp_status", new JsonObject { ["operation"] = "status", ["server"] = server });
        }
        Suite.Require(Suite.Text(result, "state") == "ready", $"{server} did not become ready: {Suite.Js(Suite.Text(result, "state"))}: {Suite.Js(Suite.Text(result, "detail"))}");
        return result;
    }

    /// <summary>Polls for up to 90 seconds until <paramref name="file"/> has a current publication with errors exactly when <paramref name="expectError"/>.</summary>
    public async Task<JsonObject> Diagnostics(string directory, string file, bool expectError)
    {
        var deadline = Environment.TickCount64 + 90_000;
        JsonNode? result;
        do
        {
            result = await Call(directory, "lsp_diagnostics", new JsonObject { ["paths"] = new JsonArray(JsonValue.Create(file)) });
            var files = Suite.Items(result, "files");
            var publication = files?.FirstOrDefault(item => Suite.Text(item, "path") is { } path && (path == file || path.EndsWith("/" + file, StringComparison.Ordinal)));
            var diagnostics = Suite.Items(result, "diagnostics")?.ToList() ?? files?.SelectMany(item => Suite.Items(item, "diagnostics") ?? new JsonArray()).ToList() ?? [];
            var errors = diagnostics.Where(item => Suite.Text(item, "kind") == "1").ToList();
            if (Suite.Text(result, "state") == "ready" && publication is not null && Suite.Text(publication, "freshness") is "current" or "unversioned" && (errors.Count > 0) == expectError)
                return new JsonObject
                {
                    ["state"] = Suite.Text(result, "state"), ["freshness"] = Suite.Text(publication, "freshness"),
                    ["errors"] = new JsonArray(errors.Select(item => (JsonNode?)JsonValue.Create(Suite.Text(item, "text"))).ToArray()),
                };
            await Task.Delay(200);
        } while (Environment.TickCount64 < deadline && !Suite.Interrupted);
        throw new InvalidOperationException($"Expected {(expectError ? "errors" : "clean diagnostics")} in {directory}: {Suite.Head(result?.ToJsonString(Suite.Compact) ?? "null", 1800)}");
    }
}
