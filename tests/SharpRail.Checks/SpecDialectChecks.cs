using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.SpecDialect;
using SharpRail.UI.Docking;

namespace SharpRail.Checks;

/// <summary>
/// The spec dialect plugin's own checks, translated from the fork's <c>packages/plugin-spec-dialect</c> tests: the host
/// half's graph read, eviction and MCP tools, local and over gRPC; the UI half's tree, store and sync; and the
/// hot-toggle, document-link and rail translations through the real workbench.
/// </summary>
internal static class SpecDialectChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Timed out: " + message);
            await Task.Delay(20);
        }
    }

    private static void WriteSpec(string root, string relative, string frontmatter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relative))!);
        File.WriteAllText(Path.Combine(root, relative), $"---\n{frontmatter}\n---\n\n## Body\n\nProse.\n");
    }

    private static SpecGraphNode Node(string id, string? parent = null, string? title = null) =>
        new(id, "module-design", title ?? id, id + "/SPEC.md", [], [], [], []) { Parent = parent };

    public static async Task Run(string root)
    {
        var directory = Path.Combine(root, "spec-dialect");
        await Host(directory);
        await GitIgnored(Path.Combine(directory, "git-ignored"));
        await SpecToolChecks.Run(directory);
        await Remote(Path.Combine(directory, "remote"));
        Tree();
        Store();
        await Sync();
        Migration();
    }

    // A repository's ignored folders (fixtures, build output) can hold thousands of specs that are not its own; the index
    // lists what Git does not ignore, untracked files included, and keeps no file text.
    private static async Task GitIgnored(string directory)
    {
        using var git = new E2E.IsolatedGit(Path.Combine(directory, "git"));
        var project = E2E.IsolatedGit.Repository(Path.Combine(directory, "project"), (".gitignore", "fixtures/\n"));
        WriteSpec(project, "SPEC.md", "id: tracked\ntype: goal-and-requirements");
        E2E.IsolatedGit.Run(project, "add", "-A");
        E2E.IsolatedGit.Run(project, "commit", "-m", "spec");
        WriteSpec(project, "draft/SPEC.md", "id: untracked\ntype: module-design");
        WriteSpec(project, "fixtures/sample/SPEC.md", "id: ignored\ntype: module-design");
        var state = new HostStateStore(Path.Combine(directory, "state"));
        await using var runtime = new PluginRuntime(new()
        {
            StateDirectory = null,
            State = state,
            Builtins = [(SpecDialectManifest.Manifest, new SpecDialectHost())],
            Worktrees = (_, _) => Task.FromResult<IReadOnlyList<WorktreeInfo>>([new(project, "main", true, false)])
        });
        await runtime.Start();
        await state.ChangeAsync([HostStateChange.OpenProject(project)]);
        var nodes = PluginJson.Convert<SpecGraphSnapshot>(await runtime.CallAsync(new(SpecDialectContract.Id, SpecDialectContract.Graph.Name, new SpecGraphParams(project), "client"))).Nodes;
        Require(nodes.Select(node => node.Id).Order().SequenceEqual(["tracked", "untracked"]),
            "The spec index skips Git-ignored folders and keeps untracked specs: " + string.Join(",", nodes.Select(node => node.Id)));
    }

    private static async Task Host(string directory)
    {
        var project = Path.Combine(directory, "project");
        var worktree = Path.Combine(directory, "worktree");
        WriteSpec(project, "SPEC.md", "id: root\ntype: goal-and-requirements\ntitle: Root\nstatus: active\ntags: [v1]");
        WriteSpec(project, "module-a/SPEC.md", "id: mod-a\ntype: module-design\nparent: root\ndepends-on: [root]\nreferences:\n  - other");
        File.WriteAllText(Path.Combine(project, "README.md"), "# not a spec\n");
        WriteSpec(worktree, "SPEC.md", "id: wt\ntype: goal-and-requirements\ntitle: Worktree");
        var state = new HostStateStore(Path.Combine(directory, "state"));
        var trees = new Dictionary<string, IReadOnlyList<WorktreeInfo>>
        {
            [project] = [new(project, "main", true, false), new(worktree, "feature", false, false)]
        };
        await using var runtime = new PluginRuntime(new()
        {
            StateDirectory = null,
            State = state,
            Builtins = [(SpecDialectManifest.Manifest, new SpecDialectHost())],
            Worktrees = (path, _) => Task.FromResult(trees.GetValueOrDefault(path) ?? [])
        });
        await runtime.Start();
        Require((await runtime.ListAsync()).Single() is { Id: "spec-dialect", Status: PluginStatus.Active, Origin: PluginOrigin.Builtin },
            "The spec dialect is a builtin plugin, on by default.");
        await state.ChangeAsync([HostStateChange.OpenProject(project)]);
        async Task<SpecGraphSnapshot> Graph(string workspace) =>
            PluginJson.Convert<SpecGraphSnapshot>(await runtime.CallAsync(new(SpecDialectContract.Id, SpecDialectContract.Graph.Name, new SpecGraphParams(workspace), "client")));

        var nodes = (await Graph(project)).Nodes;
        Require(nodes.Select(node => node.Id).Order().SequenceEqual(["mod-a", "root"]), "The graph maps the worktree's spec files: " + string.Join(",", nodes.Select(node => node.Id)));
        var rootNode = nodes.Single(node => node.Id == "root");
        Require(rootNode is { Title: "Root", Path: "SPEC.md", Status: "active", Parent: null } && rootNode.Tags.SequenceEqual(["v1"]),
            "A node carries its frontmatter; absent status and parent are null.");
        var module = nodes.Single(node => node.Id == "mod-a");
        Require(module is { Status: null, Parent: "root" } && module.Title == module.Id && module.DependsOn.SequenceEqual(["root"]) &&
            module.References.SequenceEqual(["other"]) && module.Path == Path.Combine("module-a", "SPEC.md"),
            "Lists read in flow and block form; an untitled spec takes its id.");
        Console.WriteLine("PASS fork plugin-spec-dialect host: graph maps the worktree's spec files");

        try { await Graph(Path.Combine(directory, "ghost")); throw new InvalidOperationException("An unknown workspace was read."); }
        catch (PluginCallException error) when (error.Error == PluginCallError.Failed && error.Message.Contains("Unknown workspace", StringComparison.Ordinal)) { }
        Console.WriteLine("PASS fork plugin-spec-dialect host: graph rejects an unknown workspace");

        Require((await Graph(worktree)).Nodes.Single().Id == "wt", "A worktree workspace reads its own tree.");
        trees[project] = [new(project, "main", true, false)];
        // The runtime watches the latest snapshot, so a listing and its removal published together can coalesce.
        await Until(async () =>
        {
            state.ChangeWorkspaces(current => current.Where(workspace => workspace.ProjectRoot != project)
                .Concat(new[] { project, worktree }.Select(path => new WorkspaceRecord(Guid.NewGuid().ToString(), project,
                    path == project ? WorkspaceKinds.Default : WorkspaceKinds.Managed, path, "main", "main"))));
            await Task.Delay(50);
            state.ChangeWorkspaces(current => current.Where(workspace => workspace.Path != worktree));
            await Task.Delay(50);
            try { await Graph(worktree); return false; }
            catch (PluginCallException error) when (error.Message.Contains("Unknown workspace", StringComparison.Ordinal)) { return true; }
        }, "a removed workspace's cached index is evicted");
        Console.WriteLine("PASS fork plugin-spec-dialect host: removing a workspace evicts its cached index");

        var tools = runtime.McpTools(new TerminalRef(project, "tab"), project);
        Require(tools.Select(tool => tool.Name).SequenceEqual(["spec_grep", "spec_get", "spec_graph", "spec_create", "spec_update", "spec_delete", "spec_validate"]), "The spec tools join the MCP table: " + string.Join(",", tools.Select(tool => tool.Name)));
        Require(tools[0].InputSchema["properties"]?["pattern"]?["description"] is not null && tools[0].InputSchema["required"]!.AsArray().Single()!.GetValue<string>() == "pattern",
            "A tool's schema carries its parameter descriptions: " + tools[0].InputSchema.ToJsonString());
        var got = await tools[1].Call(new JsonObject { ["id"] = "mod-a" }, CancellationToken.None);
        Require(!got.Error && got.Text.StartsWith("mod-a [module-design]", StringComparison.Ordinal) && got.Text.Contains("parent -> root (SPEC.md)", StringComparison.Ordinal) && got.Text.Contains("references -> other (missing)", StringComparison.Ordinal),
            "spec_get reads the terminal's workspace: " + got.Text);
        var grep = await tools[0].Call(new JsonObject { ["pattern"] = "PROSE", ["type"] = "goal-and-requirements" }, CancellationToken.None);
        Require(!grep.Error && grep.Text.StartsWith("1 match(es):", StringComparison.Ordinal) && grep.Text.EndsWith("\nSPEC.md:11: Prose.", StringComparison.Ordinal),
            "spec_grep matches case-insensitively, narrowed by type: " + grep.Text);
        var invalid = await tools[1].Call(new JsonObject { ["wrong"] = true }, CancellationToken.None);
        Require(invalid.Error && invalid.Text.StartsWith("Invalid arguments for spec_get", StringComparison.Ordinal), "A schema mismatch is an error result: " + invalid.Text);
        var missing = await tools[1].Call(new JsonObject { ["id"] = "nope" }, CancellationToken.None);
        Require(missing.Error, "An unknown spec id is an error result.");
        await state.ChangeAsync([HostStateChange.PluginEnabled(SpecDialectContract.Id, false)]);
        await runtime.Schedule();
        Require(runtime.McpTools(null, project).Count == 0, "A disabled spec dialect contributes no spec tools.");
        Console.WriteLine("PASS fork plugin-spec-dialect host: the spec tools register on the MCP surface, validated, and leave with the plugin");
    }

    private static async Task Remote(string directory)
    {
        var project = Path.Combine(directory, "project");
        WriteSpec(project, "SPEC.md", "id: remote-root\ntype: goal-and-requirements\ntitle: Remote root");
        await using var server = RemoteServer.Create(project, IPAddress.Loopback, 0, "spec-dialect");
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var state = new RemoteStateAdapter(address, "spec-dialect");
            using var plugins = new RemotePluginAdapter(address, "spec-dialect");
            await state.ChangeAsync([HostStateChange.OpenProject(project)]);
            await Until(async () => (await plugins.ListAsync()).Any(entry => entry is { Id: "spec-dialect", Status: PluginStatus.Active }), "the remote spec dialect");
            var remote = PluginJson.Convert<SpecGraphSnapshot>(await plugins.CallAsync(new(SpecDialectContract.Id, "graph", new SpecGraphParams(project), "client")));
            var local = PluginJson.Convert<SpecGraphSnapshot>(await server.Services.GetRequiredService<PluginRuntime>()
                .CallAsync(new(SpecDialectContract.Id, "graph", new SpecGraphParams(project), "client")));
            Require(remote.Nodes.Single().Id == "remote-root" && JsonSerializer.Serialize(remote, PluginJson.Options) == JsonSerializer.Serialize(local, PluginJson.Options),
                "The graph over gRPC equals the host's in-process answer.");
            var runtime = server.Services.GetRequiredService<PluginRuntime>();
            async Task<JsonNode?> Mcp(string method, JsonObject? parameters = null)
            {
                var (_, body) = await McpServer.HandleAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = method,
                    ["params"] = parameters
                }, runtime.McpTools(null, project));
                return body;
            }
            await state.ChangeAsync([HostStateChange.PluginEnabled(SpecDialectContract.Id, false)]);
            await Until(async () => (await plugins.ListAsync()).Single(entry => entry.Id == SpecDialectContract.Id).Status == PluginStatus.Disabled,
                "the remote spec dialect disables");
            var listed = await Mcp("tools/list");
            Require(!listed!["result"]!["tools"]!.AsArray().Any(tool => tool!["name"]!.GetValue<string>().StartsWith("spec_", StringComparison.Ordinal)),
                "Disabling Specs over gRPC withdraws all seven spec tools from MCP listings.");
            var refused = await Mcp("tools/call", new JsonObject { ["name"] = "spec_graph" });
            Require(refused!["error"]!["code"]!.GetValue<int>() == -32602, "MCP refuses a disabled spec tool.");
            var initialized = await Mcp("initialize");
            Require(!initialized!["result"]!["instructions"]!.GetValue<string>().Contains("spec", StringComparison.OrdinalIgnoreCase),
                "MCP initialization never imposes a spec workflow when Specs is off.");
            await state.ChangeAsync([HostStateChange.PluginEnabled(SpecDialectContract.Id, true)]);
            await Until(async () => (await plugins.ListAsync()).Single(entry => entry.Id == SpecDialectContract.Id).Status == PluginStatus.Active,
                "the remote spec dialect re-enables");
            Require(runtime.McpTools(null, project).Count(tool => tool.Name.StartsWith("spec_", StringComparison.Ordinal)) == 7,
                "Re-enabling Specs restores all seven MCP tools.");
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS spec dialect graph: local and gRPC parity");
    }

    private static void Tree()
    {
        Require(SpecTree.RoleLabel("goal-and-requirements") == "Goal" && SpecTree.RoleLabel("architecture-design") == "Architecture" &&
            SpecTree.RoleLabel("module-design") == "Module" && SpecTree.RoleLabel("submodule-design") == "Submodule" && SpecTree.RoleLabel("task-spec") == "Task" &&
            SpecTree.RoleLabel("risk_register") == "Risk Register" && SpecTree.RoleLabel("---") == "Spec" && SpecTree.RoleTag("architecture-design") == "ARCH" &&
            SpecTree.RoleTag("module-design") == "MODULE" && SpecTree.RoleTag("risk_register") == "RISK REGISTER", "Spec roles humanize known and extension-defined types.");
        Console.WriteLine("PASS fork specTree.test.ts: humanizes known and extension-defined spec roles");
        Require(SpecTree.DisplayTitle("ACP client — spawn, negotiate, translate") == "ACP client · spawn, negotiate, translate" &&
            SpecTree.DisplayTitle("meta – the ThinkRail extension namespace") == "meta · the ThinkRail extension namespace" &&
            SpecTree.DisplayTitle("browser↔host wire") == "browser↔host wire" && SpecTree.DisplayTitle("e2e/workflows — harness") == "e2e/workflows · harness",
            "Dashed titles render with a compact dot separator.");
        Console.WriteLine("PASS fork specTree.test.ts: renders dashed titles with a compact dot separator");
        var tree = SpecTree.Build([Node("z-root", title: "Z root"), Node("root", title: "A root"), Node("b-child", "root", "B child"), Node("a-child", "root", "A child")]);
        Require(tree.Select(item => item.Node.Id).SequenceEqual(["root", "z-root"]) && tree[0].Children.Select(item => item.Node.Id).SequenceEqual(["a-child", "b-child"]) &&
            tree[1].Children.Count == 0, "Roots and siblings nest under their parent and sort by title.");
        Console.WriteLine("PASS fork specTree.test.ts: nests children under their parent");
        Require(SpecTree.Build([Node("orphan", "nope"), Node("plain")]).Select(item => item.Node.Id).Order().SequenceEqual(["orphan", "plain"]),
            "A dangling or absent parent renders as a root.");
        var self = SpecTree.Build([Node("selfie", "selfie")]);
        Require(self.Single().Node.Id == "selfie" && self.Single().Children.Count == 0, "A self-parenting node renders as a root.");
        var cycle = SpecTree.Build([Node("a", "b"), Node("b", "a"), Node("root")]);
        Require(cycle[0].Node.Id == "root" && cycle[0].Children.Count == 0 && cycle.Count == 1,
            "A parent cycle has no tree root; well-formed roots remain intact.");
        Console.WriteLine("PASS fork specTree.test.ts: title ordering, dangling/self parents and parent-cycle handling match the fork");
    }

    private static void Store()
    {
        Require(!SpecStore.SameGraph(null, []) && SpecStore.SameGraph([Node("x")], [Node("x")]) &&
            !SpecStore.SameGraph([Node("x")], [Node("x") with { Status = "active" }]) && !SpecStore.SameGraph([Node("x")], []),
            "sameSpecGraph compares by value, not by reference.");
        var store = new SpecStore();
        var changes = 0;
        store.Changed += _ => changes++;
        store.SetSpecs("w1", [Node("x")]);
        var first = store.Specs("w1");
        store.SetSpecs("w1", [Node("x")]);
        Require(ReferenceEquals(store.Specs("w1"), first) && changes == 1, "An unchanged re-read keeps the previous list and raises nothing.");
        store.SetSpecs("w1", [Node("x") with { Status = "active" }]);
        Require(!ReferenceEquals(store.Specs("w1"), first) && changes == 2, "A changed graph replaces the list.");
        store.SetSpecs("other", []);
        store.SetFailed("w1", true);
        store.SetFailed("other", false);
        store.Evict("w1");
        Require(store.Specs("w1") is null && !store.Failed("w1") && store.Specs("other") is { Count: 0 }, "Eviction drops one workspace's graph and failed flag, not its siblings'.");
        Console.WriteLine("PASS fork store.test.ts: value comparison, kept identity on an unchanged read, per-workspace eviction");
    }

    private static async Task Sync()
    {
        var store = new SpecStore();
        store.SetFailed("w1", true);
        await new SpecSync(_ => ValueTask.FromResult(new SpecGraphSnapshot([])), store).LoadAsync("w1");
        Require(store.Specs("w1") is { Count: 0 } && !store.Failed("w1"), "A load populates the store and clears a prior failure.");
        await new SpecSync(_ => throw new InvalidOperationException("boom"), store).LoadAsync("w2");
        Require(store.Failed("w2"), "A rejected read marks the workspace failed.");
        var calls = 0;
        var gate = new TaskCompletionSource();
        var sync = new SpecSync(async _ => { calls++; await gate.Task; return new SpecGraphSnapshot([]); }, store);
        sync.Sync("w1", 1);
        sync.Sync("w1", 1);
        Require(calls == 1, "Concurrent syncs of one workspace at one revision collapse into one read.");
        gate.SetResult();
        await Until(() => Task.FromResult(store.Specs("w1") is not null), "the collapsed read");
        sync.Sync("w1", 2);
        Require(calls == 2, "A sync at an advanced revision reads again.");
        Console.WriteLine("PASS fork specSync.test.ts: load, failure, collapse per revision, and a fresh read once the revision advances");
    }

    private static void Migration()
    {
        var current = DockState.Preset("balanced");
        current.Workspaces["/w"] = new() { Selected = { [current.Groups.Single(group => group.Tools.Any(tab => tab.Id == DockState.SpecsTool)).Id] = DockState.SpecsTool } };
        current.ToolRestorePositions[DockState.SpecsTool] = 0;
        var legacy = JsonSerializer.Deserialize<DockState>(JsonSerializer.Serialize(current).Replace(DockState.SpecsTool, "specs"))!;
        Require(!LayoutSession.IsValid(legacy), "A layout naming the legacy core tool is not valid before migration.");
        legacy.MigrateLegacyTools();
        Require(LayoutSession.IsValid(legacy) && JsonSerializer.Serialize(legacy) == JsonSerializer.Serialize(current),
            "The legacy specs tool id migrates to the spec dialect's in tools, selections and restore positions.");
        Console.WriteLine("PASS fork LEGACY_LAYOUT_TOOL_IDS: a persisted specs tab reads as plugin:spec-dialect:specs");
    }

    /// <summary>The headless workbench translations, run once Avalonia is up.</summary>
    public static void RunUi(string root)
    {
        HotToggle(Path.Combine(root, "spec-dialect-toggle"));
        OffRailMovesOn(Path.Combine(root, "spec-dialect-off"));
        SpecsPanelTree(Path.Combine(root, "spec-dialect-panel"));
        RetryAfterFailedLoad(Path.Combine(root, "spec-dialect-load-failure"));
        RetryAfterFailedUpdate(Path.Combine(root, "spec-dialect-update-failure"));
    }

    /// <summary>
    /// The fork's <c>proxyOneSpecGraphFailure</c>: the next <see cref="Failures"/> spec graph reads are answered with a
    /// failure instead of reaching the host, as the fork's WebSocket route does; every other call passes through.
    /// </summary>
    private sealed class OneGraphFailure(IPluginService inner) : IPluginService
    {
        public int Failures;
        public bool Injected;
        public int Reads;
        public ValueTask<IReadOnlyList<PluginRosterEntry>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public ValueTask<IReadOnlyList<PluginRosterEntry>> RescanAsync(CancellationToken cancellationToken = default) => inner.RescanAsync(cancellationToken);
        public ValueTask<IReadOnlyList<PluginRosterEntry>> RetryAsync(string id, CancellationToken cancellationToken = default) => inner.RetryAsync(id, cancellationToken);
        public IAsyncEnumerable<object?> SubscribeAsync(PluginSubscription subscription, CancellationToken cancellationToken = default) => inner.SubscribeAsync(subscription, cancellationToken);
        public ValueTask<byte[]?> ReadFileAsync(string id, string path, CancellationToken cancellationToken = default) => inner.ReadFileAsync(id, path, cancellationToken);

        public ValueTask<object?> CallAsync(PluginCallRequest request, CancellationToken cancellationToken = default)
        {
            if (request.PluginId == SpecDialectContract.Id && request.Method == SpecDialectContract.Graph.Name)
            {
                Interlocked.Increment(ref Reads);
                if (Failures > 0)
                {
                    Failures--;
                    Injected = true;
                    return ValueTask.FromException<object?>(new PluginCallException(PluginCallError.Failed, "injected spec failure"));
                }
            }
            return inner.CallAsync(request, cancellationToken);
        }
    }

    private static TreeViewItem? SpecRow(E2eWorkspace app, string path) =>
        app.Window.GetLogicalDescendants().OfType<TreeView>().SingleOrDefault(tree => tree.Name == "SpecsTree")?
            .GetLogicalDescendants().OfType<TreeViewItem>().SingleOrDefault(node => Equals(node.Tag, path));

    private static Border SpecsError(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<Border>().Single(border => border.Name == "SpecsError");

    private static bool Skeleton(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<Border>().Any(border => border.Name?.StartsWith("SpecSkeleton", StringComparison.Ordinal) == true);

    private static string ErrorText(E2eWorkspace app) =>
        string.Concat(SpecsError(app).GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));

    private static void SpecsPanelTree(string root)
    {
        using var app = new E2eWorkspace(root, openFiles: false, prepare: _ => WriteSpec(root, "module-a/SPEC.md",
            "id: sample-module\ntype: module-design\ntitle: Sample Module\nparent: sample-root"));
        app.Click(app.Find<Button>("Tab_plugin_spec-dialect_specs"));
        E2eWorkspace.Until(() => SpecRow(app, "SPEC.md") is not null && SpecRow(app, "module-a/SPEC.md") is not null);
        var rootRow = SpecRow(app, "SPEC.md")!;
        var child = SpecRow(app, "module-a/SPEC.md")!;
        Require(child.Parent == rootRow, "The module nests under the main spec.");
        static TextBlock Role(TreeViewItem row) => ((Control)row.Header!).GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "SpecRole");
        Require(Role(rootRow).Text == "Main spec" && Role(child).Text == "MODULE", "The main spec and module carry their role tags.");
        Require(!Role(rootRow).IsVisible && !Role(child).IsVisible, "Role tags stay hidden until hovered or focused.");
        Require(!app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "SpecsRetry" && button.IsEffectivelyVisible),
            "A successful read offers no Retry and no manual refresh.");

        app.Click((Control)rootRow.Header!);
        E2eWorkspace.Until(() => app.Tabs.Any(tab => tab.Path == "SPEC.md") && rootRow.IsSelected);
        rootRow.IsExpanded = false;
        E2eWorkspace.Until(() => !child.IsEffectivelyVisible);
        rootRow.IsExpanded = true;
        E2eWorkspace.Until(() => child.IsEffectivelyVisible);
        app.Click((Control)child.Header!);
        E2eWorkspace.Until(() => app.Tabs.Any(tab => tab.Path == "module-a/SPEC.md") && child.IsSelected && !rootRow.IsSelected);
        // A focused child shows its own tag only: focus inside a tree item must not reveal every ancestor's.
        child.Focus();
        E2eWorkspace.Until(() => child.IsFocused);
        Require(Role(child).IsVisible && !Role(rootRow).IsVisible, "Focusing a spec shows its own role tag, never its parents'.");

        WriteSpec(root, "module-b/SPEC.md", "id: sample-module-b\ntype: module-design\ntitle: Sample Module B\nparent: sample-root");
        WriteSpec(root, "module-a/submodule/SPEC.md", "id: sample-submodule\ntype: submodule-design\ntitle: Sample Submodule\nparent: sample-module");
        E2eWorkspace.Until(() => SpecRow(app, "module-b/SPEC.md") is not null && SpecRow(app, "module-a/submodule/SPEC.md") is not null);
        var submodule = SpecRow(app, "module-a/submodule/SPEC.md")!;
        Require(SpecRow(app, "module-b/SPEC.md")!.Parent == SpecRow(app, "SPEC.md") && submodule.Parent == SpecRow(app, "module-a/SPEC.md")
            && Role(submodule).Text == "SUBMODULE", "Specs added mid-session appear at their depth without a manual refresh.");
        Console.WriteLine("PASS fork plugins/spec-dialect/specs-panel.spec.ts: the Specs tab renders the spec tree, opens a spec as an editor tab and follows specs added mid-session");
    }

    private static void RetryAfterFailedLoad(string root)
    {
        OneGraphFailure? failure = null;
        // A fresh profile's rail default reads the graph too, so the read fails until Retry rather than once.
        using var app = new E2eWorkspace(root, openFiles: false,
            prepare: _ => WriteSpec(root, "module-a/SPEC.md", "id: sample-module\ntype: module-design\ntitle: Sample Module\nparent: sample-root"),
            plugins: inner => failure = new OneGraphFailure(inner) { Failures = int.MaxValue });
        app.Click(app.Find<Button>("Tab_plugin_spec-dialect_specs"));
        E2eWorkspace.Until(() => SpecsError(app).IsVisible);
        Require(ErrorText(app).Contains("Couldn't load specs.", StringComparison.Ordinal) && SpecRow(app, "SPEC.md") is null && !Skeleton(app),
            "A failed first read says it couldn't load, with no tree and no skeleton rows.");
        var reads = failure!.Reads;
        failure.Failures = 0;
        app.Click(app.Find<Button>("SpecsRetry"));
        E2eWorkspace.Until(() => failure.Reads > reads && !SpecsError(app).IsVisible && SpecRow(app, "SPEC.md") is not null);
        Require(failure.Injected, "The failure was injected.");
        Console.WriteLine("PASS fork plugins/spec-dialect/specs-panel.spec.ts: Specs offers Retry only after its automatic graph read fails");
    }

    private static void RetryAfterFailedUpdate(string root)
    {
        OneGraphFailure? failure = null;
        using var app = new E2eWorkspace(root, openFiles: false,
            prepare: _ => WriteSpec(root, "module-a/SPEC.md", "id: sample-module\ntype: module-design\ntitle: Sample Module\nparent: sample-root"),
            plugins: inner => failure = new OneGraphFailure(inner));
        app.Click(app.Find<Button>("Tab_plugin_spec-dialect_specs"));
        E2eWorkspace.Until(() => SpecRow(app, "SPEC.md") is not null);
        failure!.Failures = int.MaxValue;
        WriteSpec(root, "module-a/retry.md", "id: sample-retry\ntype: module-design\ntitle: Retry Sample\nparent: sample-root");
        // The panel is judged once the failed read has settled, not at the first frame its error shows.
        E2eWorkspace.Until(() => SpecsError(app).IsVisible && !Skeleton(app) && ErrorText(app).Contains("Couldn't update specs.", StringComparison.Ordinal));
        Require(ErrorText(app).Contains("Couldn't update specs.", StringComparison.Ordinal) && SpecRow(app, "SPEC.md") is not null
            && SpecRow(app, "module-a/retry.md") is null && !Skeleton(app), "A failed update keeps the previous tree, without the new spec or skeleton rows.");
        var reads = failure.Reads;
        failure.Failures = 0;
        app.Click(app.Find<Button>("SpecsRetry"));
        E2eWorkspace.Until(() => failure.Reads > reads && !SpecsError(app).IsVisible && SpecRow(app, "module-a/retry.md") is not null);
        Require(failure.Injected && SpecRow(app, "SPEC.md") is not null, "Retry replaces the kept tree with the fresh read.");
        Console.WriteLine("PASS fork plugins/spec-dialect/specs-panel.spec.ts: a failed automatic Specs update keeps the previous tree until Retry succeeds");
    }

    private static bool HasTree(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "SpecsTree" && tree.Items.OfType<TreeViewItem>().Any(item => Equals(item.Tag, "SPEC.md")));

    private static void HotToggle(string root)
    {
        using var app = new E2eWorkspace(root, openFiles: false, prepare: _ => File.WriteAllText(Path.Combine(root, "module.md"),
            "---\nid: sample-module\ntype: module-design\ntitle: Sample Module\nparent: sample-root\n---\n\nPart of [[sample-root]].\n"));
        var tab = app.Find<Button>("Tab_plugin_spec-dialect_specs");
        app.Click(tab);
        E2eWorkspace.Until(() => HasTree(app));
        var tree = app.Find<TreeView>("SpecsTree");
        app.Click((Control)tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(node => Equals(node.Tag, "module.md")).Header!);
        E2eWorkspace.Until(() => tree.SelectedItem is TreeViewItem { Tag: "module.md" });
        app.Click((Control)tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(node => Equals(node.Tag, "SPEC.md")).Header!);
        E2eWorkspace.Until(() => tree.SelectedItem is TreeViewItem { Tag: "SPEC.md" });
        Require(ReferenceEquals(tree, app.Find<TreeView>("SpecsTree")), "An editor selection changes the active Specs row without remounting the panel.");
        app.Click(app.Find<Button>("Tab_files"));
        app.Open("module.md", true);
        Button Link() => app.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SpecLink");
        E2eWorkspace.Until(() => Link().IsEnabled);

        void Enable(bool enabled) => _ = app.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(SpecDialectContract.Id, enabled));
        Enable(false);
        E2eWorkspace.Until(() => !app.Workbench.PluginRegistry.Active.Contains(SpecDialectContract.Id));
        app.Click(app.Find<Button>("Tab_plugin_spec-dialect_specs"));
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<StackPanel>().Any(panel => panel.Name == "PluginToolDormant" &&
            panel.Children.OfType<TextBlock>().Any(text => text.Text == "Specs is off")));
        E2eWorkspace.Until(() => !Link().IsEnabled);

        Enable(true);
        E2eWorkspace.Until(() => HasTree(app));
        E2eWorkspace.Until(() => Link().IsEnabled);
        Console.WriteLine("PASS fork plugins/spec-dialect/hot-toggle.spec.ts: disabling turns the Specs tab into the dormant placeholder and its spec links off; re-enabling brings both back");
    }

    private static void OffRailMovesOn(string root)
    {
        var profile = root + "-profile";
        Directory.CreateDirectory(profile);
        var state = new HostStateStore(profile);
        state.ChangeAsync([HostStateChange.PluginEnabled(SpecDialectContract.Id, false)]).AsTask().GetAwaiter().GetResult();
        using var app = new E2eWorkspace(root, openFiles: false, profileRoot: profile);
        var group = app.Window.Layout.State.Groups.Single(item => item.Tools.Any(tool => tool.Id == DockState.SpecsTool));
        E2eWorkspace.Until(() => app.Window.Layout.Selected(group.Id)?.Id == "files");
        Require(app.Window.Layout.Tabs(group.Id).Any(tab => tab.Id == DockState.SpecsTool), "The dormant Specs tab keeps its slot.");
        Console.WriteLine("PASS fork shell railDefault: a rail seeded on a plugin tool that is off opens on the next tab");
    }
}