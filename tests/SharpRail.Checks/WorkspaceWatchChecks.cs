using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class WorkspaceWatchChecks
{
    public static async Task Run(string root)
    {
        var local = Path.Combine(root, "watch-local");
        Directory.CreateDirectory(local);
        await Cases(new LocalProjectAdapter(new ProjectServices(local)), local);
        var remote = Path.Combine(root, "watch-remote");
        Directory.CreateDirectory(remote);
        await using var server = RemoteServer.Create(remote, IPAddress.Loopback, 0, "watch-files", Path.Combine(root, "watch-state"));
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var host = new RemoteProjectAdapter(address, "watch-files");
            await Cases(host, remote);
            await host.OpenProjectAsync(remote);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var stream = host.WatchFilesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            Require(await stream.MoveNextAsync(), "A file stream is live before shutdown.");
            var waiting = stream.MoveNextAsync().AsTask();
            await server.StopAsync(timeout.Token);
            try { Require(!await waiting, "Host shutdown closes its file streams."); }
            catch (Grpc.Core.RpcException) { }
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS workspace file streams: local/remote ready, nested write, rename, Git metadata, root capture, cancellation and host shutdown");
    }

    private static async Task Cases(IProjectServices host, string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        Directory.CreateDirectory(Path.Combine(root, ".git", "refs", "heads"));
        await host.OpenProjectAsync(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var stream = host.WatchFilesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Require(await stream.MoveNextAsync() && stream.Current.Paths.Count == 0, "The first frame confirms that the watchers are active.");
        async Task Changed(params string[] expected)
        {
            var seen = new HashSet<string>();
            while (!expected.All(seen.Contains))
            {
                Require(await stream.MoveNextAsync(), "The stream remains live.");
                seen.UnionWith(stream.Current.Paths);
                Require(stream.Current.Paths.All(path => !path.StartsWith(".git/", StringComparison.Ordinal)), "Git metadata is never a workspace file revision.");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "before.txt"), "first");
        await Changed("nested/before.txt");
        File.Move(Path.Combine(root, "nested", "before.txt"), Path.Combine(root, "nested", "after.txt"));
        await Changed("nested/before.txt", "nested/after.txt");
        await File.WriteAllTextAsync(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main");
        while (!stream.Current.GitChanged) Require(await stream.MoveNextAsync(), "HEAD changes arrive as Git metadata.");
        var other = Path.Combine(root, "other-root");
        Directory.CreateDirectory(other);
        await host.OpenProjectAsync(other);
        await File.WriteAllTextAsync(Path.Combine(root, "captured.txt"), "original workspace");
        await Changed("captured.txt");
        timeout.Cancel();
        try { Require(!await stream.MoveNextAsync(), "Cancellation closes the file stream."); }
        catch (Exception error) when (error is OperationCanceledException or Grpc.Core.RpcException) { return; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}