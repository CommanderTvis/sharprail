using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

/// <summary>Typed host calls over a shared, reconnectable WebSocket connection.</summary>
public sealed class RpcChannel : IRpcClient, IDisposable
{
    private static readonly Dictionary<(string Address, RpcTransports.Registration? Transport), Connection> connections = [];
    private readonly (string Address, RpcTransports.Registration? Transport) key;
    private readonly Connection connection;
    private readonly HostConnection? host;
    private int disposed;
    public RpcChannel(Uri address, HostConnection? host = null, TimeSpan? keepAlive = null)
    {
        this.host = host;
        var transport = RpcTransports.Find(address);
        key = (address.AbsoluteUri + "|" + (host?.ClientId ?? "") + "|" + keepAlive, transport);
        lock (connections)
        {
            if (!connections.TryGetValue(key, out connection!)) connections[key] = connection = new(address, keepAlive, transport);
            connection.Users++;
        }
    }
    public static RpcChannel ForAddress(string address) => new(new Uri(address));
    public static RpcChannel ForAddress(Uri address) => new(address);
    public T CreateService<T>() => RpcServices.Create<T>(this);
    public async ValueTask<TReply> Unary<TRequest, TReply>(string method, TRequest request, CallContext context)
    {
        if (HostRequest.Timeout is { } timeout) context = new(context.Options.WithDeadline(DateTime.UtcNow + timeout));
        try
        {
            if (host is not null && ReplayHeaders.IsReplayable(method.Split('/')[1].Replace("Async", "", StringComparison.Ordinal)))
                return await Replay<TRequest, TReply>(method, request, context);
            return await Once<TRequest, TReply>(method, request, context);
        }
        catch (RpcException error) when (error.Trailers.GetValue(HostHeaders.ErrorCode) is { } name && Enum.TryParse<Abstractions.HostErrorCode>(name, out var code))
        { throw new Abstractions.HostException(code, error.Status.Detail); }
    }
    private async ValueTask<TReply> Replay<TRequest, TReply>(string method, TRequest request, CallContext context)
    {
        var id = host!.Begin();
        try
        {
            for (var delay = StateAdapterDefaults.InitialReconnect; ; delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, StateAdapterDefaults.MaxReconnect.TotalMilliseconds)))
            {
                var headers = new Metadata(); headers.AddRange(context.RequestHeaders ?? []);
                headers.Add(ReplayHeaders.Client, host.ClientId);
                headers.Add(ReplayHeaders.Request, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                headers.Add(ReplayHeaders.Resume, host.Unresolved());
                try { return await Once<TRequest, TReply>(method, request, new(context.Options.WithHeaders(headers))); }
                catch (RpcException error) when (HostCapabilities.Supports(host.HostVersion, HostProtocol.RequestReplay) && error.StatusCode == StatusCode.Unavailable &&
                    !context.CancellationToken.IsCancellationRequested && (context.Options.Deadline is not { } deadline || DateTime.UtcNow + delay < deadline))
                { await Task.Delay(delay, context.CancellationToken); }
            }
        }
        finally { host.End(id); }
    }
    private async ValueTask<TReply> Once<TRequest, TReply>(string method, TRequest request, CallContext context)
    {
        await using var replies = Stream<TRequest, TReply>(method, request, context).GetAsyncEnumerator();
        if (!await replies.MoveNextAsync()) throw new RpcException(new(StatusCode.Internal, "Missing host response."));
        var reply = replies.Current;
        if (await replies.MoveNextAsync()) throw new RpcException(new(StatusCode.Internal, "Multiple unary responses."));
        return reply;
    }
    public IAsyncEnumerable<TReply> Stream<TRequest, TReply>(string method, TRequest request, CallContext context) => Duplex<TRequest, TReply>(method, One(request), context);
    private static async IAsyncEnumerable<T> One<T>(T value) { yield return value; await Task.CompletedTask; }
    public IAsyncEnumerable<TReply> Duplex<TRequest, TReply>(string method, IAsyncEnumerable<TRequest> requests, CallContext context) => Run<TRequest, TReply>(method, requests, context);
    private async IAsyncEnumerable<TReply> Run<TRequest, TReply>(string method, IAsyncEnumerable<TRequest> requests, CallContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        RpcPeer peer;
        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            if (context.Options.Deadline is { } deadline) connecting.CancelAfter(deadline > DateTime.UtcNow ? deadline - DateTime.UtcNow : TimeSpan.Zero);
            peer = await connection.Get(connecting.Token);
        }
        catch (Exception error) when (error is not RpcException)
        { throw new RpcException(new(error is OperationCanceledException ? lifetime.IsCancellationRequested ? StatusCode.Cancelled : StatusCode.DeadlineExceeded : StatusCode.Unavailable, error.Message)); }
        await using var stream = await peer.Open(method, new CallContext(context.Options with { CancellationToken = lifetime.Token }));
        var sending = Task.Run(Send);
        try
        {
            await foreach (var reply in stream.Read<TReply>()) yield return reply;
        }
        finally
        {
            await stream.DisposeAsync();
            try { await sending; } catch (Exception) { }
        }
        async Task Send()
        {
            try
            {
                await foreach (var request in requests.WithCancellation(stream.Token)) await stream.Send(request);
                await stream.End();
            }
            catch (Exception error) { stream.Fail(error); }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (connections)
        {
            if (--connection.Users != 0) return;
            connections.Remove(key);
            connection.Dispose();
        }
    }
    private sealed class Connection(Uri address, TimeSpan? keepAlive, RpcTransports.Registration? transport)
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly CancellationTokenSource lifetime = new();
        private RpcPeer? peer;
        private HttpMessageInvoker? http;
        public int Users;
        public async Task<RpcPeer> Get(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            await gate.WaitAsync(linked.Token);
            try
            {
                if (peer?.IsAlive == true) return peer;
                if (peer is not null) await peer.DisposeAsync();
                http?.Dispose(); http = null;
                var socket = new ClientWebSocket();
                socket.Options.AddSubProtocol(RpcWire.Subprotocol);
                try
                {
                    var uri = new UriBuilder(address.Scheme == "unix" ? new Uri("http://localhost") : address)
                    { Scheme = address.Scheme is "https" or "wss" ? "wss" : "ws", Path = "/rpc", Query = "", Fragment = "" }.Uri;
                    if (address.Scheme == "unix")
                    {
                        var handler = new SocketsHttpHandler
                        {
                            ConnectCallback = async (_, ct) =>
                        {
                            var transport = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                            try { await transport.ConnectAsync(new UnixDomainSocketEndPoint(Uri.UnescapeDataString(address.AbsolutePath)), ct); return new NetworkStream(transport, ownsSocket: true); }
                            catch { transport.Dispose(); throw; }
                        }
                        };
                        http = new HttpMessageInvoker(handler);
                        await socket.ConnectAsync(uri, http, linked.Token);
                    }
                    else if (transport is not null)
                    {
                        http = new HttpMessageInvoker(transport.CreateHandler());
                        await socket.ConnectAsync(uri, http, linked.Token);
                    }
                    else await socket.ConnectAsync(uri, linked.Token);
                    return peer = new(socket, heartbeat: keepAlive);
                }
                catch { socket.Dispose(); http?.Dispose(); http = null; throw; }
            }
            finally { gate.Release(); }
        }
        public void Dispose()
        {
            lifetime.Cancel();
            _ = Close();
        }
        private async Task Close()
        {
            await gate.WaitAsync();
            try
            {
                if (peer is not null) await peer.DisposeAsync();
                http?.Dispose(); lifetime.Dispose();
            }
            finally { gate.Release(); }
        }
    }
}