using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
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

    internal static async Task Run(string root)
    {
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
                catch (Exception error) when (error is ArgumentException or Grpc.Core.RpcException) { }

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
        Console.WriteLine("PASS workspace watchers are shared per root, restart when the root is replaced, and pre-warm within a bounded pool locally and over gRPC");
    }
}