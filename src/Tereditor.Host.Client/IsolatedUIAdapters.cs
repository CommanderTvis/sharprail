using System.Collections.Concurrent;
using System.Threading.Channels;

using Tereditor.Host.Protocol;
using Tereditor.Plugins.Isolation;

namespace Tereditor.Host.Client;

/// <summary>
/// External plugins' UI halves on a remote host (Security.SPEC.md, UI halves): each session is one surface call
/// (<c>IPluginRpc.SurfaceAsync</c>) that carries the plugin's calls on this client's context and every surface it shows.
/// A dropped connection is resumed for up to <see cref="Grace"/>, and the session goes on where it was: numbered
/// messages either side missed are sent again, and every surface repaints from a keyframe.
/// </summary>
public sealed class RemoteIsolatedUIHost : IIsolatedUIHost, IDisposable
{
    private readonly RpcChannel channel;
    private readonly string token;

    public RemoteIsolatedUIHost(Uri address, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        this.token = token;
        channel = new RpcChannel(address, null);
        Service = channel.CreateService<IPluginRpc>();
    }

    internal IPluginRpc Service { get; }

    /// <summary>How long a session whose connection dropped keeps trying to resume before it ends.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(20);

    internal CallContext Context(CancellationToken cancellationToken) => new(new CallOptions(
        headers: new Metadata { { "authorization", $"Bearer {token}" } }, cancellationToken: cancellationToken));

    /// <summary>
    /// Opens this client's session with an active external plugin. Throws <see cref="InvalidOperationException"/> when the
    /// host does not run it isolated or cannot show plugins' views, and <see cref="IOException"/> when the host is not
    /// reachable.
    /// </summary>
    public async ValueTask<IIsolatedUISession> OpenAsync(string pluginId, string clientKey, CancellationToken cancellationToken = default)
    {
        var session = new RemoteIsolatedUISession(this);
        try
        {
            await session.OpenAsync(pluginId, clientKey, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose() => channel.Dispose();
}

/// <summary>
/// One client's session with an external plugin's process on a remote host. Calls on it only queue messages, so the UI
/// thread never waits on the network.
/// </summary>
internal sealed class RemoteIsolatedUISession(RemoteIsolatedUIHost host) : IIsolatedUISession, IPluginSurfaceService
{
    /// <summary>Numbered messages the host may leave unacknowledged before the session ends.</summary>
    private const int MostUnconfirmed = 4096;
    /// <summary>Numbered messages received before an acknowledgement goes back on its own.</summary>
    private const int AckEvery = 32;
    /// <summary>Calls of the plugin the app may leave unread before the session ends, as on the local host.</summary>
    internal const int MostUnread = 1024;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(250), LastRetry = TimeSpan.FromSeconds(3);

    private readonly Lock gate = new();
    private readonly List<PluginSurfaceUp> unconfirmed = [];
    private readonly Dictionary<long, TaskCompletionSource<string>> results = [];
    private readonly ConcurrentDictionary<int, RemotePluginSurface> surfaces = new();
    private readonly Channel<IsoUICall> calls = Channel.CreateBounded<IsoUICall>(new BoundedChannelOptions(MostUnread) { SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Channel<PluginSurfaceUp>? link;
    private long sent, received, acknowledged, nextCall;
    private int nextSurface;
    private string session = "";
    private bool ended, closing;
    private Exception? endedBy;
    private Task running = Task.CompletedTask;

    public string ApiTableHash { get; private set; } = "";
    public IReadOnlyList<UIRegistration> Registrations { get; private set; } = [];
    public ChannelReader<IsoUICall> Calls => calls.Reader;
    public IPluginSurfaceService Surfaces => this;

    public async Task OpenAsync(string pluginId, string clientKey, CancellationToken cancellationToken)
    {
        running = RunAsync(new() { Kind = PluginSurfaceUpKind.Open, PluginId = pluginId, ClientKey = clientKey });
        using (cancellationToken.Register(() => opened.TrySetCanceled(cancellationToken)))
            await opened.Task.ConfigureAwait(false);
    }

    // One call after another: the first opens the session, each later one resumes it after the last dropped.
    private async Task RunAsync(PluginSurfaceUp first)
    {
        Exception? reason = null;
        DateTime? lost = null;
        var retry = FirstRetry;
        while (!lifetime.IsCancellationRequested)
        {
            var requests = Channel.CreateUnbounded<PluginSurfaceUp>(new() { SingleReader = true });
            requests.Writer.TryWrite(first);
            try
            {
                var replies = host.Service.SurfaceAsync(requests.Reader.ReadAllAsync(lifetime.Token), host.Context(lifetime.Token));
                await using var reader = replies.GetAsyncEnumerator(lifetime.Token);
                if (!await reader.MoveNextAsync().ConfigureAwait(false)) throw new IOException("The host ended the plugin's session.");
                Attach(reader.Current, requests);
                (lost, retry) = (null, FirstRetry);
                while (await reader.MoveNextAsync().ConfigureAwait(false)) Receive(reader.Current);
                lock (gate) reason = closing ? null : endedBy ?? new IOException("The plugin's session ended.");
                break;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (RpcException error) when (session.Length > 0 && Dropped(error))
            {
                Detach(requests);
                lost ??= DateTime.UtcNow;
                if (DateTime.UtcNow - lost > host.Grace)
                {
                    reason = new IOException("The connection to the host was lost.", error);
                    break;
                }
                try { await Task.Delay(retry, lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, LastRetry.Ticks));
                lock (gate) first = new() { Kind = PluginSurfaceUpKind.Resume, Session = session, Ack = received };
            }
            catch (RpcException error)
            {
                reason = error.StatusCode switch
                {
                    StatusCode.NotFound => new IOException("The plugin's session ended."),
                    StatusCode.FailedPrecondition => new InvalidOperationException(error.Status.Detail),
                    StatusCode.Unimplemented => new InvalidOperationException("This host cannot show external plugins' views."),
                    _ => new IOException(error.Status.Detail, error)
                };
                break;
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                reason = error;
                break;
            }
            finally
            {
                Detach(requests);
                requests.Writer.TryComplete();
            }
        }
        End(reason);
    }

    // Transport failures the session survives; anything the host answered on purpose ends it.
    private bool Dropped(RpcException error) => error.StatusCode is StatusCode.Unavailable or StatusCode.Internal or StatusCode.DeadlineExceeded ||
        (error.StatusCode == StatusCode.Cancelled && !lifetime.IsCancellationRequested);

    // The first reply of a call: the session's identity, or the host's acknowledgement, after which whatever it missed goes again.
    private void Attach(PluginSurfaceDown reply, Channel<PluginSurfaceUp> requests)
    {
        var resumed = false;
        lock (gate)
        {
            if (reply.Kind == PluginSurfaceDownKind.Opened && session.Length == 0)
            {
                (session, ApiTableHash) = (reply.Session, reply.ApiTableHash);
                Registrations = [.. reply.Registrations.Select(registration => new UIRegistration(registration.Member, registration.Payload))];
            }
            else if (reply.Kind == PluginSurfaceDownKind.Resumed && session.Length > 0) resumed = true;
            else throw new IOException("The host answered a plugin session out of turn.");
            Confirm(reply.Ack);
            link = requests;
            foreach (var message in unconfirmed) requests.Writer.TryWrite(message);
            if (closing) requests.Writer.TryComplete();
        }
        opened.TrySetResult();
        // Frames are not sent again: what each surface held may be stale, so it repaints from a keyframe.
        if (resumed) foreach (var surface in surfaces.Values) surface.Keyframe();
    }

    private void Detach(Channel<PluginSurfaceUp> requests)
    {
        lock (gate)
            if (ReferenceEquals(link, requests)) link = null;
    }

    private void Confirm(long ack)
    {
        var kept = unconfirmed.FindIndex(message => message.Seq > ack);
        unconfirmed.RemoveRange(0, kept < 0 ? unconfirmed.Count : kept);
    }

    internal void Receive(PluginSurfaceDown message)
    {
        bool ack;
        lock (gate)
        {
            if (message.Seq != 0)
            {
                if (message.Seq <= received) return;
                if (message.Seq != received + 1) throw new IOException("A plugin session message was lost.");
                received = message.Seq;
            }
            Confirm(message.Ack);
        }
        Handle(message);
        lock (gate) ack = received - acknowledged >= AckEvery;
        if (ack) Send(new() { Kind = PluginSurfaceUpKind.Ack }, reliable: false);
    }

    private void Handle(PluginSurfaceDown message)
    {
        switch (message.Kind)
        {
            case PluginSurfaceDownKind.Ack:
                break;
            case PluginSurfaceDownKind.UiCall:
                var call = new IsoUICall(message.Member, message.Payload, message.Answered);
                if (calls.Writer.TryWrite(call))
                {
                    if (call.Answered) _ = AnswerAsync(message.Call, call);
                    break;
                }
                call.Abandon();
                // Ending the messages closes the session on the host at once.
                lock (gate) link?.Writer.TryComplete();
                End(new IOException("The plugin sent more than its client could take."));
                break;
            case PluginSurfaceDownKind.Result:
                TaskCompletionSource<string>? result;
                lock (gate) results.Remove(message.Call, out result);
                if (message.Error != 0) result?.TrySetException(new IsoCallException((IsoError)message.Error, message.Detail));
                else result?.TrySetResult(message.Payload);
                break;
            case PluginSurfaceDownKind.Shown:
                if (surfaces.TryGetValue(message.Surface, out var shown)) shown.Number = message.Number;
                break;
            case PluginSurfaceDownKind.Frame when message.Frame is { } frame:
                if (surfaces.TryGetValue(message.Surface, out var framed)) framed.Frame(frame);
                break;
            case PluginSurfaceDownKind.FocusLeave:
                if (surfaces.TryGetValue(message.Surface, out var left)) left.Left(message.Backward);
                break;
            case PluginSurfaceDownKind.Popup when message.PopupPlacement is { } popup:
                if (surfaces.TryGetValue(message.Surface, out var placed))
                    placed.Place(new SurfacePopup(placed.Id, popup.Popup, popup.X, popup.Y, popup.Width, popup.Height, popup.Open, popup.Input, popup.Dialog, popup.Title));
                break;
            case PluginSurfaceDownKind.Cursor:
                if (surfaces.TryGetValue(message.Surface, out var pointed))
                {
                    var cursor = new SurfaceCursor(pointed.Id, message.Popup, message.Cursor);
                    pointed.Report(state => state.Cursor(cursor), new SurfaceCursorChanged(cursor));
                }
                break;
            case PluginSurfaceDownKind.Ime when message.Ime is { } caret:
                if (surfaces.TryGetValue(message.Surface, out var composing))
                {
                    var ime = new SurfaceIme(composing.Id, message.Popup, caret.Active, caret.X, caret.Y, caret.Width, caret.Height);
                    composing.Report(state => state.Ime(ime), new SurfaceImeChanged(ime));
                }
                break;
            case PluginSurfaceDownKind.Automation when message.Automation is { } tree:
                if (surfaces.TryGetValue(message.Surface, out var described))
                {
                    // A child or removed id past an int is no node of the plugin's; it reads as one the protocol refuses.
                    static int Id(long id) => id is >= int.MinValue and <= int.MaxValue ? (int)id : 0;
                    var automation = new SurfaceAutomation(described.Id, message.Popup, tree.Root, tree.Reset,
                        [.. tree.Nodes.Select(node => new SurfaceNode(node.Id, node.Role, node.Name, node.Value, node.AutomationId, (SurfaceNodeStates)node.States,
                            node.X, node.Y, node.Width, node.Height, [.. node.Children.Select(Id)]))],
                        [.. tree.Removed.Select(Id)]);
                    described.Report(state => state.Automation(automation), new SurfaceAutomationChanged(automation));
                }
                break;
            case PluginSurfaceDownKind.SurfaceEnded:
                if (surfaces.TryRemove(message.Surface, out var gone))
                    gone.End(message.End switch
                    {
                        PluginSurfaceEndKind.Closed => null,
                        PluginSurfaceEndKind.Broken => new InvalidDataException(message.Detail),
                        _ => new IOException(message.Detail)
                    });
                break;
            case PluginSurfaceDownKind.Ended:
                lock (gate) endedBy = new IOException(message.Detail);
                break;
            default:
                throw new IOException("The host sent an unexpected plugin session message.");
        }
    }

    private async Task AnswerAsync(long id, IsoUICall call)
    {
        PluginSurfaceUp answer;
        try { answer = new() { Kind = PluginSurfaceUpKind.Answer, Call = id, Payload = await call.Reply.ConfigureAwait(false) }; }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            answer = new() { Kind = PluginSurfaceUpKind.Answer, Call = id, Error = (int)((error as IsoCallException)?.Error ?? IsoError.Failed), Detail = error.Message };
        }
        Send(answer, reliable: true);
    }

    /// <summary>Queues a message for the host; one that is not numbered is dropped while no call carries the session.</summary>
    internal void Send(PluginSurfaceUp message, bool reliable)
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
        if (overflow)
        {
            End(new IOException("The host is not keeping up."));
            _ = lifetime.CancelAsync();
        }
    }

    private async ValueTask<string> AwaitAsync(PluginSurfaceUp message, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (ended) throw new IOException("The plugin's session ended.");
            message.Call = ++nextCall;
            results[message.Call] = result;
        }
        Send(message, reliable: true);
        using (cancellationToken.Register(() =>
        {
            lock (gate) results.Remove(message.Call);
            result.TrySetCanceled(cancellationToken);
        }))
            return await result.Task.ConfigureAwait(false);
    }

    public ValueTask<string> CallAsync(int member, long handle, string payload, CancellationToken cancellationToken = default) =>
        AwaitAsync(new() { Kind = PluginSurfaceUpKind.Invoke, Member = member, Handle = handle, Payload = payload }, cancellationToken);

    public void OneWay(int member, long handle, string payload) =>
        Send(new() { Kind = PluginSurfaceUpKind.OneWay, Member = member, Handle = handle, Payload = payload }, reliable: true);

    public async ValueTask MirrorAsync(int member, string arguments, string json, CancellationToken cancellationToken = default) =>
        await AwaitAsync(new() { Kind = PluginSurfaceUpKind.Mirror, Member = member, Payload = arguments, Json = json }, cancellationToken).ConfigureAwait(false);

    public IPluginSurface Open(int member, long handle, string payload, string theme)
    {
        var surface = new RemotePluginSurface(this, Interlocked.Increment(ref nextSurface));
        surfaces[surface.Id] = surface;
        Send(new() { Kind = PluginSurfaceUpKind.SurfaceOpen, Surface = surface.Id, Member = member, Handle = handle, Payload = payload, Json = theme }, reliable: true);
        return surface;
    }

    internal bool Forget(RemotePluginSurface surface) => surfaces.TryRemove(new(surface.Id, surface));

    private void End(Exception? reason)
    {
        List<TaskCompletionSource<string>> waiting;
        lock (gate)
        {
            if (ended) return;
            ended = true;
            link = null;
            waiting = [.. results.Values];
            results.Clear();
            unconfirmed.Clear();
        }
        opened.TrySetException(reason ?? new IOException("The plugin's session ended."));
        foreach (var result in waiting) result.TrySetException(new IOException("The plugin's session ended."));
        calls.Writer.TryComplete(reason);
        while (calls.Reader.TryRead(out var left)) left.Abandon();
        foreach (var surface in surfaces.Values) surface.End(reason as IOException);
        surfaces.Clear();
    }

    /// <summary>Closes the session: the host runs the plugin's disposer for this client's context.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (closing) return;
            closing = true;
            link?.Writer.TryComplete();
        }
        try { await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        await lifetime.CancelAsync().ConfigureAwait(false);
        try { await running.ConfigureAwait(false); }
        catch (Exception) { }
        End(null);
    }
}

/// <summary>
/// One surface of a remote plugin session, as this client holds it. Frames are held to the surface protocol's caps again
/// before the client's control applies them.
/// </summary>
internal sealed class RemotePluginSurface(RemoteIsolatedUISession session, int id) : IPluginSurface
{
    private readonly Channel<SurfaceEvent> events = Channel.CreateUnbounded<SurfaceEvent>(new() { SingleReader = true });
    private readonly SurfaceState state = new();
    private readonly Lock gate = new();
    private int number, closed;

    public int Id { get; } = id;
    public int Number { get => Volatile.Read(ref number); set => Volatile.Write(ref number, value); }
    public ChannelReader<SurfaceEvent> Events => events.Reader;

    public void Resize(double width, double height, double scale) =>
        session.Send(new() { Kind = PluginSurfaceUpKind.Resize, Surface = Id, Width = width, Height = height, Scale = scale }, reliable: true);

    public void Input(SurfaceInput input) => session.Send(new()
    {
        Kind = PluginSurfaceUpKind.Input,
        Surface = Id,
        Input = new()
        {
            Kind = (int)input.Kind,
            X = input.X,
            Y = input.Y,
            Button = input.Button,
            Modifiers = input.Modifiers,
            DeltaX = input.DeltaX,
            DeltaY = input.DeltaY,
            Key = input.Key,
            PhysicalKey = input.PhysicalKey,
            Text = input.Text,
            Caret = input.Caret,
            Node = input.Node
        },
        Popup = input.Popup
    }, reliable: true);

    public void Acknowledge(long sequence, int popup)
    {
        lock (gate)
            if (state.Frames(popup)?.Acknowledge(sequence) != true) return;
        session.Send(new() { Kind = PluginSurfaceUpKind.FrameAck, Surface = Id, Sequence = sequence, Popup = popup }, reliable: false);
    }

    public void Dismiss() => session.Send(new() { Kind = PluginSurfaceUpKind.Dismiss, Surface = Id }, reliable: true);

    public void Focus(bool enter, bool backward, bool pointer) =>
        session.Send(new() { Kind = PluginSurfaceUpKind.Focus, Surface = Id, Enter = enter, Backward = backward, Pointer = pointer }, reliable: true);

    public void Theme(string json) => session.Send(new() { Kind = PluginSurfaceUpKind.Theme, Surface = Id, Json = json }, reliable: true);

    public void Keyframe()
    {
        // A frame half received when the connection dropped never finishes: the keyframe replaces it.
        lock (gate) state.AwaitKeyframe(lost: true);
        session.Send(new() { Kind = PluginSurfaceUpKind.Keyframe, Surface = Id }, reliable: true);
    }

    internal void Frame(PluginSurfaceFrameMessage frame)
    {
        var header = new SurfaceFrameHeader(Id, frame.Sequence, frame.Width, frame.Height, frame.Scale, frame.DesiredWidth, frame.DesiredHeight, frame.Last,
            [.. frame.Moves.Select(move => new SurfaceMove(move.X, move.Y, move.Width, move.Height, move.Dx, move.Dy))],
            [.. frame.Tiles.Select(tile => new SurfaceTile(tile.X, tile.Y, tile.Width, tile.Height, (TileCodecKind)tile.Codec, tile.Length))], frame.Keyframe, frame.Popup);
        lock (gate)
        {
            if (Volatile.Read(ref closed) != 0 || state.Stale(header)) return;
            if (state.Violation(header, frame.Data.Length) is { } violation)
            {
                Break(violation);
                return;
            }
            state.Frames(header.Popup)!.Accept(header);
            events.Writer.TryWrite(new SurfaceFrame(header, frame.Data));
        }
    }

    /// <summary>A popup opened, moved or closed; one that breaks the surface protocol ends the surface, as a bad frame does.</summary>
    internal void Place(SurfacePopup popup)
    {
        lock (gate)
        {
            if (Volatile.Read(ref closed) != 0) return;
            if (state.Place(popup) is { } violation)
            {
                Break(violation);
                return;
            }
            events.Writer.TryWrite(new SurfacePopupChanged(popup));
        }
    }

    /// <summary>Passes on a report about the surface when it keeps to the surface protocol, as the host's relay does.</summary>
    internal void Report(Func<SurfaceState, string?> violation, SurfaceEvent report)
    {
        lock (gate)
        {
            if (Volatile.Read(ref closed) != 0) return;
            if (violation(state) is { } broken)
            {
                Break(broken);
                return;
            }
            events.Writer.TryWrite(report);
        }
    }

    private void Break(string violation)
    {
        if (session.Forget(this)) session.Send(new() { Kind = PluginSurfaceUpKind.SurfaceClose, Surface = Id }, reliable: true);
        End(new InvalidDataException(violation));
    }

    internal void Left(bool backward) => events.Writer.TryWrite(new SurfaceLeft(backward));

    internal void End(Exception? reason)
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) events.Writer.TryComplete(reason);
    }

    public ValueTask DisposeAsync()
    {
        if (session.Forget(this)) session.Send(new() { Kind = PluginSurfaceUpKind.SurfaceClose, Surface = Id }, reliable: true);
        End(null);
        return ValueTask.CompletedTask;
    }
}