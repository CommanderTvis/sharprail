using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed class LocalTerminalAdapter(ITerminalService host) : ITerminalService
{
    public ValueTask<ITerminalSession> StartAsync(TerminalStartRequest request, CancellationToken cancellationToken = default) => host.StartAsync(request, cancellationToken);
    public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => host.IsBusyAsync(sessionId, cancellationToken);
}

public sealed class RemoteTerminalAdapter : ITerminalService, IDisposable
{
    private readonly GrpcChannel channel;
    private readonly ITerminalRpc service;
    private readonly string token;

    public RemoteTerminalAdapter(Uri address, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        this.token = token;
        channel = GrpcChannel.ForAddress(address);
        service = channel.CreateGrpcService<ITerminalRpc>();
    }

    private Metadata Headers => new() { { "authorization", $"Bearer {token}" } };

    public async ValueTask<ITerminalSession> StartAsync(TerminalStartRequest request, CancellationToken cancellationToken = default)
    {
        var input = Channel.CreateUnbounded<TerminalInput>(new() { SingleReader = true });
        input.Writer.TryWrite(new()
        {
            Kind = TerminalInputKind.Start,
            SessionId = request.SessionId,
            WorkspaceRoot = request.WorkspaceRoot,
            Columns = request.Columns,
            Rows = request.Rows
        });
        // Terminal calls are long-lived, so they carry no deadline.
        var call = new CancellationTokenSource();
        var outputs = service.RunAsync(input.Reader.ReadAllAsync(call.Token), new CallContext(new CallOptions(Headers, cancellationToken: call.Token)))
            .GetAsyncEnumerator(call.Token);
        try
        {
            using var registration = cancellationToken.Register(call.Cancel);
            if (!await outputs.MoveNextAsync() || !outputs.Current.Started) throw new IOException("The host did not start the terminal.");
            return new RemoteTerminalSession(request.SessionId, input.Writer, outputs, call);
        }
        catch (Exception error)
        {
            await call.CancelAsync();
            await outputs.DisposeAsync();
            call.Dispose();
            if (error is RpcException { StatusCode: StatusCode.FailedPrecondition } rejected) throw new IOException(rejected.Status.Detail, error);
            if (error is RpcException { StatusCode: StatusCode.Cancelled } && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw;
        }
    }

    public async ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default)
        => (await service.IsBusyAsync(new() { SessionId = sessionId },
            new CallContext(new CallOptions(Headers, DateTime.UtcNow.AddSeconds(15), cancellationToken)))).Busy;

    public void Dispose() => channel.Dispose();
}

internal sealed class RemoteTerminalSession(string id, ChannelWriter<TerminalInput> input, IAsyncEnumerator<TerminalOutput> outputs, CancellationTokenSource call)
    : ITerminalSession
{
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int reading, disposed;

    public string Id => id;
    public Task<int> Exit => exit.Task;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref reading, 1) != 0) throw new InvalidOperationException("A terminal session has a single reader.");
        using var registration = cancellationToken.Register(call.Cancel);
        while (true)
        {
            bool next;
            try { next = await outputs.MoveNextAsync(); }
            catch (Exception error)
            {
                exit.TrySetException(error);
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                throw;
            }
            if (!next) break;
            var message = outputs.Current;
            if (message.Exited) { exit.TrySetResult(message.ExitCode); yield break; }
            if (message.Data.Length > 0) yield return message.Data;
        }
        exit.TrySetException(new IOException("The terminal connection ended before the shell exited."));
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => Send(new() { Kind = TerminalInputKind.Data, Data = data.ToArray() });

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        => Send(new() { Kind = TerminalInputKind.Resize, Columns = columns, Rows = rows });

    public ValueTask KillAsync(CancellationToken cancellationToken = default) => Send(new() { Kind = TerminalInputKind.Kill });

    private ValueTask Send(TerminalInput message)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        input.TryWrite(message);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        input.TryComplete();
        await call.CancelAsync();
        try { await outputs.DisposeAsync(); } catch (Exception) { }
        call.Dispose();
        exit.TrySetException(new ObjectDisposedException(nameof(RemoteTerminalSession)));
    }
}
