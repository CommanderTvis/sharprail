using System.Runtime.CompilerServices;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed class LocalTerminalCatalogAdapter(ITerminalCatalogService host) : ITerminalCatalogService
{
    public ValueTask<TerminalCatalog> OpenWorkspaceAsync(string workspaceRoot, IReadOnlyList<TerminalTab> tabs, CancellationToken cancellationToken = default) => host.OpenWorkspaceAsync(workspaceRoot, tabs, cancellationToken);
    public ValueTask<TerminalCatalog> ReserveAsync(string workspaceRoot, TerminalTab tab, CancellationToken cancellationToken = default) => host.ReserveAsync(workspaceRoot, tab, cancellationToken);
    public ValueTask<TerminalCatalog> CloseTabAsync(string workspaceRoot, string key, CancellationToken cancellationToken = default) => host.CloseTabAsync(workspaceRoot, key, cancellationToken);
    public ValueTask<TerminalCatalog> CloseTabForUndoAsync(string workspaceRoot, string key, CancellationToken cancellationToken = default) => host.CloseTabForUndoAsync(workspaceRoot, key, cancellationToken);
    public ValueTask<TerminalCatalog> CloseWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default) => host.CloseWorkspaceAsync(workspaceRoot, cancellationToken);
    public IAsyncEnumerable<TerminalCatalog> WatchAsync(CancellationToken cancellationToken = default) => host.WatchAsync(cancellationToken);
    public IAsyncEnumerable<TerminalSignal> WatchSignalsAsync(CancellationToken cancellationToken = default) => host.WatchSignalsAsync(cancellationToken);
}

/// <summary>
/// The terminal catalog of a WebSocket RPC host. Refusals surface as the exceptions the local catalog throws; a host
/// that predates the catalog reports <see cref="NotSupportedException"/>.
/// </summary>
public sealed class RemoteTerminalCatalogAdapter : ITerminalCatalogService, IDisposable
{
    private readonly RpcChannel channel;
    private readonly ITerminalCatalogRpc service;
    private readonly string token;

    public RemoteTerminalCatalogAdapter(Uri address, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        this.token = token;
        channel = new RpcChannel(address, null);
        service = channel.CreateService<ITerminalCatalogRpc>();
    }

    private CallContext Context(CancellationToken cancellationToken, bool stream = false) => new(new CallOptions(
        headers: new Metadata { { "authorization", $"Bearer {token}" } },
        deadline: stream ? null : DateTime.UtcNow.AddSeconds(15), cancellationToken: cancellationToken));

    public ValueTask<TerminalCatalog> OpenWorkspaceAsync(string workspaceRoot, IReadOnlyList<TerminalTab> tabs, CancellationToken cancellationToken = default) =>
        Execute(() => service.OpenWorkspaceAsync(Request(workspaceRoot, tabs), Context(cancellationToken)));

    public ValueTask<TerminalCatalog> ReserveAsync(string workspaceRoot, TerminalTab tab, CancellationToken cancellationToken = default) =>
        Execute(() => service.ReserveAsync(Request(workspaceRoot, [tab]), Context(cancellationToken)));

    public ValueTask<TerminalCatalog> CloseTabAsync(string workspaceRoot, string key, CancellationToken cancellationToken = default) =>
        Execute(() => service.CloseTabAsync(Request(workspaceRoot, [new(key, "")]), Context(cancellationToken)));

    // A host that predates undo ignores the flag and closes the tab outright.
    public ValueTask<TerminalCatalog> CloseTabForUndoAsync(string workspaceRoot, string key, CancellationToken cancellationToken = default) =>
        Execute(() => { var request = Request(workspaceRoot, [new(key, "")]); request.Undoable = true; return service.CloseTabAsync(request, Context(cancellationToken)); });

    public ValueTask<TerminalCatalog> CloseWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
        Execute(() => service.CloseWorkspaceAsync(Request(workspaceRoot, []), Context(cancellationToken)));

    /// <summary>Ends with an exception when the transport drops; the caller resubscribes and receives a fresh snapshot.</summary>
    public async IAsyncEnumerable<TerminalCatalog> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var replies = service.WatchAsync(new(), Context(cancellationToken, stream: true)).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try { if (!await replies.MoveNextAsync()) yield break; }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { throw Unsupported(error); }
            yield return Map(replies.Current);
        }
    }

    /// <summary>Ends with an exception when the transport drops; a host that predates signals reports <see cref="NotSupportedException"/>.</summary>
    public async IAsyncEnumerable<TerminalSignal> WatchSignalsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var replies = service.WatchSignalsAsync(new(), Context(cancellationToken, stream: true)).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try { if (!await replies.MoveNextAsync()) yield break; }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { throw new NotSupportedException("This host does not report terminal signals.", error); }
            var reply = replies.Current;
            if (reply.Kind is < 0 or > 2 || reply.State is < 0 or > 4) continue;
            yield return new(reply.SessionId, (TerminalSignalKind)reply.Kind)
            {
                Progress = (TerminalProgressState)reply.State,
                Percent = Math.Clamp(reply.Percent, -1, 100),
                Title = reply.Title,
                Body = reply.Body
            };
        }
    }

    private static TerminalCatalogRequest Request(string workspaceRoot, IReadOnlyList<TerminalTab> tabs) =>
        new() { WorkspaceRoot = workspaceRoot, Tabs = tabs.Select(tab => new TerminalTabMessage { Key = tab.Key, Title = tab.Title }).ToList() };

    private static async ValueTask<TerminalCatalog> Execute(Func<ValueTask<TerminalCatalogReply>> call)
    {
        try { return Map(await call()); }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument) { throw new ArgumentException(error.Status.Detail, error); }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition) { throw new InvalidOperationException(error.Status.Detail, error); }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { throw Unsupported(error); }
    }

    private static NotSupportedException Unsupported(Exception error) => new("This host does not keep a terminal catalog.", error);

    private static TerminalCatalog Map(TerminalCatalogReply reply) => new(reply.Revision,
        reply.Workspaces.ToDictionary(entry => entry.WorkspaceRoot, entry => (IReadOnlyList<TerminalTab>)entry.Tabs.Select(tab => new TerminalTab(tab.Key, tab.Title)).ToArray()));

    public void Dispose() => channel.Dispose();
}