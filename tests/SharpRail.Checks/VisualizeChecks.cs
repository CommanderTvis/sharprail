using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.UI.Kit.Visualization;
using SharpRail.Plugins.Visualize;
using SharpRail.Plugins.Visualize.Host;

namespace SharpRail.Checks;

internal static class VisualizeChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static VisualizeParams Diagram(string? mermaid, string? title = null) => new(VisualizationType.Diagram) { Mermaid = mermaid, Title = title };

    private static VisualizeParams Comparison(params string[] names) => new(VisualizationType.Comparison) { Options = [.. names.Select(name => new ComparisonOption(name))] };

    private sealed class Memory : VisualizationStore.IPersistence
    {
        private Dictionary<string, Dictionary<string, TerminalVisualization>> value = [];
        public Dictionary<string, Dictionary<string, TerminalVisualization>> Read() =>
            value.ToDictionary(entry => entry.Key, entry => entry.Value.ToDictionary());
        public void Write(Dictionary<string, Dictionary<string, TerminalVisualization>> next) => value = next;
    }

    internal static async Task Host(string root)
    {
        Shapes();
        ComparisonArgs();
        var first = (VisualizeHost)BuiltinPlugins.All.Single(entry => entry.Manifest.Id == VisualizeContract.Id).Host!;
        var second = (VisualizeHost)BuiltinPlugins.All.Single(entry => entry.Manifest.Id == VisualizeContract.Id).Host!;
        first.Store.Record("isolation", "t1", Diagram("graph TD;A;"));
        Require(second.Store.Get("isolation", "t1") is null, "Each host runtime receives an independent builtin visualization store.");
        await Store();
        await Module(Path.Combine(root, "visualize-host"));
        await Transport(Path.Combine(root, "visualize-transports"));
        Console.WriteLine("PASS visualize host: shape validation, revisions, render reports, rollback, session persistence and local/remote channels");
    }

    // validate.test.ts
    private static void Shapes()
    {
        Require(Diagram("graph LR; A-->B").ShapeError() is null, "A diagram with mermaid is accepted.");
        Require(Diagram(null).ShapeError()?.Contains("mermaid", StringComparison.Ordinal) == true, "A diagram without mermaid is refused.");
        Require(Diagram("   ").ShapeError()?.Contains("mermaid", StringComparison.Ordinal) == true, "A diagram with blank mermaid is refused.");
        Require(Comparison("A", "B").ShapeError() is null, "A comparison with named options is accepted.");
        Require(Comparison().ShapeError()?.Contains("options", StringComparison.Ordinal) == true, "A comparison with no options is refused.");
        Require(Comparison("").ShapeError()?.Contains("options[0].name", StringComparison.Ordinal) == true, "A comparison option missing a name is refused.");
    }

    // store.test.ts
    private static async Task Store()
    {
        var w1 = new TerminalRef("w1", "t1");
        {
            var store = new VisualizationStore();
            var first = store.Record("w1", "t1", Diagram("graph TD;A-->B;"));
            Require(first is { Revision: 1, Title: "Diagram" }, "A first drawing is revision 1, titled by its type.");
            var second = store.Record("w1", "t1", Diagram("graph TD;A-->C;", "Flow"));
            Require(second is { Revision: 2, Title: "Flow" }, "A rewrite counts a revision and takes its title.");
            Require(store.Get("w1", "t1")?.Revision == 2 && store.Get("w1", "t2") is null && store.Get("w2", "t1") is null, "Drawings are kept per terminal.");
        }
        {
            var store = new VisualizationStore();
            var pushes = new List<VisualizationsChanged>();
            store.Connect(pushes.Add, null);
            store.Record("w1", "t1", Diagram("graph TD;A;"));
            store.Record("w1", "t2", Comparison("A"));
            store.Record("w2", "t1", Diagram("graph TD;B;"));
            Require(pushes.Take(2).Select(push => string.Join(",", push.Visualizations.Keys.Order())).SequenceEqual(["t1", "t1,t2"]),
                "Every rewrite publishes the whole workspace's map.");
            Require(pushes[1].Visualizations["t2"].Title == "Comparison" && store.ForWorkspace("w1").Visualizations.Keys.Order().SequenceEqual(["t1", "t2"]),
                "A workspace's map enumerates only that workspace's terminals.");
        }
        {
            var store = new VisualizationStore();
            var bad = await store.RunTool(w1, new VisualizeParams(VisualizationType.Diagram));
            Require(bad.IsError && bad.Text.Contains("mermaid", StringComparison.Ordinal) && store.Get("w1", "t1") is null, "The tool validates before it draws.");
            var drawing = store.RunTool(w1, Diagram("graph TD;A-->B;", "Wired"));
            store.ReportRender("w1", "t1", 1, null);
            var drawn = await drawing;
            Require(!drawn.IsError && drawn.Text.Contains("Rendered \"Wired\" in SharpRail (revision 1)", StringComparison.Ordinal) && store.Get("w1", "t1")?.Title == "Wired",
                "The tool draws and says how to update: " + drawn.Text);

            // A diagram the renderer refuses is a tool error, the last good one stands, and the next attempt reuses the revision.
            var broken = store.RunTool(w1, Diagram("graph TD;A--", "Broken"));
            store.ReportRender("w1", "t1", 2, "Parse error on line 1");
            var answer = await broken;
            Require(answer.IsError && answer.Text.Contains("Parse error on line 1", StringComparison.Ordinal) && answer.Text.Contains("call visualize again", StringComparison.Ordinal),
                "A refused diagram answers the parse error.");
            Require(store.Get("w1", "t1")?.Title == "Wired", "A refused drawing is rolled back to the last that rendered.");
            var better = store.RunTool(w1, Diagram("graph TD;A-->B;", "Better"));
            store.ReportRender("w1", "t1", 2, null);
            Require(!(await better).IsError && store.Get("w1", "t1")?.Title == "Better", "The next attempt reuses the revision the pane never showed.");
        }
        {
            var store = new VisualizationStore();
            store.Connect(payload => store.ReportRender(payload.WorkspaceId, "t1", payload.Visualizations["t1"].Revision, null), null);
            Require(!(await store.RunTool(w1, Comparison("A")).WaitAsync(TimeSpan.FromSeconds(1))).IsError,
                "A comparison reported synchronously during publication resolves without waiting for timeout.");
            store.Connect(payload => store.ReportRender(payload.WorkspaceId, "t1", payload.Visualizations["t1"].Revision, "Immediate parse error"), null);
            Require((await store.RunTool(w1, Diagram("broken")).WaitAsync(TimeSpan.FromSeconds(1))).IsError && store.Get("w1", "t1")?.Revision == 1,
                "An immediate renderer refusal is returned and restores the previous revision.");
        }
        {
            var store = new VisualizationStore();
            using var cancel = new CancellationTokenSource();
            var abandoned = store.RunTool(w1, Diagram("graph TD;A;"), cancel.Token);
            await cancel.CancelAsync();
            try { await abandoned; throw new InvalidOperationException("The render wait ignored cancellation."); }
            catch (OperationCanceledException) { }
            store.ReportRender("w1", "t1", 1, "late error");
            var next = store.RunTool(w1, Comparison("A"));
            store.ReportRender("w1", "t1", 2, null);
            Require(!(await next.WaitAsync(TimeSpan.FromSeconds(1))).IsError, "Cancellation removes only its own pending verdict.");
        }
        {
            // No client watching is not a failure: the wait times out and the drawing stands.
            var store = new VisualizationStore(TimeSpan.FromMilliseconds(50));
            var unwatched = await store.RunTool(w1, Diagram("graph TD;A;", "Alone"));
            Require(!unwatched.IsError && store.Get("w1", "t1")?.Title == "Alone", "An unwatched drawing stands.");
        }
        {
            var store = new VisualizationStore();
            store.Record("w1", "t1", Diagram("graph TD;A;"));
            store.Record("w2", "t1", Diagram("graph TD;B;"));
            store.Forget("w1");
            Require(store.Get("w1", "t1") is null && store.Get("w2", "t1")?.Revision == 1, "Forgetting a workspace drops its views and no other's.");
        }
        {
            var store = new VisualizationStore();
            var pushes = new List<VisualizationsChanged>();
            store.Connect(null, (_, tab) => tab == "t1" ? "sess-1" : null, new Memory());
            store.Record("w1", "t1", Diagram("graph TD;A;", "Kept"));
            store.Connect(pushes.Add, (_, tab) => tab == "t1" ? "sess-1" : null);
            Require(store.AdoptForSession("w1", "t9", "sess-1")?.Title == "Kept" && store.Get("w1", "t9")?.Title == "Kept" && pushes.Count == 1,
                "A resumed conversation reclaims its drawing in whatever tab it lands in.");
            Require(store.AdoptForSession("w1", "t9", "sess-1") is null && pushes.Count == 1, "Re-adopting a revision the tab holds is a no-op.");
            Require(store.AdoptForSession("w1", "t9", "sess-unknown") is null, "An unknown session adopts nothing.");
        }
        {
            var store = new VisualizationStore();
            store.Connect(null, (_, _) => "sess-2", new Memory());
            store.Record("w2", "t1", Diagram("graph TD;B;"));
            Require(store.AdoptForSession("w2", "t2", "sess-2")?.Revision == 1, "A session's drawing is persisted.");
            store.Forget("w2");
            Require(store.Get("w2", "t1") is null && store.AdoptForSession("w2", "t3", "sess-2") is null, "A workspace's drawings are forgotten on disk as well as in memory.");
        }
    }

    // index.test.ts, through the real host runtime: the tool on a terminal's MCP table, the report and get methods,
    // the changed channel, adoption on an agent record change, and forgetting a removed workspace.
    private static async Task Module(string directory)
    {
        Directory.CreateDirectory(directory);
        IPluginHostContext? agents = null;
        var removed = new List<string>();
        var recorder = new Recorder(context =>
        {
            agents = context;
            context.OnWorkspace(change => { if (change is WorkspaceRemoved gone) lock (removed) removed.Add(gone.Id); });
        });
        var state = new HostStateStore(directory);
        await using var runtime = new PluginRuntime(new PluginHostSeams
        {
            StateDirectory = directory,
            State = state,
            Builtins = [(VisualizeContract.Manifest, new VisualizeHost()), (new PluginManifest("agents", "Agents", "puzzle-2-line", "1", PluginApi.Generation, 1) { EnabledByDefault = true }, recorder)]
        });
        await runtime.Start();
        Require((await runtime.ListAsync()).Single(entry => entry.Id == "visualize") is { Status: PluginStatus.Active, Origin: PluginOrigin.Builtin, Label: "Visualize" },
            "The visualize plugin is a builtin, on by default.");

        var w1 = Path.Combine(directory, "w1");
        var tool = runtime.McpTools(new TerminalRef(w1, "t1"), w1).Single(tool => tool.Name == "visualize");
        Require(tool.InputSchema["properties"]?["type"]?["enum"]?.AsArray().Select(value => value!.GetValue<string>()).SequenceEqual(["diagram", "comparison"]) == true &&
            tool.InputSchema["properties"]?["mermaid"]?["description"]?.GetValue<string>().StartsWith("Required when type='diagram'", StringComparison.Ordinal) == true &&
            tool.Description.Contains("Calling again replaces this terminal's view in place", StringComparison.Ordinal),
            "The tool's schema carries the type enum and the field descriptions the agent reads: " + tool.InputSchema.ToJsonString());

        using var stop = new CancellationTokenSource();
        var pushes = Channel.CreateUnbounded<object?>();
        var stream = runtime.SubscribeAsync(new(VisualizeContract.Id, "changed", new VisualizationsQuery(w1), "client"), stop.Token);
        var listening = Task.Run(async () =>
        {
            try { await foreach (var push in stream) pushes.Writer.TryWrite(push); }
            catch (OperationCanceledException) { }
        });

        var drawing = tool.Call(JsonNode.Parse("""{"type":"diagram","title":"Wired","mermaid":"graph TD;A-->B;"}""")!.AsObject(), CancellationToken.None);
        var pushed = PluginJson.Convert<VisualizationsChanged>(await pushes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Require(pushed.WorkspaceId == w1 && pushed.Visualizations["t1"] is { Title: "Wired", Revision: 1 } &&
            JsonElement.DeepEquals(pushed.Visualizations["t1"].Args, JsonDocument.Parse("""{"type":"diagram","title":"Wired","mermaid":"graph TD;A-->B;"}""").RootElement),
            "Drawing publishes the workspace's map, the arguments verbatim: " + JsonSerializer.Serialize(pushed, PluginJson.Options));
        Require(PluginJson.Convert<Ack>(await runtime.CallAsync(new(VisualizeContract.Id, "report", new RenderReport(w1, "t1", 1), "client"))).Ok, "The report method acknowledges.");
        var drawn = await drawing.WaitAsync(TimeSpan.FromSeconds(10));
        Require(!drawn.Error && drawn.Text.Contains("revision 1", StringComparison.Ordinal), "The verdict resolves the tool call: " + drawn.Text);
        async Task<VisualizationsChanged> Get() =>
            PluginJson.Convert<VisualizationsChanged>(await runtime.CallAsync(new(VisualizeContract.Id, "get", new VisualizationsQuery(w1), "client")));
        var get = await Get();
        Require(get.Visualizations.Single().Key == "t1" && get.Visualizations["t1"].Title == "Wired", "The get method reads the workspace's map back.");
        var nowhere = await runtime.McpTools(null, w1).Single(tool => tool.Name == "visualize").Call(JsonNode.Parse("""{"type":"diagram","mermaid":"graph TD;A;"}""")!.AsObject(), CancellationToken.None);
        Require(nowhere.Error && nowhere.Text.Contains("only available from a terminal", StringComparison.Ordinal), "Outside a terminal the tool refuses.");

        // The tab that drew reports its session: the drawing is bound to it, and another tab resuming that session adopts it.
        agents!.SetAgentRecord(new TerminalRef(w1, "t1"), new TerminalAgentRecord("claude", "claude") { SessionId = "sess-1" });
        agents.SetAgentRecord(new TerminalRef(w1, "t9"), new TerminalAgentRecord("claude", "claude --resume sess-1") { SessionId = "sess-1" });
        get = await Get();
        Require(get.Visualizations.Keys.Order().SequenceEqual(["t1", "t9"]) && get.Visualizations["t9"] is { Title: "Wired", Revision: 1 },
            "A resumed session adopts its drawing when the terminal reports its agent.");
        Require(File.Exists(Path.Combine(directory, PluginIdentity.StateFile(VisualizeContract.Id, "visualizations"))), "Drawings persist under the plugin's state.");

        // Removing a workspace forgets its drawings. Snapshots coalesce, so the list is published until a removal is seen.
        bool Removed() { lock (removed) return removed.Contains(w1); }
        for (var attempt = 0; attempt < 100 && !Removed(); attempt++)
        {
            state.PublishWorkspaces(directory, [w1]);
            await Task.Delay(50);
            state.PublishWorkspaces(directory, [], removed: w1);
            await Task.Delay(50);
        }
        Require(Removed() && (await Get()).Visualizations.Count == 0, "Removing a workspace forgets its drawings.");
        await stop.CancelAsync();
        await listening;
    }

    private sealed class Recorder(Action<IPluginHostContext> activated) : PluginHostModule
    {
        public override PluginContract Contract { get; } = PluginContract.Create("agents", 1, [], []);
        public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
        {
            activated(context);
            return ValueTask.FromResult<PluginDisposer?>(null);
        }
    }

    private static async Task Transport(string directory)
    {
        Directory.CreateDirectory(directory);
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);
        var state = Path.Combine(directory, "state");
        IPluginHostContext? agents = null;
        var module = new VisualizeHost();
        await using (var server = RemoteServer.Create(workspace, IPAddress.Loopback, 0, "visualize-check", state,
            plugins: seams => seams with
            {
                Builtins = [(VisualizeContract.Manifest, module),
                (new PluginManifest("agents", "Agents", "puzzle", "1", PluginApi.Generation, 1) { EnabledByDefault = true }, new Recorder(context => agents = context))]
            }))
        {
            await server.StartAsync();
            try
            {
                var runtime = server.Services.GetRequiredService<PluginRuntime>();
                var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var remote = new RemotePluginAdapter(address, "visualize-check");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (agents is null) await Task.Delay(10, timeout.Token);
                foreach (var plugins in new IPluginService[] { new LocalPluginAdapter(runtime), remote })
                {
                    var tab = plugins == remote ? "remote" : "local";
                    var changed = Channel.CreateUnbounded<VisualizationsChanged>();
                    using var stop = new CancellationTokenSource();
                    var listening = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var payload in plugins.SubscribeAsync(new(VisualizeContract.Id, "changed", new VisualizationsQuery(workspace), tab), stop.Token))
                                changed.Writer.TryWrite(PluginJson.Convert<VisualizationsChanged>(payload));
                        }
                        catch (OperationCanceledException) { }
                    });
                    try
                    {
                        while (!changed.Reader.TryRead(out _))
                        {
                            module.Store.Record(workspace, "readiness", Diagram("graph TD;Ready;"));
                            await Task.Delay(10, timeout.Token);
                        }
                        while (changed.Reader.TryRead(out _)) { }
                        async Task<VisualizationsChanged> Read(int revision)
                        {
                            while (true)
                            {
                                var payload = await changed.Reader.ReadAsync(timeout.Token);
                                if (payload.Visualizations.TryGetValue(tab, out var value) && value.Revision == revision) return payload;
                            }
                        }
                        var initial = PluginJson.Convert<VisualizationsChanged>(await plugins.CallAsync(new(VisualizeContract.Id, "get", new VisualizationsQuery(workspace), tab), timeout.Token));
                        Require(initial.WorkspaceId == workspace && !initial.Visualizations.ContainsKey(tab), "The state snapshot hydrates the correct workspace.");
                        var terminal = new TerminalRef(workspace, tab);
                        agents!.SetAgentRecord(terminal, new TerminalAgentRecord("claude", "claude") { SessionId = tab + "-session" });
                        var tool = runtime.McpTools(terminal, workspace).Single(tool => tool.Name == "visualize");
                        var drawing = tool.Call(JsonNode.Parse("""{"type":"comparison","title":"Persisted","options":[{"name":"A","pros":["fast"],"recommended":true}]}""")!.AsObject(), timeout.Token);
                        var pushed = await Read(1);
                        Require(pushed.Visualizations[tab] is { Revision: 1, Title: "Persisted" } &&
                            pushed.Visualizations[tab].Args.GetProperty("options")[0].GetProperty("pros")[0].GetString() == "fast",
                            "The adapter streams the full drawing arguments.");
                        await plugins.CallAsync(new(VisualizeContract.Id, "report", new RenderReport(workspace, tab, 99) { Error = "wrong revision" }, tab), timeout.Token);
                        Require(!drawing.IsCompleted, "A report for another revision does not settle the drawing.");
                        await plugins.CallAsync(new(VisualizeContract.Id, "report", new RenderReport(workspace, tab, 1), tab), timeout.Token);
                        Require(!(await drawing.WaitAsync(TimeSpan.FromSeconds(2))).Error, "A local or remote report settles its exact revision.");
                        var broken = tool.Call(JsonNode.Parse("""{"type":"diagram","mermaid":"broken","title":"Broken"}""")!.AsObject(), timeout.Token);
                        var next = await Read(2);
                        Require(next.Visualizations[tab].Revision == 2, "A rewrite advances the revision over the channel.");
                        await plugins.CallAsync(new(VisualizeContract.Id, "report", new RenderReport(workspace, tab, 2) { Error = "Parse error" }, tab), timeout.Token);
                        Require((await broken.WaitAsync(TimeSpan.FromSeconds(2))).Error, "A remote renderer refusal is a tool error.");
                        var restored = await Read(1);
                        Require(restored.Visualizations[tab] is { Revision: 1, Title: "Persisted" }, "Rollback republishes the whole workspace map.");
                        var replacement = tool.Call(JsonNode.Parse("""{"type":"comparison","title":"Restart me","options":[{"name":"B"}]}""")!.AsObject(), timeout.Token);
                        var reused = await Read(2);
                        Require(reused.Visualizations[tab].Revision == 2, "A new attempt reuses the refused revision.");
                        await plugins.CallAsync(new(VisualizeContract.Id, "report", new RenderReport(workspace, tab, 2), tab), timeout.Token);
                        Require(!(await replacement.WaitAsync(TimeSpan.FromSeconds(2))).Error, "A replacement succeeds after rollback.");
                        var snapshot = PluginJson.Convert<VisualizationsChanged>(await plugins.CallAsync(new(VisualizeContract.Id, "get", new VisualizationsQuery(workspace), tab), timeout.Token));
                        Require(snapshot.Visualizations[tab].Title == "Restart me" && snapshot.Visualizations.Keys.Count(key => key != "readiness") == (tab == "remote" ? 2 : 1), "The get snapshot agrees with the streamed workspace map.");
                    }
                    finally { await stop.CancelAsync(); await listening; }
                }
            }
            finally { await server.StopAsync(); }
        }
        agents = null;
        await using var restarted = RemoteServer.Create(workspace, IPAddress.Loopback, 0, "visualize-check", state,
            plugins: seams => seams with
            {
                Builtins = [(VisualizeContract.Manifest, new VisualizeHost()),
                (new PluginManifest("agents", "Agents", "puzzle", "1", PluginApi.Generation, 1) { EnabledByDefault = true }, new Recorder(context => agents = context))]
            });
        await restarted.StartAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (agents is null) await Task.Delay(10, timeout.Token);
            agents.SetAgentRecord(new TerminalRef(workspace, "resumed"), new TerminalAgentRecord("claude", "claude --resume remote-session") { SessionId = "remote-session" });
            var address = new Uri(restarted.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remote = new RemotePluginAdapter(address, "visualize-check");
            var snapshot = PluginJson.Convert<VisualizationsChanged>(await remote.CallAsync(new(VisualizeContract.Id, "get", new VisualizationsQuery(workspace), "restart"), timeout.Token));
            Require(snapshot.Visualizations.Single() is { Key: "resumed", Value: { Title: "Restart me", Revision: 2 } },
                "After host restart an agent session reclaims its persisted drawing in a new terminal, without restoring stale tab identities.");
        }
        finally { await restarted.StopAsync(); }
    }

    // args.test.ts
    private static void ComparisonArgs()
    {
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
        Require(VisualizationArgs.ComparisonOptions(default).Count == 0 && VisualizationArgs.ComparisonOptions(Json("\"nope\"")).Count == 0, "A non-array is no options.");
        var all = VisualizationArgs.ComparisonOptions(Json("""[{"name":"A","description":"d","pros":["p"],"cons":["c"],"recommended":true,"mermaid":"graph LR;A-->B"}]""")).Single();
        Require(all is { Name: "A", Description: "d", Recommended: true, Mermaid: "graph LR;A-->B" } && all.Pros.SequenceEqual(["p"]) && all.Cons.SequenceEqual(["c"]), "Every field is read when present.");
        var bare = VisualizationArgs.ComparisonOptions(Json("""[{"name":"B"}]""")).Single();
        Require(bare is { Name: "B", Description: null, Recommended: false, Mermaid: null, Pros.Count: 0, Cons.Count: 0 }, "Missing fields default safely.");
        var loose = VisualizationArgs.ComparisonOptions(Json("""[{"name":"C","pros":["ok",3,null],"recommended":"yes"}]""")).Single();
        Require(loose.Pros.SequenceEqual(["ok"]) && !loose.Recommended, "Non-string pros are dropped and recommended must be true itself.");
        var kept = VisualizationArgs.ComparisonOptions(Json("""[null,5,{"name":"D"}]"""));
        Require(kept.Count == 3 && kept[0].Name == "" && kept[2].Name == "D", "Non-object entries keep their positions as empty options.");
    }
}