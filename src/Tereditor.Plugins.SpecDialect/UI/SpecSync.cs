namespace SharpRail.Plugins.SpecDialect;

/// <summary>Reads a workspace's graph into the store; concurrent syncs of one workspace at one file revision collapse into one read.</summary>
public sealed class SpecSync(Func<string, ValueTask<SpecGraphSnapshot>> read, SpecStore store)
{
    private readonly HashSet<(string Workspace, int Revision)> inFlight = [];

    public SpecStore Store => store;

    public async Task LoadAsync(string workspaceId)
    {
        try
        {
            var snapshot = await read(workspaceId);
            store.SetSpecs(workspaceId, snapshot.Nodes);
            store.SetFailed(workspaceId, false);
        }
        catch (Exception)
        {
            store.SetFailed(workspaceId, true);
        }
    }

    public void Sync(string workspaceId, int revision)
    {
        if (!inFlight.Add((workspaceId, revision))) return;
        _ = Run();

        async Task Run()
        {
            try { await LoadAsync(workspaceId); }
            finally { inFlight.Remove((workspaceId, revision)); }
        }
    }
}