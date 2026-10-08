using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ProtoBuf.Grpc.Server;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>Named host failures and reconnect behaviour, through the embedded host and a real gRPC host.</summary>
internal static class ReconnectChecks
{
    private const string Token = "reconnect-checks";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    internal static async Task Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "reconnect-git"));
        await NamedErrors(Path.Combine(root, "named-errors"));
    }

    private static async Task<string> Failure(Func<Task> action)
    {
        try { await action(); return "ok"; }
        catch (HostException error) { return error.Code + ": " + error.Message; }
        catch (Exception error) { return "unnamed " + (error is Grpc.Core.RpcException or IOException or ArgumentException ? "failure" : error.GetType().Name); }
    }

    private static async Task NamedErrors(string directory)
    {
        var repo = IsolatedGit.Repository(Path.Combine(directory, "repo"));
        var gone = new string('0', 40);
        var local = new LocalProjectAdapter(new ProjectServices(repo));
        await using var server = RemoteServer.Create(repo, IPAddress.Loopback, 0, Token);
        await server.StartAsync();
        using var remote = new RemoteProjectAdapter(new Uri(Address(server)), Token);
        await remote.OpenProjectAsync(repo);
        var logs = new List<string[]>();
        foreach (var host in new IProjectServices[] { local, remote })
            logs.Add(
            [
                await Failure(async () => await host.GetGitAsync(gone, scope: "commit")),
                await Failure(async () => await host.GetDiffAsync("README.md", "commit", gone)),
                await Failure(async () => await host.GetDiffSidesAsync("README.md", "commit", gone)),
                await Failure(async () => await host.ReadContentBytesAsync("README.md", gone)),
                await Failure(async () => await host.GetGitAsync("--all", scope: "commit")),
                await Failure(async () => await host.ReadFileAsync("missing.txt")),
                await Failure(async () => await host.GetGitAsync())
            ]);
        Require(logs[0].SequenceEqual(logs[1]), "Local and remote hosts name failures differently:\n" + string.Join('\n', logs[0].Zip(logs[1])));
        Require(logs[0][..4].All(line => line == $"{HostErrorCode.UnknownCommit}: Unknown commit: {gone}"), "A vanished commit is named on every read that resolves it: " + string.Join(" | ", logs[0]));
        Require(logs[0][4] == "unnamed failure" && logs[0][5] == "unnamed failure" && logs[0][6] == "ok", "Only named failures carry a code: " + string.Join(" | ", logs[0]));
        await server.StopAsync();

        // No operation refuses with these two yet; a host that does must reach the client with the code intact.
        foreach (var code in new[] { HostErrorCode.NotGit, HostErrorCode.AlreadyOpen })
        {
            await using var refusing = await Serve(new Refusing(code));
            using var state = new RemoteStateAdapter(new Uri(Address(refusing)), Token);
            Require(await Failure(async () => await state.ChangeAsync([HostStateChange.OpenProject(repo)])) == $"{code}: refused {repo}",
                $"{code} must survive the state service's transport.");
            Require(await Failure(async () => await new LocalStateAdapter(new Refusing(code)).ChangeAsync([HostStateChange.OpenProject(repo)])) == $"{code}: refused {repo}",
                $"{code} must pass through the local adapter.");
        }
        Console.WriteLine("PASS named host failures reach local and remote clients as the same typed exception");
    }

    /// <summary>A gRPC host serving only shared state from <paramref name="state"/>.</summary>
    private static async Task<WebApplication> Serve(IHostStateService state)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(state);
        builder.Services.AddCodeFirstGrpc();
        var app = builder.Build();
        app.MapGrpcService<StateRpc>();
        await app.StartAsync();
        return app;
    }

    private sealed class Refusing(HostErrorCode code) : IHostStateService
    {
        public ValueTask<HostHandshake> GetHandshakeAsync(CancellationToken cancellationToken = default) => new(new HostHandshake(HostProtocol.Current, ""));
        public ValueTask<HostState> GetStateAsync(CancellationToken cancellationToken = default) => new(new HostState());
        public ValueTask<HostState> ChangeAsync(IReadOnlyList<HostStateChange> changes, CancellationToken cancellationToken = default)
            => throw new HostException(code, "refused " + changes[0].Key);
        public async IAsyncEnumerable<HostState> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}