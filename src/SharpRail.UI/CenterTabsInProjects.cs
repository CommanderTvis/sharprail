using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.UI.Docking;

namespace SharpRail.UI;

/// <summary>
/// Centre tabs live under directories or workspace rows in Projects:
/// the active workspace shows its live strips there, and every other workspace a read-only list of its retained tabs.
/// Projects also lists shared terminals this window has not placed or whose strips live elsewhere.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private readonly Dictionary<string, ContentControl> workspaceTabHosts = [];
    private readonly Dictionary<string, string> workspaceTabPreviewSignatures = [];
    private IReadOnlyDictionary<string, Control> centerStripsInProjects = new Dictionary<string, Control>();
    private string tabLayoutSignature = "";

    private CenterTabsMode CenterTabsModeNow() =>
        new(Preferences.DefaultPaneDirection);

    private void WireCenterTabs()
    {
        surface.CenterTabs = CenterTabsModeNow;
        surface.NestedStripsChanged += strips => { centerStripsInProjects = new Dictionary<string, Control>(strips); PlaceWorkspaceTabs(); };
        tabLayoutSignature = TabLayoutSignature();
        surface.Rebuild();
    }

    private string TabLayoutSignature() =>
        Preferences.DefaultPaneDirection;

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
        // One level deeper than its workspace's row, which sits one level under the project.
        var host = new ContentControl { Name = "WorkspaceTabs", Tag = workspace, Margin = new Thickness(40, 0, 4, 4) };
        workspaceTabHosts[workspace] = host;
        Avalonia.Threading.Dispatcher.UIThread.Post(PlaceWorkspaceTabs);
        return host;
    }

    private void PlaceWorkspaceTabs()
    {
        // Keep the outgoing rail intact while another workspace mounts. At startup there is no
        // outgoing workspace, so plugin refreshes must mount their current strips immediately.
        if (switchingWorkspace && workspaceRoot.Length > 0) return;
        foreach (var (workspace, host) in workspaceTabHosts)
        {
            if (workspace == Layout.State.ActiveWorkspace && !workspace.StartsWith("home:", StringComparison.Ordinal))
            {
                workspaceTabPreviewSignatures.Remove(workspace);
                var strips = host.Content as StackPanel;
                if (strips?.Name != "CenterTabsInProjects") strips = new StackPanel { Name = "CenterTabsInProjects", Spacing = 8 };
                var children = new List<Control>();
                foreach (var group in Layout.State.Center.Leaves())
                    if (centerStripsInProjects.GetValueOrDefault(group) is { } strip)
                    {
                        if (!ReferenceEquals(strip.Parent, strips)) (strip.Parent as Panel)?.Children.Remove(strip);
                        children.Add(strip);
                    }
                ReconcileRailChildren(strips, children);
                if (!ReferenceEquals(host.Content, strips)) host.Content = strips;
            }
            else
            {
                var view = Layout.State.Workspaces.GetValueOrDefault(workspace);
                var signature = System.Text.Json.JsonSerializer.Serialize(Layout.State.Center.Leaves().Select(group => new
                {
                    Group = group,
                    Tabs = view?.Documents.GetValueOrDefault(group),
                    Decorations = view?.Documents.GetValueOrDefault(group)?.Select(tab => TabDecorationSignature(tab, workspace)),
                    Selected = view?.Selected.GetValueOrDefault(group),
                    Panes = view?.Panes.GetValueOrDefault(group)
                })) + System.Text.Json.JsonSerializer.Serialize(TerminalsOutsidePreview(workspace).Select(tab => new { Tab = tab, Decoration = TabDecorationSignature(tab, workspace) }));
                if (workspaceTabPreviewSignatures.GetValueOrDefault(workspace) == signature) continue;
                workspaceTabPreviewSignatures[workspace] = signature;
                host.Content = WorkspaceTabsPreview(workspace);
            }
        }
    }

    // Another workspace's tabs, read-only, from its retained documents, drawn like the live strip: one section per group,
    // a pane's members together, the selected tab boxed, and the icons and badges plugins give tabs (an agent's chat
    // mark among them). Choosing one selects it there and switches to it.
    private Control? WorkspaceTabsPreview(string workspace)
    {
        var view = Layout.State.Workspaces.GetValueOrDefault(workspace);
        var groups = new StackPanel { Name = "WorkspaceTabsPreview", Spacing = 8 };
        foreach (var group in view is null ? [] : Layout.State.Center.Leaves())
        {
            var tabs = view!.Documents.GetValueOrDefault(group) ?? [];
            if (tabs.Count == 0) continue;
            var section = new StackPanel { Name = "WorkspaceTabsPreviewGroup", Tag = group, Spacing = 2 };
            var selected = view.Selected.GetValueOrDefault(group) ?? tabs[0].Id;
            var subtitles = DockSurface.AmbiguousSubtitles(tabs);
            var panes = view.Panes.GetValueOrDefault(group) ?? [];
            foreach (var tab in tabs)
            {
                var pane = panes.FirstOrDefault(candidate => candidate.TabIds.Contains(tab.Id));
                if (pane is not null && pane.TabIds[0] != tab.Id) continue;
                if (pane is null) { section.Children.Add(PreviewTab(workspace, group, tab, tab.Id == selected, boxed: true, subtitles.GetValueOrDefault(tab.Id))); continue; }
                // A pane's members sit in one bubble, boxed together when one of them is selected.
                var members = pane.TabIds.Select(id => tabs.FirstOrDefault(item => item.Id == id)).OfType<DockTab>().ToArray();
                var active = members.Any(member => member.Id == selected);
                var bubble = new StackPanel { Spacing = 2 };
                foreach (var member in members) bubble.Children.Add(PreviewTab(workspace, group, member, member.Id == selected, boxed: false, subtitles.GetValueOrDefault(member.Id)));
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
        if (TerminalsOutsidePreview(workspace) is { Length: > 0 } elsewhere)
        {
            var section = new StackPanel { Name = "WorkspaceTabsPreviewTerminals", Spacing = 2 };
            foreach (var tab in elsewhere) section.Children.Add(PreviewTab(workspace, null, tab, selected: false, boxed: true));
            groups.Children.Add(section);
        }
        return groups.Children.Count == 0 ? null : groups;
    }

    // The terminals of a workspace that its centre tabs do not show: those its view keeps in another region, and
    // those in the host's catalog this window has not placed, as for a workspace it never opened.
    private DockTab[] TerminalsOutsidePreview(string workspace)
    {
        var view = Layout.State.Workspaces.GetValueOrDefault(workspace);
        var center = Layout.State.Center.Leaves().ToHashSet();
        var placed = view?.Documents.Values.SelectMany(tabs => tabs).Select(tab => tab.Id).ToHashSet() ?? [];
        return
        [
            .. view?.Documents.Where(entry => !center.Contains(entry.Key)).SelectMany(entry => entry.Value).Where(tab => tab.Kind == "terminal") ?? [],
            .. (workbench.TerminalCatalog?.Workspaces.GetValueOrDefault(workspace) ?? []).Where(tab => !placed.Contains(tab.Key)).Select(tab => new DockTab(tab.Key, state.Current.TerminalTitles.FirstOrDefault(title => title.Terminal.WorkspaceId == workspace && title.Terminal.TabKey == tab.Key)?.Title ?? tab.Title, "terminal"))
        ];
    }

    private Control PreviewTab(string workspace, string? group, DockTab tab, bool selected, bool boxed, string? subtitle = null)
    {
        var foreground = selected ? Ui.TextBrush : Ui.Muted;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*,Auto"), Margin = new Thickness(0, 0, 4, 0) };
        Ui.Place(row, TabIcon(tab, foreground, workspace) ?? Ui.Icon(tab.Kind == "terminal" ? "terminal" : tab.Kind == "diff" ? "fileDiff" : "fileText", foreground, 14));
        var title = Ui.Text(tab.Title, foreground, 14);
        title.LineHeight = 20;
        title.FontStyle = tab.Preview ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal;
        title.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        if (subtitle is null) Ui.Place(row, title, 0, 2);
        else
        {
            var folder = Ui.Text(subtitle, Ui.Hint, 12);
            folder.Name = "TabSubtitle";
            folder.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            Ui.Place(row, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, folder } }, 0, 2);
        }
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
            Height = (subtitle is null ? 36 : 44) + (boxed ? 2 : 0),
            Padding = new Thickness(8, 0, 24, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(boxed ? 1 : 0),
            BorderBrush = selected && boxed ? Ui.BorderBrush : Avalonia.Media.Brushes.Transparent,
            Background = selected && boxed ? Ui.Hover : Avalonia.Media.Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        if (tab.Kind == "terminal")
        {
            var rename = Ui.Menu("Rename", () => RenameTerminal(workspace, tab));
            rename.Name = "TerminalRename";
            button.ContextMenu = new ContextMenu { Items = { rename } };
        }
        void OpenTab()
        {
            if (group is null) { _ = RevealTerminalAsync(workspace, tab.Id); return; }
            Layout.SelectIn(workspace, group, tab.Id); _ = OpenWorkspaceAsync(workspace, false);
        }
        button.Click += (_, _) => OpenTab();
        AutomationProperties.SetName(button, tab.Title);
        ToolTip.SetTip(button, tab.Path.Length > 0 ? tab.Path : tab.Title);
        if (workbench.Touch)
        {
            button.IsHitTestVisible = false;
            button.Height = 48;
            var open = Ui.IconButton("arrowRight", "Open " + tab.Title, OpenTab);
            open.Name = "OpenProjectTab";
            open.Tag = tab.Id;
            open.Classes.Add("project-tab-open");
            var frame = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ContextMenu = button.ContextMenu };
            Ui.Place(frame, button);
            Ui.Place(frame, open, 0, 1);
            return frame;
        }
        return button;
    }
}