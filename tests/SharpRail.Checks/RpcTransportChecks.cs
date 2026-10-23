using System.Net.Sockets;

using SharpRail.Host.Client;
using SharpRail.Host.Protocol;

namespace SharpRail.Checks;

internal static class RpcTransportChecks
{
    public static async Task Run(Uri address, CallContext context)
    {
        using var direct = new RpcChannel(address);
        await Handshake(direct);
        var created = 0;
        var disposed = 0;
        var sockets = new List<Stream>();
        var routedAddress = new UriBuilder(address) { Scheme = "ws" }.Uri;
        using var registration = RpcTransports.RegisterTransport(routedAddress, () =>
        {
            Interlocked.Increment(ref created);
            return new RecordingHandler(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (connection, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(connection.DnsEndPoint, token);
                        var stream = new NetworkStream(socket, ownsSocket: true);
                        lock (sockets) sockets.Add(stream);
                        return stream;
                    }
                    catch { socket.Dispose(); throw; }
                }
            }, () => Interlocked.Increment(ref disposed));
        });
        try
        {
            using var duplicate = RpcTransports.RegisterTransport(address, () => new SocketsHttpHandler());
            throw new InvalidOperationException("Duplicate endpoint transport registration was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message == "A transport is already registered for this endpoint.") { }
        var routed = new RpcChannel(address);
        try
        {
            await Handshake(routed);
            Require(created == 1, "A scoped transport shared the pre-existing direct connection or failed scheme normalization.");
            using (var shared = new RpcChannel(address)) await Handshake(shared);
            Require(created == 1, "Channels using the same route did not share their connection.");
            lock (sockets) sockets[0].Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (created < 2)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { await Handshake(routed); }
                // A call in flight when the socket dropped ends as cancelled or past its deadline rather than unavailable.
                catch (RpcException error) when (error.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled or StatusCode.DeadlineExceeded) { }
                await Task.Delay(20, timeout.Token);
            }
            await Handshake(routed);
            Require(disposed >= 1, "Reconnection retained the previous route handler.");
            registration.Dispose();
            using (var ordinary = new RpcChannel(address)) await Handshake(ordinary);
            Require(created == 2, "Disposing a registration affected ordinary connections.");
            var replacementCreated = 0;
            using (RpcTransports.RegisterTransport(address, () =>
            {
                replacementCreated++;
                return new SocketsHttpHandler { UseProxy = false };
            }))
            using (var replacement = new RpcChannel(address))
            {
                await Handshake(replacement);
                Require(replacementCreated == 1, "A replacement registration reused the retired route's connection.");
                registration.Dispose();
                using var stillRegistered = new RpcChannel(address);
                await Handshake(stillRegistered);
                Require(replacementCreated == 1, "Disposing an old registration removed its replacement.");
            }
            await Handshake(routed);
        }
        finally { routed.Dispose(); }
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (Volatile.Read(ref disposed) < Volatile.Read(ref created)) await Task.Delay(20, timeout.Token);
        Console.WriteLine("PASS endpoint-scoped RPC transport normalization, route isolation, reconnect and handler disposal");

        async Task Handshake(RpcChannel channel)
        {
            var reply = await channel.CreateService<IStateRpc>().HandshakeAsync(new(), context);
            Require(reply.ProtocolVersion > 0, "A scoped WebSocket RPC transport lost the host handshake.");
        }
    }

    private sealed class RecordingHandler(HttpMessageHandler inner, Action disposed) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) disposed();
            base.Dispose(disposing);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}