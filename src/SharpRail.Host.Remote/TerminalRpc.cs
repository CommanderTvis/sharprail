using Grpc.Core;
using ProtoBuf.Grpc;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed class TerminalRpc(ITerminalService terminals) : ITerminalRpc
{
    // A session lives exactly as long as its call; a dropped client ends the shell.
    public async IAsyncEnumerable<TerminalOutput> RunAsync(IAsyncEnumerable<TerminalInput> input, CallContext context = default)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var inputs = input.GetAsyncEnumerator(call.Token);
        var pump = Task.CompletedTask;
        try
        {
            if (!await inputs.MoveNextAsync() || inputs.Current.Kind != TerminalInputKind.Start)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "A terminal call must start with a start message."));
            await using var session = await StartAsync(inputs.Current, call.Token);
            yield return new TerminalOutput { Started = true };
            pump = Drive(inputs, session, call.Token);
            await foreach (var chunk in session.ReadAsync(call.Token)) yield return new TerminalOutput { Data = chunk.ToArray() };
            yield return new TerminalOutput { Exited = true, ExitCode = await session.Exit };
        }
        finally
        {
            await call.CancelAsync();
            try { await pump; } catch (Exception) { }
            await inputs.DisposeAsync();
        }
    }

    public async ValueTask<TerminalBusyReply> IsBusyAsync(TerminalBusyRequest request, CallContext context = default)
        => new() { Busy = await terminals.IsBusyAsync(request.SessionId, context.CancellationToken) };

    private async Task<ITerminalSession> StartAsync(TerminalInput start, CancellationToken cancellationToken)
    {
        try { return await terminals.StartAsync(new(start.SessionId, start.WorkspaceRoot, start.Columns, start.Rows), cancellationToken); }
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
                case TerminalInputKind.Resize: await session.ResizeAsync(message.Columns, message.Rows, cancellationToken); break;
                case TerminalInputKind.Kill: await session.KillAsync(cancellationToken); break;
            }
        }
    }
}
