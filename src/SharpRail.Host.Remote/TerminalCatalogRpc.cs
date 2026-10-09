using Grpc.Core;

using ProtoBuf.Grpc;

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
        Execute(() => catalog.CloseTabAsync(request.WorkspaceRoot, request.Tabs.FirstOrDefault()?.Key ?? "", context.CancellationToken));

    public ValueTask<TerminalCatalogReply> CloseWorkspaceAsync(TerminalCatalogRequest request, CallContext context = default) =>
        Execute(() => catalog.CloseWorkspaceAsync(request.WorkspaceRoot, context.CancellationToken));

    public async IAsyncEnumerable<TerminalCatalogReply> WatchAsync(TerminalCatalogRequest request, CallContext context = default)
    {
        // Streams end with the host so a graceful shutdown does not wait for watchers.
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        await foreach (var snapshot in catalog.WatchAsync(watch.Token)) yield return Map(snapshot);
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