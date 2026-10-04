using Avalonia.Controls;

namespace SharpRail.UI.Docking;

public sealed partial class DockSurface
{
    public void RefreshContents(params string[] tabIds)
    {
        foreach (var site in sites)
        {
            if (site.Header || site.Control is not DockPanel body || !contentHosts.Contains(body)) continue;
            var selected = Session.Selected(site.Group);
            var pane = selected is null ? null : Session.PaneFor(site.Group, selected.Id);
            if (selected is null || tabIds.Length > 0 && !tabIds.Contains(selected.Id) && pane?.TabIds.Any(tabIds.Contains) != true) continue;
            if (pane is not null && TryRefreshPane(body, Session.Group(site.Group), selected)) continue;
            body.Mount(() => pane is null ? renderContent(selected) : BodyFor(Session.Group(site.Group), selected), keep: true);
        }
    }

    private bool TryRefreshPane(Border body, DockGroup group, DockTab selected)
    {
        if (group.Region != "center" || Session.PaneFor(group.Id, selected.Id) is not { } pane ||
            body.Child is not Grid grid || !Equals(grid.Tag, (pane.Id, pane.Direction))) return false;
        var members = pane.TabIds.Select(id => Session.Tabs(group.Id).FirstOrDefault(tab => tab.Id == id)).OfType<DockTab>().ToArray();
        var hosts = grid.Children.OfType<Border>().Where(host => host.Name == "PaneMember").ToArray();
        if (!hosts.Select(host => host.Tag).SequenceEqual(members.Select(member => (object?)member.Id))) return false;
        for (var index = 0; index < members.Length; index++)
        {
            var content = renderContent(members[index]);
            if (!ReferenceEquals(hosts[index].Child, content))
            {
                hosts[index].Child = null;
                hosts[index].Child = Adopt(content);
            }
            var weight = new Avalonia.Controls.GridLength(pane.Weights.ElementAtOrDefault(index) * 1000, Avalonia.Controls.GridUnitType.Star);
            if (pane.Direction == "horizontal") grid.ColumnDefinitions[index * 2].Width = weight;
            else grid.RowDefinitions[index * 2].Height = weight;
        }
        return true;
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