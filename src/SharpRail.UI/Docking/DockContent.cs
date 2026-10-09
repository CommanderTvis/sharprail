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
            if (selected is null || tabIds.Length > 0 && !tabIds.Contains(selected.Id)) continue;
            body.Mount(() => renderContent(selected), keep: true);
        }
    }
}