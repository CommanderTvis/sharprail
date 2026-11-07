using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;

using SharpRail.Host.Protocol;
using SharpRail.Plugins.Isolation;

namespace SharpRail.Host.Remote;

/// <summary>
/// The sessions remote clients hold with external plugins' processes on this host (Security.SPEC.md, UI halves), one
/// per client and plugin, each carried by one surface call. A session outlives a dropped call by <see cref="Grace"/>,
/// so a client that reconnects in time resumes it with the plugin's state intact.
/// </summary>
public sealed class PluginSurfaceSessions : IAsyncDisposable
{
    /// <summary>Sessions one host holds at once.</summary>
    public const int MostSessions = 256;

    private readonly ConcurrentDictionary<string, HostedSurfaceSession> sessions = new();

    /// <summary>How long a session whose call dropped waits for its client to resume it.</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Sessions a client holds or could resume.</summary>
    public int Count => sessions.Count;

    internal async Task<HostedSurfaceSession> OpenAsync(IIsolatedUIHost host, string pluginId, string clientKey, CancellationToken cancellationToken)
    {
        if (sessions.Count >= MostSessions) throw new RpcException(new(StatusCode.ResourceExhausted, "This host holds too many plugin sessions."));
        var session = await host.OpenAsync(pluginId, clientKey, cancellationToken).ConfigureAwait(false);
        var hosted = new HostedSurfaceSession(this, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), session);
        sessions[hosted.Token] = hosted;
        hosted.Start();
        return hosted;
    }

    internal HostedSurfaceSession? Find(string token) => sessions.GetValueOrDefault(token);

    internal void Remove(HostedSurfaceSession session) => sessions.TryRemove(new(session.Token, session));

    public async ValueTask DisposeAsync()
    {
        foreach (var session in sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// One client's session with an external plugin's process, as the host serves it over a surface call: the plugin's
/// calls on the client's context go down, the client's answers, callbacks and surface messages come up. Numbered
/// messages are held until the client acknowledges them, so a resumed call sends again what the dropped one lost.
/// </summary>
internal sealed class HostedSurfaceSession : IAsyncDisposable
{
    /// <summary>Numbered messages the client may leave unacknowledged before the session ends.</summary>
    private const int MostUnconfirmed = 4096;
    /// <summary>Numbered messages received before an acknowledgement goes back on its own.</summary>
    private const int AckEvery = 32;

    private readonly PluginSurfaceSessions owner;
    private readonly IIsolatedUISession session;
    private readonly Lock gate = new();
    private readonly List<PluginSurfaceDown> unconfirmed = [];
    private readonly Dictionary<long, IsoUICall> calls = [];
    private readonly ConcurrentDictionary<int, IPluginSurface> surfaces = new();
    private readonly CancellationTokenSource lifetime = new();
    private Channel<PluginSurfaceDown>? link;
    private CancellationTokenSource? grace;
    private long sent, received, acknowledged, nextCall;
    private bool ended;
    private int ending;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HostedSurfaceSession(PluginSurfaceSessions owner, string token, IIsolatedUISession session)
    {
        this.owner = owner;
        this.session = session;
        Token = token;
    }

    public string Token { get; }

    public void Start() => _ = PumpCallsAsync();

    /// <summary>
    /// Carries the session on a new call: Opened or Resumed first, then every numbered message the client has not
    /// acknowledged. A call that still looked alive is replaced.
    /// </summary>
    public Channel<PluginSurfaceDown> Attach(bool opened, long clientReceived)
    {
        var channel = Channel.CreateUnbounded<PluginSurfaceDown>(new() { SingleReader = true });
        Channel<PluginSurfaceDown>? replaced;
        lock (gate)
        {
            if (ended)
            {
                channel.Writer.TryComplete(new RpcException(new(StatusCode.NotFound, "The plugin's session ended.")));
                return channel;
            }
            grace?.Cancel();
            grace = null;
            replaced = link;
            link = channel;
            Confirm(clientReceived);
            channel.Writer.TryWrite(opened
                ? new()
                {
                    Kind = PluginSurfaceDownKind.Opened,
                    Session = Token,
                    ApiTableHash = session.ApiTableHash,
                    Registrations = [.. session.Registrations.Select(registration => new PluginSurfaceRegistrationMessage { Member = registration.Member, Payload = registration.Payload })],
                    Ack = received
                }
                : new() { Kind = PluginSurfaceDownKind.Resumed, Ack = received });
            foreach (var message in unconfirmed) channel.Writer.TryWrite(message);
            acknowledged = received;
        }
        replaced?.Writer.TryComplete();
        return channel;
    }

    /// <summary>The call carrying the session ended without closing it: the session waits out the grace for a resume.</summary>
    public void Detach(Channel<PluginSurfaceDown> channel)
    {
        lock (gate)
        {
            if (!ReferenceEquals(link, channel)) return;
            link = null;
            if (ended) return;
            grace = new CancellationTokenSource();
            _ = ExpireAsync(grace.Token);
        }
    }

    private async Task ExpireAsync(CancellationToken token)
    {
        try { await Task.Delay(owner.Grace, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// One message from the client on <paramref name="channel"/>'s call, in arrival order. A message on a call another
    /// replaced is ignored; the client sends it again on the new one.
    /// </summary>
    public void Receive(Channel<PluginSurfaceDown> channel, PluginSurfaceUp message)
    {
        bool ack;
        lock (gate)
        {
            if (!ReferenceEquals(link, channel) || ended) return;
            if (message.Seq != 0)
            {
                if (message.Seq <= received) return;
                if (message.Seq != received + 1) throw new RpcException(new(StatusCode.InvalidArgument, "A plugin session message was lost."));
                received = message.Seq;
            }
            Confirm(message.Ack);
        }
        Handle(message);
        lock (gate) ack = received - acknowledged >= AckEvery;
        if (ack) Send(new() { Kind = PluginSurfaceDownKind.Ack }, reliable: false);
    }

    private void Confirm(long ack)
    {
        var kept = unconfirmed.FindIndex(message => message.Seq > ack);
        unconfirmed.RemoveRange(0, kept < 0 ? unconfirmed.Count : kept);
    }

    private void Handle(PluginSurfaceUp message)
    {
        switch (message.Kind)
        {
            case PluginSurfaceUpKind.Ack:
                break;
            case PluginSurfaceUpKind.Answer:
                IsoUICall? call;
                lock (gate) calls.Remove(message.Call, out call);
                if (call is null) break;
                if (message.Error != 0) call.Fail(new IsoCallException((IsoError)message.Error, message.Detail));
                else call.Answer(message.Payload);
                break;
            case PluginSurfaceUpKind.Invoke:
                _ = InvokeAsync(message.Call, () => session.CallAsync(message.Member, message.Handle, message.Payload, lifetime.Token));
                break;
            case PluginSurfaceUpKind.Mirror:
                _ = InvokeAsync(message.Call, async () =>
                {
                    await session.MirrorAsync(message.Member, message.Payload, message.Json, lifetime.Token).ConfigureAwait(false);
                    return "";
                });
                break;
            case PluginSurfaceUpKind.OneWay:
                try { session.OneWay(message.Member, message.Handle, message.Payload); }
                catch (Exception error) when (error is IsoCallException or IOException or InvalidOperationException or ObjectDisposedException) { }
                break;
            case PluginSurfaceUpKind.SurfaceOpen:
                Open(message);
                break;
            case PluginSurfaceUpKind.Resize:
                Surface(message)?.Resize(message.Width, message.Height, message.Scale);
                break;
            case PluginSurfaceUpKind.Input when message.Input is { } input:
                Surface(message)?.Input(new SurfaceInput(0, (SurfaceInputKind)input.Kind, input.X, input.Y, input.Button, input.Modifiers, input.DeltaX, input.DeltaY,
                    input.Key, input.PhysicalKey, input.Text));
                break;
            case PluginSurfaceUpKind.FrameAck:
                Surface(message)?.Acknowledge(message.Sequence);
                break;
            case PluginSurfaceUpKind.Focus:
                Surface(message)?.Focus(message.Enter, message.Backward, message.Pointer);
                break;
            case PluginSurfaceUpKind.Theme:
                Surface(message)?.Theme(message.Json);
                break;
            case PluginSurfaceUpKind.Keyframe:
                Surface(message)?.Keyframe();
                break;
            case PluginSurfaceUpKind.SurfaceClose:
                if (surfaces.TryRemove(message.Surface, out var closed)) _ = closed.DisposeAsync().AsTask();
                break;
            default:
                throw new RpcException(new(StatusCode.InvalidArgument, "Unexpected plugin session message."));
        }
    }

    private IPluginSurface? Surface(PluginSurfaceUp message) => surfaces.GetValueOrDefault(message.Surface);

    private void Open(PluginSurfaceUp message)
    {
        if (surfaces.ContainsKey(message.Surface)) throw new RpcException(new(StatusCode.InvalidArgument, "That surface is already open."));
        var surface = session.Surfaces.Open(message.Member, message.Handle, message.Payload, message.Json);
        surfaces[message.Surface] = surface;
        Send(new() { Kind = PluginSurfaceDownKind.Shown, Surface = message.Surface, Number = surface.Number }, reliable: true);
        _ = PumpSurfaceAsync(message.Surface, surface);
    }

    // Frames are not numbered: one lost with a dropped call is repainted by the keyframe the resumed client asks for.
    private async Task PumpSurfaceAsync(int id, IPluginSurface surface)
    {
        var (end, detail) = (PluginSurfaceEndKind.Closed, "");
        try
        {
            await foreach (var message in surface.Events.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                if (message is SurfaceFrame frame)
                    foreach (var piece in Split(id, frame)) Send(piece, reliable: false);
                else if (message is SurfaceLeft left)
                    Send(new() { Kind = PluginSurfaceDownKind.FocusLeave, Surface = id, Backward = left.Backward }, reliable: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (InvalidDataException error) { (end, detail) = (PluginSurfaceEndKind.Broken, error.Message); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { (end, detail) = (PluginSurfaceEndKind.Failed, error.Message); }
        // A surface the client closed ends without a word.
        if (surfaces.TryRemove(new(id, surface))) Send(new() { Kind = PluginSurfaceDownKind.SurfaceEnded, Surface = id, End = end, Detail = detail }, reliable: true);
    }

    /// <summary>
    /// A frame message the relay checked, as surface messages under <see cref="PluginSurfaceWire.MessageBytes"/>: tiles
    /// in order, moves in the first, <c>Last</c> only on the last when the frame's message was its last.
    /// </summary>
    internal static IEnumerable<PluginSurfaceDown> Split(int id, SurfaceFrame frame)
    {
        var (header, data) = frame;
        int index = 0, offset = 0;
        var first = true;
        do
        {
            var tiles = new List<PluginSurfaceTileMessage>();
            var length = 0;
            while (index < header.Tiles.Count && (tiles.Count == 0 || length + header.Tiles[index].Length <= PluginSurfaceWire.FrameDataBytes))
            {
                var tile = header.Tiles[index++];
                tiles.Add(new() { X = tile.X, Y = tile.Y, Width = tile.Width, Height = tile.Height, Codec = (int)tile.Codec, Length = tile.Length });
                length += tile.Length;
            }
            yield return new()
            {
                Kind = PluginSurfaceDownKind.Frame,
                Surface = id,
                Frame = new()
                {
                    Sequence = header.Sequence,
                    Width = header.Width,
                    Height = header.Height,
                    Scale = header.Scale,
                    DesiredWidth = header.DesiredWidth,
                    DesiredHeight = header.DesiredHeight,
                    Last = header.Last && index >= header.Tiles.Count,
                    Keyframe = header.Keyframe,
                    Moves = first ? [.. header.Moves.Select(move => new PluginSurfaceMoveMessage { X = move.X, Y = move.Y, Width = move.Width, Height = move.Height, Dx = move.Dx, Dy = move.Dy })] : [],
                    Tiles = tiles,
                    Data = data.AsSpan(offset, length).ToArray()
                }
            };
            offset += length;
            first = false;
        }
        while (index < header.Tiles.Count);
    }

    private async Task InvokeAsync(long call, Func<ValueTask<string>> invoke)
    {
        PluginSurfaceDown result;
        try { result = new() { Kind = PluginSurfaceDownKind.Result, Call = call, Payload = await invoke().ConfigureAwait(false) }; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (IsoCallException error) { result = new() { Kind = PluginSurfaceDownKind.Result, Call = call, Error = (int)error.Error, Detail = error.Message }; }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            result = new() { Kind = PluginSurfaceDownKind.Result, Call = call, Error = (int)IsoError.Failed, Detail = error.Message };
        }
        Send(result, reliable: true);
    }

    private async Task PumpCallsAsync()
    {
        var reason = "The plugin's session ended.";
        try
        {
            await foreach (var call in session.Calls.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                long id;
                lock (gate)
                {
                    id = ++nextCall;
                    if (call.Answered) calls[id] = call;
                }
                Send(new() { Kind = PluginSurfaceDownKind.UiCall, Call = id, Member = call.Member, Payload = call.Payload, Answered = call.Answered }, reliable: true);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or InvalidOperationException) { reason = error.Message; }
        await EndAsync(reason).ConfigureAwait(false);
    }

    /// <summary>Queues a message for the client; one that is not numbered is dropped while no call carries the session.</summary>
    private void Send(PluginSurfaceDown message, bool reliable)
    {
        var overflow = false;
        lock (gate)
        {
            if (ended) return;
            if (reliable)
            {
                message.Seq = ++sent;
                unconfirmed.Add(message);
                overflow = unconfirmed.Count > MostUnconfirmed;
            }
            message.Ack = received;
            acknowledged = received;
            if (!overflow) link?.Writer.TryWrite(message);
        }
        if (overflow) _ = EndAsync("The client is not keeping up.");
    }

    /// <summary>Ends the session, telling the client <paramref name="reason"/> when the plugin's side ended it.</summary>
    private Task EndAsync(string? reason)
    {
        if (Interlocked.Exchange(ref ending, 1) == 0) _ = EndingAsync(reason);
        return finished.Task;
    }

    private async Task EndingAsync(string? reason)
    {
        try { await EndOnceAsync(reason).ConfigureAwait(false); }
        finally { finished.TrySetResult(); }
    }

    private async Task EndOnceAsync(string? reason)
    {
        Channel<PluginSurfaceDown>? current;
        List<IsoUICall> abandoned;
        lock (gate)
        {
            if (reason is not null) link?.Writer.TryWrite(new() { Kind = PluginSurfaceDownKind.Ended, Seq = ++sent, Ack = received, Detail = reason });
            ended = true;
            (current, link) = (link, null);
            grace?.Cancel();
            abandoned = [.. calls.Values];
            calls.Clear();
            unconfirmed.Clear();
        }
        current?.Writer.TryComplete();
        owner.Remove(this);
        foreach (var call in abandoned) call.Abandon();
        await lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var surface in surfaces.Values) await surface.DisposeAsync().ConfigureAwait(false);
        surfaces.Clear();
        await session.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(EndAsync(null));
}