using System.Net;
using System.Net.Sockets;

using Grpc.Core;
using Grpc.Core.Interceptors;

namespace SharpRail.Checks;

// Sits between a client and a real gRPC host so a check can cut the connection, or hold new
// connections back, while the host and its sessions keep running.
internal sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Uri target;
    private readonly List<(TcpClient Client, TcpClient Upstream)> pairs = [];
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource? held;

    internal TcpProxy(Uri target)
    {
        this.target = target;
        listener.Start();
        Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        _ = Task.Run(AcceptAsync);
    }

    internal Uri Address { get; }
    internal int Connections;

    // New connections wait until the returned source completes.
    internal TaskCompletionSource Hold() => held = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Sever()
    {
        lock (pairs)
        {
            foreach (var (client, upstream) in pairs) { client.Close(); upstream.Close(); }
            pairs.Clear();
        }
    }

    private async Task AcceptAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(lifetime.Token); }
            catch (Exception) { return; }
            Interlocked.Increment(ref Connections);
            _ = PipeAsync(client, held);
        }
    }

    private async Task PipeAsync(TcpClient client, TaskCompletionSource? gate)
    {
        var upstream = new TcpClient { NoDelay = true };
        client.NoDelay = true;
        try
        {
            if (gate is not null) await gate.Task.WaitAsync(lifetime.Token);
            await upstream.ConnectAsync(target.Host, target.Port, lifetime.Token);
            lock (pairs) pairs.Add((client, upstream));
            await Task.WhenAny(client.GetStream().CopyToAsync(upstream.GetStream(), lifetime.Token), upstream.GetStream().CopyToAsync(client.GetStream(), lifetime.Token));
        }
        catch (Exception) { }
        finally
        {
            lock (pairs) pairs.Remove((client, upstream));
            client.Close(); upstream.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener.Stop();
        Sever();
    }
}

// Faults the reply to a terminal attach, as a connection lost or slowed right after the host answered.
internal sealed class AttachFaults : Interceptor
{
    private int calls;
    internal int Calls => Volatile.Read(ref calls);
    // Drops the reply to the next attach and breaks its call.
    internal bool DropNextReply { get; set; }
    // Delays the replies to later attaches.
    internal TimeSpan Delay { get; set; }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(context);
        Interlocked.Increment(ref calls);
        var drop = DropNextReply;
        DropNextReply = false;
        var responses = new FaultyReplies<TResponse>(call.ResponseStream, call.Dispose, drop, Delay);
        return new(call.RequestStream, responses, call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
    }

    private sealed class FaultyReplies<T>(IAsyncStreamReader<T> inner, Action breakCall, bool drop, TimeSpan delay) : IAsyncStreamReader<T>
    {
        private bool first = true;
        public T Current => inner.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var next = await inner.MoveNext(cancellationToken);
            if (!first) return next;
            first = false;
            if (drop)
            {
                breakCall();
                throw new RpcException(new Status(StatusCode.Unavailable, "The attach reply was lost with its connection."));
            }
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            return next;
        }
    }
}