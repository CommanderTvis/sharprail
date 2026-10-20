using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Tailscale;
using SharpRail.UI.Android;

namespace SharpRail.Checks;

/// <summary>
/// The opt-in `--headscale` mode: a real SharpRail host and a real client on two distinct tailnet nodes of a
/// throwaway Headscale control server. Provisioned by `scripts/build-headscale-test.sh`; see tests/SharpRail.Checks/SPEC.md.
/// </summary>
internal static class HeadscaleChecks
{
    private const string RequireVariable = "SHARPRAIL_REQUIRE_HEADSCALE";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(170);

    internal static async Task Run(string root)
    {
        var tools = Path.Combine(Directory.GetCurrentDirectory(), ".tools", "headscale-test");
        var library = Path.Combine(tools, OperatingSystem.IsMacOS() ? "libSharpRailTailscale.dylib" : "libSharpRailTailscale.so");
        var missing = new[] { "headscale", "tsforward" }.Select(name => Path.Combine(tools, name)).Append(library).Where(path => !File.Exists(path)).ToArray();
        var reason = !(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) ? "Headscale checks run on macOS and Linux only."
            : missing.Length > 0 ? "Run `sh scripts/build-headscale-test.sh` first; missing " + string.Join(", ", missing.Select(path => Path.GetRelativePath(Directory.GetCurrentDirectory(), path))) + "."
            : null;
        if (reason is not null)
        {
            if (Environment.GetEnvironmentVariable(RequireVariable) == "1") throw new InvalidOperationException("Headscale prerequisites are required (" + RequireVariable + "=1): " + reason);
            Console.WriteLine("SKIP headscale connectivity: " + reason + " Set " + RequireVariable + "=1 to fail instead of skipping.");
            return;
        }
        var clock = Stopwatch.StartNew();
        var fixture = Path.Combine(root, "headscale");
        Directory.CreateDirectory(fixture);
        using var timeout = new CancellationTokenSource(Budget);
        ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; timeout.Cancel(); };
        Console.CancelKeyPress += interrupt;
        var processes = new List<Child>();
        void Stage(string name) => Console.WriteLine($"  headscale {clock.Elapsed.TotalSeconds,6:0.0}s  {name}");
        try
        {
            var token = timeout.Token;
            var control = await Headscale.StartAsync(Path.Combine(tools, "headscale"), Path.Combine(fixture, "control"), processes, token);
            Stage($"headscale started at {control.Url} with config {control.Config}");
            var hostKey = await control.PreAuthKeyAsync("sharprail-host", token);
            var clientKey = await control.PreAuthKeyAsync("sharprail-client", token);
            Stage("pre-auth keys created for users sharprail-host and sharprail-client");

            // The host: a real Kestrel host with its pairing token, on loopback only. Its tailnet presence is a
            // separate userspace node that forwards the RPC and pairing ports to it.
            var pairing = TailscaleClient.NewPairing("sharprail-host");
            await using var rpc = RemoteServer.Create(root, IPAddress.Loopback, 0, pairing.Token, stateDirectory: Path.Combine(fixture, "host-state"));
            await rpc.StartAsync(token);
            var rpcPort = PortOf(rpc);
            var advertised = "";
            var pairRequests = 0;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            await using var bootstrap = builder.Build();
            bootstrap.MapGet("/sharprail", () => Results.Json(new { name = "Headscale fixture" }));
            bootstrap.MapPost("/sharprail/pair", (HttpContext context) =>
            {
                Check(context.Request.Headers["X-SharpRail-Pairing"] == pairing.Token, "Pairing lost its approval credential in transit.");
                return Results.Json(new { state = Interlocked.Increment(ref pairRequests) == 1 ? "pending" : "approved", address = advertised });
            });
            await bootstrap.StartAsync(token);

            var hostNode = await Child.StartForwarderAsync(Path.Combine(tools, "tsforward"), Path.Combine(fixture, "host-node"), control.Url, hostKey,
                "sharprail-host", [$"{rpcPort}=127.0.0.1:{rpcPort}", $"{TailscaleContract.DiscoveryPort}=127.0.0.1:{PortOf(bootstrap)}"], processes, token);
            Stage($"host node {hostNode.Ip} joined");
            advertised = $"http://{hostNode.Ip}:{rpcPort}/";

            // The client: the embedded library the Android app ships, loaded as a desktop dylib, on its own identity.
            using var native = new NativeNode(library, control.Url, clientKey);
            await using var network = new EmbeddedTailscaleNetwork(Path.Combine(fixture, "client-node"), native.Call);
            await network.StartAsync(token);
            TailscalePeer? seen = null;
            for (var attempt = 0; seen is null; attempt++)
            {
                if (attempt > 0) await Task.Delay(500, token);
                try { seen = (await network.PeersAsync(token)).FirstOrDefault(peer => peer.Address == hostNode.Ip); }
                catch (IOException) { }
                Check(attempt < 100, "The client node never saw the host node in its netmap.");
            }
            Stage($"client node joined (embedded library) and sees the host at {hostNode.Ip}");

            using var client = new TailscaleClient(network);
            // Probing a peer can lose the race against the first direct path between the two nodes.
            Check(await WithRetry(async () =>
            {
                var peers = await client.DiscoverAsync(token);
                if (!peers.Any(peer => peer.Address == hostNode.Ip)) throw new IOException("Discovery did not list the host node yet.");
                return true;
            }, token), "Discovery did not list the host node.");
            var connection = await client.PairAsync(pairing, null, token);
            Check(pairRequests == 2 && connection.Address == advertised, "Pairing did not go pending, then approved, over the tailnet.");
            Stage("discovery and pairing over the tailnet");

            var address = new Uri(connection.Address);
            using var route = RpcTransports.RegisterTransport(address, network.CreateHandler);
            using var remote = new RemoteStateAdapter(address, connection.Token);
            var handshake = await WithRetry(() => remote.GetHandshakeAsync(token).AsTask(), token);
            Check(handshake.ProtocolVersion > 0, "The authenticated handshake over the tailnet failed.");
            var before = await remote.GetStateAsync(token);
            Stage($"authenticated RPC to {address} over the tailnet: protocol {handshake.ProtocolVersion}");

            using var wrong = new RemoteStateAdapter(address, "wrong-" + connection.Token);
            try { await wrong.GetStateAsync(token); throw new InvalidOperationException("The host accepted a client without the pairing token."); }
            catch (Exception error) when (error is HostException or RpcException) { }

            // A host-side actor (direct loopback, not the tailnet) changes state; the tailnet client must receive it.
            using var local = new RemoteStateAdapter(new Uri($"http://127.0.0.1:{rpcPort}/"), pairing.Token);
            var enabled = before.Settings.NotificationsEnabled;
            using var watching = CancellationTokenSource.CreateLinkedTokenSource(token);
            await using var watch = remote.WatchAsync(watching.Token).GetAsyncEnumerator(watching.Token);
            Check(await watch.MoveNextAsync(), "The watch delivered no initial snapshot.");
            await local.ChangeAsync([HostStateChange.Setting("notifications", (!enabled).ToString())], token);
            var broadcast = await Until(watch, state => state.Settings.NotificationsEnabled == !enabled, token);
            Check(broadcast, "The host broadcast did not arrive over the tailnet.");
            Stage("host broadcast received over the tailnet");

            // A brief outage: the forwarder drops the live connections and refuses new ones. The watch must end,
            // a change made meanwhile must be there when the client reconnects, and calls must work again.
            await hostNode.CommandAsync("pause", token);
            var dropped = false;
            try { while (await watch.MoveNextAsync()) { } dropped = true; }
            catch (Exception error) when (error is RpcException or IOException or HostException) { dropped = true; }
            Check(dropped, "The watch survived a dropped tailnet connection.");
            await local.ChangeAsync([HostStateChange.Setting("notifications", enabled.ToString())], token);
            await Task.Delay(1500, token);
            await hostNode.CommandAsync("resume", token);
            var restored = await WithRetry(async () =>
            {
                var state = await remote.GetStateAsync(token);
                Check(state.Settings.NotificationsEnabled == enabled, "The reconnected client read stale state.");
                return state;
            }, token);
            await using var resumed = remote.WatchAsync(token).GetAsyncEnumerator(token);
            Check(await WithRetry(async () => await resumed.MoveNextAsync(), token) && resumed.Current.Settings.NotificationsEnabled == enabled, "Resubscribing after reconnect gave no fresh snapshot.");
            Stage("disconnect, offline change and reconnect");

            var remotes = hostNode.Accepted.Where(accepted => accepted.Port == rpcPort.ToString()).Select(accepted => accepted.Remote.Split(':')[0]).Distinct().ToArray();
            Check(remotes.Length == 1 && remotes[0] != hostNode.Ip && IPAddress.TryParse(remotes[0], out var from) && TailscaleCli.IsTailnetAddress(from),
                "The host node did not see RPC traffic from a distinct tailnet client identity: " + string.Join(",", remotes) + " (accepted: " + string.Join(" ", hostNode.Accepted.Select(accepted => accepted.Port + "<-" + accepted.Remote)) + ")");
            Check(hostNode.Accepted.Count(accepted => accepted.Port == rpcPort.ToString()) >= 3, "Too few tailnet connections reached the host after the reconnect.");
            Stage($"PASS: client {remotes[0]} reached host {hostNode.Ip} through Headscale");
            Console.WriteLine($"PASS headscale connectivity: discovery, pairing, authenticated RPC, broadcast and reconnect over a real tailnet in {clock.Elapsed.TotalSeconds:0.0}s");
        }
        catch (Exception error)
        {
            foreach (var child in processes) Console.Error.WriteLine($"----- {child.Name} log (tail) -----\n{child.Tail()}");
            Console.Error.WriteLine("----- the embedded client node discards its own log; see its status in the failure below -----");
            throw new InvalidOperationException("Headscale connectivity failed after " + clock.Elapsed.TotalSeconds.ToString("0.0") + "s: " + error.Message, error);
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
            foreach (var child in processes.AsEnumerable().Reverse()) child.Dispose();
            try { Directory.Delete(fixture, true); } catch (IOException) { }
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static int PortOf(WebApplication app) => new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;

    private static async Task<bool> Until(IAsyncEnumerator<HostState> watch, Func<HostState, bool> predicate, CancellationToken token)
    {
        while (await watch.MoveNextAsync()) { token.ThrowIfCancellationRequested(); if (predicate(watch.Current)) return true; }
        return false;
    }

    /// <summary>The first calls after a node joins or a route returns can still be establishing a path.</summary>
    private static async Task<T> WithRetry<T>(Func<Task<T>> call, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await call(); }
            catch (Exception error) when (attempt < 40 && !token.IsCancellationRequested && error is RpcException or IOException or HostException or HttpRequestException)
            { await Task.Delay(500, token); }
        }
    }

    private sealed class NativeNode(string path, string controlUrl, string authKey) : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint CallFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string operation, [MarshalAs(UnmanagedType.LPUTF8Str)] string input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeFunction(nint value);

        private readonly nint library = NativeLibrary.Load(path);
        private CallFunction? call;
        private FreeFunction? free;

        /// <summary>The embedded library's C ABI; enrolment into the throwaway control server is added to its start options.</summary>
        internal string Call(string operation, string input)
        {
            call ??= Marshal.GetDelegateForFunctionPointer<CallFunction>(NativeLibrary.GetExport(library, "sr_ts_call"));
            free ??= Marshal.GetDelegateForFunctionPointer<FreeFunction>(NativeLibrary.GetExport(library, "sr_ts_free"));
            if (operation == "start")
            {
                var options = JsonSerializer.Deserialize<Dictionary<string, string>>(input)!;
                options["controlUrl"] = controlUrl;
                options["authKey"] = authKey;
                input = JsonSerializer.Serialize(options);
            }
            var reply = call(operation, input);
            try { return Marshal.PtrToStringUTF8(reply) ?? throw new IOException("The embedded library returned nothing."); }
            finally { free(reply); }
        }

        // The Go runtime cannot be unloaded; the owning network has already stopped the node.
        public void Dispose() { }
    }

    private sealed class Headscale
    {
        internal string Url { get; private init; } = "";
        internal string Config => config;
        private string binary = "", config = "", directory = "";

        internal static async Task<Headscale> StartAsync(string binary, string directory, List<Child> processes, CancellationToken token)
        {
            Directory.CreateDirectory(directory);
            int http = FreePort(), grpc = FreePort(), metrics = FreePort();
            // The control socket path is limited to ~100 bytes on macOS, which a deep workspace path exceeds.
            var socket = Path.Combine(Path.GetTempPath(), "sr-hs-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
            var config = Path.Combine(directory, "config.yaml");
            File.WriteAllText(Path.Combine(directory, "derp.yaml"), """
                regions:
                  999:
                    regionid: 999
                    regioncode: test
                    regionname: unreachable
                    nodes:
                      - name: "999"
                        regionid: 999
                        hostname: 127.0.0.1
                        ipv4: 127.0.0.1
                        derpport: 1
                        stunport: -1
                        insecurefortests: true
                """);
            // No relay is reachable by design: nodes on this machine find each other directly, with no external network.
            File.WriteAllText(config, $"""
                server_url: http://127.0.0.1:{http}
                listen_addr: 127.0.0.1:{http}
                metrics_listen_addr: 127.0.0.1:{metrics}
                grpc_listen_addr: 127.0.0.1:{grpc}
                grpc_allow_insecure: true
                unix_socket: {socket}
                noise:
                  private_key_path: {directory}/noise_private.key
                prefixes:
                  v4: 100.64.0.0/10
                  v6: fd7a:115c:a1e0::/48
                derp:
                  server:
                    enabled: false
                  urls: []
                  paths:
                    - {directory}/derp.yaml
                  auto_update_enabled: false
                disable_check_updates: true
                database:
                  type: sqlite
                  sqlite:
                    path: {directory}/db.sqlite
                dns:
                  magic_dns: false
                  override_local_dns: false
                  base_domain: sharprail.test
                  nameservers:
                    global: []
                log:
                  level: info
                """);
            var server = Child.Start("headscale", binary, ["serve", "-c", config], null, processes, onDispose: () => { try { File.Delete(socket); } catch (IOException) { } });
            var url = $"http://127.0.0.1:{http}";
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                Check(!server.Exited, "Headscale exited during startup.");
                try { if ((await probe.GetAsync(url + "/health", token)).IsSuccessStatusCode && File.Exists(socket)) break; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                Check(attempt < 60, "Headscale did not become healthy.");
                await Task.Delay(250, token);
            }
            return new Headscale { Url = url, binary = binary, config = config, directory = directory };
        }

        internal async Task<string> PreAuthKeyAsync(string user, CancellationToken token)
        {
            await RunAsync(["users", "create", user], token);
            using var users = JsonDocument.Parse(await RunAsync(["users", "list", "-o", "json"], token));
            var id = users.RootElement.EnumerateArray().Single(item => item.GetProperty("name").GetString() == user).GetProperty("id").GetInt64();
            using var key = JsonDocument.Parse(await RunAsync(["preauthkeys", "create", "-u", id.ToString(), "-e", "1h", "-o", "json"], token));
            return key.RootElement.GetProperty("key").GetString()!;
        }

        private async Task<string> RunAsync(string[] arguments, CancellationToken token)
        {
            var start = new ProcessStartInfo(binary) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments.Concat(["-c", config])) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            Check(process.ExitCode == 0, $"headscale {string.Join(' ', arguments)} failed: {await error} {await output}");
            return await output;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A child process whose output is kept for failure reports and which never outlives the check.</summary>
    private sealed class Child : IDisposable
    {
        private readonly Process process;
        private readonly StringBuilder log = new();
        private readonly Action? onDispose;
        private readonly TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(string Port, string Remote)> accepted = [];
        private readonly ProcessExitHandler exit;
        private string secret = "";
        private TaskCompletionSource<string>? waiting;
        internal string Name { get; }
        internal string Ip { get; private set; } = "";
        internal bool Exited => process.HasExited;
        internal IReadOnlyList<(string Port, string Remote)> Accepted { get { lock (accepted) return accepted.ToArray(); } }

        private Child(string name, Process process, Action? onDispose)
        {
            Name = name;
            this.process = process;
            this.onDispose = onDispose;
            exit = new(Dispose);
        }

        internal static Child Start(string name, string binary, string[] arguments, Dictionary<string, string>? environment, List<Child> processes, Action? onDispose = null, bool redirectInput = false)
        {
            var start = new ProcessStartInfo(binary) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectInput, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var (key, value) in environment ?? []) start.Environment[key] = value;
            var child = new Child(name, Process.Start(start)!, onDispose);
            if (environment?.TryGetValue("TSF_AUTH_KEY", out var key2) == true) child.secret = key2;
            processes.Add(child);
            child.process.OutputDataReceived += (_, e) => child.OnOutput(e.Data);
            child.process.ErrorDataReceived += (_, e) => child.Append(e.Data);
            child.process.BeginOutputReadLine();
            child.process.BeginErrorReadLine();
            return child;
        }

        internal static async Task<Child> StartForwarderAsync(string binary, string state, string controlUrl, string authKey, string hostname, string[] forwards, List<Child> processes, CancellationToken token)
        {
            Directory.CreateDirectory(state);
            var child = Start("tsforward(" + hostname + ")", binary, forwards, new()
            {
                ["TSF_STATE"] = state,
                ["TSF_CONTROL_URL"] = controlUrl,
                ["TSF_AUTH_KEY"] = authKey,
                ["TSF_HOSTNAME"] = hostname
            }, processes, redirectInput: true);
            child.Ip = await child.ready.Task.WaitAsync(token);
            return child;
        }

        internal async Task CommandAsync(string command, CancellationToken token)
        {
            waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await process.StandardInput.WriteLineAsync(command.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            Check(await waiting.Task.WaitAsync(token) == command, "The forwarder acknowledged a different command.");
        }

        private void OnOutput(string? line)
        {
            if (line is null) return;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                switch (root.GetProperty("event").GetString())
                {
                    case "ready": ready.TrySetResult(root.GetProperty("ip").GetString()!); break;
                    case "error": ready.TrySetException(new IOException(root.GetProperty("message").GetString())); break;
                    case "ack": waiting?.TrySetResult(root.GetProperty("command").GetString()!); break;
                    case "accept": lock (accepted) accepted.Add((root.GetProperty("port").GetString()!, root.GetProperty("remote").GetString()!)); break;
                }
            }
            catch (JsonException) { }
            Append(line);
        }

        private void Append(string? line)
        {
            if (line is null) return;
            if (secret.Length > 0) line = line.Replace(secret, "<auth key>");
            lock (log)
            {
                log.AppendLine(line);
                if (log.Length > 64 * 1024) log.Remove(0, log.Length - 48 * 1024);
            }
            if (!ready.Task.IsCompleted && process.HasExited) ready.TrySetException(new IOException(Name + " exited before it was ready."));
        }

        internal string Tail()
        {
            lock (log)
            {
                // The unreachable relay by design logs a refused dial every few hundred milliseconds.
                var lines = log.ToString().Split('\n').Where(line => !line.Contains("derp-999") && !line.Contains("derphttp") && !line.Contains("netcheck")).ToArray();
                return string.Join('\n', lines[Math.Max(0, lines.Length - 60)..]);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    // The forwarder stops when its input closes; Headscale gets SIGINT-equivalent termination.
                    try { process.StandardInput.Close(); } catch (Exception error) when (error is InvalidOperationException or IOException) { }
                    if (!process.WaitForExit(3000)) { process.Kill(true); process.WaitForExit(5000); }
                }
            }
            catch (InvalidOperationException) { }
            exit.Dispose();
            onDispose?.Invoke();
        }

        /// <summary>Kills the child if the check process exits without unwinding (interrupt, crash of the runner).</summary>
        private sealed class ProcessExitHandler : IDisposable
        {
            private readonly EventHandler handler;
            internal ProcessExitHandler(Action action) { handler = (_, _) => action(); AppDomain.CurrentDomain.ProcessExit += handler; }
            public void Dispose() => AppDomain.CurrentDomain.ProcessExit -= handler;
        }
    }
}