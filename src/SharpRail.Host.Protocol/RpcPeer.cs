using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SharpRail.Host.Protocol;

/// <summary>One multiplexed connection. The receive pump never waits for an individual call's consumer.</summary>
public sealed class RpcPeer : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<RpcFrame> outgoing = Channel.CreateBounded<RpcFrame>(new BoundedChannelOptions(64) { SingleReader = true });
    private readonly ConcurrentDictionary<long, RpcStream> calls = new();
    private readonly SemaphoreSlim encoding = new(1, 1);
    private readonly Func<RpcStream, RpcFrame, Task>? dispatch;
    private readonly TimeSpan heartbeat;
    private readonly Task run;
    private long nextId, buffered, pong;
    private int disposed;
    public RpcPeer(WebSocket socket, Func<RpcStream, RpcFrame, Task>? dispatch = null, TimeSpan? heartbeat = null)
    {
        this.socket = socket; this.dispatch = dispatch; this.heartbeat = heartbeat ?? TimeSpan.FromSeconds(15);
        pong = Environment.TickCount64;
        run = Task.Run(Run);
    }
    public bool IsAlive => !lifetime.IsCancellationRequested;
    public Task Completion => run;
    internal CancellationToken Token => lifetime.Token;
    internal void Reserve(int bytes)
    {
        if (Interlocked.Add(ref buffered, bytes) <= RpcWire.ConnectionBytes) return;
        Interlocked.Add(ref buffered, -bytes);
        throw new RpcException(new(StatusCode.ResourceExhausted, "RPC connection buffer budget exceeded."));
    }
    internal async ValueTask<byte[]> Encode<T>(T value, CancellationToken token)
    {
        await encoding.WaitAsync(token);
        try
        {
            var bytes = RpcWire.Encode(value);
            Reserve(bytes.Length);
            return bytes;
        }
        finally { encoding.Release(); }
    }
    internal void Release(int bytes) => Interlocked.Add(ref buffered, -bytes);
    internal void Remove(long id) => calls.TryRemove(id, out _);
    internal ValueTask Send(RpcFrame frame, CancellationToken token) => outgoing.Writer.WriteAsync(frame, token);
    public async ValueTask<RpcStream> Open(string method, CallContext context)
    {
        if (!IsAlive) throw new RpcException(new(StatusCode.Unavailable, "The host connection ended."));
        var id = Interlocked.Increment(ref nextId);
        var stream = Add(id, context.Options);
        try
        {
            await Send(new()
            {
                Kind = RpcFrameKind.Open,
                Id = id,
                Method = method,
                Headers = context.RequestHeaders?.ToDictionary(entry => entry.Key, entry => entry.Value) ?? [],
                TimeoutMilliseconds = context.Options.Deadline is { } deadline ? Math.Max(1, (long)Math.Ceiling((deadline - DateTime.UtcNow).TotalMilliseconds)) : 0
            }, stream.Token);
            return stream;
        }
        catch (OperationCanceledException) { await stream.DisposeAsync(); throw stream.Cancelled(); }
        catch { await stream.DisposeAsync(); throw; }
    }
    private RpcStream Add(long id, CallOptions options)
    {
        lock (calls)
        {
            if (id <= 0 || calls.Count >= RpcWire.MaxCalls || calls.ContainsKey(id))
                throw new RpcException(new(StatusCode.ResourceExhausted, "Invalid or excessive concurrent RPC calls."));
            var stream = new RpcStream(this, id, options);
            calls[id] = stream;
            return stream;
        }
    }
    private async Task Run()
    {
        var sending = PumpSend();
        var pulse = Pulse();
        Exception failure = new RpcException(new(StatusCode.Unavailable, "The host connection ended."));
        try
        {
            while (true)
            {
                var frame = await RpcWire.Receive(socket, lifetime.Token);
                if (!Enum.IsDefined(frame.Kind) || (dispatch is not null && frame.Kind == RpcFrameKind.Complete) || (dispatch is null && frame.Kind == RpcFrameKind.End))
                    throw new IOException("Unexpected RPC frame direction or kind.");
                if (frame.Kind == RpcFrameKind.Ping) { await Send(new() { Kind = RpcFrameKind.Pong }, lifetime.Token); continue; }
                if (frame.Kind == RpcFrameKind.Pong) { Interlocked.Exchange(ref pong, Environment.TickCount64); continue; }
                if (frame.Kind == RpcFrameKind.Open)
                {
                    if (dispatch is null) throw new IOException("The host cannot initiate RPC calls.");
                    var options = new CallOptions(new Metadata(), frame.TimeoutMilliseconds == 0 ? null : DateTime.UtcNow.AddMilliseconds(frame.TimeoutMilliseconds));
                    foreach (var (key, value) in frame.Headers) options.Headers!.Add(key, value);
                    var stream = Add(frame.Id, options);
                    _ = Dispatch(stream, frame);
                }
                else if (calls.TryGetValue(frame.Id, out var call)) call.Receive(frame);
                // Frames already in flight may arrive after local cancellation removed the call.
            }
        }
        catch (Exception error) { failure = error is RpcException ? error : new RpcException(new(StatusCode.Unavailable, error.Message)); }
        finally
        {
            foreach (var call in calls.Values) call.Fail(failure);
            lifetime.Cancel(); socket.Abort(); outgoing.Writer.TryComplete();
            try { await sending; } catch (Exception) { }
            try { await pulse; } catch (Exception) { }
        }
    }
    private async Task Dispatch(RpcStream stream, RpcFrame frame)
    {
        try { await Task.Run(() => dispatch!(stream, frame), lifetime.Token); }
        catch (Exception error)
        {
            try { await stream.Finish(error); } catch (Exception) { }
        }
        finally { await stream.DisposeAsync(); }
    }
    private async Task PumpSend()
    {
        try
        {
            await foreach (var frame in outgoing.Reader.ReadAllAsync(lifetime.Token))
            {
                var bytes = RpcWire.Encode(frame);
                if (bytes.Length > RpcWire.FrameBytes) throw new IOException("RPC frame exceeds the size limit.");
                await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, lifetime.Token);
            }
        }
        finally { socket.Abort(); }
    }
    private async Task Pulse()
    {
        try
        {
            while (true)
            {
                await Task.Delay(heartbeat, lifetime.Token);
                if (Environment.TickCount64 - Interlocked.Read(ref pong) > heartbeat.TotalMilliseconds * 3)
                    throw new IOException("The host stopped answering heartbeats.");
                await Send(new() { Kind = RpcFrameKind.Ping }, lifetime.Token);
            }
        }
        finally { socket.Abort(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); socket.Abort();
        await run;
        foreach (var call in calls.Values) await call.DisposeAsync();
        socket.Dispose();
    }
}

/// <summary>Each call pipelines a bounded window of messages; consuming a message replenishes one credit.</summary>
public sealed class RpcStream : IAsyncDisposable
{
    private readonly RpcPeer peer;
    private readonly long id;
    private readonly CallOptions options;
    private readonly CancellationToken token;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenSource deadlineLifetime = new();
    private readonly Channel<MemoryStream> incoming = Channel.CreateBounded<MemoryStream>(RpcWire.CallWindow);
    private readonly SemaphoreSlim credit = new(RpcWire.CallWindow, RpcWire.CallWindow);
    private MemoryStream partial = new();
    private readonly object gate = new();
    private bool receiving, ended, completed;
    private int receiveCredits = RpcWire.CallWindow;
    private Exception? failure;
    private int disposed;
    internal RpcStream(RpcPeer peer, long id, CallOptions options)
    {
        this.peer = peer; this.id = id; this.options = options;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(peer.Token, options.CancellationToken, deadlineLifetime.Token);
        token = lifetime.Token;
        if (options.Deadline is { } deadline) deadlineLifetime.CancelAfter(deadline > DateTime.UtcNow ? deadline - DateTime.UtcNow : TimeSpan.Zero);
    }
    public CancellationToken Token => token;
    public CallContext Context(string method) => new(options with { CancellationToken = Token }) { Method = method };
    internal RpcException Cancelled() => new(new(deadlineLifetime.IsCancellationRequested || (options.Deadline is { } deadline && deadline <= DateTime.UtcNow)
        ? StatusCode.DeadlineExceeded : StatusCode.Cancelled, "The RPC was cancelled or exceeded its deadline."));
    internal void Receive(RpcFrame frame)
    {
        lock (gate)
        {
            if (disposed != 0) return;
            switch (frame.Kind)
            {
                case RpcFrameKind.Credit:
                    if (credit.CurrentCount >= RpcWire.CallWindow) throw new IOException("Unexpected RPC flow-control credit.");
                    credit.Release(); break;
                case RpcFrameKind.Data:
                    if (ended || (!receiving && receiveCredits == 0)) throw new IOException("RPC sender exceeded its receive credit.");
                    if (!receiving) receiveCredits--;
                    receiving = true;
                    if (partial.Length + frame.Payload.Length > RpcWire.MessageBytes) throw new IOException("RPC message exceeds the size limit.");
                    var required = checked((int)partial.Length + frame.Payload.Length);
                    if (required > partial.Capacity)
                    {
                        var capacity = Math.Min(RpcWire.MessageBytes, Math.Max(required, Math.Max(RpcWire.ChunkBytes, partial.Capacity * 2)));
                        peer.Reserve(capacity - partial.Capacity);
                        partial.Capacity = capacity;
                    }
                    partial.Write(frame.Payload);
                    if (frame.Last)
                    {
                        partial.Position = 0;
                        if (!incoming.Writer.TryWrite(partial)) throw new IOException("RPC sender exceeded its receive credit.");
                        partial = new(); receiving = false;
                    }
                    break;
                case RpcFrameKind.End:
                    if (receiving || ended) throw new IOException("Invalid RPC half-close.");
                    ended = true; incoming.Writer.TryComplete(); break;
                case RpcFrameKind.Complete:
                    if (receiving) throw new IOException("Incomplete RPC response.");
                    completed = true;
                    var trailers = new Metadata();
                    foreach (var (key, value) in frame.Headers) trailers.Add(key, value);
                    failure = frame.Code == StatusCode.OK ? null : new RpcException(new(frame.Code, frame.Detail), trailers);
                    incoming.Writer.TryComplete(failure);
                    break;
                case RpcFrameKind.Cancel: lifetime.Cancel(); incoming.Writer.TryComplete(Cancelled()); break;
                default: throw new IOException("Unexpected RPC frame.");
            }
        }
    }
    public void Fail(Exception error)
    {
        lock (gate)
        {
            if (completed || disposed != 0) return;
            failure = error is OperationCanceledException ? Cancelled() : error; incoming.Writer.TryComplete(failure); lifetime.Cancel();
        }
    }
    public async ValueTask Send<T>(T value)
    {
        try
        {
            await credit.WaitAsync(Token);
            var bytes = await peer.Encode(value, Token);
            try
            {
                for (var offset = 0; offset < bytes.Length || offset == 0; offset += RpcWire.ChunkBytes)
                {
                    var count = Math.Min(RpcWire.ChunkBytes, bytes.Length - offset);
                    await peer.Send(new() { Kind = RpcFrameKind.Data, Id = id, Payload = bytes.AsSpan(offset, count).ToArray(), Last = offset + count == bytes.Length }, Token);
                }
            }
            finally { peer.Release(bytes.Length); }
        }
        catch (OperationCanceledException) { throw Cancelled(); }
    }
    public ValueTask End() => peer.Send(new() { Kind = RpcFrameKind.End, Id = id }, Token);
    public async IAsyncEnumerable<T> Read<T>([EnumeratorCancellation] CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, token);
        while (true)
        {
            // Parsing a large response must never run on the caller's UI dispatcher.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            MemoryStream bytes;
            try
            {
                if (!await incoming.Reader.WaitToReadAsync(linked.Token)) yield break;
                bytes = await incoming.Reader.ReadAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw Cancelled();
            }
            T value;
            try { value = ProtoBuf.Serializer.Deserialize<T>(bytes); }
            finally { peer.Release(bytes.Capacity); bytes.Dispose(); }
            yield return value;
            if (!completed)
            {
                lock (gate) receiveCredits++;
                try { await peer.Send(new() { Kind = RpcFrameKind.Credit, Id = id }, linked.Token); }
                catch (OperationCanceledException) { throw Cancelled(); }
            }
        }
    }
    public async ValueTask Finish(Exception? error = null)
    {
        error = this.failure ?? error;
        var failure = error as RpcException ?? (error is OperationCanceledException ? Cancelled() : error is null ? null : new RpcException(new(StatusCode.Internal, "The host could not complete the RPC.")));
        await peer.Send(new()
        {
            Kind = RpcFrameKind.Complete,
            Id = id,
            Code = failure?.StatusCode ?? StatusCode.OK,
            Detail = failure?.Message ?? "",
            Headers = failure?.Trailers.ToDictionary(entry => entry.Key, entry => entry.Value) ?? []
        }, peer.Token);
        completed = true;
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
            lifetime.Cancel(); lifetime.Dispose(); deadlineLifetime.Dispose(); peer.Remove(id);
            peer.Release(partial.Capacity); partial.Dispose();
            while (incoming.Reader.TryRead(out var bytes)) { peer.Release(bytes.Capacity); bytes.Dispose(); }
            if (!completed && peer.IsAlive) return Cancel();
            return ValueTask.CompletedTask;
        }
    }
    private async ValueTask Cancel()
    {
        try { await peer.Send(new() { Kind = RpcFrameKind.Cancel, Id = id }, peer.Token); }
        catch (Exception) { }
    }
}