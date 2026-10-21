using System.Diagnostics;

using Avalonia.LogicalTree;
using Avalonia.Threading;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Creating and deleting a workspace of a project with a large ignored tree listed in <c>.sharprail/workspace.toml</c> must leave
/// the dispatcher responsive: a heartbeat on the UI thread records the longest gap between two beats across both operations.
/// </summary>
internal static class WorkspaceResponsivenessE2E
{
    // Generous for a loaded CI machine; the synchronous profile write this guards against stalled 600-850 ms.
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(250);

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "workspace-responsiveness-git"));
        CreateAndDelete(Path.Combine(root, "workspace-responsiveness"));
        DeleteOther(Path.Combine(root, "workspace-responsiveness-other"));
    }

    /// <summary>
    /// Force-deletes another, dirty workspace with a large build tree (read-only files included) from the rail while the Default
    /// workspace is open, and has the window re-sync its project's worktrees as it does when it is activated again. The UI thread
    /// keeps beating, the open workspace never unmounts or shows its loading placeholder, and switching to a third workspace while
    /// the deletion runs mounts it promptly instead of waiting behind the removal.
    /// </summary>
    internal static void DeleteOther(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var path = CreateWorkspaceViaDialog(app);
        var other = CreateWorkspaceViaDialog(app);
        app.Click(Select(app, app.Window.ProjectRoot));
        Until(() => Active(app, app.Window.ProjectRoot));
        File.WriteAllText(Path.Combine(path, "unsaved.txt"), "dirty\n");
        File.WriteAllText(Path.Combine(path, ".gitignore"), "bin/\nobj/\n.tools/\n");
        foreach (var tree in new[] { "bin", "obj", ".tools" })
            for (var folder = 0; folder < 600; folder++)
            {
                var folderPath = Path.Combine(path, tree, "d" + folder);
                Directory.CreateDirectory(folderPath);
                for (var file = 0; file < 40; file++)
                {
                    var filePath = Path.Combine(folderPath, "f" + file);
                    File.WriteAllText(filePath, "x");
                    if (tree == ".tools") File.SetAttributes(filePath, FileAttributes.ReadOnly);
                }
            }
        Settle(1000);
        var root = app.Window.WorkspaceRoot;
        var worst = TimeSpan.Zero;
        var unmounted = false;
        var last = Stopwatch.GetTimestamp();
        var beat = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Normal, (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var gap = Stopwatch.GetElapsedTime(last, now);
            last = now;
            if (gap > worst) worst = gap;
            if (!app.Window.WorkspaceMounted || app.Window.WorkspaceRoot != root || app.Window.AtProjectHome ||
                app.Window.GetLogicalDescendants().OfType<Avalonia.Controls.Control>().Any(control => control.Name == "WorkspaceLoading" && control.IsEffectivelyVisible))
                unmounted = true;
        });
        beat.Start();
        try
        {
            RemoveWorkspace(app, path);
            var confirm = Dialog(app);
            Require(confirm.Title == "Delete uncommitted changes?", "Dirty removal must ask before force deletion.");
            app.Click(Named<Avalonia.Controls.Button>(confirm, "WorkspaceForceRemove"));
            // Git is deleting the checkout now; the window re-syncs its worktrees as activation does.
            Until(() => ItemText(app, path, "WorkspaceBranch") == "Removing…");
            Settle(300);
            typeof(SharpRail.UI.WorkbenchWindow).GetMethod("RefreshProjectWorkspaces", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(app.Window, null);
            Settle(200);
            var stillMounted = !unmounted;
            var switching = Stopwatch.GetTimestamp();
            app.Click(Select(app, other));
            Until(() => Active(app, other));
            var switched = Stopwatch.GetElapsedTime(switching);
            var removing = Directory.Exists(path);
            Until(() => !WorktreePaths(app).Contains(path) && !Directory.Exists(path));
            Settle(300);
            Console.WriteLine($"MEASURE delete other workspace max UI stall {worst.TotalMilliseconds:0} ms, switch during removal {switched.TotalMilliseconds:0} ms (removal still running: {removing})");
            Require(stillMounted, "Deleting another workspace unmounted or reloaded the open one.");
            Require(worst < Limit, $"Deleting another workspace stalled the UI thread for {worst.TotalMilliseconds:0} ms.");
            Require(switched < TimeSpan.FromMilliseconds(1000), $"Opening a workspace waited {switched.TotalMilliseconds:0} ms behind another workspace's removal.");
        }
        finally { beat.Stop(); }
        Console.WriteLine("PASS deleting another dirty workspace keeps the open one mounted, the UI thread responsive and switching prompt");
    }

    private static void CreateAndDelete(string directory)
    {
        var project = Path.Combine(directory, "sample-project");
        IsolatedGit.Repository(project);
        Directory.CreateDirectory(Path.Combine(project, ".sharprail"));
        File.WriteAllText(Path.Combine(project, ".sharprail", "workspace.toml"), "version = 1\n\n[clone]\ninclude = [\"cache\"]\n");
        File.WriteAllText(Path.Combine(project, ".gitignore"), "cache/\n");
        for (var folder = 0; folder < 1500; folder++)
        {
            var path = Path.Combine(project, "cache", "d" + folder);
            Directory.CreateDirectory(path);
            for (var file = 0; file < 60; file++) File.WriteAllText(Path.Combine(path, "f" + file), "x");
        }
        using var app = new E2eWorkspace(project, openFiles: false, embedded: true);
        IsolatedGit.Run(project, "add", "-A");
        IsolatedGit.Run(project, "commit", "-m", "fixture");
        Until(() => app.Find<Avalonia.Controls.TextBlock>("BranchLabel").Text == "main");
        GoProjectHome(app);

        var worst = TimeSpan.Zero;
        var last = Stopwatch.GetTimestamp();
        var beat = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Normal, (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var gap = Stopwatch.GetElapsedTime(last, now);
            last = now;
            if (gap > worst) worst = gap;
        });
        beat.Start();
        try
        {
            Settle(300);
            worst = TimeSpan.Zero;
            var path = CreateWorkspaceViaDialog(app);
            // The clone is background work that the open workspace watches landing; measure through it, and delete the full tree.
            Until(() => Directory.Exists(Path.Combine(path, "cache", "d1499")));
            Settle(1000);
            var created = worst;
            worst = TimeSpan.Zero;
            RemoveWorkspace(app, path);
            Until(() => !WorktreePaths(app).Contains(path) && !Directory.Exists(path));
            Settle(300);
            var removed = worst;
            Console.WriteLine($"MEASURE workspace create max UI stall {created.TotalMilliseconds:0} ms, delete {removed.TotalMilliseconds:0} ms");
            Require(created < Limit, $"Creating a workspace stalled the UI thread for {created.TotalMilliseconds:0} ms.");
            Require(removed < Limit, $"Deleting a workspace stalled the UI thread for {removed.TotalMilliseconds:0} ms.");
        }
        finally { beat.Stop(); }
        Console.WriteLine("PASS workspace create and delete keep the UI thread responsive");
    }
}