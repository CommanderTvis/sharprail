using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Client;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Tailscale;
using SharpRail.UI.Android;

namespace SharpRail.Checks;

internal static class EmbeddedTailscaleRpcChecks
{
    internal static async Task Run(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var pairing = TailscaleClient.NewPairing("fixture");
        await using var rpc = RemoteServer.Create(root, IPAddress.Loopback, 0, pairing.Token,
            stateDirectory: Path.Combine(root, "embedded-rpc-state"));
        await rpc.StartAsync(token);
        var rpcAddress = Address(rpc);
        var advertised = new UriBuilder("http", "100.70.1.2", rpcAddress.Port).Uri;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var bootstrap = builder.Build();
        var pairRequests = 0;
        bootstrap.MapGet("/sharprail", () => Results.Json(new { name = "Fixture SharpRail" }));
        bootstrap.MapPost("/sharprail/pair", (HttpContext context) =>
        {
            Require(context.Request.Headers["X-SharpRail-Pairing"] == pairing.Token, "The embedded pairing lost its approval credential.");
            return Results.Json(new { state = Interlocked.Increment(ref pairRequests) == 1 ? "pending" : "approved", address = advertised.AbsoluteUri });
        });
        await bootstrap.StartAsync(token);
        var bootstrapAddress = Address(bootstrap);
        await using var proxy = new SocksProxy(bootstrapAddress.Port, rpcAddress.Port, token);
        var started = false;
        string Native(string operation, string _)
        {
            if (operation == "start") started = true;
            if (operation == "stop") started = false;
            return JsonSerializer.Serialize(new
            {
                state = started ? "Running" : "Stopped",
                online = started,
                proxyAddress = proxy.Address,
                proxyUsername = "route-user",
                proxyPassword = "route-secret",
                peers = new[] { new { name = "fixture.tailnet.ts.net", address = "100.70.1.2" } }
            });
        }
        await using var network = new EmbeddedTailscaleNetwork(Path.Combine(root, "embedded-rpc-node"), Native);
        await network.StartAsync(token);
        using var client = new TailscaleClient(network);
        var peers = await client.DiscoverAsync(token);
        Require(peers.Count == 1 && peers[0].Address == "100.70.1.2", "Embedded discovery did not traverse its authenticated private proxy.");
        var connection = await client.PairAsync(pairing, null, token);
        Require(pairRequests == 2 && connection.Address == advertised.AbsoluteUri, "Pending approval did not advance to the approved private endpoint.");
        using (RpcTransports.RegisterTransport(new Uri(connection.Address), network.CreateHandler))
        using (var state = new RemoteStateAdapter(new Uri(connection.Address), connection.Token))
        {
            var handshake = await state.GetHandshakeAsync(token);
            Require(handshake.ProtocolVersion > 0, "The approved private route did not reach the authenticated RPC host.");
        }
        Require(proxy.Destinations.Contains(TailscaleContract.DiscoveryPort) && proxy.Destinations.Contains(rpcAddress.Port),
            "Discovery/pairing and WebSocket RPC did not both use the embedded SOCKS transport.");
        await proxy.CheckAsync();
        Console.WriteLine("PASS embedded Android discovery, pending approval, pairing and authenticated WebSocket RPC over one private SOCKS route");
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private sealed class SocksProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime;
        private readonly Task accepting;
        private readonly ConcurrentBag<Task> connections = [];
        internal ConcurrentBag<int> Destinations { get; } = [];
        internal string Address => "socks5://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;

        internal SocksProxy(int bootstrapPort, int rpcPort, CancellationToken token)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            listener.Start();
            accepting = AcceptAsync();
            async Task AcceptAsync()
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                        connections.Add(ForwardAsync(await listener.AcceptTcpClientAsync(lifetime.Token)));
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (SocketException) when (lifetime.IsCancellationRequested) { }
            }
            async Task ForwardAsync(TcpClient downstream)
            {
                using (downstream)
                await using (var stream = downstream.GetStream())
                {
                    var ct = lifetime.Token;
                    var head = new byte[2];
                    await stream.ReadExactlyAsync(head, ct);
                    Require(head[0] == 5, "The integrated route did not negotiate SOCKS5.");
                    var methods = new byte[head[1]];
                    await stream.ReadExactlyAsync(methods, ct);
                    Require(methods.Contains((byte)2), "The integrated route omitted SOCKS authentication.");
                    await stream.WriteAsync(new byte[] { 5, 2 }, ct);
                    await stream.ReadExactlyAsync(head, ct);
                    Require(head[0] == 1, "The SOCKS credential exchange used an invalid version.");
                    var username = new byte[head[1]];
                    await stream.ReadExactlyAsync(username, ct);
                    var length = new byte[1];
                    await stream.ReadExactlyAsync(length, ct);
                    var password = new byte[length[0]];
                    await stream.ReadExactlyAsync(password, ct);
                    Require(Encoding.UTF8.GetString(username) == "route-user" && Encoding.UTF8.GetString(password) == "route-secret",
                        "Pairing or RPC omitted the app-private proxy credentials.");
                    await stream.WriteAsync(new byte[] { 1, 0 }, ct);
                    var connect = new byte[4];
                    await stream.ReadExactlyAsync(connect, ct);
                    Require(connect[0] == 5 && connect[1] == 1 && connect[2] == 0 && connect[3] == 1,
                        "The integrated route must dial the numeric netmap address without system DNS.");
                    var target = new byte[4];
                    await stream.ReadExactlyAsync(target, ct);
                    Require(new IPAddress(target).ToString() == "100.70.1.2", "The integrated route dialed outside the approved netmap peer.");
                    var portBytes = new byte[2];
                    await stream.ReadExactlyAsync(portBytes, ct);
                    var port = (portBytes[0] << 8) | portBytes[1];
                    Require(port == TailscaleContract.DiscoveryPort || port == rpcPort, "The private route dialed an unexpected port.");
                    Destinations.Add(port);
                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(IPAddress.Loopback, port == TailscaleContract.DiscoveryPort ? bootstrapPort : rpcPort, ct);
                    await using var upstreamStream = upstream.GetStream();
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
                    using var copying = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var outbound = stream.CopyToAsync(upstreamStream, copying.Token);
                    var inbound = upstreamStream.CopyToAsync(stream, copying.Token);
                    await Task.WhenAny(outbound, inbound);
                    await copying.CancelAsync();
                    try { await Task.WhenAll(outbound, inbound); }
                    catch (OperationCanceledException) when (copying.IsCancellationRequested) { }
                    catch (IOException) when (copying.IsCancellationRequested) { }
                }
            }
        }

        internal async Task CheckAsync()
        {
            foreach (var connection in connections.Where(task => task.IsCompleted)) await connection;
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            listener.Stop();
            await accepting;
            try { await Task.WhenAll(connections); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { lifetime.Dispose(); }
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}