using System.Collections.Concurrent;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.SpecDialect;

/// <summary>The spec dialect's host half: the per-workspace graph read, its eviction on workspace removal, and the MCP spec tools.</summary>
public sealed class SpecDialectHost : PluginHostModule
{
    public override PluginContract Contract => SpecDialectContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        // A workspace id is its worktree root; once resolved, its index is reused until the workspace goes away.
        var indexes = new ConcurrentDictionary<string, SpecIndex>(StringComparer.Ordinal);

        async ValueTask<SpecGraphSnapshot> GraphAsync(string workspaceId, CancellationToken ct)
        {
            var workspace = await context.WorkspaceAsync(workspaceId, ct) ?? throw new InvalidOperationException($"Unknown workspace: {workspaceId}");
            await context.WatchWorkspaceAsync(workspaceId, ct);
            if (!indexes.TryGetValue(workspaceId, out var index))
            {
                index = indexes.GetOrAdd(workspaceId, _ => new SpecIndex(workspace.Path));
            }
            return new(await Task.Run(() => index.ReadAsync(ct), ct));
        }

        context.Method(SpecDialectContract.Graph, (parameters, _, ct) => GraphAsync(parameters.WorkspaceId, ct));
        context.OnWorkspace(change =>
        {
            if (change is WorkspaceRemoved removed) indexes.TryRemove(removed.Id, out _);
        });
        foreach (var tool in SpecTools.All) context.Tool(tool);
        return ValueTask.FromResult<PluginDisposer?>(null);
    }
}