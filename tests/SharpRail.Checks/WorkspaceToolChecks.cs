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
        using var unknown = await http.PostAsync(endpoint + "/mcp/unknown", new StringContent("{}", Encoding.UTF8, "application/json"));
        Require(unknown.StatusCode == HttpStatusCode.NotFound, "Workspace tools must require a terminal token.");
        Console.WriteLine($"PASS workspace MCP tool ({(remote ? "remote" : "local")}): validation, managed paths, base, descriptions, linked terminals and authentication");
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
        Console.WriteLine("PASS agent-created workspace displays its task description in Projects without switching the user");
    }
}