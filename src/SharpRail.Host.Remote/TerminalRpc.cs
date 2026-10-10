using Grpc.Core;

using ProtoBuf.Grpc;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed class TerminalRpc(ITerminalService terminals, IHostApplicationLifetime lifetime) : ITerminalRpc
{
    // A call is one attachment: a dropped client detaches, and the shell keeps running on the host.
    public async IAsyncEnumerable<TerminalOutput> RunAsync(IAsyncEnumerable<TerminalInput> input, CallContext context = default)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        var inputs = input.GetAsyncEnumerator(call.Token);
        var pump = Task.CompletedTask;
        try
        {
            if (!await inputs.MoveNextAsync() || inputs.Current.Kind != TerminalInputKind.Attach)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "A terminal call must start with an attach message."));
            await using var session = await AttachAsync(inputs.Current, call.Token);
            yield return new TerminalOutput
            {
                Attached = true,
                Created = session.Created,
                Detached = session.Detached.IsCompleted,
                Watching = session.Watching,
                Columns = session.Grid.Columns,
                Rows = session.Grid.Rows,
                Data = session.Replay.ToArray(),
                Position = session.Position,
                PrefillText = session.Prefill?.Text ?? "",
                PrefillSubmit = session.Prefill?.Submit ?? false
            };
            pump = Drive(inputs, session, call.Token);
            await foreach (var chunk in session.ReadAsync(call.Token))
                yield return new TerminalOutput { Data = chunk.ToArray(), Position = session.Position, Columns = session.Grid.Columns, Rows = session.Grid.Rows };
            await Task.WhenAny(session.Exit, session.Detached);
            if (session.Detached.IsCompleted) yield return new TerminalOutput { Detached = true, Position = session.Position };
            else if (session.Exit.IsCompletedSuccessfully) yield return new TerminalOutput { Exited = true, ExitCode = session.Exit.Result, Position = session.Position };
        }
        finally
        {
            await call.CancelAsync();
            try { await pump; } catch (Exception) { }
            await inputs.DisposeAsync();
        }
    }

    public async ValueTask<TerminalBusyReply> IsBusyAsync(TerminalSessionRequest request, CallContext context = default)
        => new() { Busy = await terminals.IsBusyAsync(request.SessionId, context.CancellationToken) };

    public async ValueTask<TerminalClosed> CloseAsync(TerminalSessionRequest request, CallContext context = default)
    {
        await terminals.CloseAsync(request.SessionId, context.CancellationToken);
        return new();
    }

    private async Task<ITerminalSession> AttachAsync(TerminalInput attach, CancellationToken cancellationToken)
    {
        try
        {
            return await terminals.AttachAsync(new(attach.SessionId, attach.WorkspaceRoot, attach.ClientId, attach.Columns, attach.Rows, attach.Offset)
            {
                TabKey = attach.TabKey,
                Yield = attach.Yield,
                Watch = attach.Watch
            }, cancellationToken);
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
    }

    private static async Task Drive(IAsyncEnumerator<TerminalInput> inputs, ITerminalSession session, CancellationToken cancellationToken)
    {
        while (await inputs.MoveNextAsync())
        {
            var message = inputs.Current;
            switch (message.Kind)
            {
                case TerminalInputKind.Data: await session.WriteAsync(message.Data, cancellationToken); break;
                // A client that bypasses its adapter's own grid check is ignored rather than ending the call.
                case TerminalInputKind.Resize when message.Columns is >= 1 and <= TerminalGrid.Max && message.Rows is >= 1 and <= TerminalGrid.Max:
                    await session.ResizeAsync(message.Columns, message.Rows, cancellationToken); break;
                case TerminalInputKind.Kill: await session.KillAsync(cancellationToken); break;
            }
        }
    }
}