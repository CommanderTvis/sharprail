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
    // Preview units after which a workspace's tab list is cut so a workspace made by that tab nests right below it, and the
    // hosts holding the list's later segments.
    private readonly Dictionary<string, int[]> workspaceTabBreaks = [];
    private readonly Dictionary<string, List<ContentControl>> workspaceTabSegments = [];
    private IReadOnlyDictionary<string, Control> centerStripsInProjects = new Dictionary<string, Control>();
    private string tabLayoutSignature = "";

    private CenterTabsMode CenterTabsModeNow() =>
        new(Preferences.DefaultPaneDirection);

    private void WireCenterTabs()
    {
        surface.CenterTabs = CenterTabsModeNow;
        surface.WorkspaceRoot = () => workspaceRoot;
        surface.ShowInDirectory = path => _ = RevealInFilesAsync(workspaceRoot, path);
        // Terminal tabs stay in their workspace, so the rail's other workspaces and projects are no drop destination for them.
        surface.ForeignWorkspace = control => control.Name switch
        {
            "WorkspaceTabs" or "WorkspaceItem" => !Equals(control.Tag, Layout.State.ActiveWorkspace),
            "ProjectRow" => true,
            _ => false
        };
        surface.NestedStripsChanged += strips => { centerStripsInProjects = new Dictionary<string, Control>(strips); PlaceWorkspaceTabs(); RefreshRailNesting(); FillTabTails(); };
        tabLayoutSignature = TabLayoutSignature();
        Layout.Changed += RefreshRailNesting;
        surface.Rebuild();
    }

    private string TabLayoutSignature() =>
        Preferences.DefaultPaneDirection;

    /// <summary>Redraws the workbench when this app's tab layout preferences changed, as every window must.</summary>
    internal void ApplyTabLayout()
    {
        RefreshRailNesting();
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

    private void SetTabBreaks(string workspace, int[] breaks)
    {
        if (breaks.Length == 0) workspaceTabBreaks.Remove(workspace);
        else workspaceTabBreaks[workspace] = breaks;
        if (workspaceTabSegments.GetValueOrDefault(workspace) is { } hosts && hosts.Count > breaks.Length)
        {
            foreach (var host in hosts.Skip(breaks.Length)) host.Content = null;
            hosts.RemoveRange(breaks.Length, hosts.Count - breaks.Length);
            workspaceTabPreviewSignatures.Remove(workspace);
        }
    }

    /// <summary>The host of a tab list's segment after its first, which sits below the nested workspaces of the tab the previous segment ends with.</summary>
    private ContentControl WorkspaceTabSegment(string workspace, int segment)
    {
        if (!workspaceTabSegments.TryGetValue(workspace, out var hosts)) workspaceTabSegments[workspace] = hosts = [];
        while (hosts.Count < segment)
        {
            hosts.Add(new ContentControl { Name = "WorkspaceTabs", Tag = workspace, IsVisible = false });
            workspaceTabPreviewSignatures.Remove(workspace);
            Avalonia.Threading.Dispatcher.UIThread.Post(PlaceWorkspaceTabs);
        }
        var host = hosts[segment - 1];
        host.Margin = workspaceTabHosts[workspace].Margin;
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
                host.IsVisible = children.Count > 0;
                foreach (var extra in workspaceTabSegments.GetValueOrDefault(workspace) ?? []) { extra.Content = null; extra.IsVisible = false; }
            }
            else
            {
                var view = Layout.State.Workspaces.GetValueOrDefault(workspace);
                var breaks = workspaceTabBreaks.GetValueOrDefault(workspace) ?? [];
                var signature = string.Join(",", breaks) + System.Text.Json.JsonSerializer.Serialize(Layout.State.Center.Leaves().Select(group => new PreviewGroupSignature(
                    group,
                    view?.Documents.GetValueOrDefault(group),
                    view?.Documents.GetValueOrDefault(group)?.Select(tab => TabDecorationSignature(tab, workspace)).ToArray(),
                    view?.Selected.GetValueOrDefault(group),
                    view?.Panes.GetValueOrDefault(group))).ToArray(), AppJson.Default.PreviewGroupSignatureArray) +
                    System.Text.Json.JsonSerializer.Serialize(TerminalsOutsidePreview(workspace).Select(tab => new PreviewTerminalSignature(tab, TabDecorationSignature(tab, workspace))).ToArray(), AppJson.Default.PreviewTerminalSignatureArray);
                if (workspaceTabPreviewSignatures.GetValueOrDefault(workspace) == signature) continue;
                workspaceTabPreviewSignatures[workspace] = signature;
                var segments = WorkspaceTabsPreview(workspace, breaks);
                host.Content = segments[0];
                // An empty list takes no room, its bottom margin and the rail's spacing included.
                host.IsVisible = host.Content is not null;
                var hosts = workspaceTabSegments.GetValueOrDefault(workspace) ?? [];
                for (var index = 0; index < hosts.Count; index++)
                {
                    hosts[index].Content = segments.ElementAtOrDefault(index + 1);
                    hosts[index].IsVisible = hosts[index].Content is not null;
                }
            }
        }
        ShowOccupiedWorkspaces();
    }

    /// <summary>An expanded project whose workspace panel holds nothing visible takes the room of a closed one: none beyond its own row.</summary>
    private void ShowOccupiedWorkspaces()
    {
        foreach (var (project, entry) in projectRailRows)
            entry.Workspaces.IsVisible = !Collapsed(project) && entry.Workspaces.Children.Any(child => child.IsVisible);
    }

    // Another workspace's tabs, read-only, from its retained documents, drawn like the live strip: one section per group,
    // a pane's members together, the selected tab boxed, and the icons and badges plugins give tabs (an agent's chat
    // mark among them). Choosing one selects it there and switches to it.
    // The preview's units in order, each a lone tab or a pane's members drawn in one bubble; a unit is the finest place to cut the list.
    private List<DockTab[]> PreviewUnits(string workspace)
    {
        var view = Layout.State.Workspaces.GetValueOrDefault(workspace);
        var units = new List<DockTab[]>();
        foreach (var group in view is null ? [] : Layout.State.Center.Leaves())
            units.AddRange(GroupUnits(view!, group).Select(unit => unit.Members));
        units.AddRange(TerminalsOutsidePreview(workspace).Select(tab => new[] { tab }));
        return units;
    }

    private static IEnumerable<(DockTab[] Members, DockPane? Pane)> GroupUnits(WorkspaceView view, string group)
    {
        var tabs = view.Documents.GetValueOrDefault(group) ?? [];
        var panes = view.Panes.GetValueOrDefault(group) ?? [];
        foreach (var tab in tabs)
        {
            var pane = panes.FirstOrDefault(candidate => candidate.TabIds.Contains(tab.Id));
            if (pane is null) yield return ([tab], null);
            else if (pane.TabIds[0] == tab.Id)
                yield return ([.. pane.TabIds.Select(id => tabs.FirstOrDefault(item => item.Id == id)).OfType<DockTab>()], pane);
        }
    }

    // Another workspace's tabs, read-only, from its retained documents, drawn like the live strip: one section per group,
    // a pane's members together, the selected tab boxed, and the icons and badges plugins give tabs (an agent's chat
    // mark among them). Choosing one selects it there and switches to it. Cut after each unit in breaks, the list comes back
    // as that many plus one segments (a segment is null when nothing follows the last cut).
    private List<Control?> WorkspaceTabsPreview(string workspace, int[] breaks)
    {
        var view = Layout.State.Workspaces.GetValueOrDefault(workspace);
        var segments = new List<Control?>();
        StackPanel? terminals = null;
        var groups = new StackPanel { Name = "WorkspaceTabsPreview", Spacing = 8 };
        var unit = 0;
        void Cut()
        {
            segments.Add(groups.Children.Count == 0 ? null : groups);
            groups = new StackPanel { Name = "WorkspaceTabsPreview", Spacing = 8 };
        }
        StackPanel Section(StackPanel? section, string name, string? tag)
        {
            if (section is not null) return section;
            section = new StackPanel { Name = name, Tag = tag, Spacing = 2 };
            groups.Children.Add(section);
            return section;
        }
        void EndUnit() { if (breaks.Contains(unit)) Cut(); unit++; }
        foreach (var group in view is null ? [] : Layout.State.Center.Leaves())
        {
            var tabs = view!.Documents.GetValueOrDefault(group) ?? [];
            if (tabs.Count == 0) continue;
            StackPanel? section = null;
            var selected = view.Selected.GetValueOrDefault(group) ?? tabs[0].Id;
            var subtitles = DockSurface.AmbiguousSubtitles(tabs);
            foreach (var (members, pane) in GroupUnits(view, group))
            {
                section = Section(section, "WorkspaceTabsPreviewGroup", group);
                if (pane is null) section.Children.Add(PreviewTab(workspace, group, members[0], members[0].Id == selected, boxed: true, subtitles.GetValueOrDefault(members[0].Id)));
                else
                {
                    // A pane's members sit in one bubble, boxed together when one of them is selected.
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
                if (breaks.Contains(unit)) section = null;
                EndUnit();
            }
        }
        foreach (var tab in TerminalsOutsidePreview(workspace))
        {
            terminals = Section(terminals, "WorkspaceTabsPreviewTerminals", null);
            terminals.Children.Add(PreviewTab(workspace, null, tab, selected: false, boxed: true));
            if (breaks.Contains(unit)) terminals = null;
            EndUnit();
        }
        segments.Add(groups.Children.Count == 0 ? null : groups);
        return segments;
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

    private static bool ExternalSource(string path, string workspace)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) return true;
            path = uri.LocalPath;
        }
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("../", StringComparison.Ordinal)) return true;
        var windowsPath = normalized.Length > 2 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/';
        if (!Path.IsPathRooted(path) && !windowsPath) return false;
        var root = workspace.Replace('\\', '/').TrimEnd('/');
        return !normalized.StartsWith(root + "/", OperatingSystem.IsWindows() || windowsPath ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && normalized != root;
    }

    private Control PreviewTab(string workspace, string? group, DockTab tab, bool selected, bool boxed, string? subtitle = null)
    {
        var foreground = selected ? Ui.TextBrush : Ui.Muted;
        var marked = new MarkedRow();
        var row = new Grid { Margin = new Thickness(0, 0, 4, 0), Children = { marked } };
        marked.Add(TabIcon(tab, foreground, workspace) ?? Ui.Icon(tab.Kind == "terminal" ? "terminal" : tab.Kind == "diff" ? "fileDiff" : "fileText", foreground, 14), RowRole.Leading);
        var external = tab.Kind == "file" && ExternalSource(tab.Path, workspace);
        var title = Ui.Text(tab.Title, foreground, 14);
        title.LineHeight = 20;
        title.FontStyle = tab.Preview ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal;
        title.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        marked.Add(title, RowRole.Title);
        if (subtitle is not null)
        {
            var folder = Ui.Text(subtitle, Ui.Hint, 12);
            folder.Name = "TabSubtitle";
            folder.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            marked.Add(folder, RowRole.Below);
        }
        if (TabAdornment(tab, workspace) is { } adornment)
        {
            adornment.VerticalAlignment = VerticalAlignment.Center;
            marked.Add(adornment, RowRole.Mark);
        }
        if (workbench.Signals.Strip(workspace, tab) is { } strip)
        {
            strip.Margin = new Thickness(0, 0, 0, -4);
            row.Children.Add(strip);
        }
        var button = new Button
        {
            Name = "WorkspaceTabPreview",
            Tag = tab.Id,
            Content = row,
            Height = (subtitle is null ? 36 : 44) + (boxed ? 2 : 0),
            // The tab list is inset 4 from the rail edge, its row 4 more, and a boxed tab's border 1, so this puts the dot on the workspace dot's column.
            Padding = new Thickness(8, 0, RailDotInset - 8 - (boxed ? 1 : 0), 0),
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
            var menu = new ContextMenu { Items = { rename } };
            DockSurface.AddLazyItems(menu, () => TabMenuItems(tab, workspace));
            button.ContextMenu = menu;
        }
        void OpenTab()
        {
            if (group is null) { _ = RevealTerminalAsync(workspace, tab.Id); return; }
            Layout.SelectIn(workspace, group, tab.Id); _ = OpenWorkspaceAsync(workspace, false);
        }
        button.Click += (_, _) => OpenTab();
        AutomationProperties.SetName(button, tab.Title);
        ToolTip.SetTip(button, (external ? "Outside workspace\n" : "") + (tab.Path.Length > 0 ? tab.Path : tab.Title));
        if (workbench.Touch)
        {
            button.IsHitTestVisible = false;
            button.Height = 48;
            var open = Ui.IconButton("arrowRight", "Open " + TabNames.Quote(tab.Title), OpenTab);
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