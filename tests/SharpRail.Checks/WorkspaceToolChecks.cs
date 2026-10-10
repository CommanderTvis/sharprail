using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks;

internal static class WorkspaceToolChecks
{
    internal static async Task Run(string root)
    {
        await Scenario(Path.Combine(root, "workspace-tool-local"), false);
        await Scenario(Path.Combine(root, "workspace-tool-remote"), true);
    }

    private static async Task Scenario(string root, bool remote)
    {
        Directory.CreateDirectory(root);
        await GitRepository.RunAsync(root, default, "init", "-b", "main");
        File.WriteAllText(Path.Combine(root, "task.txt"), "initial\n");
        await GitRepository.RunAsync(root, default, "add", ".");
        await GitRepository.RunAsync(root, default, "commit", "-m", "Initial");
        var initial = (await GitRepository.RunAsync(root, default, "rev-parse", "HEAD")).Trim();
        var stateDirectory = root + "-state";
        await using var pty = new PtyTerminalService();
        await using var server = remote ? RemoteServer.Create(root, IPAddress.Loopback, 0, "workspace-tools", stateDirectory,
            terminals: pty, plugins: seams => seams with { Builtins = [] }) : null;
        if (server is not null) await server.StartAsync();
        var state = server is null ? new HostStateStore(stateDirectory) : (HostStateStore)server.Services.GetRequiredService<IHostStateService>();
        await using var loopback = server is null ? new LoopbackServer(pty, state) : null;
        var endpoint = loopback?.BaseUrl ?? pty.McpEndpoint!;
        using var http = new HttpClient();
        var url = endpoint + "/mcp/" + pty.Token(new TerminalRef(root, "codex"));
        async Task<JsonNode> Request(string method, object? parameters = null)
        {
            using var reply = await http.PostAsync(url, new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json"));
            reply.EnsureSuccessStatusCode();
            return JsonNode.Parse(await reply.Content.ReadAsStringAsync())!;
        }
        var listed = (await Request("tools/list"))["result"]!["tools"]!.AsArray();
        Require(listed.Any(tool => tool!["name"]!.GetValue<string>() == "workspace_create"), "Workspace creation must be available without enabled plugins.");
        foreach (var arguments in new object[] { new { description = "  " }, new { description = "Invalid branch", branch = "../bad" }, new { description = "Invalid base", baseBranch = "missing-base" } })
        {
            var rejected = await Request("tools/call", new { name = "workspace_create", arguments });
            Require(rejected["result"]!["isError"]?.GetValue<bool>() == true, "Invalid workspace arguments must be tool errors.");
            Require((await GitRepository.ListWorktreesAsync(root, default)).Count == 1, "Rejected calls must not create a worktree.");
        }
        var created = await Request("tools/call", new { name = "workspace_create", arguments = new { description = "Fix live diffs", branch = "fix-live-diffs", baseBranch = initial } });
        var result = JsonNode.Parse(created["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        var path = result["path"]!.GetValue<string>();
        Require(Path.GetDirectoryName(path) == SharpRail.Host.Core.WorktreePaths.ProjectDirectory(root, stateDirectory), "The tool must use this host's managed directory.");
        Require((await GitRepository.RunAsync(path, default, "rev-parse", "HEAD")).Trim() == initial, "The requested base must determine the new HEAD.");
        IHostStateService client = server is null ? new LocalStateAdapter(state) : new RemoteStateAdapter(
            new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "workspace-tools");
        var snapshot = await client.GetStateAsync();
        Require(snapshot.WorkspaceLabels.GetValueOrDefault(path) == "Fix live diffs" && snapshot.WorkspacesOf(root).Any(workspace => workspace.Path == path), "Every client must receive the workspace and its task description.");
        Require(new HostStateStore(stateDirectory).Current.WorkspaceLabels.GetValueOrDefault(path) == "Fix live diffs", "The task description must survive a host restart.");
        File.WriteAllText(Path.Combine(path, "task.txt"), "workspace-only commit\n");
        await GitRepository.RunAsync(path, default, "add", ".");
        await GitRepository.RunAsync(path, default, "commit", "-m", "Advance calling workspace");
        var workspaceHead = (await GitRepository.RunAsync(path, default, "rev-parse", "HEAD")).Trim();
        Require(workspaceHead != initial, "The calling workspace must differ from Default for this regression.");
        url = endpoint + "/mcp/" + pty.Token(new TerminalRef(path, "claude"));
        var second = await Request("tools/call", new { name = "workspace_create", arguments = new { description = "Check the fix" } });
        Require(second["result"]!["isError"] is null, "A linked-worktree terminal must create another workspace with default branch and base.");
        var secondPath = JsonNode.Parse(second["result"]!["content"]![0]!["text"]!.GetValue<string>())!["path"]!.GetValue<string>();
        Require((await GitRepository.RunAsync(secondPath, default, "rev-parse", "HEAD")).Trim() == workspaceHead,
            "An omitted base must use the calling workspace's HEAD, rather than Default's HEAD.");
        var explicitHead = await Request("tools/call", new { name = "workspace_create", arguments = new { description = "Explicit workspace HEAD", baseBranch = "HEAD" } });
        var explicitPath = JsonNode.Parse(explicitHead["result"]!["content"]![0]!["text"]!.GetValue<string>())!["path"]!.GetValue<string>();
        Require((await GitRepository.RunAsync(explicitPath, default, "rev-parse", "HEAD")).Trim() == workspaceHead,
            "An explicit HEAD must also resolve in the calling workspace.");
        Require((await GitRepository.ListWorktreesAsync(root, default)).Count == 4, "Both terminal owners must create workspaces in the same project.");
        var parallel = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Request("tools/call",
            new { name = "workspace_create", arguments = new { description = "Parallel task " + index } })));
        Require(parallel.All(reply => reply["result"]!["isError"] is null) && (await GitRepository.ListWorktreesAsync(root, default)).Count == 6,
            "Concurrent agent calls must allocate distinct workspaces.");
        await Titling(root, stateDirectory, state, client, server is null ? new LocalStateAdapter(state) : new RemoteStateAdapter(
            new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "workspace-tools"),
            async (workspace, arguments) =>
            {
                url = endpoint + "/mcp/" + pty.Token(new TerminalRef(workspace, "claude"));
                var reply = (await Request("tools/call", new { name = "set_title", arguments }))["result"]!;
                return (reply["content"]![0]!["text"]!.GetValue<string>(), reply["isError"]?.GetValue<bool>() == true);
            });
        Require((await Request("tools/list"))["result"]!["tools"]!.AsArray().Any(tool => tool!["name"]!.GetValue<string>() == "set_title"),
            "Terminal titling must be available without enabled plugins.");
        await Deleting(root, client, async (workspace, arguments) =>
        {
            url = endpoint + "/mcp/" + pty.Token(new TerminalRef(workspace, "claude"));
            var reply = (await Request("tools/call", new { name = "workspace_delete", arguments }))["result"]!;
            return (reply["content"]![0]!["text"]!.GetValue<string>(), reply["isError"]?.GetValue<bool>() == true);
        }, async (workspace, description) =>
        {
            url = endpoint + "/mcp/" + pty.Token(new TerminalRef(workspace, "claude"));
            var reply = await Request("tools/call", new { name = "workspace_create", arguments = new { description } });
            return JsonNode.Parse(reply["result"]!["content"]![0]!["text"]!.GetValue<string>())!["path"]!.GetValue<string>();
        });
        Require((await Request("tools/list"))["result"]!["tools"]!.AsArray().Any(tool => tool!["name"]!.GetValue<string>() == "workspace_delete"),
            "Workspace deletion must be available without enabled plugins.");
        using var unknown = await http.PostAsync(endpoint + "/mcp/unknown", new StringContent("{}", Encoding.UTF8, "application/json"));
        Require(unknown.StatusCode == HttpStatusCode.NotFound, "Workspace tools must require a terminal token.");
        Console.WriteLine($"PASS workspace MCP tool ({(remote ? "remote" : "local")}): validation, managed paths, base, descriptions, linked terminals, terminal titles, deletion and authentication");
    }

    private static async Task Deleting(string root, IHostStateService client, Func<string, object, Task<(string Text, bool Error)>> delete,
        Func<string, string, Task<string>> create)
    {
        var finished = await create(root, "Finished task");
        var dirty = await create(root, "Unfinished task");
        var branch = (await GitRepository.RunAsync(finished, default, "branch", "--show-current")).Trim();
        File.WriteAllText(Path.Combine(dirty, "unsaved.txt"), "work in progress\n");
        async Task<bool> Listed(string path) => (await client.GetStateAsync()).WorkspacesOf(root).Any(workspace => workspace.Path == path);
        foreach (var (caller, arguments, why) in new (string, object, string)[]
        {
            (root, new { }, "a missing path"),
            (root, new { path = finished, force = true }, "an unknown argument"),
            (root, new { path = Path.Combine(root, "not-a-workspace") }, "a path that is no workspace"),
            (root, new { path = root }, "the Default workspace"),
            (finished, new { path = finished }, "the calling terminal's own workspace"),
            (root, new { path = dirty }, "a checkout with uncommitted work")
        })
        {
            var refused = await delete(caller, arguments);
            Require(refused.Error && Directory.Exists(finished) && Directory.Exists(dirty) && await Listed(finished) && await Listed(dirty),
                $"Deleting must refuse {why} and change nothing: {refused.Text}");
        }
        Require(File.Exists(Path.Combine(dirty, "unsaved.txt")), "A refused deletion must leave uncommitted work in place.");
        var deleted = await delete(dirty, new { path = finished });
        Require(!deleted.Error && deleted.Text.Contains(finished, StringComparison.Ordinal) && deleted.Text.Contains(branch, StringComparison.Ordinal),
            "Another workspace's terminal must delete a clean workspace and be told what was kept: " + deleted.Text);
        // The record is gone when the call returns: no filesystem watcher or later refresh is involved.
        Require(!Directory.Exists(finished) && !await Listed(finished) && await Listed(dirty), "The checkout and its record must be gone as soon as the tool answers.");
        Require((await GitRepository.ListWorktreesAsync(root, default)).All(tree => tree.Path != finished)
            && (await GitRepository.RunAsync(root, default, "branch", "--list", branch)).Contains(branch, StringComparison.Ordinal),
            "Git must no longer list the worktree, and its branch must be kept.");
        Require((await delete(root, new { path = finished })).Error, "Deleting a workspace twice must be a tool error.");
    }

    private static async Task Titling(string root, string stateDirectory, HostStateStore state, IHostStateService client, IHostStateService second,
        Func<string, object, Task<(string Text, bool Error)>> title)
    {
        var workspace = (await client.GetStateAsync()).WorkspacesOf(root).First(known => known.Path != root);
        async Task<string?> Tab(string path, IHostStateService? from = null) =>
            (await (from ?? client).GetStateAsync()).TerminalTitles.FirstOrDefault(known => known.Terminal == new TerminalRef(path, "claude"))?.Title;
        foreach (var arguments in new object[] { new { }, new { title = " - " }, new { title = "Fix auth", workspace_name = "Fix auth" }, new { title = "Fix auth", branch = "fix-auth" } })
            Require((await title(root, arguments)).Error && await Tab(root) is null,
                "A missing title, a title without letters and the arguments that once named a workspace must be rejected before any write.");

        var titled = await title(root, new { title = "  Fix   auth redirect " });
        Require(!titled.Error && titled.Text == "Terminal titled \"Fix auth redirect\"." && await Tab(root) == "Fix auth redirect" && await Tab(root, second) == "Fix auth redirect",
            "A terminal in the Default workspace takes a title, for every client: " + titled.Text);
        Require(new HostStateStore(stateDirectory).Current.TerminalTitles.Any(known => known.Terminal == new TerminalRef(root, "claude") && known.Title == "Fix auth redirect"),
            "A terminal's title must survive a host restart.");
        var again = await title(root, new { title = "Review #12 Zoom" });
        Require(!again.Error && await Tab(root) == "Review #12 Zoom", "A later call retitles the terminal for its next task.");

        var before = await client.GetStateAsync();
        var branch = (await GitRepository.RunAsync(workspace.Path, default, "symbolic-ref", "--short", "HEAD")).Trim();
        var other = await title(workspace.Path, new { title = "Виправити вхід" });
        var after = await client.GetStateAsync();
        Require(!other.Error && await Tab(workspace.Path) == "Виправити вхід" && await Tab(root) == "Review #12 Zoom", "Each terminal keeps its own title, in any script.");
        Require(after.WorkspaceLabels.GetValueOrDefault(workspace.Path) == before.WorkspaceLabels.GetValueOrDefault(workspace.Path) &&
            after.Workspaces.SequenceEqual(before.Workspaces) && (await GitRepository.RunAsync(workspace.Path, default, "symbolic-ref", "--short", "HEAD")).Trim() == branch,
            "Titling a terminal never renames the workspace it runs in or moves its branch.");
        state.RemoveTerminalTitles(known => known.WorkspaceId == root);
        Require(await Tab(root) is null && await Tab(workspace.Path) == "Виправити вхід", "A closed terminal's title is dropped, and only its own.");
    }

    internal static void RunUi(string root)
    {
        using var app = new SharpRail.Checks.E2E.E2eWorkspace(Path.Combine(root, "workspace-tool-ui"));
        Git(app.Root, "init", "-b", "main");
        Git(app.Root, "add", ".");
        Git(app.Root, "commit", "-m", "Initial");
        Until(() => app.Window.WorkspaceMounted);
        var created = Task.Run(async () => await new WorkspaceMcpTools(app.State!).Create(app.Root).Call(
            new JsonObject { ["description"] = "Investigate terminal marks" }, default));
        Until(() => created.IsCompleted);
        Require(!created.GetAwaiter().GetResult().Error, "The workspace tool must create the UI fixture.");
        Until(() => app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(label => label.Name == "WorkspaceName" && label.Text == "Investigate terminal marks"));
        Require(app.Window.WorkspaceRoot == app.Root, "Creating an agent workspace must not switch the user's workspace.");
        app.Window.Layout.NewTerminal(app.Center);
        var terminalTab = app.Window.Layout.Tabs(app.Center).Last(tab => tab.Kind == "terminal");
        var defaultTitle = terminalTab.Title;
        var titled = Task.Run(async () => await new WorkspaceMcpTools(app.State!).Title(new TerminalRef(app.Root, terminalTab.Id)).Call(
            new JsonObject { ["title"] = "Watch the build" }, default));
        Until(() => titled.IsCompleted);
        Require(!titled.GetAwaiter().GetResult().Error && defaultTitle.StartsWith("Terminal ", StringComparison.Ordinal), "The tool must title a terminal in the Default workspace: " + titled.GetAwaiter().GetResult().Text);
        Until(() => app.Window.Layout.Tabs(app.Center).Single(tab => tab.Id == terminalTab.Id).Title == "Watch the build");
        Until(() => app.Find<Button>("Tab_" + terminalTab.Id.Replace(':', '_')).GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Watch the build"));
        Require(app.Window.Layout.Selected(app.Center)?.Id == terminalTab.Id && app.Window.WorkspaceRoot == app.Root, "Titling a terminal keeps the selection and the workspace.");
        var createdPath = app.State!.Current.WorkspaceLabels.Single(label => label.Value == "Investigate terminal marks").Key;
        var deleted = Task.Run(async () => await new WorkspaceMcpTools(app.State!).Delete(app.Root).Call(new JsonObject { ["path"] = createdPath }, default));
        Until(() => deleted.IsCompleted);
        Require(!deleted.GetAwaiter().GetResult().Error, "The deletion tool must remove the UI fixture: " + deleted.GetAwaiter().GetResult().Text);
        Until(() => !app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(label => label.Name == "WorkspaceName" && label.Text == "Investigate terminal marks"));
        Require(app.Window.WorkspaceRoot == app.Root, "Deleting an agent's workspace must not switch the user's workspace.");
        Console.WriteLine("PASS agent-created workspace displays its task description, a titled terminal its title, and a deleted workspace leaves Projects, without switching the user");
    }
}