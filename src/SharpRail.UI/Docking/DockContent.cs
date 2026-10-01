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
            if (selected is null || tabIds.Length > 0 && !tabIds.Contains(selected.Id)) continue;
            var content = renderContent(selected);
            if (ReferenceEquals(body.Child, content)) continue;
            body.Child = null;
            body.Child = content;
        }
    }

    /// <summary>Renders the empty-group content again, for groups that show no tab, leaving every tab and its chrome in place.</summary>
    public void RefreshEmptyContents()
    {
        foreach (var site in sites)
        {
            if (site.Header || site.Control is not Border body || !contentHosts.Contains(body) || Session.Selected(site.Group) is not null) continue;
            body.Child = null;
            body.Child = renderContent(null);
        }
    }
}
