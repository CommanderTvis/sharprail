using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed class TerminalCatalogRpc(ITerminalCatalogService catalog, IHostApplicationLifetime lifetime) : ITerminalCatalogRpc
{
    public ValueTask<TerminalCatalogReply> OpenWorkspaceAsync(TerminalCatalogRequest request, CallContext context = default) =>
        Execute(() => catalog.OpenWorkspaceAsync(request.WorkspaceRoot, request.Tabs.Select(tab => new TerminalTab(tab.Key, tab.Title)).ToArray(), context.CancellationToken));

    public ValueTask<TerminalCatalogReply> ReserveAsync(TerminalCatalogRequest request, CallContext context = default) => Execute(() =>
    {
        if (request.Tabs is not [var tab]) throw new ArgumentException("Invalid terminal tab key.");
        return catalog.ReserveAsync(request.WorkspaceRoot, new(tab.Key, tab.Title), context.CancellationToken);
    });

    public ValueTask<TerminalCatalogReply> CloseTabAsync(TerminalCatalogRequest request, CallContext context = default) =>
        Execute(() => request.Undoable
            ? catalog.CloseTabForUndoAsync(request.WorkspaceRoot, request.Tabs.FirstOrDefault()?.Key ?? "", context.CancellationToken)
            : catalog.CloseTabAsync(request.WorkspaceRoot, request.Tabs.FirstOrDefault()?.Key ?? "", context.CancellationToken));

    public ValueTask<TerminalCatalogReply> CloseWorkspaceAsync(TerminalCatalogRequest request, CallContext context = default) =>
        Execute(() => catalog.CloseWorkspaceAsync(request.WorkspaceRoot, context.CancellationToken));

    public async IAsyncEnumerable<TerminalCatalogReply> WatchAsync(TerminalCatalogRequest request, CallContext context = default)
    {
        // Streams end with the host so a graceful shutdown does not wait for watchers.
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        await foreach (var snapshot in catalog.WatchAsync(watch.Token)) yield return Map(snapshot);
    }

    public async IAsyncEnumerable<TerminalSignalMessage> WatchSignalsAsync(TerminalCatalogRequest request, CallContext context = default)
    {
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        IAsyncEnumerator<TerminalSignal> signals;
        try { signals = catalog.WatchSignalsAsync(watch.Token).GetAsyncEnumerator(watch.Token); }
        catch (NotSupportedException error) { throw new RpcException(new Status(StatusCode.Unimplemented, error.Message)); }
        await using (signals)
            while (true)
            {
                try { if (!await signals.MoveNextAsync()) yield break; }
                catch (NotSupportedException error) { throw new RpcException(new Status(StatusCode.Unimplemented, error.Message)); }
                var signal = signals.Current;
                yield return new()
                {
                    SessionId = signal.SessionId, Kind = (int)signal.Kind, State = (int)signal.Progress, Percent = signal.Percent,
                    Title = signal.Title, Body = signal.Body
                };
            }
    }

    private static async ValueTask<TerminalCatalogReply> Execute(Func<ValueTask<TerminalCatalog>> operation)
    {
        try { return Map(await operation()); }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
        catch (Exception error) when (error is InvalidOperationException or IOException) { throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message)); }
    }

    private static TerminalCatalogReply Map(TerminalCatalog snapshot) => new()
    {
        Revision = snapshot.Revision,
        Workspaces = snapshot.Workspaces.Select(entry => new TerminalWorkspaceMessage
        {
            WorkspaceRoot = entry.Key,
            Tabs = entry.Value.Select(tab => new TerminalTabMessage { Key = tab.Key, Title = tab.Title }).ToList()
        }).ToList()
    };
}