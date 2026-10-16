using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>The host's shared workspace watchers: one set per root, healed when the root is replaced, and a bounded pre-warm pool.</summary>
internal static class WatchChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<WorkspaceFileChanges> Next(IAsyncEnumerator<WorkspaceFileChanges> stream, Func<WorkspaceFileChanges, bool> wanted)
    {
        while (await stream.MoveNextAsync())
            if (wanted(stream.Current)) return stream.Current;
        throw new InvalidOperationException("The watch stream ended.");
    }

    private static async Task StartupFailures(string root)
    {
        var workspace = Path.Combine(root, "watch-start-failure");
        Directory.CreateDirectory(workspace);
        var attempts = new List<long>();
        var depth = 0;
        var maximumDepth = 0;
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<Exception>? lateError = null;
        void Start(FileSystemWatcher watcher, Action<Exception> error)
        {
            maximumDepth = Math.Max(maximumDepth, ++depth);
            attempts.Add(Environment.TickCount64);
            lateError = error;
            try
            {
                if (attempts.Count <= 3)
                {
                    if (attempts.Count == 2) throw new IOException("Simulated thrown startup failure");
                    error(new IOException("Simulated synchronous FSEventStream startup failure"));
                    error(new IOException("Duplicate startup error"));
                }
                else
                {
                    watcher.EnableRaisingEvents = true;
                    if (attempts.Count == 4) recovering.TrySetResult();
                    else restarted.TrySetResult();
                }
            }
            finally { depth--; }
        }
        using (WorkspaceWatches.Subscribe(workspace, path =>
        {
            if (path == "recovered.txt") changed.TrySetResult();
        }, Start))
        {
            Require(attempts.Count == 1 && maximumDepth == 1, "A synchronous startup error must return without restarting on its stack.");
            await recovering.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(attempts.Count == 4 && maximumDepth == 1, "Persistent startup failures must recover without recursive callbacks or duplicate retries.");
            Require(attempts[1] - attempts[0] >= 200 && attempts[2] - attempts[1] >= 400 && attempts[3] - attempts[2] >= 800,
                "Consecutive failures must back off, rather than spin on a worker thread.");
            Require(WorkspaceWatches.Inspect(workspace) == (1, 4), "Recovery must preserve the original subscription.");
            File.WriteAllText(Path.Combine(workspace, "recovered.txt"), "watched after failure");
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lateError!(new IOException("Runtime error after a successful start"));
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(WorkspaceWatches.Inspect(workspace) == (1, 5), "An already active watcher must also recover without losing its subscription.");
            lateError!(new IOException("Failure immediately before unsubscribe"));
        }
        var stoppedAt = attempts.Count;
        lateError!(new IOException("Late callback after disposal"));
        await Task.Delay(4300);
        Require(attempts.Count == stoppedAt && WorkspaceWatches.Inspect(workspace) is null,
            "Unsubscribing must cancel queued recovery, and late callbacks must not resurrect a removed entry.");
        using var replacement = WorkspaceWatches.Subscribe(workspace, _ => { });
        lateError!(new IOException("Old watcher callback after replacement"));
        Require(WorkspaceWatches.Inspect(workspace) == (1, 1), "An old callback must not affect a replacement entry for the same path.");
        Console.WriteLine("PASS synchronous watcher errors: bounded deferred backoff, recovery, preserved subscription and cancellation");
    }

    internal static async Task Run(string root)
    {
        await StartupFailures(root);
        var workspace = Path.Combine(root, "watch-shared");
        Directory.CreateDirectory(workspace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var firstEnd = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var secondEnd = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Require(WorkspaceWatches.Inspect(workspace) is null, "Nothing is watched before a subscription.");
        var first = new ProjectServices(workspace).WatchFilesAsync(firstEnd.Token).GetAsyncEnumerator();
        var second = new ProjectServices(workspace).WatchFilesAsync(secondEnd.Token).GetAsyncEnumerator();
        await Next(first, change => change.Rescan); await Next(second, change => change.Rescan);
        Require(WorkspaceWatches.Inspect(workspace) == (2, 1), "Two subscriptions to one workspace must share one set of watchers.");
        File.WriteAllText(Path.Combine(workspace, "shared.txt"), "one");
        await Next(first, change => change.Paths.Contains("shared.txt")); await Next(second, change => change.Paths.Contains("shared.txt"));

        // The same path becomes another directory, as when a worktree is removed and created again.
        Directory.Delete(workspace, recursive: true);
        await Task.Delay(50);
        Directory.CreateDirectory(workspace);
        using var thirdEnd = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var third = new ProjectServices(workspace).WatchFilesAsync(thirdEnd.Token).GetAsyncEnumerator();
        await Next(third, change => change.Rescan);
        Require(WorkspaceWatches.Inspect(workspace) == (3, 2), "A replaced root must restart the shared watchers once and keep its subscribers.");
        await Next(first, change => change.Rescan);
        File.WriteAllText(Path.Combine(workspace, "healed.txt"), "two");
        await Next(first, change => change.Rescan || change.Paths.Contains("healed.txt")); await Next(third, change => change.Rescan || change.Paths.Contains("healed.txt"));

        static async Task End(CancellationTokenSource source, IAsyncEnumerator<WorkspaceFileChanges> stream)
        {
            source.Cancel();
            try { while (await stream.MoveNextAsync()) { } }
            catch (OperationCanceledException) { }
            await stream.DisposeAsync();
        }
        await End(firstEnd, first); await End(secondEnd, second);
        Require(WorkspaceWatches.Inspect(workspace) == (1, 2), "Leaving subscribers must not stop the watchers of the one that remains.");
        await End(thirdEnd, third);
        Require(WorkspaceWatches.Inspect(workspace) is null, "The last subscriber leaving must release the watchers.");

        var pool = Enumerable.Range(0, WorkspaceWatches.PrewarmLimit + 2).Select(index => Path.Combine(root, "watch-pool", "w" + index)).ToArray();
        foreach (var path in pool) Directory.CreateDirectory(path);
        IProjectServices local = new LocalProjectAdapter(new ProjectServices(root));
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, "watch-test");
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "watch-test");
            for (var index = 0; index < pool.Length; index++) await (index % 2 == 0 ? local : remote).PrewarmWorkspaceAsync(pool[index]);
            Require(pool.Count(path => WorkspaceWatches.Inspect(path) is not null) == WorkspaceWatches.PrewarmLimit &&
                WorkspaceWatches.Inspect(pool[0]) is null && WorkspaceWatches.Inspect(pool[1]) is null && WorkspaceWatches.Inspect(pool[^1]) == (0, 1),
                "The pre-warm pool keeps only the most recently warmed workspaces.");
            await local.PrewarmWorkspaceAsync(pool[2]);
            await remote.PrewarmWorkspaceAsync(pool[0]);
            Require(WorkspaceWatches.Inspect(pool[2]) is not null && WorkspaceWatches.Inspect(pool[3]) is null, "Warming a workspace again keeps it ahead of older ones.");
            await remote.PrewarmWorkspaceAsync(Path.Combine(root, "watch-pool", "absent"));
            foreach (var host in new[] { local, remote })
                try { await host.PrewarmWorkspaceAsync("relative"); throw new InvalidOperationException("A relative path was pre-warmed."); }
                catch (Exception error) when (error is ArgumentException or SharpRail.Host.Protocol.RpcException) { }

            using var warmEnd = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var warm = new ProjectServices(pool[^1]).WatchFilesAsync(warmEnd.Token).GetAsyncEnumerator();
            await Next(warm, change => change.Rescan);
            Require(WorkspaceWatches.Inspect(pool[^1]) == (1, 1), "A subscription must take over a pre-warmed watcher instead of starting another.");
            File.WriteAllText(Path.Combine(pool[^1], "warm.txt"), "three");
            await Next(warm, change => change.Paths.Contains("warm.txt"));
            for (var index = 0; index < WorkspaceWatches.PrewarmLimit; index++) await local.PrewarmWorkspaceAsync(pool[index]);
            Require(WorkspaceWatches.Inspect(pool[^1]) == (1, 1), "A watcher with a subscriber is never evicted from the pool.");
            await End(warmEnd, warm);

            Directory.Delete(pool[4], recursive: true);
            await local.PrewarmWorkspaceAsync(pool[5]);
            Require(WorkspaceWatches.Inspect(pool[4]) is null, "A pre-warmed watcher of a workspace that is gone must be reaped.");
        }
        finally { await server.StopAsync(); }
        foreach (var path in pool) WorkspaceWatches.Forget(path);
        Require(pool.All(path => WorkspaceWatches.Inspect(path) is null), "Forgetting a workspace drops its pre-warmed watcher.");
        Console.WriteLine("PASS workspace watchers are shared per root, restart when the root is replaced, and pre-warm within a bounded pool locally and over WebSocket RPC");
    }
}