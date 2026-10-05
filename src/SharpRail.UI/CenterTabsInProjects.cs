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

    // Another workspace's tabs, read-only, from its retained documents, drawn like the live strip: one section per group,
    // a pane's members together, the selected tab boxed, and the icons and badges plugins give tabs (an agent's chat
    // mark among them). Choosing one selects it there and switches to it.
    private Control? WorkspaceTabsPreview(string workspace)
    {
        if (Layout.State.Workspaces.GetValueOrDefault(workspace) is not { } view) return null;
        var groups = new StackPanel { Name = "WorkspaceTabsPreview", Spacing = 8 };
        foreach (var group in Layout.State.Center.Leaves())
        {
            var tabs = view.Documents.GetValueOrDefault(group) ?? [];
            if (tabs.Count == 0) continue;
            var section = new StackPanel { Name = "WorkspaceTabsPreviewGroup", Tag = group, Spacing = 2 };
            var selected = view.Selected.GetValueOrDefault(group) ?? tabs[0].Id;
            var panes = view.Panes.GetValueOrDefault(group) ?? [];
            foreach (var tab in tabs)
            {
                var pane = panes.FirstOrDefault(candidate => candidate.TabIds.Contains(tab.Id));
                if (pane is not null && pane.TabIds[0] != tab.Id) continue;
                if (pane is null) { section.Children.Add(PreviewTab(workspace, group, tab, tab.Id == selected, boxed: true)); continue; }
                // A pane's members sit in one bubble, boxed together when one of them is selected.
                var members = pane.TabIds.Select(id => tabs.FirstOrDefault(item => item.Id == id)).OfType<DockTab>().ToArray();
                var active = members.Any(member => member.Id == selected);
                var bubble = new StackPanel { Spacing = 2 };
                foreach (var member in members) bubble.Children.Add(PreviewTab(workspace, group, member, member.Id == selected, boxed: false));
                section.Children.Add(new Border
                {
                    Name = "WorkspaceTabsPreviewPane",
                    Child = bubble,
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    BorderBrush = active ? Ui.BorderBrush : Avalonia.Media.Brushes.Transparent,
                    Background = active ? Ui.Hover : Avalonia.Media.Brushes.Transparent
                });
            }
            groups.Children.Add(section);
        }
        return groups.Children.Count == 0 ? null : groups;
    }

    private Button PreviewTab(string workspace, string group, DockTab tab, bool selected, bool boxed)
    {
        var foreground = selected ? Ui.TextBrush : Ui.Muted;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*,Auto") };
        Ui.Place(row, TabIcon(tab, foreground, workspace) ?? Ui.Icon(tab.Kind == "terminal" ? "terminal" : tab.Kind == "diff" ? "fileDiff" : "fileText", foreground, 14));
        var title = Ui.Text(tab.Title, foreground, 13);
        title.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        Ui.Place(row, title, 0, 2);
        if (TabAdornment(tab, workspace) is { } adornment)
        {
            adornment.Margin = new Thickness(4, 0, 0, 0);
            adornment.VerticalAlignment = VerticalAlignment.Center;
            Ui.Place(row, adornment, 0, 3);
        }
        var button = new Button
        {
            Name = "WorkspaceTabPreview",
            Tag = tab.Id,
            Content = row,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(boxed ? 1 : 0),
            BorderBrush = selected && boxed ? Ui.BorderBrush : Avalonia.Media.Brushes.Transparent,
            Background = selected && boxed ? Ui.Hover : Avalonia.Media.Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        button.Click += (_, _) => { Layout.SelectIn(workspace, group, tab.Id); _ = OpenWorkspaceAsync(workspace, false); };
        AutomationProperties.SetName(button, tab.Title);
        ToolTip.SetTip(button, tab.Path.Length > 0 ? tab.Path : tab.Title);
        return button;
    }
}