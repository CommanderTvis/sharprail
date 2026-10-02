using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.SpecDialect;

/// <summary>
/// The spec dialect's UI half: the Specs side tool, the graph read kept current for the active workspace (driven from
/// activation, not the panel, since the document-link slot needs it whether or not the tab is showing), and the
/// <c>spec:&lt;id&gt;</c> document links.
/// </summary>
public sealed class SpecDialectUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var store = new SpecStore();
        var sync = new SpecSync(async workspace => await context.RequestAsync(SpecDialectContract.Graph, new SpecGraphParams(workspace)), store);
        // A document link resolves against this store, so a graph that changed re-asks the open documents.
        store.Changed += _ => context.Invalidate();

        context.SideTool(new(SpecDialectManifest.Tool, workspace => new SpecsPanel(context, sync, workspace))
        {
            RailDefault = async (workspace, ct) =>
                (await context.RequestAsync(SpecDialectContract.Graph, new SpecGraphParams(workspace), ct)).Nodes.Count > 0
        });

        // The read follows the active workspace and its file revision once the host knows the workspace's project.
        (string? Workspace, int Revision, bool Known) Scope(PluginHostProjection host) => (host.ActiveWorkspaceId,
            host.ActiveWorkspaceId is { } id ? host.WorkspaceRevisions.GetValueOrDefault(id) : 0,
            host.ContextProjectId is { } project && host.Projects.Any(known => known.Id == project));
        void Follow((string? Workspace, int Revision, bool Known) scope)
        {
            if (scope is { Workspace: { } workspace, Known: true }) sync.Sync(workspace, scope.Revision);
        }
        Follow(Scope(context.Host()));
        context.WatchHost(Scope, (next, _) => Follow(next));

        context.DocumentLinkSlot((workspace, href) =>
        {
            if (!href.StartsWith("spec:", StringComparison.Ordinal)) return null;
            var target = Uri.UnescapeDataString(href["spec:".Length..]);
            return target.Length == 0 ? null : store.Specs(workspace)?.FirstOrDefault(node => node.Id == target)?.Path;
        });

        context.OnWorkspaceRemoved(store.Evict);
        return null;
    }
}