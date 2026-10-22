using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;

namespace SharpRail.Checks;

/// <summary>
/// Agents never have to start language servers by hand: the tools start the owning server, wait a bounded time, report honestly and never hang.
/// </summary>
internal static class LanguageOnDemandChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Rig(string root) : IAsyncDisposable
    {
        internal string Workspace = Path.Combine(root, "workspace");
        internal HostStateStore State = null!;
        internal IReadOnlyList<McpServer.McpTool> Tools = null!;
        private string? disabled;
        private int next;
        internal LanguageServerOptions Fake = null!;

        internal async Task StartAsync()
        {
            Directory.CreateDirectory(Workspace);
            await File.WriteAllTextAsync(Path.Combine(Workspace, "code.ts"), "😀 target");
            await File.WriteAllTextAsync(Path.Combine(Workspace, "agent.py"), "broken = missing");
            disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
            State = new HostStateStore(Path.Combine(root, "state"));
            var executable = Environment.ProcessPath!;
            string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
            Fake = new LanguageServerOptions(Command: executable, Arguments: arguments);
            foreach (var id in new[] { "typescript", "python" })
                await State.ChangeAsync([HostStateChange.Setting("language-server:" + id, JsonSerializer.Serialize(Fake, JsonSerializerOptions.Web))]);
            Tools = LanguageMcpTools.Create(State, Workspace);
        }

        internal async Task<JsonNode> Call(string tool, object arguments)
        {
            var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++next, method = "tools/call", @params = new { name = tool, arguments } });
            var (_, body) = await McpServer.HandleAsync(JsonNode.Parse(message), Tools, default);
            return JsonNode.Parse(body!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        }

        internal async Task<LanguageServerState> Row(string id)
            => (await State.Languages.ExecuteAsync(new(Workspace, Operation: "servers"))).Servers.Single(item => item.Id == id);

        public async ValueTask DisposeAsync()
        {
            await State.StopLanguagesAsync();
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled);
        }
    }

    internal static async Task Run(string fixture)
    {
        var root = Path.Combine(fixture, "language-on-demand");
        await using var rig = new Rig(root);
        await rig.StartAsync();
        var workspace = rig.Workspace;

        // Nothing is running; diagnostics start both servers in parallel instead of skipping the files.
        Require((await rig.Row("typescript")).State == "idle" && (await rig.Row("python")).State == "idle", "The fixture started with a running server.");
        var scan = await rig.Call("lsp_diagnostics", new { paths = new[] { "code.ts", "agent.py" }, workspace });
        Require(scan["files"]!.AsArray().All(file => file!["state"]!.ToString() == "checked") && scan["summary"]!["skipped"]!.GetValue<int>() == 0,
            "Diagnostics skipped a file because its server was not running: " + scan);
        Require((await rig.Row("typescript")).State == "ready" && (await rig.Row("python")).State == "ready", "Diagnostics did not leave the started servers running.");

        var listed = await rig.Call("lsp_status", new { operation = "servers", workspace });
        Require(listed["servers"]!.AsArray().Where(row => row!["id"]!.ToString() is "typescript" or "python").All(row => row!["startedBy"]!.ToString() == "agent"), "lsp_status did not say that an agent started the servers: " + listed);

        var typescriptUse = listed["servers"]!.AsArray().Single(row => row!["id"]!.ToString() == "typescript")!["usage"];
        Require(typescriptUse is not null && typescriptUse["calls"]!.GetValue<int>() == 1 && typescriptUse["successes"]!.GetValue<int>() == 1 && typescriptUse["lastUsed"]!.ToString().Length > 0, "lsp_status did not count the agent's call: " + listed);

        // A server the user stopped explicitly stays stopped and the answer names the exact call that starts it.
        await rig.State.Languages.ExecuteAsync(new(workspace, Operation: "stop", Server: "python"));
        var stopped = await rig.Call("lsp_diagnostics", new { paths = new[] { "agent.py" }, workspace });
        var stoppedFile = stopped["files"]![0]!;
        Require(stopped["hint"]!.ToString().Contains("agent.py", StringComparison.Ordinal) && stopped["hint"]!.ToString().Contains("retry", StringComparison.Ordinal), "A skipped file carried no one-line hint: " + stopped);
        Require((await rig.Row("python")).Usage is { Calls: 2 }, "The pane's usage summary does not count both calls.");
        Require(stoppedFile["state"]!.ToString() == "skipped" && stoppedFile["detail"]!.ToString().Contains("\"operation\":\"retry\"", StringComparison.Ordinal), "A stopped server was not reported with the call that starts it: " + stopped);


        // A symbol query without a path searches the workspace; a missing path names the working call.
        var search = await rig.Call("lsp_navigate", new { operation = "symbols", query = "target", workspace });
        Require(search["state"]!.ToString() == "ready" && search["locations"]!.AsArray().Count > 0 && search["detail"]!.ToString().Contains("searched workspace symbols", StringComparison.Ordinal), "A query without a path did not search the workspace: " + search);
        // A positional operation named by a symbol runs at its declaration through the tool, with no position.
        var named = await rig.Call("lsp_navigate", new { operation = "references", query = "target", workspace });
        Require(named["state"]!.ToString() == "ready" && named["locations"]!.AsArray().Count > 0 && named["detail"]!.ToString().Contains("Resolved \"target\" to code.ts", StringComparison.Ordinal), "References by name did not resolve the declaration: " + named);
        // A server missing for an unrelated language (pwsh, go) never turns another server's answer into a failed call.
        Require(LanguageUsage.CombinedState("workspaceSymbols", ["ready", "error", "error"]) == "ready" && LanguageUsage.CombinedState("workspaceSymbols", ["error", "error"]) == "error"
            && LanguageUsage.CombinedState("diagnostics", ["ready", "error"]) == "error", "A symbol search answered by one server was reported as failed because another server is missing.");
        var navigate = rig.Tools.Single(tool => tool.Name == "lsp_navigate");
        foreach (var phrase in new[] { "{\"operation\":\"references\",\"query\":", "zero-based", "one-based", "ambiguous", "instead of grep" })
            Require(navigate.Description.Contains(phrase, StringComparison.Ordinal), "lsp_navigate's description lacks: " + phrase);
        var rejected = (await McpServer.HandleAsync(JsonNode.Parse(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 99, method = "tools/call", @params = new { name = "lsp_navigate", arguments = new { operation = "definition", workspace } } })), rig.Tools, default)).Body!.ToJsonString();
        Require(rejected.Contains("Working call", StringComparison.Ordinal) && rejected.Contains("a symbol name as query", StringComparison.Ordinal) && rejected.Contains("zero-based", StringComparison.Ordinal), "A missing path did not name the working call: " + rejected);

        // A server that hangs cannot hang the tool: the call returns an initializing status at its deadline.
        await rig.State.Languages.ExecuteAsync(new(workspace, Operation: "restart", Server: "typescript", Configuration: """{"settings":{"fixtureDie":{"hang":true}}}"""));
        using (var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            while ((await rig.State.Languages.ExecuteAsync(new(workspace, Operation: "status", Server: "typescript"), ready.Token)).State != "ready") await Task.Delay(30, ready.Token);
        await Task.Delay(500);
        var previous = LanguageMcpTools.ToolDeadline;
        LanguageMcpTools.ToolDeadline = TimeSpan.FromSeconds(2);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var hung = await rig.Call("lsp_navigate", new { path = "code.ts", operation = "hover", line = 0, column = 3, workspace });
            Require(watch.Elapsed < TimeSpan.FromSeconds(30) && hung["state"]!.ToString() == "initializing" && hung["detail"]!.ToString().Contains("deadline", StringComparison.Ordinal) && hung["detail"]!.ToString().Contains("elapsed", StringComparison.Ordinal),
                "A hanging server hung or was not reported as initializing: " + hung);
        }
        finally { LanguageMcpTools.ToolDeadline = previous; }
        // The counts survive a host restart and stay bounded.
        await rig.State.StopLanguagesAsync();
        var reopened = new HostStateStore(Path.Combine(root, "state"));
        try
        {
            var again = (await reopened.Languages.ExecuteAsync(new(workspace, Operation: "servers"))).Servers.Single(row => row.Id == "python");
            Require(again.Usage is { Calls: >= 2 } && new FileInfo(Path.Combine(root, "state", "language", "usage.json")).Length < 100_000, "Usage counts were lost or unbounded after a restart.");
        }
        finally { await reopened.StopLanguagesAsync(); }
        Console.WriteLine("PASS language on demand: tools start the owning servers, report stopped and disabled servers actionably and never hang");
    }

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(30);
        }
    }

    /// <summary>
    /// Language servers live as long as their workspace: project autoStart starts one in every workspace including the Default and later worktrees,
    /// removing a workspace stops only its own, workspace_create starts the requested servers in the background, and nothing stops a server for being unused.
    /// </summary>
    internal static async Task Lifetime(string fixture)
    {
        var root = Path.Combine(fixture, "language-lifetime");
        using var git = new IsolatedGit(Path.Combine(root, "git"));
        var repo = IsolatedGit.Repository(Path.Combine(root, "project"), ("code.ts", "const target = 1;"));
        Directory.CreateDirectory(Path.Combine(repo, ".sharprail"));
        await File.WriteAllTextAsync(Path.Combine(repo, ".sharprail", "language.toml"), "version = 1\n\n[servers.typescript]\nautoStart = true\n");
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        var state = new HostStateStore(Path.Combine(root, "state"));
        try
        {
            var executable = Environment.ProcessPath!;
            string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
            await state.ChangeAsync([HostStateChange.Setting("language-server:typescript", JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            IProjectServices host = new LocalProjectAdapter(new ProjectServices(repo, state));
            var removals = new System.Collections.Concurrent.ConcurrentDictionary<string, LanguageExit>();
            state.Languages.ExitObserved = (workspace, _, exit) => removals[workspace] = exit;
            async Task<LanguageServerState> Row(string workspace) => (await host.LanguageAsync(new(workspace, Operation: "servers"))).Servers.Single(item => item.Id == "typescript");
            async Task<bool> Ready(string workspace) => (await Row(workspace)).State == "ready";

            await state.ChangeAsync([HostStateChange.OpenProject(repo)]);
            try { await Until(() => Ready(repo), "the Default workspace's configured server"); }
            catch (TimeoutException) { Console.WriteLine("DEBUG projects=" + string.Join(",", state.Current.Projects) + " repo=" + repo + " row=" + (await Row(repo)).State + " prepare=" + (await host.LanguageAsync(new(repo, Operation: "prepareAgent"))).Detail + " " + (await Row(repo)).State); throw; }
            Require((await Row(repo)).StartedBy == "config", "The configured server does not say that the project configuration started it.");
            var first = (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo, "First")))!;
            var second = (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo, "Second")))!;
            await Until(() => Ready(first.Path), "the first worktree's configured server");
            await Until(() => Ready(second.Path), "the second worktree's configured server");

            // Removing one workspace stops only its own server, with the reason.
            await host.ApplyWorkspaceActionAsync(WorkspaceAction.ForceRemove(first.Id));
            await Until(() => Task.FromResult(removals.ContainsKey(first.Path)), "the removed workspace's server to stop");
            Require(removals[first.Path] is { Unexpected: false } stopped && stopped.Reason.Contains("workspace removed", StringComparison.Ordinal) && stopped.StartedBy == "config", "Removal did not stop the server with its reason: " + removals[first.Path].Reason);
            Require(await Ready(second.Path) && await Ready(repo), "Removing a workspace stopped another workspace's server.");

            // workspace_create starts the requested servers in the background and reports their states.
            var create = new WorkspaceMcpTools(state).Create(repo);
            var created = await create.Call(new JsonObject { ["description"] = "Needs TypeScript", ["languageServers"] = new JsonArray("auto", "no-such-language") }, default);
            var reply = JsonNode.Parse(created.Text)!;
            var requested = reply["languageServers"]!.AsArray().ToDictionary(row => row!["server"]!.ToString(), row => row!["state"]!.ToString());
            Require(!created.Error && requested["typescript"] is "starting" or "ready" && requested["no-such-language"] == "failed", "workspace_create did not report the requested servers: " + created.Text);
            var path = reply["path"]!.ToString();
            await Until(() => Ready(path), "the server requested by workspace_create");
            Require((await Row(path)).StartedBy is "config" or "workspace creation", "The server's origin is missing.");
            Console.WriteLine("PASS language server lifetime: autoStart in every workspace, removal stops only its own, workspace_create starts and reports servers");
        }
        finally
        {
            await state.StopLanguagesAsync();
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled);
        }
    }
}