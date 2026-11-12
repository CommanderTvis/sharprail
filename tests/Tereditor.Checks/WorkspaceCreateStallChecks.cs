using System.Diagnostics;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Host.Core;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>
/// An agent's workspace_create, with a sizeable ignored tree to clone, while the window shows the
/// Default workspace: a probe thread posts to the dispatcher every few milliseconds and the longest
/// wait for a posted job must stay under the budget while Projects gains the new row.
/// </summary>
internal static class WorkspaceCreateStallChecks
{
    internal static void Run(string root)
    {
        var budget = double.TryParse(Environment.GetEnvironmentVariable("SHARPRAIL_CREATE_STALL_BUDGET_MS"), out var value) ? value : 50;
        using var app = new SharpRail.Checks.E2E.E2eWorkspace(Path.Combine(root, "workspace-create-stall"), embedded: true);
        File.WriteAllText(Path.Combine(app.Root, ".gitignore"), "node_modules/\n");
        Directory.CreateDirectory(Path.Combine(app.Root, ".sharprail"));
        File.WriteAllText(Path.Combine(app.Root, ".sharprail", "workspace.toml"), "version = 1\n\n[clone]\ninclude = [\"node_modules\"]\n");
        for (var package = 0; package < 60; package++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(app.Root, "node_modules", $"package-{package}", "lib")).FullName;
            for (var file = 0; file < 200; file++) File.WriteAllText(Path.Combine(directory, $"m{file}.js"), new string('x', 2048));
        }
        // SHARPRAIL_CREATE_STALL_LSP (server ids, comma-separated) also starts language servers, as agents ask; the C# fixture gives csharp a project.
        var servers = Environment.GetEnvironmentVariable("SHARPRAIL_CREATE_STALL_LSP")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (servers.Length > 0) File.WriteAllText(Path.Combine(app.Root, "Probe.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        if (servers.Length > 0) File.WriteAllText(Path.Combine(app.Root, "Probe.cs"), "class Probe { }\n");
        SharpRail.Checks.E2E.IsolatedGit.Run(app.Root, "init", "-b", "main");
        SharpRail.Checks.E2E.IsolatedGit.Run(app.Root, "add", "-A");
        SharpRail.Checks.E2E.IsolatedGit.Run(app.Root, "commit", "-m", "fixture");
        Until(() => app.Window.WorkspaceMounted);
        // SHARPRAIL_CREATE_STALL_OTHERS fills Projects with that many workspaces first.
        var others = int.TryParse(Environment.GetEnvironmentVariable("SHARPRAIL_CREATE_STALL_OTHERS"), out var count) ? count : 0;
        for (var index = 0; index < others; index++)
        {
            var other = Task.Run(async () => await new WorkspaceMcpTools(app.State!).Create(app.Root).Call(new JsonObject { ["description"] = $"Other {index}" }, default));
            Until(() => other.IsCompleted);
        }
        Settle(3000);

        var stalls = new List<double>();
        using var stop = new CancellationTokenSource();
        var probe = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var clock = Stopwatch.StartNew();
                var job = Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
                while (!job.GetTask().IsCompleted && !stop.IsCancellationRequested) Thread.Sleep(1);
                lock (stalls) stalls.Add(clock.Elapsed.TotalMilliseconds);
                Thread.Sleep(4);
            }
        })
        { IsBackground = true };
        probe.Start();
        var started = Stopwatch.StartNew();
        var created = Task.Run(async () => await new WorkspaceMcpTools(app.State!).Create(app.Root).Call(
            servers.Length > 0
                ? new JsonObject { ["description"] = "Stall probe workspace", ["languageServers"] = new JsonArray([.. servers.Select(server => (JsonNode)server)]) }
                : new JsonObject { ["description"] = "Stall probe workspace" }, default));
        Until(() => created.IsCompleted);
        var result = created.GetAwaiter().GetResult();
        Require(!result.Error, "workspace_create must succeed: " + result.Text);
        Until(() => app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(label => label.Name == "WorkspaceName" && label.Text == "Stall probe workspace"));
        var path = JsonNode.Parse(result.Text)!["path"]!.GetValue<string>();
        Until(() => File.Exists(Path.Combine(path, "node_modules", "package-59", "lib", "m199.js")));
        Settle(1500);
        stop.Cancel();
        probe.Join();
        double[] samples;
        lock (stalls) samples = [.. stalls.Order()];
        double max = samples[^1], p99 = samples[(int)(samples.Length * 0.99)];
        Console.WriteLine($"SHARPRAIL_CREATE_STALL_MS max={max:F1} p99={p99:F1} budget={budget:F0} samples={samples.Length} total={started.Elapsed.TotalMilliseconds:F0}");
        Require(max < budget, $"Creating a workspace must not stall the dispatcher beyond {budget:F0} ms (max {max:F1}, p99 {p99:F1}).");
        Console.WriteLine($"PASS workspace_create with a cloned tree keeps the dispatcher under {budget:F0} ms (max {max:F1})");
    }
}