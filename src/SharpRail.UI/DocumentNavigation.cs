using SharpRail.UI.Docking;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private sealed record Navigation(string Workspace, string Group, long Stamp, long Project);
    private readonly Dictionary<string, long> navigationClocks = [];
    private sealed class BrowseFlight(Navigation navigation, bool keep)
    {
        internal Navigation Navigation = navigation;
        internal bool Keep = keep;
        internal bool ClaimPreview = !keep;
    }
    private readonly Dictionary<(string Workspace, string Path), BrowseFlight> browseFlights = [];

    private async Task BrowseDocumentAsync(string path, bool keep)
    {
        if (!WorkspaceMounted) return;
        if (atHome) await OpenWorkspaceAsync(projectRoot, false);
        if (!WorkspaceMounted || atHome) return;
        if (Plugins.FileViewer(path)?.Registration.Open is { } takeOver && takeOver(workspaceRoot, path)) return;
        keep |= !Preferences.PreviewTabs;
        var navigation = BeginNavigation();
        var identity = (workspaceRoot, path);
        if (browseFlights.TryGetValue(identity, out var pending))
        {
            pending.Keep |= keep; pending.ClaimPreview |= !keep; pending.Navigation = navigation;
            return;
        }
        var flight = new BrowseFlight(navigation, keep);
        browseFlights.Add(identity, flight);
        var settle = Task.Delay(250, lifetime.Token);
        try
        {
            var kind = DocumentKind(path, false);
            var tab = new DockTab(kind + ":" + path, Path.GetFileName(path), kind, path);
            var key = identity.workspaceRoot + ":" + tab.Id;
            if (!documents.TryGetValue(key, out var document)) document = await ReadForKindAsync(path, kind, lifetime.Token);
            if (!flight.Keep) await settle;
            if (flight.Navigation.Project != projectRequest || flight.Navigation.Workspace != workspaceRoot) return;
            var destination = AcceptNavigation(flight.Navigation);
            if (destination is null && !flight.Keep) return;
            documents[key] = document;
            Layout.Open(tab, flight.Keep, destination ?? flight.Navigation.Group,
                claimPreview: flight.Keep && flight.ClaimPreview && destination is not null,
                activate: destination is not null && destination == Layout.View.FocusedCenter);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
        finally { browseFlights.Remove(identity); }
    }

    /// <summary>Mod+Shift+F: one query over the active worktree; a hit opens its file at that line.</summary>
    private async Task SearchWorkspaceAsync()
    {
        if (OwnedWindows.Any(window => Equals(window.Tag, "SearchDialog"))) return;
        var request = projectRequest; var workspace = workspaceRoot;
        var dialog = new Panels.SearchDialog(async (query, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            return await Task.Run(async () => await host.SearchAsync(query, linked.Token), linked.Token);
        });
        var undim = Dim();
        Host.Abstractions.SearchHit? hit;
        try { hit = await dialog.ShowAsync(this); }
        finally { undim(); }
        if (hit is null || request != projectRequest || workspace != workspaceRoot) return;
        await OpenDocumentAsync(hit.Path, true, line: hit.Line);
    }

    private long AdvanceNavigation(string group)
    {
        surface?.InvalidatePreviewKeep();
        var stamp = navigationClocks.GetValueOrDefault(group) + 1;
        navigationClocks[group] = stamp;
        return stamp;
    }

    private Navigation BeginNavigation()
    {
        var leaves = Layout.State.Center.Leaves().ToArray();
        var group = leaves.Contains(Layout.View.FocusedCenter) ? Layout.View.FocusedCenter : leaves[0];
        return new(workspaceRoot, group, AdvanceNavigation(group), projectRequest);
    }

    private string? AcceptNavigation(Navigation request)
    {
        if (request.Project != projectRequest || request.Workspace != workspaceRoot) return null;
        var leaves = Layout.State.Center.Leaves().ToArray();
        if (leaves.Contains(request.Group))
            return navigationClocks.GetValueOrDefault(request.Group) == request.Stamp ? request.Group : null;
        var destination = leaves.Contains(Layout.View.FocusedCenter) ? Layout.View.FocusedCenter : leaves[0];
        AdvanceNavigation(destination);
        return destination;
    }
}