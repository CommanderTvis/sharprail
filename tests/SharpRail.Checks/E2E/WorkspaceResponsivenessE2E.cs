using System.Diagnostics;

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