using System.Net;
using System.Net.Sockets;
using System.Text;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Checks;

internal static class HostListenerChecks
{
    private sealed record Counter(int Value);
    private sealed class Probe : PluginHostModule
    {
        private static readonly PluginMethod<Counter, Counter> Next = new("next");
        public int Activations, Disposals, Calls;
        public override PluginContract Contract => PluginContract.Create("listener-probe", 1, [Next], []);
        public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
        {
            Activations++;
            context.Method(Next, (_, _, _) => ValueTask.FromResult(new Counter(++Calls)));
            return ValueTask.FromResult<PluginDisposer?>(() => { Disposals++; return ValueTask.CompletedTask; });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Run(string root)
    {
        var directory = Path.Combine(root, "listener");
        var other = Path.Combine(root, "listener-other");
        Directory.CreateDirectory(directory); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "first");
        File.WriteAllText(Path.Combine(other, "notes.txt"), "second");
        await using var terminals = OperatingSystem.IsWindows() ? null : new PtyTerminalService("/bin/sh");
        var state = new HostStateStore(directory + "-state") { Terminals = terminals };
        var probe = new Probe();
        await using var plugins = new PluginRuntime(new()
        {
            StateDirectory = null,
            State = state,
            Terminals = terminals,
            Builtins = [(new("listener-probe", "Listener probe", "puzzle", "1.0.0", PluginApi.Generation, 1) { EnabledByDefault = true, Host = "probe.dll" }, probe)]
        });
        await plugins.Start();
        var directState = new LocalStateAdapter(state);
        var directFiles = new LocalProjectAdapter(new ProjectServices(directory, state, plugins.AllowsExternalFile));
        await using var listener = new HostListener(directory, state, terminals, plugins);
        var initial = await plugins.CallAsync(new("listener-probe", "next", new Counter(0), "local"));
        Require(PluginJson.Convert<Counter>(initial).Value == 1, "The embedded plugin must run before serving starts.");
        ITerminalSession? localShell = null, remoteShell = null;
        IAsyncEnumerator<ReadOnlyMemory<byte>>? localOutput = null, remoteOutput = null;
        using var shellTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (terminals is not null)
            {
                localShell = await new LocalTerminalAdapter(terminals).AttachAsync(new("listener-local", directory, "embedded"));
                localOutput = localShell.ReadAsync(shellTimeout.Token).GetAsyncEnumerator();
                await localShell.WriteAsync(Encoding.UTF8.GetBytes("kept=LOCAL; printf 'BEFORE_%s\\n' \"$kept\"\r"));
                await ReadUntil(localOutput, "BEFORE_LOCAL");
            }
            using (var occupied = new TcpListener(IPAddress.Loopback, 0))
            {
                occupied.Start();
                try
                {
                    await listener.StartAsync(IPAddress.Loopback, ((IPEndPoint)occupied.LocalEndpoint).Port, "listener-token");
                    throw new InvalidOperationException("An occupied port must fail.");
                }
                catch (IOException) { }
            }
            Require(listener.Endpoint is null, "A failed start must leave serving off.");
            var previousDirectory = Environment.CurrentDirectory;
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), "This is a workspace file, not host configuration.");
            try
            {
                Environment.CurrentDirectory = directory;
                await listener.StartAsync(IPAddress.Loopback, 0, "listener-token").WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { Environment.CurrentDirectory = previousDirectory; }
            var endpoint = listener.Endpoint!;
            try { await listener.StartAsync(IPAddress.Loopback, 0, "other-token"); throw new Exception("A running listener must not be replaced."); }
            catch (InvalidOperationException) { }
            Require(listener.Endpoint == endpoint && listener.Token == "listener-token", "A duplicate start must leave the active listener unchanged.");
            using var first = new RemoteStateAdapter(endpoint, "listener-token");
            using var second = new RemoteStateAdapter(endpoint, "listener-token");
            using var remotePlugins = new RemotePluginAdapter(endpoint, "listener-token");
            using var firstFiles = new RemoteProjectAdapter(endpoint, "listener-token");
            using var secondFiles = new RemoteProjectAdapter(endpoint, "listener-token");
            using var watchCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var localWatch = directState.WatchAsync(watchCancellation.Token).GetAsyncEnumerator();
            await using var firstWatch = first.WatchAsync(watchCancellation.Token).GetAsyncEnumerator();
            await using var secondWatch = second.WatchAsync(watchCancellation.Token).GetAsyncEnumerator();
            Require(await localWatch.MoveNextAsync() && await firstWatch.MoveNextAsync() && await secondWatch.MoveNextAsync(), "All clients receive the live host snapshot.");
            var changed = await second.ChangeAsync([HostStateChange.Setting("theme", "light")]);
            Require(await localWatch.MoveNextAsync() && await firstWatch.MoveNextAsync() && await secondWatch.MoveNextAsync() &&
                localWatch.Current.Revision == changed.Revision && firstWatch.Current.Revision == changed.Revision && secondWatch.Current.Revision == changed.Revision,
                "Two remote clients and the embedded client must share one state broadcast.");
            await firstFiles.OpenProjectAsync(directory); await secondFiles.OpenProjectAsync(other);
            Require((await firstFiles.ReadFileAsync("notes.txt")).Text == "first" && (await secondFiles.ReadFileAsync("notes.txt")).Text == "second" &&
                (await directFiles.ReadFileAsync("notes.txt")).Text == "first", "Remote workspace selection must not replace the embedded client's project session.");
            Require(PluginJson.Convert<Counter>(await remotePlugins.CallAsync(new("listener-probe", "next", new Counter(0), "remote"))).Value == 2 &&
                PluginJson.Convert<Counter>(await plugins.CallAsync(new("listener-probe", "next", new Counter(0), "local"))).Value == 3 && probe.Activations == 1,
                "Serving must expose the existing plugin instance without reactivation.");
            using var wrong = new RemoteStateAdapter(endpoint, "wrong-token");
            try { await wrong.GetHandshakeAsync(); throw new InvalidOperationException("A bad token must be rejected."); }
            catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.Unauthenticated) { }
            using var remoteTerminals = new RemoteTerminalAdapter(endpoint, "listener-token");
            if (terminals is not null)
            {
                remoteShell = await remoteTerminals.AttachAsync(new("listener-remote", other, "remote"));
                remoteOutput = remoteShell.ReadAsync(shellTimeout.Token).GetAsyncEnumerator();
                await remoteShell.WriteAsync(Encoding.UTF8.GetBytes("kept=REMOTE; printf 'BEFORE_%s\\n' \"$kept\"\r"));
                await ReadUntil(remoteOutput, "BEFORE_REMOTE");
            }
            await listener.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(listener.Endpoint is null && probe.Disposals == 0 && localShell?.Exit.IsCompleted != true,
                "Stopping the listener must preserve embedded terminals and plugins.");
            watchCancellation.Cancel();
            if (remoteOutput is not null) { await remoteOutput.DisposeAsync(); remoteOutput = null; }
            if (remoteShell is not null) { await remoteShell.DisposeAsync(); remoteShell = null; }
            Require(PluginJson.Convert<Counter>(await plugins.CallAsync(new("listener-probe", "next", new Counter(0), "local"))).Value == 4,
                "Direct plugin calls must continue while serving is stopped.");
            if (localShell is not null)
            {
                await localShell.WriteAsync(Encoding.UTF8.GetBytes("printf 'AFTER_%s\\n' \"$kept\"\r"));
                await ReadUntil(localOutput!, "AFTER_LOCAL");
            }
            await listener.StartAsync(IPAddress.Loopback, endpoint.Port, "replacement-token");
            using var restarted = new RemoteStateAdapter(listener.Endpoint!, "replacement-token");
            Require((await restarted.GetStateAsync()).Revision == state.Current.Revision, "Restarted serving must retain the live state.");
            try { await first.GetHandshakeAsync(); throw new InvalidOperationException("The old token must be rejected after rotation."); }
            catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.Unauthenticated) { }
            if (terminals is not null)
            {
                await using var resumed = await terminals.AttachAsync(new("listener-remote", other, "local-after-stop"));
                Require(!resumed.Created, "Stopping serving must not end a remote client's shell.");
                await resumed.WriteAsync(Encoding.UTF8.GetBytes("printf 'AFTER_%s\\n' \"$kept\"\r"));
                await using var output = resumed.ReadAsync(shellTimeout.Token).GetAsyncEnumerator();
                await ReadUntil(output, "AFTER_REMOTE");
            }
            await listener.DisposeAsync();
            await directState.ChangeAsync([HostStateChange.Setting("theme", "dark")]);
            Require(probe.Disposals == 0 && probe.Activations == 1 && (await directFiles.ReadFileAsync("notes.txt")).Text == "first",
                "Disposing serving must leave the caller-owned host usable.");
            try { await listener.StartAsync(IPAddress.Loopback, 0, "token"); throw new InvalidOperationException("A disposed listener must not restart."); }
            catch (ObjectDisposedException) { }
        }
        finally
        {
            if (remoteOutput is not null) await remoteOutput.DisposeAsync();
            if (localOutput is not null) await localOutput.DisposeAsync();
            if (remoteShell is not null) await remoteShell.DisposeAsync();
            if (localShell is not null) await localShell.DisposeAsync();
        }
        Console.WriteLine("PASS spontaneous serving: two authenticated clients share embedded state and plugins, independent workspaces, bind failure recovery, token rotation and shells surviving stop/restart");
    }

    private static async Task ReadUntil(IAsyncEnumerator<ReadOnlyMemory<byte>> output, string marker)
    {
        var text = new StringBuilder();
        while (await output.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
        {
            text.Append(Encoding.UTF8.GetString(output.Current.Span));
            if (text.ToString().Contains(marker, StringComparison.Ordinal)) return;
        }
        throw new InvalidOperationException("Terminal did not produce " + marker);
    }
}