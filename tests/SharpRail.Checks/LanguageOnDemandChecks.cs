using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
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

        // A server the user stopped explicitly stays stopped and the answer names the exact call that starts it.
        await rig.State.Languages.ExecuteAsync(new(workspace, Operation: "stop", Server: "python"));
        var stopped = await rig.Call("lsp_diagnostics", new { paths = new[] { "agent.py" }, workspace });
        var stoppedFile = stopped["files"]![0]!;
        Require(stoppedFile["state"]!.ToString() == "skipped" && stoppedFile["detail"]!.ToString().Contains("\"operation\":\"retry\"", StringComparison.Ordinal), "A stopped server was not reported with the call that starts it: " + stopped);

        // A disabled server says so.
        await rig.State.ChangeAsync([HostStateChange.Setting("language-server:typescript", JsonSerializer.Serialize(new LanguageServerOptions(Disabled: true), JsonSerializerOptions.Web))]);
        await rig.State.Languages.ExecuteAsync(new(workspace, "code.ts", "stop"));
        var disabledScan = await rig.Call("lsp_diagnostics", new { paths = new[] { "code.ts" }, workspace });
        Require(disabledScan["files"]![0]!["detail"]!.ToString().Contains("Disabled", StringComparison.Ordinal), "A disabled server was not reported as disabled: " + disabledScan);
        await rig.State.ChangeAsync([HostStateChange.Setting("language-server:typescript", JsonSerializer.Serialize(rig.Fake, JsonSerializerOptions.Web))]);
        await rig.State.Languages.ExecuteAsync(new(workspace, "code.ts", "retry"));

        // A symbol query without a path searches the workspace; a missing path names the working call.
        var search = await rig.Call("lsp_navigate", new { operation = "definition", query = "target", workspace });
        Require(search["state"]!.ToString() == "ready" && search["locations"]!.AsArray().Count > 0 && search["detail"]!.ToString().Contains("searched workspace symbols", StringComparison.Ordinal), "A query without a path did not search the workspace: " + search);
        var rejected = (await McpServer.HandleAsync(JsonNode.Parse(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 99, method = "tools/call", @params = new { name = "lsp_navigate", arguments = new { operation = "definition", workspace } } })), rig.Tools, default)).Body!.ToJsonString();
        Require(rejected.Contains("Working call", StringComparison.Ordinal) && rejected.Contains("src/Foo.cs", StringComparison.Ordinal), "A missing path did not name the working call: " + rejected);

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
        Console.WriteLine("PASS language on demand: tools start the owning servers, report stopped and disabled servers actionably and never hang");
    }
}