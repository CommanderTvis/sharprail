using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.SpecDialect;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed class BlueprintStore(IPluginUIContext context) : IDisposable
{
    private readonly Dictionary<string, BlueprintState?> states = [];
    private readonly Dictionary<string, IDisposable> subscriptions = [];
    private readonly Dictionary<string, IReadOnlyList<SpecGraphNode>> graphs = [];
    private readonly Dictionary<string, int> revisions = [];
    private bool disposed;
    public event Action<string>? Changed;
    public bool Loaded(string workspace) => states.ContainsKey(workspace);
    public BlueprintState? Get(string workspace) => states.GetValueOrDefault(workspace);
    public string? SpecPath(string workspace, string id) => graphs.GetValueOrDefault(workspace)?.FirstOrDefault(node => node.Id == id)?.Path;

    public void Follow(string workspace)
    {
        if (subscriptions.ContainsKey(workspace)) return;
        subscriptions[workspace] = context.Subscribe(BlueprintContract.Changed, payload => Set(payload.WorkspaceId, payload.State), new BlueprintScope(workspace));
        _ = Watch(workspace);
        RefreshGraph(workspace);
    }

    public async void RefreshGraph(string workspace)
    {
        var revision = context.Host().WorkspaceRevisions.GetValueOrDefault(workspace);
        if (revisions.TryGetValue(workspace, out var known) && known == revision) return;
        revisions[workspace] = revision;
        try
        {
            var graph = await context.Dependency(SpecDialectContract.Contract).RequestAsync(SpecDialectContract.Graph, new(workspace));
            if (disposed || revisions.GetValueOrDefault(workspace) != revision) return;
            graphs[workspace] = graph.Nodes;
            Changed?.Invoke(workspace);
        }
        catch (Exception error) { if (!disposed) context.Log.Warn("Could not read Blueprint's spec links", new { Workspace = workspace, error.Message }); }
    }

    private async Task Watch(string workspace)
    {
        try { await context.WatchWorkspaceAsync(workspace); }
        catch (Exception error) { context.Log.Warn("Could not watch Blueprint's workspace", new { Workspace = workspace, error.Message }); }
    }

    public void Set(string workspace, BlueprintState? state)
    {
        states[workspace] = state;
        Changed?.Invoke(workspace);
        context.Invalidate();
        if (state?.Author is BlueprintTerminalAuthor author && context.Host().Terminals.GetValueOrDefault(workspace)?.Any(tab => tab.TabKey == author.TabKey) == true)
            context.FocusCompanion(new(workspace, author.TabKey), "blueprint");
    }

    public void Evict(string workspace)
    {
        if (subscriptions.Remove(workspace, out var subscription)) subscription.Dispose();
        states.Remove(workspace);
        graphs.Remove(workspace); revisions.Remove(workspace);
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var subscription in subscriptions.Values) subscription.Dispose();
        subscriptions.Clear();
        states.Clear();
    }
}