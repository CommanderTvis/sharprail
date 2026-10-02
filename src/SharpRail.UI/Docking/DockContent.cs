using Avalonia.Controls;

namespace SharpRail.UI.Docking;

public sealed partial class DockSurface
{
    public void RefreshContents(params string[] tabIds)
    {
        foreach (var site in sites)
        {
            if (site.Header || site.Control is not Border body || !contentHosts.Contains(body)) continue;
            var selected = Session.Selected(site.Group);
            var pane = selected is null ? null : Session.PaneFor(site.Group, selected.Id);
            if (selected is null || tabIds.Length > 0 && !tabIds.Contains(selected.Id) && pane?.TabIds.Any(tabIds.Contains) != true) continue;
            if (pane is null && ReferenceEquals(body.Child, renderContent(selected))) continue;
            body.Child = null;
            body.Child = BodyFor(Session.Group(site.Group), selected);
        }
    }

    /// <summary>Renders the empty-group content again, for groups that show no tab, leaving every tab and its chrome in place.</summary>
    public void RefreshEmptyContents()
    {
        foreach (var site in sites)
        {
            if (site.Header || site.Control is not Border body || !contentHosts.Contains(body) || Session.Selected(site.Group) is not null) continue;
            body.Child = null;
            body.Child = Empty(Session.Group(site.Group));
        }
    }
}