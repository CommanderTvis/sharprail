using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed class LocalTerminalAdapter(ITerminalService host) : ITerminalService
{
    public ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default) => host.AttachAsync(request, cancellationToken);
    public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => host.IsBusyAsync(sessionId, cancellationToken);
    public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => host.CloseAsync(sessionId, cancellationToken);
}

// Terminal sessions on a gRPC host. A session survives a lost connection: it reconnects and resumes from the
// last output it read, so nothing is shown twice and final output still precedes the exit status.
public sealed class RemoteTerminalAdapter : ITerminalService, IDisposable
{
    private readonly GrpcChannel channel;
    private readonly string token;

    // A unix: address names a Unix domain socket, such as the app's own terminal relay endpoint.
    public RemoteTerminalAdapter(Uri address, string token, Interceptor? interceptor = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        this.token = token;
        channel = address.Scheme == "unix"
            ? GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    ConnectCallback = async (_, cancellationToken) =>
                    {
                        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        try
                        {
                            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Uri.UnescapeDataString(address.AbsolutePath)), cancellationToken);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); throw; }
                    }
                }
            })
            : GrpcChannel.ForAddress(address);
        var invoker = interceptor is null ? channel.CreateCallInvoker() : channel.Intercept(interceptor);
        Service = invoker.CreateGrpcService<ITerminalRpc>();
    }

    internal ITerminalRpc Service { get; }
    internal Metadata Headers => new() { { "authorization", $"Bearer {token}" } };
    // How long a session keeps trying to reach its host before it reports the connection lost.
    public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        var session = new RemoteTerminalSession(this, request);
        try
        {
            await session.ConnectAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default)
        => (await Service.IsBusyAsync(new() { SessionId = sessionId }, Options(cancellationToken))).Busy;

    public async ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default)
        => await Service.CloseAsync(new() { SessionId = sessionId }, Options(cancellationToken));

    private CallContext Options(CancellationToken cancellationToken) => new(new CallOptions(Headers, DateTime.UtcNow.AddSeconds(15), cancellationToken));

    public void Dispose() => channel.Dispose();
}

internal sealed class RemoteTerminalSession(RemoteTerminalAdapter adapter, TerminalAttachRequest request) : ITerminalSession
{
    private readonly Channel<TerminalInput> outbound = Channel.CreateUnbounded<TerminalInput>();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? call;
    private IAsyncEnumerator<TerminalOutput>? outputs;
    private ReadOnlyMemory<byte> pendingReplay;
    private int reading, disposed;
    private bool connected;
    private long position = request.Offset;
    private (int Columns, int Rows) size = (request.Columns, request.Rows);

    public string Id => request.SessionId;
    public bool Created { get; private set; }
    public long Position => Interlocked.Read(ref position);
    public ReadOnlyMemory<byte> Replay { get; private set; }
    public Task<int> Exit => exit.Task;
    public Task Detached => detached.Task;
    public TerminalPrefill? Prefill { get; private set; }

    // Attaches, retrying a connection that fails before the host's reply arrives. The retry repeats the
    // same fresh attach, so a reply lost with its connection still yields one shell and one replay.
    internal async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + adapter.ReconnectTimeout;
        var delay = TimeSpan.FromMilliseconds(100);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        while (true)
        {
            try
            {
                var attached = await OpenCallAsync(linked.Token);
                if (!connected)
                {
                    Created = attached.Created; Replay = attached.Data; connected = true;
                    if (attached.PrefillText.Length > 0) Prefill = new(attached.PrefillText, attached.PrefillSubmit);
                }
                else pendingReplay = attached.Data;
                Interlocked.Exchange(ref position, attached.Position);
                return;
            }
            catch (Exception error) when (Transient(error) && !linked.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(delay, linked.Token);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition) { throw new IOException(error.Status.Detail, error); }
            catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private async Task<TerminalOutput> OpenCallAsync(CancellationToken cancellationToken)
    {
        CloseCall();
        if (outputs is not null) try { await outputs.DisposeAsync(); } catch (Exception) { }
        // Terminal calls are long-lived, so they carry no deadline.
        var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        call = current;
        var attach = new TerminalInput
        {
            Kind = TerminalInputKind.Attach,
            SessionId = request.SessionId,
            WorkspaceRoot = request.WorkspaceRoot,
            ClientId = request.ClientId,
            Columns = size.Columns,
            Rows = size.Rows,
            Offset = Position,
            TabKey = request.TabKey
        };
        var stream = adapter.Service.RunAsync(Inputs(attach, current.Token), new CallContext(new CallOptions(adapter.Headers, cancellationToken: current.Token)))
            .GetAsyncEnumerator(current.Token);
        outputs = stream;
        using var registration = cancellationToken.Register(current.Cancel);
        if (!await stream.MoveNextAsync() || !stream.Current.Attached) throw new IOException("The host did not attach the terminal.");
        return stream.Current;
    }

    private async IAsyncEnumerable<TerminalInput> Inputs(TerminalInput attach, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return attach;
        while (await outbound.Reader.WaitToReadAsync(cancellationToken))
            while (!cancellationToken.IsCancellationRequested && outbound.Reader.TryRead(out var message)) yield return message;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref reading, 1) != 0) throw new InvalidOperationException("A terminal session has a single reader.");
        using var registration = cancellationToken.Register(lifetime.Cancel);
        while (true)
        {
            if (!pendingReplay.IsEmpty) { var replay = pendingReplay; pendingReplay = default; yield return replay; }
            TerminalOutput? message = null;
            Exception? lost = null;
            try { message = await outputs!.MoveNextAsync() ? outputs.Current : null; }
            catch (Exception error) when (!lifetime.IsCancellationRequested && Transient(error)) { lost = error; }
            catch (Exception error)
            {
                exit.TrySetException(error);
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                throw;
            }
            if (lost is null && message is null) lost = new IOException("The terminal stream ended before the shell exited.");
            if (lost is not null)
            {
                try { await ConnectAsync(CancellationToken.None); }
                catch (Exception error)
                {
                    var failure = new IOException("The terminal connection was lost: " + error.Message, lost);
                    exit.TrySetException(failure);
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    throw failure;
                }
                continue;
            }
            if (message!.Detached) { detached.TrySetResult(); yield break; }
            if (message.Exited) { exit.TrySetResult(message.ExitCode); yield break; }
            if (message.Data.Length == 0) continue;
            Interlocked.Exchange(ref position, message.Position);
            yield return message.Data;
        }
    }

    private static bool Transient(Exception error) => error switch
    {
        RpcException rpc => rpc.StatusCode is StatusCode.Unavailable or StatusCode.Internal or StatusCode.Unknown or StatusCode.Aborted or StatusCode.Cancelled,
        IOException or HttpRequestException or ObjectDisposedException => true,
        _ => false
    };

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => Send(new() { Kind = TerminalInputKind.Data, Data = data.ToArray() });

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        size = (columns, rows);
        return Send(new() { Kind = TerminalInputKind.Resize, Columns = columns, Rows = rows });
    }

    public ValueTask KillAsync(CancellationToken cancellationToken = default) => Send(new() { Kind = TerminalInputKind.Kill });

    private ValueTask Send(TerminalInput message)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        outbound.Writer.TryWrite(message);
        return ValueTask.CompletedTask;
    }

    private void CloseCall()
    {
        call?.Cancel();
        call?.Dispose();
        call = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        outbound.Writer.TryComplete();
        await lifetime.CancelAsync();
        if (outputs is not null) try { await outputs.DisposeAsync(); } catch (Exception) { }
        CloseCall();
        exit.TrySetException(new ObjectDisposedException(nameof(RemoteTerminalSession)));
        _ = exit.Task.Exception;
    }
}