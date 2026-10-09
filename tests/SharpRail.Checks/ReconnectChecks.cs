using System.Net;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Protocol;
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
        await ReplayedMutations();
        await ReplayCache();
        await ReplayedSave(Path.Combine(root, "replayed-save"));
        await TimeoutOverride();
    }

    private static double Seconds(string timeout) => double.Parse(timeout[..^1], System.Globalization.CultureInfo.InvariantCulture) *
        timeout[^1] switch { 'H' => 3600, 'M' => 60, 'S' => 1, 'm' => 1e-3, 'u' => 1e-6, _ => 1e-9 };

    /// <summary>A caller's timeout replaces the adapter's deadline for the calls it wraps, and only for those.</summary>
    private static async Task TimeoutOverride()
    {
        var wait = TimeSpan.Zero;
        var sent = 0.0;
        var host = new FakeState(HostProtocol.Current, (_, _) => new(new HostState()), () => Task.Delay(wait));
        await using var server = await Serve(host, inspect: context => sent = Seconds(context.Request.Headers["grpc-timeout"].ToString()));
        using var remote = new RemoteStateAdapter(new Uri(Address(server)), Token);
        await remote.GetStateAsync();
        Require(sent is > 50 and <= 60, $"A state call waits a minute by default, not {sent} s.");
        using (HostRequest.WithTimeout(TimeSpan.FromMinutes(10)))
        {
            await remote.GetStateAsync();
            Require(sent is > 590 and <= 600, $"A raised timeout must reach the call, not {sent} s.");
            await remote.ChangeAsync([HostStateChange.SavePreset("slow", "{}")]);
            Require(sent is > 590 and <= 600, $"A raised timeout must reach a mutation, not {sent} s.");
            using (HostRequest.WithTimeout(TimeSpan.FromSeconds(2)))
            {
                await Task.Run(async () => await remote.GetStateAsync());
                Require(sent <= 2, $"The innermost timeout applies, also on another thread of the same flow, not {sent} s.");
            }
            await remote.GetStateAsync();
            Require(sent > 590, "Leaving a scope restores the enclosing timeout.");
        }
        await remote.GetStateAsync();
        Require(sent is > 50 and <= 60, "Leaving the last scope restores the adapter's deadline.");
        wait = TimeSpan.FromSeconds(2);
        using (HostRequest.WithTimeout(TimeSpan.FromMilliseconds(300)))
            Require(await Status(async () => await remote.GetStateAsync()) == StatusCode.DeadlineExceeded, "The overriding timeout is the one that expires.");
        Console.WriteLine("PASS a per-request timeout replaces the adapter deadline within its scope");
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Require(waited < 20_000, "Timed out waiting for a host or client transition.");
            await Task.Delay(20);
        }
    }

    private static int Port(WebApplication app) => new Uri(Address(app)).Port;

    /// <summary>A mutation whose connection dies in flight is sent again under its id and answered with the first run's outcome.</summary>
    private static async Task ReplayedMutations()
    {
        var runs = 0;
        var cancelled = false;
        TaskCompletionSource gate = new();
        var host = new FakeState(HostProtocol.RequestReplay, async (changes, token) =>
        {
            var run = Interlocked.Increment(ref runs);
            await gate.Task;
            cancelled |= token.IsCancellationRequested;
            if (changes[0].Key == "refuse") throw new InvalidOperationException("refused once");
            return new() { Revision = run };
        });
        await using var server = await Serve(host);
        using var proxy = new CutProxy(Port(server));
        var connection = new HostConnection();
        using var remote = new RemoteStateAdapter(proxy.Endpoint, Token, connection);
        await remote.GetHandshakeAsync();
        Require(connection.HostVersion == HostProtocol.RequestReplay, "The connection learns the host's version from the handshake.");

        async Task<string> Lost(string key)
        {
            var before = runs;
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var call = Failure(async () => Require((await remote.ChangeAsync([HostStateChange.SavePreset(key, "{}")])).Revision == before + 1, "A replay must return the first run's reply."));
            await Wait(() => runs == before + 1);
            proxy.Cut();
            gate.SetResult();
            await Task.Delay(400);
            Require(!call.IsCompleted, "A mutation lost with its connection stays pending while the host is unreachable.");
            proxy.Allow();
            var outcome = await call;
            Require(runs == before + 1, $"The host ran a replayed mutation {runs - before} times.");
            return outcome;
        }

        Require(await Lost("kept") == "ok", "An accepted mutation must not report a failure after its reply was lost.");
        Require(await Lost("refuse") == "unnamed failure", "A refused mutation replays its refusal instead of running again.");
        Require(!cancelled && proxy.Accepted >= 3, "The host finishes a mutation whose connection dropped.");

        // An older host has no deduplication, so a lost mutation is not sent again and fails to the caller as before.
        connection.HostVersion = HostProtocol.RequestReplay - 1;
        var ran = runs;
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = Failure(async () => await remote.ChangeAsync([HostStateChange.SavePreset("old", "{}")]));
        await Wait(() => runs == ran + 1);
        proxy.Cut();
        Require(await failing == "unnamed failure", "Without host deduplication a lost mutation fails to its caller.");
        gate.SetResult(); proxy.Allow();
        await Task.Delay(700);
        Require(runs == ran + 1, "Nothing is replayed against a host that predates request replay.");
        Require(HostProtocol.Current >= HostProtocol.RequestReplay == new HostHandshake(HostProtocol.Current, "").Supports(HostProtocol.RequestReplay),
            "Replay is gated on the host's reported version.");
        Console.WriteLine("PASS a mutation lost with its connection is replayed under its request id and runs once");
    }

    private static CallContext Call(string client, string request, string? resume = null)
    {
        var headers = new Metadata { { ReplayHeaders.Client, client }, { ReplayHeaders.Request, request } };
        if (resume is not null) headers.Add(ReplayHeaders.Resume, resume);
        return new(new CallOptions(headers, DateTime.UtcNow.AddSeconds(20)));
    }

    private static StateChangeRequest Preset(string name) => new() { Changes = [new() { Kind = "preset-save", Key = name, Value = "{}" }] };

    private static async Task<StatusCode?> Status(Func<Task> action)
    {
        try { await action(); return null; }
        catch (RpcException error) { return error.StatusCode; }
    }

    /// <summary>The host's side of the bargain, driven with explicit request ids.</summary>
    private static async Task ReplayCache()
    {
        var runs = 0;
        TaskCompletionSource gate = new();
        gate.SetResult();
        var host = new FakeState(HostProtocol.RequestReplay, async (_, _) => { var run = Interlocked.Increment(ref runs); await gate.Task; return new() { Revision = run }; });
        await using var server = await Serve(host, maxRequests: 2);
        using var channel = GrpcChannel.ForAddress(Address(server));
        var rpc = channel.CreateGrpcService<IStateRpc>();
        Require((await rpc.ChangeAsync(Preset("a"), Call("one", "1", "1"))).Revision == 1 && (await rpc.ChangeAsync(Preset("a"), Call("one", "1", "1"))).Revision == 1 && runs == 1,
            "A reply stays replayable until its client stops naming the request.");
        Require(await Status(async () => await rpc.ChangeAsync(Preset("b"), Call("one", "1", "1"))) == StatusCode.InvalidArgument && runs == 1,
            "A request id reused with another payload is refused.");
        Require((await rpc.ChangeAsync(Preset("a"), Call("two", "1", "1"))).Revision == 2, "Request ids are scoped to their client.");
        Require((await rpc.ChangeAsync(Preset("b"), Call("one", "2", "2"))).Revision == 3, "A second request runs.");
        Require((await rpc.ChangeAsync(Preset("a"), Call("one", "1", "1,2"))).Revision == 4,
            "A result its client no longer names as unresolved is released, so the id runs afresh.");
        Require((await rpc.ChangeAsync(Preset("c"), new CallContext(new CallOptions(deadline: DateTime.UtcNow.AddSeconds(20))))).Revision == 5 &&
            (await rpc.ChangeAsync(Preset("c"), new CallContext(new CallOptions(deadline: DateTime.UtcNow.AddSeconds(20))))).Revision == 6,
            "A call without replay metadata runs every time, as from an older client.");

        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = rpc.ChangeAsync(Preset("d"), Call("full", "1", "1")).AsTask();
        var second = rpc.ChangeAsync(Preset("e"), Call("full", "2", "1,2")).AsTask();
        await Wait(() => runs == 8);
        Require(await Status(async () => await rpc.ChangeAsync(Preset("f"), Call("full", "3", "1,2,3"))) == StatusCode.ResourceExhausted && runs == 8,
            "A client holding its limit of unresolved requests is refused another.");
        gate.SetResult();
        await Task.WhenAll(first, second);

        await using var small = await Serve(host, maxWeight: 1);
        using var smallChannel = GrpcChannel.ForAddress(Address(small));
        var tight = smallChannel.CreateGrpcService<IStateRpc>();
        await tight.ChangeAsync(Preset("g"), Call("one", "1", "1"));
        var before = runs;
        Require(await Status(async () => await tight.ChangeAsync(Preset("g"), Call("one", "1", "1"))) == StatusCode.FailedPrecondition && runs == before,
            "A reply beyond the retention budget is not kept, and its request still never runs twice.");
        Console.WriteLine("PASS the host deduplicates requests per client and releases results on resume");
    }

    /// <summary>The real host: a save sent twice under one id succeeds twice, where a second run would report a conflict.</summary>
    private static async Task ReplayedSave(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "note.txt");
        await File.WriteAllTextAsync(file, "before");
        await using var server = RemoteServer.Create(directory, IPAddress.Loopback, 0, Token);
        await server.StartAsync();
        using var channel = GrpcChannel.ForAddress(Address(server));
        var rpc = channel.CreateGrpcService<IProjectRpc>();
        static CallContext Authorized(CallContext call)
        {
            call.RequestHeaders!.Add("authorization", "Bearer " + Token);
            return call;
        }
        var save = new SaveFileRequest { WorkspaceRoot = Path.GetFullPath(directory), Path = "note.txt", OriginalText = "before", Text = "after" };
        await rpc.SaveFileAsync(save, Authorized(Call("editor", "1", "1")));
        await rpc.SaveFileAsync(save, Authorized(Call("editor", "1", "1")));
        Require(await File.ReadAllTextAsync(file) == "after", "The save applied.");
        Require(await Status(async () => await rpc.SaveFileAsync(save, Authorized(Call("editor", "2", "2")))) == StatusCode.FailedPrecondition,
            "The same save under a new id is a second save and meets the conflict check.");
        await server.StopAsync();
        Console.WriteLine("PASS a replayed save answers with its first outcome through the real host");
    }

    private static async Task<string> Failure(Func<Task> action)
    {
        try { await action(); return "ok"; }
        catch (HostException error) { return error.Code + ": " + error.Message; }
        catch (Exception error) { return "unnamed " + (error is RpcException or IOException or ArgumentException ? "failure" : error.GetType().Name); }
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
            ValueTask<HostState> Refuse(IReadOnlyList<HostStateChange> changes, CancellationToken token) => throw new HostException(code, "refused " + changes[0].Key);
            await using var refusing = await Serve(new FakeState(HostProtocol.Current, Refuse));
            using var state = new RemoteStateAdapter(new Uri(Address(refusing)), Token);
            Require(await Failure(async () => await state.ChangeAsync([HostStateChange.OpenProject(repo)])) == $"{code}: refused {repo}",
                $"{code} must survive the state service's transport.");
            Require(await Failure(async () => await new LocalStateAdapter(new FakeState(HostProtocol.Current, Refuse)).ChangeAsync([HostStateChange.OpenProject(repo)])) == $"{code}: refused {repo}",
                $"{code} must pass through the local adapter.");
        }
        Console.WriteLine("PASS named host failures reach local and remote clients as the same typed exception");
    }

    /// <summary>A gRPC host serving only shared state from <paramref name="state"/>.</summary>
    private static async Task<WebApplication> Serve(IHostStateService state, int maxRequests = 512, long maxWeight = 16 * 1024 * 1024,
        Action<Microsoft.AspNetCore.Http.HttpContext>? inspect = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(services => new RequestReplayCache(services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(), maxRequests, maxWeight));
        builder.Services.AddCodeFirstGrpc();
        var app = builder.Build();
        if (inspect is not null) app.Use((context, next) => { inspect(context); return next(context); });
        app.MapGrpcService<StateRpc>();
        await app.StartAsync();
        return app;
    }

    private sealed class FakeState(int version, Func<IReadOnlyList<HostStateChange>, CancellationToken, ValueTask<HostState>> change, Func<Task>? read = null) : IHostStateService
    {
        public ValueTask<HostHandshake> GetHandshakeAsync(CancellationToken cancellationToken = default) => new(new HostHandshake(version, ""));
        public async ValueTask<HostState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            if (read is not null) await read();
            return new();
        }
        public ValueTask<HostState> ChangeAsync(IReadOnlyList<HostStateChange> changes, CancellationToken cancellationToken = default) => change(changes, cancellationToken);
        public async IAsyncEnumerable<HostState> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        public async IAsyncEnumerable<LifecycleEvent> WatchLifecycleAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
    }
}