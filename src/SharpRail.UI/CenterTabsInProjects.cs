using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.UI.Docking;

namespace SharpRail.UI;

/// <summary>
/// Where centre tabs render, from this app's layout preferences, and their home under the workspace rows in Projects:
/// the active workspace shows its live strips there, and every other workspace a read-only list of its retained tabs.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private readonly Dictionary<string, ContentControl> workspaceTabHosts = [];
    private IReadOnlyDictionary<string, Control> centerStripsInProjects = new Dictionary<string, Control>();
    private string tabLayoutSignature = "";

    // Projects is a home only while it is on screen: in a visible region, an unfolded group, and that group's shown tab.
    // Otherwise the strips come back to the centre, so tabs are never somewhere the user cannot see.
    private CenterTabsMode CenterTabsModeNow() => new(Preferences.VerticalCenterTabs, Preferences.VerticalCenterTabsWidth,
        Preferences.VerticalCenterTabs && Preferences.VerticalTabsInProjects && Layout.IsToolShowing("projects") ? "projects" : "column",
        Preferences.DefaultPaneDirection);

    private void WireCenterTabs()
    {
        surface.CenterTabs = CenterTabsModeNow;
        surface.CenterTabsWidthChanged = width => { Preferences.VerticalCenterTabsWidth = VerticalTabs.ClampWidth(width); SaveProfile(); };
        surface.NestedStripsChanged += strips => { centerStripsInProjects = new Dictionary<string, Control>(strips); PlaceWorkspaceTabs(); };
        tabLayoutSignature = TabLayoutSignature();
        if (Preferences.VerticalCenterTabs) surface.Rebuild();
    }

    private string TabLayoutSignature() =>
        $"{Preferences.VerticalCenterTabs}|{Preferences.VerticalTabsInProjects}|{Preferences.DefaultPaneDirection}|{VerticalTabs.ClampWidth(Preferences.VerticalCenterTabsWidth)}";

    /// <summary>Redraws the workbench when this app's tab layout preferences changed, as every window must.</summary>
    internal void ApplyTabLayout()
    {
        var signature = TabLayoutSignature();
        if (signature == tabLayoutSignature) return;
        tabLayoutSignature = signature;
        surface.Rebuild();
    }

    private ContentControl WorkspaceTabsHost(string workspace)
    {
        var host = new ContentControl { Name = "WorkspaceTabs", Tag = workspace, Margin = new Thickness(20, 0, 4, 4) };
        workspaceTabHosts[workspace] = host;
        Avalonia.Threading.Dispatcher.UIThread.Post(PlaceWorkspaceTabs);
        return host;
    }

    private void PlaceWorkspaceTabs()
    {
        var inProjects = CenterTabsModeNow().Home == "projects";
        foreach (var (workspace, host) in workspaceTabHosts)
        {
            host.Content = null;
            if (!inProjects) continue;
            if (!atHome && workspace == workspaceRoot)
            {
                var strips = new StackPanel { Name = "CenterTabsInProjects", Spacing = 8 };
                foreach (var group in Layout.State.Center.Leaves())
                    if (centerStripsInProjects.GetValueOrDefault(group) is { } strip)
                    {
                        (strip.Parent as Panel)?.Children.Remove(strip);
                        strips.Children.Add(strip);
                    }
                host.Content = strips;
            }
            else host.Content = WorkspaceTabsPreview(workspace);
        }
    }

    // Another workspace's tabs, read-only, from its retained documents: choosing one selects it there and switches to it.
    private Control? WorkspaceTabsPreview(string workspace)
    {
        if (Layout.State.Workspaces.GetValueOrDefault(workspace) is not { } view) return null;
        var list = new StackPanel { Name = "WorkspaceTabsPreview", Spacing = 2 };
        foreach (var group in Layout.State.Center.Leaves())
            foreach (var tab in view.Documents.GetValueOrDefault(group) ?? [])
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
                Ui.Place(row, Ui.Icon(tab.Kind == "terminal" ? "terminal" : tab.Kind == "diff" ? "fileDiff" : "fileText", Ui.Hint, 14));
                var title = Ui.Text(tab.Title, Ui.Muted, 13);
                title.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
                Ui.Place(row, title, 0, 2);
                var button = new Button
                {
                    Name = "WorkspaceTabPreview",
                    Tag = tab.Id,
                    Content = row,
                    Padding = new Thickness(8, 4),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = default,
                    Background = Avalonia.Media.Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                var groupId = group;
                button.Click += (_, _) => { Layout.SelectIn(workspace, groupId, tab.Id); _ = OpenWorkspaceAsync(workspace, false); };
                AutomationProperties.SetName(button, tab.Title);
                ToolTip.SetTip(button, tab.Path.Length > 0 ? tab.Path : tab.Title);
                list.Children.Add(button);
            }
        return list.Children.Count == 0 ? null : list;
    }
}