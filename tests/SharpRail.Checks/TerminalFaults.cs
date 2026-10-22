using System.Net;
using System.Net.Sockets;

using SharpRail.Host.Protocol;

namespace SharpRail.Checks;

// Sits between a client and a real WebSocket RPC host so a check can cut the connection, or hold new
// connections back, while the host and its sessions keep running.
internal sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Uri target;
    private readonly List<(TcpClient Client, TcpClient Upstream)> pairs = [];
    private int stalled;
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

    // Existing connections stay open but carry nothing more, as after a machine slept; new ones work.
    internal void Stall() => Interlocked.Increment(ref stalled);

    private async Task CopyAsync(NetworkStream from, NetworkStream to, int epoch)
    {
        var buffer = new byte[64 * 1024];
        while (await from.ReadAsync(buffer, lifetime.Token) is var count and > 0)
            if (Volatile.Read(ref stalled) == epoch) await to.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
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
            var epoch = Volatile.Read(ref stalled);
            await Task.WhenAny(CopyAsync(client.GetStream(), upstream.GetStream(), epoch), CopyAsync(upstream.GetStream(), client.GetStream(), epoch));
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
internal sealed class AttachFaults : SharpRail.Host.Client.ITerminalRpcInterceptor
{
    private int calls;
    internal int Calls => Volatile.Read(ref calls);
    internal bool DropNextReply { get; set; }
    internal TimeSpan Delay { get; set; }
    public ITerminalRpc Wrap(ITerminalRpc service) => new FaultyService(this, service);
    private sealed class FaultyService(AttachFaults faults, ITerminalRpc service) : ITerminalRpc
    {
        public ValueTask<TerminalFileLinksReply> FindFileLinksAsync(TerminalFileLinksRequest request, CallContext context = default) => service.FindFileLinksAsync(request, context);
        public ValueTask<DocumentReply> SaveClipboardImageAsync(TerminalClipboardRequest request, CallContext context = default) => service.SaveClipboardImageAsync(request, context);
        public ValueTask<TerminalBusyReply> IsBusyAsync(TerminalSessionRequest request, CallContext context = default) => service.IsBusyAsync(request, context);
        public ValueTask<TerminalShellReply> ShellStatusAsync(TerminalSessionRequest request, CallContext context = default) => service.ShellStatusAsync(request, context);
        public ValueTask<TerminalClosed> CloseAsync(TerminalSessionRequest request, CallContext context = default) => service.CloseAsync(request, context);
        public async IAsyncEnumerable<TerminalOutput> RunAsync(IAsyncEnumerable<TerminalInput> input, CallContext context = default)
        {
            Interlocked.Increment(ref faults.calls);
            var drop = faults.DropNextReply; faults.DropNextReply = false;
            var delay = faults.Delay;
            var first = true;
            await foreach (var reply in service.RunAsync(input, context))
            {
                if (first)
                {
                    first = false;
                    if (drop) throw new RpcException(new(StatusCode.Unavailable, "The attach reply was lost with its connection."));
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, context.CancellationToken);
                }
                yield return reply;
            }
        }
    }
}