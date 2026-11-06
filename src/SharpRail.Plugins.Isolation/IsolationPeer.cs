using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace SharpRail.Plugins.Isolation;

/// <summary>Which end of the channel a peer is: the host allocates odd ids and refuses descriptors, the runner even ids.</summary>
public enum IsolationRole { Host, Runner }

/// <summary>Member ids: FNV-1a 32 of the member's name (the generated tables use the documentation-comment id).</summary>
public static class IsoMember
{
    public static int Id(string name)
    {
        var hash = 2166136261u;
        foreach (var value in Encoding.UTF8.GetBytes(name)) hash = (hash ^ value) * 16777619u;
        return unchecked((int)hash);
    }
}

/// <summary>An inbound call or one-way. The handler owns <see cref="Descriptors"/>.</summary>
public sealed class IsoCall
{
    internal IsoCall(IsoFrame frame, object? target, SafeHandle[] descriptors)
    {
        Member = frame.Member;
        Target = frame.Target;
        TargetObject = target;
        Payload = frame.Payload;
        Handles = frame.Handles;
        Bytes = frame.Bytes;
        Descriptors = descriptors;
        Streaming = frame.Count > 0;
    }

    public int Member { get; }
    /// <summary>The handle the call is on; 0 is the connection's root.</summary>
    public long Target { get; }
    /// <summary>The live object behind <see cref="Target"/>.</summary>
    public object? TargetObject { get; }
    public string Payload { get; }
    public IReadOnlyList<long> Handles { get; }
    public byte[] Bytes { get; }
    public SafeHandle[] Descriptors { get; }
    /// <summary>The caller reads a stream: answer with <see cref="IsoResult.Items"/>.</summary>
    public bool Streaming { get; }
}

/// <summary>A call handler's answer: a value, or items for a streaming call.</summary>
public sealed class IsoResult
{
    public string Payload { get; init; } = "";
    public IReadOnlyList<long> Handles { get; init; } = [];
    /// <summary>Sent with the reply; the channel references them until written and the handler may dispose them after.</summary>
    public IReadOnlyList<SafeHandle> Descriptors { get; init; } = [];
    public IAsyncEnumerable<string>? Items { get; init; }

    public static IsoResult Of(string payload) => new() { Payload = payload };
}

/// <summary>A reply. The caller owns <see cref="Descriptors"/>.</summary>
public sealed record IsoReply(string Payload, IReadOnlyList<long> Handles, SafeHandle[] Descriptors);

/// <summary>A call that failed on the other side, or that this side gave up on (deadline).</summary>
public sealed class IsoCallException(IsoError error, string message, string permission = "") : Exception(message)
{
    public IsoError Error { get; } = error;
    /// <summary>On <see cref="IsoError.Denied"/>: the permission that was missing.</summary>
    public string Permission { get; } = permission;
}

/// <summary>What a peer does with inbound traffic.</summary>
public sealed class IsolationPeerOptions
{
    /// <summary>Serves a call on a pool thread; throw <see cref="IsoCallException"/> to fault with a specific error.</summary>
    public Func<IsoCall, CancellationToken, ValueTask<IsoResult>>? Calls { get; init; }
    /// <summary>Runs one-ways one at a time in arrival order; a throw is logged against the plugin.</summary>
    public Action<IsoCall>? OneWays { get; init; }
    /// <summary>Handshake and push frames, in the same order as one-ways. Throw <see cref="IsolationProtocolException"/> to end the channel.</summary>
    public Action<ReceivedFrame>? Control { get; init; }
    public Action<string>? Log { get; init; }
}

/// <summary>
/// One end of a plugin channel, the same on both sides: calls with replies or faults, cancellation, deadlines,
/// one-ways in order, streams with a credit window, handles with tombstones, ping. Anything the other side sends that
/// the protocol does not allow ends the channel as a violation (<see cref="Completion"/> faults with
/// <see cref="IsolationProtocolException"/>); the host then kills the runner.
/// </summary>
public sealed class IsolationPeer : IAsyncDisposable
{
    private readonly FdTransport transport;
    private readonly IsolationRole role;
    private readonly IsolationPeerOptions options;
    private readonly BlockingCollection<(PreparedFrame Frame, SafeHandle[] Referenced)> outbound = [];
    private readonly ConcurrentDictionary<long, Outgoing> outgoing = new();
    private readonly ConcurrentDictionary<long, Incoming> incoming = new();
    private readonly Channel<Action> serial = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long lastId;
    private long queuedFrames, queuedBytes;
    private int serialBacklog;
    private int finished;
    private int started;

    public IsolationPeer(FdTransport transport, IsolationRole role, IsolationPeerOptions options)
    {
        this.transport = transport;
        this.role = role;
        this.options = options;
        lastId = role == IsolationRole.Host ? -1 : 0;
    }

    /// <summary>The objects this side lent the other.</summary>
    public HandleTable Handles { get; } = new();

    /// <summary>Completes when the channel ends; faults with <see cref="IsolationProtocolException"/> on a violation.</summary>
    public Task Completion => completion.Task;

    /// <summary>Why the other side was cut off, or null.</summary>
    public string? Violation { get; private set; }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        new Thread(ReadLoop) { IsBackground = true, Name = "SharpRail plugin channel read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "SharpRail plugin channel write" }.Start();
        _ = Task.Run(SerialLoop);
    }

    private long NewId() => Interlocked.Add(ref lastId, 2);
    private bool Mine(long id) => id > 0 && (id & 1) == (role == IsolationRole.Host ? 1 : 0);
    private bool Theirs(long id) => id > 0 && (id & 1) == (role == IsolationRole.Host ? 0 : 1);

    /// <summary>Queues a frame as it is. Handshakes and probes use it; calls go through <see cref="CallAsync"/>.</summary>
    public void Send(IsoFrame frame, IReadOnlyList<SafeHandle>? descriptors = null)
    {
        if (Volatile.Read(ref finished) != 0) throw new IOException("The plugin channel closed.");
        var prepared = transport.Prepare(frame, descriptors ?? []);
        var frames = Interlocked.Increment(ref queuedFrames);
        var bytes = Interlocked.Add(ref queuedBytes, prepared.Length);
        if (frames > IsoWire.QueueFrames || bytes > IsoWire.QueueBytes)
        {
            Interlocked.Decrement(ref queuedFrames);
            Interlocked.Add(ref queuedBytes, -prepared.Length);
            if (role == IsolationRole.Runner) throw new InvalidOperationException("The plugin channel is full: the host is not reading it.");
            var stalled = new IsolationProtocolException("The plugin stopped reading its channel.");
            Fail(stalled);
            throw stalled;
        }
        var referenced = new List<SafeHandle>();
        foreach (var descriptor in descriptors ?? [])
        {
            var added = false;
            descriptor.DangerousAddRef(ref added);
            if (added) referenced.Add(descriptor);
        }
        try { outbound.Add((prepared, [.. referenced])); }
        catch (InvalidOperationException)
        {
            foreach (var descriptor in referenced) descriptor.DangerousRelease();
            throw new IOException("The plugin channel closed.");
        }
    }

    private void TrySend(IsoFrame frame)
    {
        try { Send(frame); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { }
    }

    /// <summary>Calls a member on the other side. <paramref name="deadline"/> also travels, so the callee stops too.</summary>
    public async Task<IsoReply> CallAsync(int member, string payload, long target = 0, IReadOnlyList<SafeHandle>? descriptors = null,
        TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = NewId();
        var call = new Outgoing(stream: false);
        outgoing[id] = call;
        try
        {
            Send(new IsoFrame
            {
                Kind = IsoFrameKind.Call,
                Id = id,
                Member = member,
                Target = target,
                Payload = payload,
                DeadlineMs = deadline is { } limit ? Math.Max(1, (long)limit.TotalMilliseconds) : 0
            }, descriptors);
        }
        catch
        {
            outgoing.TryRemove(id, out _);
            throw;
        }
        await using var registration = cancellationToken.Register(() => Abandon(id, new OperationCanceledException(cancellationToken)));
        await using var timer = deadline is { } after
            ? new Timer(_ => Abandon(id, new IsoCallException(IsoError.DeadlineExceeded, "The plugin did not answer in time.")), null, after, Timeout.InfiniteTimeSpan)
            : null;
        return await call.Reply.Task.ConfigureAwait(false);
    }

    /// <summary>Reads a streaming member, granting one credit per item read, so the other side stays at most a window ahead.</summary>
    public async IAsyncEnumerable<string> StreamAsync(int member, string payload, long target = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = NewId();
        var stream = new Outgoing(stream: true);
        outgoing[id] = stream;
        try { Send(new IsoFrame { Kind = IsoFrameKind.Call, Id = id, Member = member, Target = target, Payload = payload, Count = IsoWire.StreamWindow }); }
        catch
        {
            outgoing.TryRemove(id, out _);
            throw;
        }
        var ended = false;
        try
        {
            while (await stream.Items!.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                while (stream.Items.Reader.TryRead(out var item))
                {
                    Interlocked.Decrement(ref stream.Outstanding);
                    TrySend(new IsoFrame { Kind = IsoFrameKind.Credit, Id = id, Count = 1 });
                    yield return item;
                }
            ended = true;
        }
        finally
        {
            if (!ended) Abandon(id, new OperationCanceledException());
        }
        if (stream.Error is { } error) throw error;
    }

    /// <summary>Sends a <c>void</c> member; the other side runs one-ways in arrival order.</summary>
    public void OneWay(int member, string payload, long target = 0) =>
        Send(new IsoFrame { Kind = IsoFrameKind.OneWay, Member = member, Target = target, Payload = payload });

    /// <summary>Drops this side's reference to a handle the other side lent.</summary>
    public void Release(long handle) => TrySend(new IsoFrame { Kind = IsoFrameKind.Release, Target = handle });

    /// <summary>Round-trips a ping; a wedged peer misses <paramref name="timeout"/>.</summary>
    public async Task PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var id = NewId();
        var ping = new Outgoing(stream: false);
        outgoing[id] = ping;
        try
        {
            Send(new IsoFrame { Kind = IsoFrameKind.Ping, Id = id });
            await ping.Reply.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally { outgoing.TryRemove(id, out _); }
    }

    private void Abandon(long id, Exception error)
    {
        if (!outgoing.TryRemove(id, out var call)) return;
        TrySend(new IsoFrame { Kind = IsoFrameKind.Cancel, Id = id });
        call.Fail(error);
    }

    private void ReadLoop()
    {
        try
        {
            while (transport.Read() is { } received) Dispatch(received);
            Fail(null);
        }
        catch (IsolationProtocolException violation) { Fail(violation); }
        catch (Exception error) { Fail(Volatile.Read(ref finished) != 0 ? null : error); }
    }

    private void WriteLoop()
    {
        foreach (var (frame, referenced) in outbound.GetConsumingEnumerable())
        {
            try
            {
                if (Volatile.Read(ref finished) == 0) transport.Write(frame);
            }
            catch (Exception error) { Fail(error is IOException ? null : error); }
            finally
            {
                foreach (var descriptor in referenced) descriptor.DangerousRelease();
                Interlocked.Decrement(ref queuedFrames);
                Interlocked.Add(ref queuedBytes, -frame.Length);
            }
        }
    }

    private async Task SerialLoop()
    {
        await foreach (var action in serial.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Interlocked.Decrement(ref serialBacklog);
            try { action(); }
            catch (IsolationProtocolException violation) { Fail(violation); }
            catch (Exception error) { options.Log?.Invoke("A plugin one-way call failed: " + error.Message); }
        }
    }

    private void Serially(Action action)
    {
        if (Interlocked.Increment(ref serialBacklog) > IsoWire.QueueFrames)
            throw new IsolationProtocolException("The plugin sent one-way calls faster than they could run.");
        serial.Writer.TryWrite(action);
    }

    private static IsolationProtocolException Broken(string message) => new(message);

    private static void Dispose(SafeHandle[] descriptors)
    {
        foreach (var descriptor in descriptors) descriptor.Dispose();
    }

    private void Dispatch(ReceivedFrame received)
    {
        var (frame, descriptors) = received;
        var consumed = false;
        try
        {
            switch (frame.Kind)
            {
                case IsoFrameKind.Call:
                    if (!Theirs(frame.Id) || incoming.ContainsKey(frame.Id)) throw Broken($"The plugin reused or misnumbered call {frame.Id}.");
                    if (Target(frame) is not { } target) return;
                    consumed = true;
                    StartCall(frame, target.Value, descriptors);
                    return;
                case IsoFrameKind.OneWay:
                    if (Target(frame, fault: false) is not { } oneWayTarget) return;
                    consumed = true;
                    var oneWay = new IsoCall(frame, oneWayTarget.Value, descriptors);
                    Serially(() => options.OneWays?.Invoke(oneWay));
                    return;
                case IsoFrameKind.Reply:
                case IsoFrameKind.Fault:
                case IsoFrameKind.Pong:
                    if (!Mine(frame.Id)) throw Broken($"The plugin answered call {frame.Id}, which this side never made.");
                    if (outgoing.TryRemove(frame.Id, out var call))
                    {
                        consumed = true;
                        call.Complete(frame, descriptors);
                    }
                    else if (frame.Id > Interlocked.Read(ref lastId)) throw Broken($"The plugin answered call {frame.Id}, which this side never made.");
                    return;
                case IsoFrameKind.Item:
                case IsoFrameKind.End:
                    if (!Mine(frame.Id)) throw Broken($"The plugin streamed into call {frame.Id}, which this side never made.");
                    if (outgoing.TryGetValue(frame.Id, out var stream) && stream.Items is not null)
                    {
                        if (frame.Kind == IsoFrameKind.End)
                        {
                            outgoing.TryRemove(frame.Id, out _);
                            stream.Items.Writer.TryComplete();
                        }
                        else if (Interlocked.Increment(ref stream.Outstanding) > IsoWire.StreamWindow)
                            throw Broken("The plugin streamed past its credit.");
                        else stream.Items.Writer.TryWrite(frame.Payload);
                    }
                    else if (frame.Id > Interlocked.Read(ref lastId)) throw Broken($"The plugin streamed into call {frame.Id}, which this side never made.");
                    return;
                case IsoFrameKind.Credit:
                    if (!Theirs(frame.Id) || frame.Count <= 0) throw Broken("The plugin sent a malformed credit.");
                    if (incoming.TryGetValue(frame.Id, out var credited) && credited.Credit is { } credit)
                    {
                        try { credit.Release(frame.Count); }
                        catch (SemaphoreFullException) { throw Broken("The plugin sent more credit than a stream can hold."); }
                        catch (ObjectDisposedException) { }
                    }
                    return;
                case IsoFrameKind.Cancel:
                    if (!Theirs(frame.Id)) throw Broken($"The plugin cancelled call {frame.Id}, which it never made.");
                    if (incoming.TryGetValue(frame.Id, out var cancelled)) cancelled.Cancel();
                    return;
                case IsoFrameKind.Release:
                    if (Handles.Release(frame.Target) == HandleState.Never) throw Broken($"The plugin released handle {frame.Target}, which was never lent to it.");
                    return;
                case IsoFrameKind.Ping:
                    TrySend(new IsoFrame { Kind = IsoFrameKind.Pong, Id = frame.Id });
                    return;
                case IsoFrameKind.Hello or IsoFrameKind.Init or IsoFrameKind.Ready or IsoFrameKind.Push or IsoFrameKind.Grants or IsoFrameKind.Surface:
                    if (options.Control is not { } control) throw Broken($"The plugin sent an unexpected {frame.Kind} frame.");
                    consumed = true;
                    Serially(() => control(received));
                    return;
                default:
                    throw Broken($"The plugin sent an unknown frame kind {(int)frame.Kind}.");
            }
        }
        finally
        {
            if (!consumed) Dispose(descriptors);
        }
    }

    /// <summary>The live target of a call, null when it is answered already (tombstone); throws for a never-minted id.</summary>
    private StrongBox<object?>? Target(IsoFrame frame, bool fault = true)
    {
        if (frame.Target == 0) return new(null);
        switch (Handles.Resolve(frame.Target, out var value, out var permission))
        {
            case HandleState.Live: return new(value);
            case HandleState.Never: throw Broken($"The plugin called handle {frame.Target}, which was never lent to it.");
            case HandleState.Revoked:
                if (fault) TrySend(new IsoFrame { Kind = IsoFrameKind.Fault, Id = frame.Id, Error = IsoError.Denied, Permission = permission ?? "", Detail = "The permission behind this handle was revoked." });
                return null;
            default:
                if (fault) TrySend(new IsoFrame { Kind = IsoFrameKind.Fault, Id = frame.Id, Error = IsoError.Released, Detail = "The handle was released." });
                else options.Log?.Invoke($"A one-way call on released handle {frame.Target} was dropped.");
                return null;
        }
    }

    private void StartCall(IsoFrame frame, object? target, SafeHandle[] descriptors)
    {
        var state = new Incoming(frame.Count > 0 ? new SemaphoreSlim(Math.Min(frame.Count, IsoWire.StreamWindow), int.MaxValue) : null);
        if (frame.DeadlineMs > 0) state.Source.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(frame.DeadlineMs, int.MaxValue)));
        incoming[frame.Id] = state;
        var call = new IsoCall(frame, target, descriptors);
        _ = Task.Run(async () =>
        {
            var token = state.Source.Token;
            try
            {
                if (options.Calls is not { } calls) throw new IsoCallException(IsoError.Unknown, "This side serves no calls.");
                var result = await calls(call, token).ConfigureAwait(false);
                if (call.Streaming != result.Items is not null)
                    throw new IsoCallException(IsoError.Failed, call.Streaming ? "The member does not stream." : "The member streams.");
                if (result.Items is { } items)
                {
                    await foreach (var item in items.WithCancellation(token).ConfigureAwait(false))
                    {
                        await state.Credit!.WaitAsync(token).ConfigureAwait(false);
                        Send(new IsoFrame { Kind = IsoFrameKind.Item, Id = frame.Id, Payload = item });
                    }
                    Send(new IsoFrame { Kind = IsoFrameKind.End, Id = frame.Id });
                }
                else Send(new IsoFrame { Kind = IsoFrameKind.Reply, Id = frame.Id, Payload = result.Payload, Handles = [.. result.Handles] }, result.Descriptors);
            }
            catch (Exception error) when (Volatile.Read(ref finished) == 0)
            {
                var (code, permission) = error switch
                {
                    IsoCallException failed => (failed.Error, failed.Permission),
                    OperationCanceledException when token.IsCancellationRequested => (state.CancelledByPeer ? IsoError.Cancelled : IsoError.DeadlineExceeded, ""),
                    _ => (IsoError.Failed, "")
                };
                TrySend(new IsoFrame { Kind = IsoFrameKind.Fault, Id = frame.Id, Error = code, Permission = permission, Detail = error.Message });
            }
            catch (Exception) { }
            finally
            {
                incoming.TryRemove(frame.Id, out _);
                state.Dispose();
            }
        });
    }

    /// <summary>Ends the channel: pending calls fail, inbound calls are cancelled and lent objects disposed.</summary>
    private void Fail(Exception? error)
    {
        if (Interlocked.Exchange(ref finished, 1) != 0) return;
        if (error is IsolationProtocolException violation) Violation = violation.Message;
        transport.Dispose();
        outbound.CompleteAdding();
        var closed = error as IOException ?? new IOException("The plugin channel closed.", error);
        foreach (var id in outgoing.Keys)
            if (outgoing.TryRemove(id, out var call)) call.Fail(closed);
        foreach (var state in incoming.Values) state.Cancel(byPeer: false);
        serial.Writer.TryComplete();
        Handles.Close();
        if (error is not null && error is not IsolationProtocolException) options.Log?.Invoke("The plugin channel failed: " + error.Message);
        if (error is IsolationProtocolException) completion.TrySetException(error);
        else completion.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        Fail(null);
        try { await Completion.ConfigureAwait(false); }
        catch (IsolationProtocolException) { }
    }

    private sealed class Outgoing(bool stream)
    {
        internal readonly TaskCompletionSource<IsoReply> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Channel<string>? Items = stream ? Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true }) : null;
        internal int Outstanding;
        internal Exception? Error;

        internal void Complete(IsoFrame frame, SafeHandle[] descriptors)
        {
            if (frame.Kind == IsoFrameKind.Fault) { Dispose(descriptors); Fail(new IsoCallException(frame.Error, frame.Detail, frame.Permission)); return; }
            if (Items is not null) { Dispose(descriptors); Fail(new IsoCallException(IsoError.Failed, "The member does not stream.")); return; }
            if (!Reply.TrySetResult(new(frame.Payload, frame.Handles, descriptors))) Dispose(descriptors);
        }

        internal void Fail(Exception error)
        {
            if (Items is null) { Reply.TrySetException(error); return; }
            Error = error;
            Items.Writer.TryComplete();
        }
    }

    private sealed class Incoming(SemaphoreSlim? credit) : IDisposable
    {
        internal readonly CancellationTokenSource Source = new();
        internal readonly SemaphoreSlim? Credit = credit;
        internal volatile bool CancelledByPeer;

        internal void Cancel(bool byPeer = true)
        {
            CancelledByPeer = byPeer;
            try { Source.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            Source.Dispose();
            Credit?.Dispose();
        }
    }
}