using System.Net;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.BranchGraph;
using SharpRail.Plugins.BranchGraph.Host;
using SharpRail.Plugins.BranchGraph.UI;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

internal static class BranchGraphChecks
{
    private const string Tool = "plugin:branch-graph:graph";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Git(string root, params string[] arguments) => IsolatedGit.Run(root, arguments).Trim();

    private static string Repository(string root) => Path.Combine(root, "branch-graph", "repo");

    internal static async Task RunHostAsync(string root)
    {
        Parsing();
        Lanes();
        var source = Environment.GetEnvironmentVariable("SHARPRAIL_TEST_GIT_SOURCE");
        if (source is null) { Console.WriteLine("SKIP branch graph Git fixtures: set SHARPRAIL_TEST_GIT_SOURCE to an existing clone."); return; }
        var repo = Repository(root);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "main");
        Git(repo, "fetch", "--no-tags", "--depth=600", source, "refs/remotes/origin/claude-code-integration-plugin-api");
        Git(repo, "checkout", "-B", "main", "FETCH_HEAD");
        Git(repo, "branch", "feature-one", "main~2");
        Git(repo, "branch", "a-branch-name-long-enough-to-shove-the-row-sideways-and-then-some", "main");
        Git(repo, "worktree", "add", "--detach", Path.Combine(root, "branch-graph", "secondary"), "main~2");
        var sha = Git(repo, "rev-parse", "HEAD");
        await using var server = RemoteServer.Create(repo, IPAddress.Loopback, 0, "graph", Path.Combine(root, "branch-graph", "state"));
        await server.StartAsync();
        try
        {
            var state = server.Services.GetRequiredService<IHostStateService>();
            await state.ChangeAsync([HostStateChange.OpenProject(repo)]);
            var runtime = server.Services.GetRequiredService<SharpRail.Host.Core.Plugins.PluginRuntime>();
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remote = new RemotePluginAdapter(address, "graph");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await remote.ListAsync(timeout.Token)).Single(entry => entry.Id == BranchGraphPlugin.Id).Status != PluginStatus.Active)
                await Task.Delay(25, timeout.Token);
            using var projectRemote = new RemoteProjectAdapter(address, "graph");
            await projectRemote.OpenProjectAsync(repo, timeout.Token);
            foreach (var projects in new IProjectServices[] { new LocalProjectAdapter(new ProjectServices(repo)), projectRemote })
            {
                Require((await projects.ListCommitsAsync("", timeout.Token)).Count == 0 && await projects.GetCommitAsync(sha, timeout.Token) is { Sha: var found } && found == sha,
                    "An explicit commit lookup works without a comparison menu.");
                var old = Git(repo, "rev-parse", "main~450");
                Require(await projects.GetCommitAsync(old, timeout.Token) is { Sha: var older } && older == old && await projects.GetCommitAsync("deadbeef", timeout.Token) is null,
                    "Old history is independently readable and missing commits are absent.");
            }
            foreach (var plugins in new IPluginService[] { new LocalPluginAdapter(runtime), remote })
            {
                async Task<T> Call<T>(string method, object parameters) => PluginJson.Convert<T>(await plugins.CallAsync(new(BranchGraphPlugin.Id, method, parameters, "graph-checks"), timeout.Token));
                var first = await Call<GitGraph>("graph", new GitGraphParams(repo));
                Require(first.Commits.Count == GraphBuild.Page && first.HasMore && first.Commits[0].Sha == sha, "The first graph page is bounded and reports older history.");
                Require(first.Commits.Any(commit => commit.Refs.Contains("feature-one")) && first.Worktrees.Count == 2, "Branch decorations and both worktrees reach the graph.");
                var second = await Call<GitGraph>("graph", new GitGraphParams(repo, GraphBuild.Page));
                Require(second.Commits.Count > 0 && !second.Commits.Any(commit => first.Commits.Any(before => before.Sha == commit.Sha)), "The next page starts after the first.");
                var expected = GraphBuild.ParseLog(Git(repo, GraphBuild.LogArgs(0).ToArray()));
                Require(JsonSerializer.Serialize(first.Commits, PluginJson.Options) == JsonSerializer.Serialize(expected.Commits, PluginJson.Options), "The plugin page matches Git's history over each adapter.");
                var patch = await Call<GitPatch>("patch", new GitPatchParams(repo, "HEAD"));
                Require(patch.Patch.StartsWith("From " + sha, StringComparison.Ordinal), "The fork patch method accepts Git revision expressions and returns format-patch.");
                foreach (var (method, parameters, reason) in new (string, object, string)[]
                {
                    ("graph", new GitGraphParams("unknown"), "Unknown project"),
                    ("patch", new GitPatchParams(repo, "deadbeef"), "Could not generate patch")
                })
                {
                    try { await Call<object>(method, parameters); throw new InvalidOperationException("An invalid graph call was accepted."); }
                    catch (PluginCallException error) { Require(error.Message.Contains(reason, StringComparison.Ordinal), "The graph failure retains its reason."); }
                }
            }
            Console.WriteLine("PASS branch graph local/remote host: bounded pages, refs, worktrees, Git revision patch and failures");
        }
        finally { await server.StopAsync(); }
    }

    internal static void RunUi(string root)
    {
        Fixtures(root);
        var repo = Repository(root);
        if (!Directory.Exists(Path.Combine(repo, ".git"))) return;
        RunUi(root, remote: false);
        RunUi(root, remote: true);
        using var plain = new E2eWorkspace(Path.Combine(root, "branch-graph", "plain"), profileRoot: Path.Combine(root, "branch-graph", "plain-profile"));
        E2eWorkspace.Until(() => plain.Workbench.PluginLoader.Registry.Active.Contains(BranchGraphPlugin.Id));
        Require(!plain.Window.Layout.Tools.Any(tool => tool.Id == Tool), "A Gitless workspace withholds the manifest's RequiresGit tool.");
    }

    private static void RunUi(string root, bool remote)
    {
        var repo = Repository(root);
        var mode = remote ? "remote" : "local";
        var profile = Path.Combine(root, "branch-graph", "profile-" + mode);
        var server = remote ? RemoteServer.Create(repo, IPAddress.Loopback, 0, "graph-ui", Path.Combine(root, "branch-graph", "ui-state")) : null;
        try
        {
            if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
            using var app = server is null ? new E2eWorkspace(repo, profileRoot: profile)
                : new E2eWorkspace(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "graph-ui", repo, profile, repo);
            E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
            Scenario(app);
            Console.WriteLine($"PASS branch graph {mode} UI lifecycle");
        }
        finally
        {
            if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
        }
    }

    private static void Scenario(E2eWorkspace app)
    {
        var repo = app.Root;
        var window = app.Window;
        E2eWorkspace.Until(() => app.Workbench.PluginLoader.Registry.Active.Contains(BranchGraphPlugin.Id));
        window.Layout.RestoreTool(Tool);
        E2eWorkspace.Until(() => window.GetLogicalDescendants().OfType<GraphPanel>().Any(panel => panel.IsEffectivelyVisible && panel.Rows > 0));
        var panel = window.GetLogicalDescendants().OfType<GraphPanel>().Single();
        Require(panel.Rows == 400 && panel.Built < panel.Rows, "History is windowed, with older rows retained as data.");
        var rows = window.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "GraphCommit").ToArray();
        Require(rows.All(row => row.Bounds.Height == 40) && rows.SelectMany(row => row.GetLogicalDescendants().OfType<LaneArt>()).Select(art => art.Bounds.Width).Distinct().Count() == 1,
            "Each row shares a gutter and is exactly as tall as its lane art.");
        var sha = Git(repo, "rev-parse", "HEAD");
        var firstRow = rows[0];
        app.Click(firstRow, mouseButton: Avalonia.Input.MouseButton.Right);
        E2eWorkspace.Until(() => firstRow.ContextMenu!.IsOpen);
        var reads = panel.Reads;
        File.WriteAllText(Path.Combine(repo, "graph-refresh-nudge.txt"), Guid.NewGuid().ToString());
        E2eWorkspace.Until(() => panel.Reads > reads);
        Require(firstRow.ContextMenu!.IsOpen && firstRow.GetLogicalAncestors().Contains(panel) &&
            ReferenceEquals(firstRow, window.GetLogicalDescendants().OfType<Button>().First(button => button.Name == "GraphCommit")),
            "An unchanged Git refresh preserves the mounted row and its open context menu.");
        firstRow.Focus();
        var focused = window.FocusManager?.GetFocusedElement();
        Require(ReferenceEquals(focused, firstRow), "The commit row owns focus before its refs change.");
        var refName = "graph-live-" + Guid.NewGuid().ToString("N");
        reads = panel.Reads;
        Git(repo, "branch", refName, "main");
        E2eWorkspace.Until(() => panel.Reads > reads && firstRow.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == refName));
        Require(firstRow.ContextMenu.IsOpen && ReferenceEquals(window.FocusManager?.GetFocusedElement(), focused) &&
            ReferenceEquals(firstRow, window.GetLogicalDescendants().OfType<Button>().First(button => button.Name == "GraphCommit")),
            "A Git-only ref update preserves the row, focus and open menu while replacing its labels.");
        firstRow.ContextMenu.Close();
        app.ContextAction(rows[0], "Copy commit hash");
        E2eWorkspace.Until(() => window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult() == sha);
        rows = window.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "GraphCommit").ToArray();
        app.ContextAction(rows[0], "Copy patch to clipboard");
        E2eWorkspace.Until(() => window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult()?.StartsWith("From " + sha, StringComparison.Ordinal) == true);
        rows = window.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "GraphCommit").ToArray();
        app.Click(rows[0]);
        window.Layout.RestoreTool("changes");
        E2eWorkspace.Until(() => window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "ChangesScope" && button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains(sha[..7], StringComparison.Ordinal) == true)));
        window.Layout.RestoreTool(Tool);
        var scroll = panel.GetLogicalDescendants().OfType<ScrollViewer>().Single();
        scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
        E2eWorkspace.Until(() => panel.Rows > 400);
        Require(panel.Built < 60, "Paging retains a bounded control window.");
        void SetEnabled(bool enabled)
        {
            window.ShowSettings("Plugins");
            E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
            var settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
            var row = settings.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "PluginRow_branch-graph");
            row.GetLogicalDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "PluginToggle").IsChecked = enabled;
            E2eWorkspace.Until(() => app.Workbench.PluginLoader.Registry.Active.Contains(BranchGraphPlugin.Id) == enabled);
            settings.Close();
        }
        SetEnabled(false);
        E2eWorkspace.Until(() => !panel.GetLogicalAncestors().Contains(window));
        SetEnabled(true);
        window.Layout.RestoreTool(Tool);
        E2eWorkspace.Until(() => window.GetLogicalDescendants().OfType<GraphPanel>().Any(next => !ReferenceEquals(next, panel) && next.Rows > 0));
        Console.WriteLine("PASS branch graph UI: windowing, shared gutter, copy hash and patch, Changes scope and paging");
    }

    private static GraphPanel Panel(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<GraphPanel>().Single(panel => panel.IsEffectivelyVisible);

    private static GraphCommitRow Row(GraphPanel panel, string subject) =>
        panel.GetLogicalDescendants().OfType<GraphCommitRow>().Single(row => row.Model.Commit.Subject == subject);

    private static void Open(E2eWorkspace app, string project)
    {
        var opening = app.Window.OpenProjectAsync(project);
        E2eWorkspace.Until(() => opening.IsCompleted);
        opening.GetAwaiter().GetResult();
        app.Window.Layout.RestoreTool(Tool);
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<GraphPanel>().Any(panel => panel.IsEffectivelyVisible && panel.ProjectId == project));
    }

    // Detaching cancels the panel's reads and attaching reads again, as a tool hidden and shown does.
    private static void Remount(GraphPanel panel)
    {
        var parent = panel.Parent as ContentControl ?? throw new InvalidOperationException($"The graph panel sits in a {panel.Parent?.GetType().Name}.");
        parent.Content = null;
        E2eWorkspace.Until(() => panel.Parent is null);
        parent.Content = panel;
    }

    // Fresh repositories with commits of their own, translating the fork's merge/orphan, gutter and empty scenarios,
    // and two windows on different projects.
    private static void Fixtures(string root)
    {
        var directory = Path.Combine(root, "branch-graph", "fixtures");
        using var isolated = new IsolatedGit(Path.Combine(directory, "git"));
        var merges = IsolatedGit.Repository(Path.Combine(directory, "merges"));
        Git(merges, "checkout", "-q", "-b", "feature-one", "main");
        Git(merges, "commit", "--allow-empty", "-q", "-m", "work on feature-one");
        Git(merges, "checkout", "-q", "main");
        Git(merges, "merge", "-q", "--no-ff", "-m", "Merge feature-one", "feature-one");
        // An orphan branch shares no ancestry with anything: its own root, its own lane.
        Git(merges, "checkout", "-q", "--orphan", "docs-site");
        Git(merges, "commit", "--allow-empty", "-q", "-m", "orphan start");
        Git(merges, "checkout", "-q", "main");
        var orphanSha = Git(merges, "rev-parse", "docs-site");

        var gutter = IsolatedGit.Repository(Path.Combine(directory, "gutter"));
        // A burst of branching, dated into the past so it sorts below the tip rather than beside it.
        foreach (var name in new[] { "old-a", "old-b", "old-c" })
        {
            Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", "2001-01-01T00:00:00");
            Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", "2001-01-01T00:00:00");
            try { Git(gutter, "branch", name, Git(gutter, "commit-tree", Git(gutter, "rev-parse", "HEAD^{tree}"), "-p", Git(gutter, "rev-parse", "HEAD"), "-m", name)); }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", null);
                Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", null);
            }
        }
        for (var index = 0; index < 40; index++) Git(gutter, "commit", "--allow-empty", "-q", "-m", $"c{index}");

        var empty = Path.Combine(directory, "empty");
        Directory.CreateDirectory(empty);
        Git(empty, "init", "-q", "-b", "main");

        using var first = new E2eWorkspace(merges, profileRoot: Path.Combine(directory, "profile"));
        E2eWorkspace.Until(() => first.Window.WorkspaceMounted && first.Workbench.PluginLoader.Registry.Active.Contains(BranchGraphPlugin.Id));
        first.Window.Layout.RestoreTool(Tool);
        E2eWorkspace.Until(() => first.Window.GetLogicalDescendants().OfType<GraphPanel>().Any(panel => panel.IsEffectivelyVisible && panel.Rows == 4));
        var graph = Panel(first);
        // The orphan is in the history like any other branch, under its own ref; a merge draws the lane it opens
        // leaving its own dot, and an ordinary commit opens nothing.
        var orphan = Row(graph, "orphan start");
        Require(orphan.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "docs-site"), "The orphan row carries its docs-site ref.");
        Require(Row(graph, "Merge feature-one").Lanes.BranchCount == 1 && Row(graph, "work on feature-one").Lanes.BranchCount == 0 &&
            orphan.Lanes.BranchCount == 0, "Only the merge row draws the lane it opens.");
        Console.WriteLine("PASS branch graph UI: a merge branches out visibly, and an orphan draws as its own history");

        // A second window on another project leaves the first window's graph on its own project.
        using var second = first.NewWindow();
        Open(second, gutter);
        second.Window.Activate();
        E2eWorkspace.Until(() => ReferenceEquals(second.Workbench.ActiveWindow, second.Window) && Panel(second).Rows > 40);
        var reads = graph.Reads;
        Dispatcher.UIThread.RunJobs();
        Require(graph.ProjectId == merges && graph.Reads == reads && graph.Rows == 4 && Row(graph, "Merge feature-one").IsEffectivelyVisible,
            "The graph in a background window keeps its own project when another window takes focus.");
        first.ContextAction(Row(graph, "orphan start"), "Copy patch to clipboard");
        E2eWorkspace.Until(() => first.Window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult()?.StartsWith("From " + orphanSha, StringComparison.Ordinal) == true);
        Console.WriteLine("PASS branch graph UI: each window's graph follows its own project, Copy patch included");

        // The tip is one line of history, so the gutter is one lane wide however wide the graph gets below; scrolling
        // into the branching widens it, sliding rather than jumping.
        var lanes = Panel(second);
        Require(lanes.ShownLanes == 1, "The tip window needs one lane.");
        var narrow = lanes.GetLogicalDescendants().OfType<GraphCommitRow>().First().Lanes.Bounds.Width;
        var scroll = lanes.GetLogicalDescendants().OfType<ScrollViewer>().Single();
        for (var turn = 0; turn < 30 && lanes.ShownLanes == 1; turn++)
        {
            scroll.Offset = new Vector(0, Math.Min(scroll.Offset.Y + 200, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
            Dispatcher.UIThread.RunJobs();
        }
        Require(lanes.ShownLanes > 1, "Scrolling into the branching widens the gutter.");
        E2eWorkspace.Until(() => lanes.GetLogicalDescendants().OfType<GraphCommitRow>().Select(row => row.Lanes.Bounds.Width).Distinct().Count() == 1 &&
            lanes.GetLogicalDescendants().OfType<GraphCommitRow>().First().Lanes.Bounds.Width > narrow);
        Require(lanes.GetLogicalDescendants().OfType<GraphCommitRow>().First().Lanes.Bounds.Width == lanes.ShownLanes * LaneArt.LaneWidth,
            "Every row on screen widens together to the lanes they need.");
        Console.WriteLine("PASS branch graph UI: the lane gutter is only as wide as the rows on screen need");

        // Switching the window's project resets the graph to the new project's history, here none at all.
        Open(second, empty);
        E2eWorkspace.Until(() => Panel(second).MessageText == "No commits on any branch yet.");
        var blank = Panel(second);
        Require(blank.Rows == 0 && blank.ProjectId == empty, "A project switch drops the previous history.");
        // A read Git cannot even start reports a failure, and the next read recovers.
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(empty, UnixFileMode.None);
        try
        {
            Remount(blank);
            E2eWorkspace.Until(() => blank.MessageText == "Could not read the history.");
        }
        finally { File.SetUnixFileMode(empty, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Require(blank.MessageText == "Could not read the history.", "A failed read says so.");
        Remount(blank);
        E2eWorkspace.Until(() => blank.MessageText == "No commits on any branch yet.");
        Require(blank.MessageText == "No commits on any branch yet.", "The next read after a failure recovers.");
        Console.WriteLine("PASS branch graph UI: project switch reset, empty history and failed read");
    }

    private static void Parsing()
    {
        static string Line(params string[] fields) => string.Join('\0', fields);
        var (commits, hasMore) = GraphBuild.ParseLog(Line("abc123", "abc1234", "parent1 parent2", "2024-01-01T00:00:00Z", "Ada", "HEAD -> main, origin/main", "fix: ‮hidden‬ bug"));
        var commit = commits.Single();
        Require(commit is { Sha: "abc123", ShortSha: "abc1234", Subject: "fix: hidden bug", Author: "Ada", CommittedAt: "2024-01-01T00:00:00Z" } &&
            commit.Parents.SequenceEqual(["parent1", "parent2"]) && commit.Refs.SequenceEqual(["main", "origin/main"]) && !hasMore,
            "The log reads sha, parents, refs and subject, and strips deceptive control text.");
        var page = GraphBuild.ParseLog(string.Join('\n', Enumerable.Range(0, GraphBuild.Page + 1).Select(i => Line($"sha{i}", $"s{i}", "", "2024-01-01T00:00:00Z", "Ada", "", $"c{i}"))));
        Require(page.Commits.Count == GraphBuild.Page && page.HasMore, "A page one past its size reports more and is cropped.");
        Require(GraphBuild.ParseLog(Line("a", "a1", "", "2024-01-01T00:00:00Z", "Ada", "", "c") + "\n\n").Commits.Count == 1, "Blank lines emit no commit.");
        var worktrees = GraphBuild.ParseWorktrees("worktree /repo/main\nHEAD deadbeef\n\nworktree /repo/scratch\nHEAD c0ffee",
            [new HostWorkspace("w1", "p1", "main", "main", "/repo/main", true)]);
        Require(worktrees.SequenceEqual([new GitGraphWorktree("deadbeef", "main", "w1"), new GitGraphWorktree("c0ffee", "scratch")]),
            "Each worktree pairs with the workspace at its path, and the rest stay anonymous.");
        Console.WriteLine("PASS branch graph log and worktree parsing");
    }

    private static GitGraphCommit Commit(string sha, params string[] parents) => new(sha, sha, parents, [], sha, "t", "2026-01-01T00:00:00Z");

    private static void Lanes()
    {
        var straight = GraphLanes.Layout([Commit("c", "b"), Commit("b", "a"), Commit("a")]).Rows;
        Require(straight.All(row => row is { Lane: 0, Width: 0 }), "A straight history stays in one lane.");
        var forked = GraphLanes.Layout([Commit("c", "a"), Commit("b", "a"), Commit("a")]).Rows;
        Require(forked.Select(row => row.Lane).SequenceEqual([0, 1, 0]) && forked[2].Edges.Any(edge => edge.Joins == 0),
            "A branch takes its own lane and gives it back where it forked.");
        var merged = GraphLanes.Layout([Commit("m", "a", "b"), Commit("b", "a"), Commit("a")]).Rows;
        Require(merged.Select(row => row.Lane).SequenceEqual([0, 1, 0]), "A merge opens a lane for its second parent.");
        var spanning = GraphLanes.Layout([Commit("d", "c"), Commit("c", "a"), Commit("b", "a"), Commit("a")]).Rows;
        Require(spanning[1].FromAbove && spanning[1].Edges.All(edge => edge.Lane != 0) && spanning[2].Lane == 1 && spanning[2].Edges.Any(edge => edge.Lane == 0),
            "A lane keeps drawing through the rows it spans.");
        var ends = GraphLanes.Layout([Commit("b", "a"), Commit("a")]).Rows;
        Require(ends[0] is { FromAbove: false, ToBelow: true } && ends[1] is { FromAbove: true, ToBelow: false }, "A tip has no line above it, and a root none below.");
        var wide = GraphLanes.Layout([.. Enumerable.Range(0, 12).Select(index => Commit($"t{index}", "root")), Commit("root")]).Rows;
        Require(wide.Max(row => row.Lane) <= GraphLanes.MaxLanes - 1, "History wider than the rail shares the last lane.");
        GitGraphCommit[] all = [Commit("d", "c"), Commit("c", "a"), Commit("b", "a"), Commit("a")];
        var whole = GraphLanes.Layout(all).Rows;
        var first = GraphLanes.Layout(all[..2]);
        var second = GraphLanes.Layout(all[2..], first.Carry);
        Require(first.Rows.Concat(second.Rows).Select(row => row.Lane).SequenceEqual(whole.Select(row => row.Lane)) && !second.Rows[0].FromAbove,
            "A page picks up the lanes the page before it left open.");
        var merge = GraphLanes.Layout([Commit("m", "a", "b"), Commit("b", "base"), Commit("a", "base"), Commit("base")]).Rows;
        Require(merge[0].Branches.Count == 1 && merge[0].Branches[0] != merge[0].Lane && merge[1].Lane == merge[0].Branches[0] && merge[2].Branches.Count == 0,
            "A merge says where the lane it opens came from; an ordinary commit opens nothing.");
        var orphan = GraphLanes.Layout([Commit("x2", "x1"), Commit("o2", "o1"), Commit("x1"), Commit("o1")]).Rows;
        int Lane(string sha) => orphan.Single(row => row.Commit.Sha == sha).Lane;
        Require(Lane("x2") != Lane("o2") && Lane("x1") == Lane("x2") && Lane("o1") == Lane("o2") &&
            orphan.Where(row => row.Commit.Sha is "x2" or "o2").All(row => !row.FromAbove) &&
            orphan.Where(row => row.Commit.Sha is "x1" or "o1").All(row => !row.ToBelow && row.Branches.Count == 0) &&
            !orphan[^1].Edges.Any(edge => edge.Joins is null), "An orphan branch keeps a lane of its own, ending where its root does.");
        Console.WriteLine("PASS branch graph lane layout");
    }
}