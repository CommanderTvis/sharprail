using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Client;
using Tereditor.Plugins.Api;

namespace Tereditor.UI;

public sealed partial class WorkbenchWindow
{
    private sealed record ProjectRailRow(Border Header, StackPanel Workspaces)
    {
        public Dictionary<string, (Control? Row, ContentControl Tabs)> Rows { get; } = [];
    }

    private readonly Dictionary<string, Action> railSelection = [];
    private readonly Dictionary<string, ProjectRailRow> projectRailRows = [];
    private string railSignature = "";
    private StackPanel? projectsTree;
    private Grid? projectsPanel;
    private StackPanel? pendingWorkspaceTabs;
    private bool ProjectsHoverEnabled => projectsPanel?.Classes.Contains("touch-projects") != true;

    internal static void ConfigureProjectsTouch(Grid panel, bool touch)
    {
        panel.Classes.Set("touch-projects", touch);
        ToolTip.SetServiceEnabled(panel, !touch);
    }
    private Action? placeProjectTrailing;

    /// <summary>The rail's project order: pinned projects first in the order they were pinned, then the rest in the host's order.</summary>
    internal List<string> RailProjectOrder()
    {
        var projects = state.Current.Projects;
        var pinned = profile.Data.PinnedProjects.Distinct().Where(projects.Contains).ToList();
        pinned.AddRange(projects.Where(project => !pinned.Contains(project)));
        return pinned;
    }

    private bool IsPinned(string project) => profile.Data.PinnedProjects.Contains(project);

    private void SetPinned(string project, bool pin)
    {
        profile.Data.PinnedProjects.RemoveAll(item => item == project);
        if (pin) profile.Data.PinnedProjects.Add(project);
        SaveProfile(); UpdateRailSelection();
    }

    internal void UpdateRailSelection()
    {
        if (projectsTree is null) return;
        var projects = state.Current.Projects.ToHashSet();
        foreach (var project in projectRailRows.Keys.Where(project => !projects.Contains(project)).ToArray())
        {
            var removed = projectRailRows[project];
            foreach (var path in removed.Rows.Keys) ForgetWorkspaceRow(path);
            railSelection.Remove("project:" + project);
            projectRailRows.Remove(project);
        }
        var children = new List<Control>();
        railTabTails.Clear();
        railPreviewTails.Clear();
        foreach (var project in RailProjectOrder())
        {
            if (!projectRailRows.TryGetValue(project, out var entry))
            {
                var workspaces = new StackPanel { Name = "ProjectWorkspaces", Tag = project, Spacing = RailRowSpacing };
                entry = new(ProjectHeader(project, workspaces), workspaces);
                projectRailRows[project] = entry;
            }
            children.Add(entry.Header); children.Add(entry.Workspaces);
            var trees = RailWorkspaces(project);
            var paths = trees.Select(tree => tree.Path).ToHashSet();
            foreach (var path in entry.Rows.Keys.Where(path => !paths.Contains(path)).ToArray())
            { entry.Rows.Remove(path); ForgetWorkspaceRow(path); }
            var rows = new List<Control>();
            var showWorkspaces = HasWorkspaceRows(project, trees);
            var ordered = RailOrder(project);
            var nesting = RailNesting(ordered);
            // target is the rail's rows, or the rows of a tab's slot inside an open workspace's strip, where indents are relative to the strip.
            void Add(WorkspaceRecord worktree, int depth, List<Control> target, int relative)
            {
                if (!entry.Rows.TryGetValue(worktree.Path, out var pair))
                    pair = (null, WorkspaceTabsHost(worktree.Path));
                if (showWorkspaces) pair.Row ??= WorkspaceItem(worktree);
                else if (pair.Row is not null) { pair.Row = null; railSelection.Remove("workspace:" + worktree.Path); railStats.Remove(worktree.Path); }
                var indent = showWorkspaces ? (relative < 0 ? 32 * depth : 16 * relative) : 0;
                var itemLeft = relative < 0 ? 24 : 16;
                // A row's own tabs sit one level (16) right of the row, in a tab's slot as in the rail list.
                var tabsLeft = relative < 0 ? (showWorkspaces ? 40 : 20) : 32;
                if (pair.Row is Grid item)
                {
                    item.Margin = new Thickness(itemLeft + indent, 0, 0, 0);
                    if (item.Children.OfType<Border>().FirstOrDefault(child => child.Name == "WorkspaceNestGuide") is { } guide) guide.IsVisible = depth > 0;
                }
                pair.Tabs.Margin = new Thickness(tabsLeft + indent, 0, 4, 4);
                entry.Rows[worktree.Path] = pair;
                if (pair.Row is not null) target.Add(pair.Row);
                target.Add(pair.Tabs);
                // A created workspace sits right below the creator's tab row, in the slot that row keeps below it: in the live
                // strip of the open workspace or in a closed workspace's preview, inside the bubble for a pane member. Both
                // slots sit at the same place, so focusing the tab or a nested workspace moves no row.
                var kids = nesting.GetValueOrDefault(worktree.Path) ?? [];
                var open = worktree.Path == Layout.State.ActiveWorkspace;
                var units = kids.Select(child => (Child: child, Tab: open ? OpenCreatorTab(child.Path) : null,
                    Member: open ? null : PreviewCreatorTab(worktree.Path, child.Path))).ToArray();
                var next = relative < 0 ? 1 : relative + 1;
                foreach (var kid in units.Where(kid => kid.Tab is not null))
                {
                    if (!railTabTails.TryGetValue(kid.Tab!, out var tail)) railTabTails[kid.Tab!] = tail = [];
                    Add(kid.Child, depth + 1, tail, 0);
                }
                // In a closed workspace the tab's slot in the preview holds what that tab created.
                foreach (var kid in units.Where(kid => kid.Member is not null))
                {
                    if (!railPreviewTails.TryGetValue(worktree.Path, out var members)) railPreviewTails[worktree.Path] = members = [];
                    if (!members.TryGetValue(kid.Member!, out var tail)) members[kid.Member!] = tail = [];
                    Add(kid.Child, depth + 1, tail, 0);
                }
                foreach (var kid in units.Where(kid => kid.Tab is null && kid.Member is null)) Add(kid.Child, depth + 1, target, relative < 0 ? -1 : next);
            }
            var nested = nesting.Values.SelectMany(children => children).Select(child => child.Path).ToHashSet();
            foreach (var worktree in ordered.Where(worktree => !nested.Contains(worktree.Path))) Add(worktree, 0, rows, -1);
            ReconcileRailChildren(entry.Workspaces, rows);
            // One budget for the whole list, nested rows included, so every title in it wraps to the same limit.
            var lines = showWorkspaces ? RailTitleBudget.Lines(trees.Count) : RailTitleBudget.MostLines;
            foreach (var pair in entry.Rows)
            {
                if (pair.Value.Row is not Grid row || row.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "WorkspaceName") is not { } title) continue;
                title.MaxLines = lines;
                if (row.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "WorkspaceSelect") is { } select)
                    ToolTip.SetTip(select, RailTitleBudget.Tip(title.Text ?? "", pair.Key, lines));
            }
        }
        var active = Layout.State.ActiveWorkspace;
        if (active.Length > 0 && !active.StartsWith("home:", StringComparison.Ordinal) &&
            Layout.View.Documents.Values.Any(tabs => tabs.Count > 0) &&
            !projectRailRows.Values.Any(entry => entry.Rows.ContainsKey(active)))
        {
            if (!Equals(pendingWorkspaceTabs?.Tag, active))
            {
                var tabs = WorkspaceTabsHost(active);
                tabs.Margin = new Thickness(24, 0, 4, 4);
                pendingWorkspaceTabs = new StackPanel
                {
                    Name = "PendingWorkspaceTabs",
                    Tag = active,
                    Spacing = 4,
                    Children = { Ui.Text(Path.GetFileName(active), Ui.Muted, 12), tabs }
                };
            }
            children.Add(pendingWorkspaceTabs!);
        }
        else pendingWorkspaceTabs = null;
        var anchor = RailAnchor();
        ReconcileRailChildren(projectsTree, children);
        KeepRailAnchor(anchor);
        FillTabTails();
        foreach (var update in railSelection.Values) update();
        placeProjectTrailing?.Invoke();
        ShowOccupiedWorkspaces();
    }

    // Rows listed under a tab of the open workspace, by tab id, and filled into that tab's slot in the live strip.
    private readonly Dictionary<string, List<Control>> railTabTails = [];
    // Rows listed under a centre tab of a closed workspace's preview, by workspace and tab id.
    private readonly Dictionary<string, Dictionary<string, List<Control>>> railPreviewTails = [];
    // The space between a project's workspace rows, also kept between rows nested in a tab's slot, so focus moves none of them.
    private const double RailRowSpacing = 4;

    internal void FillTabTails()
    {
        foreach (var (tab, slot) in surface.TabTails)
        {
            slot.Spacing = RailRowSpacing;
            ReconcileRailChildren(slot, railTabTails.GetValueOrDefault(tab) ?? []);
            slot.IsVisible = slot.Children.Count > 0;
        }
        foreach (var (workspace, slots) in previewTabTails)
            foreach (var (tab, slot) in slots)
            {
                slot.Spacing = RailRowSpacing;
                ReconcileRailChildren(slot, railPreviewTails.GetValueOrDefault(workspace)?.GetValueOrDefault(tab) ?? []);
                slot.IsVisible = slot.Children.Count > 0;
            }
        QueueRailEdgePill();
    }

    /// <summary>The centre tab of a closed parent's preview that created a nested workspace; null when the parent is open or the creator is not a centre tab there.</summary>
    private string? PreviewCreatorTab(string parent, string child)
    {
        if (parent == Layout.State.ActiveWorkspace || Layout.State.Workspaces.GetValueOrDefault(parent) is not { } view ||
            state.Current.WorkspaceCreators.FirstOrDefault(entry => entry.Workspace == child)?.Creator is not { } creator) return null;
        foreach (var group in Layout.State.Center.Leaves())
            foreach (var (members, _) in GroupUnits(view, group))
                if (members.FirstOrDefault(tab => IsCreatorTab(tab, creator.TabKey)) is { } member) return member.Id;
        return null;
    }

    /// <summary>The open workspace's tab that created a workspace, if its row is in the live strip.</summary>
    private string? OpenCreatorTab(string child)
    {
        if (state.Current.WorkspaceCreators.FirstOrDefault(entry => entry.Workspace == child)?.Creator is not { } creator) return null;
        return Layout.View.Documents.Values.SelectMany(tabs => tabs).FirstOrDefault(tab => IsCreatorTab(tab, creator.TabKey) && surface.TabTails.ContainsKey(tab.Id))?.Id;
    }

    /// <summary>The visible tab row, in the rail, of the tab that created a workspace: a closed workspace's preview row or the open one's live row.</summary>
    private Control? FindCreatorRow(string child)
    {
        if (projectsTree is null || state.Current.WorkspaceCreators.FirstOrDefault(entry => entry.Workspace == child)?.Creator is not { } creator) return null;
        var ids = (Layout.State.Workspaces.GetValueOrDefault(creator.WorkspaceId)?.Documents.Values.SelectMany(tabs => tabs) ?? [])
            .Where(tab => IsCreatorTab(tab, creator.TabKey)).Select(tab => tab.Id).Append(creator.TabKey).ToHashSet();
        var live = ids.Select(id => "DockTab_" + id.Replace(':', '_').Replace('/', '_')).ToHashSet();
        return projectsTree.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.IsEffectivelyVisible &&
            (control.Name == "WorkspaceTabPreview" && control.Tag is string id && ids.Contains(id) || control.Name is not null && live.Contains(control.Name)));
    }

    private ScrollViewer? railScroller;
    private string railNestingSignature = "";

    /// <summary>
    /// With "Group workspaces under the terminal or chat that created them" on, the live workspaces of a project whose creator tab
    /// is still listed in the rail, by the creator's workspace. A workspace whose creator is gone, unknown or outside the project's
    /// live rows stays out and keeps its flat place.
    /// </summary>
    private Dictionary<string, List<WorkspaceRecord>> RailNesting(IReadOnlyList<WorkspaceRecord> live)
    {
        var nesting = new Dictionary<string, List<WorkspaceRecord>>();
        if (!Preferences.GroupWorkspacesUnderCreator) return nesting;
        var creators = state.Current.WorkspaceCreators;
        foreach (var worktree in live)
        {
            if (creators.FirstOrDefault(entry => entry.Workspace == worktree.Path)?.Creator is not { } creator || creator.WorkspaceId == worktree.Path ||
                live.All(parent => parent.Path != creator.WorkspaceId) || !CreatorListed(creator)) continue;
            // A creation loop would leave both ends out of every root; the second link stays flat.
            if (Ancestors(creator.WorkspaceId).Contains(worktree.Path)) continue;
            if (!nesting.TryGetValue(creator.WorkspaceId, out var children)) nesting[creator.WorkspaceId] = children = [];
            children.Add(worktree);
        }
        return nesting;

        HashSet<string> Ancestors(string path)
        {
            var seen = new HashSet<string>();
            while (seen.Add(path) && nesting.Any(pair => pair.Value.Any(child => child.Path == path)))
                path = nesting.First(pair => pair.Value.Any(child => child.Path == path)).Key;
            return seen;
        }
    }

    /// <summary>Whether the terminal or chat that created a workspace still has a tab the rail lists.</summary>
    private bool CreatorListed(TerminalRef creator)
    {
        var tabs = Layout.State.Workspaces.GetValueOrDefault(creator.WorkspaceId)?.Documents.Values.SelectMany(items => items) ?? [];
        return tabs.Any(tab => IsCreatorTab(tab, creator.TabKey)) ||
            workbench.TerminalCatalog?.Workspaces.GetValueOrDefault(creator.WorkspaceId)?.Any(tab => tab.Key == creator.TabKey) == true;
    }

    /// <summary>Whether a tab is the one a creator key names: a chat's key is its chat id and its plugin tab path is "scheme:id"; a terminal's key is its tab id.</summary>
    internal static bool IsCreatorTab(Docking.DockTab tab, string key) =>
        tab.Kind == "plugin" && tab.Path.IndexOf(':') is var split and >= 0 && tab.Path[(split + 1)..] == key || tab.Kind == "terminal" && tab.Id == key;

    /// <summary>Redraws the rail when the grouping under creators changed: the preference, a creator recorded, or a creator tab opened or closed.</summary>
    private void RefreshRailNesting()
    {
        if (projectsTree is null) return;
        var signature = Preferences.GroupWorkspacesUnderCreator
            ? string.Join("\n", state.Current.Projects.SelectMany(project => RailNesting(RailOrder(project)).SelectMany(pair => pair.Value.Select(child => pair.Key + "\t" + child.Path + "\t" + PreviewCreatorTab(pair.Key, child.Path) + "\t" + (pair.Key == Layout.State.ActiveWorkspace ? OpenCreatorTab(child.Path) : null)))))
            : "";
        if (signature == railNestingSignature) return;
        railNestingSignature = signature;
        UpdateRailSelection();
    }

    /// <summary>The topmost rail row reaching into the viewport and where it sits, so a reconcile can keep it still.</summary>
    private (Control Row, double Top)? RailAnchor()
    {
        if (railScroller is not { Offset.Y: > 0 } scroller || projectsTree is null) return null;
        foreach (var row in projectsTree.GetVisualDescendants().OfType<Control>().Where(control => control.Name is "ProjectRow" or "WorkspaceItem" or "WorkspaceTabs"))
            if (row.TranslatePoint(new Point(0, 0), scroller) is { } at && at.Y + row.Bounds.Height > 0) return (row, at.Y);
        return null;
    }

    private void KeepRailAnchor((Control Row, double Top)? anchor)
    {
        if (anchor is not var (row, top) || railScroller is not { } scroller) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(TopLevel.GetTopLevel(row), TopLevel.GetTopLevel(scroller)) || row.TranslatePoint(new Point(0, 0), scroller) is not { } at) return;
            var delta = at.Y - top;
            if (Math.Abs(delta) > .01)
                scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(scroller.Offset.Y + delta, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
        }, DispatcherPriority.Loaded);
    }

    private void RefreshRailWorkspace(string path)
    {
        foreach (var entry in projectRailRows.Values)
            if (entry.Rows.TryGetValue(path, out var pair)) entry.Rows[path] = (null, pair.Tabs);
        railSelection.Remove("workspace:" + path);
        railStats.Remove(path);
        UpdateRailSelection();
    }

    private void ForgetWorkspaceRow(string path)
    {
        railSelection.Remove("workspace:" + path);
        railStats.Remove(path);
        workspaceTabHosts.Remove(path);
        workspaceTabPreviewSignatures.Remove(path);
        previewTabTails.Remove(path);
    }

    private static void ReconcileRailChildren(Panel panel, IReadOnlyList<Control> desired)
    {
        var keep = desired.ToHashSet();
        // Rows move between the rail and a tab's slot in the open strip, so a row leaves its old parent first.
        foreach (var child in desired.Where(child => child.Parent is Panel parent && !ReferenceEquals(parent, panel))) ((Panel)child.Parent!).Children.Remove(child);
        foreach (var child in panel.Children.Where(child => !keep.Contains(child)).ToArray()) panel.Children.Remove(child);
        for (var index = 0; index < desired.Count; index++)
        {
            var child = desired[index];
            var current = panel.Children.IndexOf(child);
            if (current == index) continue;
            if (current < 0) panel.Children.Insert(index, child);
            else panel.Children.Move(current, index);
        }
    }

    /// <summary>The button that adds a project or starts work from the project menu.</summary>
    private Button NewAddProjectButton()
    {
        var open = Ui.IconButton("add", "Add project", () => { });
        open.Name = "AddProjectMenu";
        var addMenu = ProjectMenu();
        // Directly under the button, left edges aligned, so the menu never covers the button it came from.
        addMenu.Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft;
        addMenu.PlacementTarget = open;
        open.ContextMenu = addMenu;
        open.Click += (_, _) => { FillProjectMenu(addMenu); addMenu.Open(open); };
        open.Width = open.Height = 28;
        return open;
    }

    private Control ProjectsPanel()
    {
        railSignature = RailSignature();
        railStats.Clear();
        railSelection.Clear(); projectRailRows.Clear(); workspaceTabHosts.Clear(); workspaceTabPreviewSignatures.Clear();
        pendingWorkspaceTabs = null;
        var panel = new Grid { Name = "ProjectsPanel", Margin = new Thickness(12, unified ? 0 : 12, 0, 12), RowDefinitions = new RowDefinitions(unified ? "*" : "28,8,*") };
        projectsPanel = panel;
        ConfigureProjectsTouch(panel, OperatingSystem.IsAndroid());
        // The unified rail has no caption row: its header carries the add button.
        if (!unified)
        {
            var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 0, 16, 0) };
            var title = Ui.Text("PROJECTS", size: 12); title.FontWeight = Avalonia.Media.FontWeight.Medium;
            Ui.Place(toolbar, title);
            Ui.Place(toolbar, NewAddProjectButton(), 0, 1);
            Ui.Place(panel, toolbar);
        }
        // The gutter belongs inside the scroller, beside the rows' trailing buttons.
        projectsTree = new StackPanel { Name = "ProjectsTree", Spacing = 4, Margin = new Thickness(0, 0, 12, 0) };
        // A tab strip in a rail row reveals its active tab inside the strip; the request must not also scroll the rail,
        // whose offset belongs to KeepRailAnchor alone.
        projectsTree.AddHandler(Control.RequestBringIntoViewEvent, (_, e) =>
        {
            if (e.Source is Visual source && source.GetVisualAncestors().Any(ancestor => ancestor is Control { Name: "WorkspaceTabs" })) e.Handled = true;
        });
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var add = Ui.IconButton("add", $"Start work ({Shortcut(Tereditor.Host.Abstractions.KeymapActions.NewWorkspace)})", () => _ = CreateWorkspaceDialogAsync());
        add.Name = "AddWorkspace";
        add.Width = add.Height = 28; add.Padding = new(7);
        trailing.Children.Add(add);
        placeProjectTrailing = () =>
        {
            var row = projectRailRows.GetValueOrDefault(projectRoot)?.Header.Child as Grid;
            if (!ReferenceEquals(trailing.Parent, row))
            {
                (trailing.Parent as Panel)?.Children.Remove(trailing);
                if (row is not null) Ui.Place(row, trailing, 0, 4);
            }
            add.IsEnabled = WorkspaceMounted;
        };
        UpdateRailSelection();
        Ui.Place(panel, railScroller = new ScrollViewer { Content = projectsTree, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, unified ? 0 : 2);
        // The pill shares the scroller's cell, so it floats over the rows and moves none of them.
        Ui.Place(panel, NewRailEdgePill(railScroller), unified ? 0 : 2);
        return panel;
    }

    private Border ProjectHeader(string project, StackPanel workspaces)
    {
        var row = new Grid { Name = "ProjectRow", ColumnDefinitions = new ColumnDefinitions("16,4,*,Auto,Auto"), Height = 28, Margin = new Thickness(4, 0), Background = Avalonia.Media.Brushes.Transparent, Tag = project };
        var highlight = new Border { Name = "ProjectHighlight", Tag = project, Child = row };
        highlight.Classes.Add("project-row");
        var collapsed = Collapsed(project);
        var toggle = Ui.IconButton(collapsed ? "arrowRight" : "arrowDown", collapsed ? "Expand project" : "Collapse project", () =>
        {
            // A revealed project shows expanded, so its toggle collapses it and ends the reveal.
            if (revealedProject == project) { revealedProject = null; profile.Data.CollapsedProjects.Add(project); }
            else if (!profile.Data.CollapsedProjects.Add(project))
            {
                profile.Data.CollapsedProjects.Remove(project); _ = ExpandProjectAsync(project);
            }
            SaveProfile(); UpdateRailSelection();
        });
        toggle.Name = "ProjectExpand"; toggle.Tag = project;
        toggle.Width = toggle.Height = 16; toggle.Padding = new(0);
        Ui.Place(row, toggle);
        var select = new Button { Name = "ProjectName", Tag = project };
        select.Classes.Add("project-name");
        select.Click += (_, _) => _ = RailWorkspaces(project) is { } trees && !HasWorkspaceRows(project, trees)
            ? OpenProjectAsync(project) : OpenProjectHomeAsync(project);
        AutomationProperties.SetName(select, state.Current.ProjectName(project));
        var projectLabel = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*,Auto") };
        var folder = Ui.Icon("folderFill", Ui.Muted, 14);
        var name = Ui.Text(state.Current.ProjectName(project), Ui.Muted);
        var countText = Ui.Text("", Ui.Hint, 12);
        countText.Name = "ProjectWorkspaceCount";
        countText.VerticalAlignment = VerticalAlignment.Center;
        Ui.Place(row, countText, 0, 3);
        Ui.Place(projectLabel, folder); Ui.Place(projectLabel, name, 0, 2);
        var pinMark = ActionIcons.Create(ActionIcon.Pin, Ui.Hint, 12);
        pinMark.Name = "ProjectPinMark"; pinMark.Margin = new Thickness(4, 0, 0, 0); pinMark.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(pinMark, "Pinned"); AutomationProperties.SetName(pinMark, "Pinned");
        Ui.Place(projectLabel, pinMark, 0, 3);
        railSelection["project:" + project] = () =>
        {
            var plain = RailWorkspaces(project) is { } trees && !HasWorkspaceRows(project, trees);
            highlight.Classes.Set("active", project == projectRoot && (atHome || plain));
            ((Border)folder).Background = project == projectRoot ? Ui.Accent : Ui.Muted;
            name.Foreground = project == projectRoot ? Ui.TextBrush : Ui.Muted;
            // A rename arrives as a host broadcast; the row keeps its controls and only its text follows.
            if (name.Text != state.Current.ProjectName(project)) { name.Text = state.Current.ProjectName(project); AutomationProperties.SetName(select, name.Text); }
            var folded = Collapsed(project);
            if (folded != collapsed) toggle.Content = Ui.Icon(folded ? "arrowRight" : "arrowDown");
            collapsed = folded;
            var count = RailWorkspaces(project).Count(tree => tree.Kind != WorkspaceKinds.Default);
            countText.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            countText.IsVisible = folded && count > 0;
            ToolTip.SetTip(countText, count == 1 ? "1 workspace" : $"{count} workspaces");
            pinMark.IsVisible = IsPinned(project);
            ToolTip.SetTip(toggle, folded ? "Expand project" : "Collapse project");
            workspaces.IsVisible = !folded && workspaces.Children.Any(child => child.IsVisible);
        };
        select.Content = projectLabel;
        select.Background = Avalonia.Media.Brushes.Transparent;
        select.BorderThickness = new(0); select.Padding = new Thickness(0);
        select.HorizontalAlignment = HorizontalAlignment.Stretch; select.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ToolTip.SetTip(select, project); Ui.Place(row, select, 0, 2);
        row.ContextMenu = ProjectActions(project);
        select.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) row.ContextMenu.Open(row);
        };
        return highlight;
    }

    private bool HasWorkspaceRows(string project, IReadOnlyList<WorkspaceRecord> trees) =>
        trees is not [{ Kind: WorkspaceKinds.Default, Branch: "" }] || project == projectRoot && (git.IsRepository || gitError is not null);

    /// <summary>
    /// Lists an expanded project's workspaces; one without workspace rows has only its folder to reveal, so expanding it opens it
    /// as its name does. Rows are judged on the listing itself: the rail's copy of another project's workspaces may still be empty.
    /// </summary>
    private async Task ExpandProjectAsync(string project)
    {
        WorkspaceCatalog catalog;
        var request = projectRequest;
        try { catalog = await Task.Run(async () => await host.ListWorkspacesAsync(project, lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { Console.Error.WriteLine("Workspaces could not be listed: " + error.Message); return; }
        // A switch made while the listing ran wins: the expand must not pull the window back to this project.
        if (request != projectRequest) return;
        if (!Collapsed(project) && !HasWorkspaceRows(project, project == projectRoot ? RailWorkspaces(project) : catalog.Workspaces)) await OpenProjectAsync(project);
    }

    private void RefreshProjectWorkspaces()
    {
        foreach (var project in state.Current.Projects.Where(project => !Collapsed(project)))
            _ = SyncWorkspacesAsync(project);
    }

    // Last known change totals per workspace path, kept across switches so a rebuilt row shows them at once; a refresh
    // redraws only the rows whose totals changed, as each workspace's totals arrive.
    private readonly Dictionary<string, DiffStats> workspaceStats = [];
    private readonly Dictionary<string, Action> railStats = [];
    internal int RailStatsCount => railStats.Count;
    private CancellationTokenSource? statsRefresh;

    private async Task RefreshWorkspaceStatsAsync(long request)
    {
        statsRefresh?.Cancel();
        statsRefresh?.Dispose();
        statsRefresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = statsRefresh.Token;
        // The open workspace first: its row is the one the user just switched to.
        foreach (var worktree in git.Worktrees.OrderBy(tree => tree.Path != workspaceRoot).ToArray())
        {
            DiffStats? stats;
            try { stats = await Task.Run(async () => await host.GetDiffStatsAsync(worktree.Path, token), token); }
            // A superseded refresh cancels the call in flight, which a remote host reports as an RPC status, not as a cancellation.
            catch (Exception) when (token.IsCancellationRequested) { return; }
            // A badge is a convenience: a workspace whose totals cannot be read simply shows none.
            catch (Exception error) { Console.Error.WriteLine($"Change totals of {worktree.Path} are unavailable: {error.Message}"); stats = null; }
            if (token.IsCancellationRequested || request != projectRequest) return;
            var known = workspaceStats.GetValueOrDefault(worktree.Path);
            if (known == stats) continue;
            if (stats is null) workspaceStats.Remove(worktree.Path); else workspaceStats[worktree.Path] = stats;
            railStats.GetValueOrDefault(worktree.Path)?.Invoke();
        }
    }

    /// <summary>
    /// A project's rows come from the host's registry, so every project lists its own without being shown. Until the
    /// host has listed the shown project, its folder alone stands in as the Default workspace.
    /// </summary>
    private IReadOnlyList<WorkspaceRecord> RailWorkspaces(string project)
    {
        var known = state.Current.WorkspacesOf(project).ToArray();
        return project != projectRoot || known.Any(workspace => workspace.Kind == WorkspaceKinds.Default) ? known
            : [new("", project, WorkspaceKinds.Default, project, git.Worktrees.FirstOrDefault(tree => tree.IsMain)?.Branch ?? "", ""), .. known];
    }

    /// <summary>Space a rail row keeps free at its right edge; indicators follow the title inside the rest.</summary>
    internal const double RailDotInset = 36;

    private Control WorkspaceItem(WorkspaceRecord worktree)
    {
        // Another project's workspace opens that project; renaming and removal stay with the project that is open, while
        // opening it elsewhere and copying its path or name work from any row.
        var name = WorkspaceName(worktree.Path);
        var active = !atHome && worktree.Path == workspaceRoot;
        var color = active ? Ui.Accent : Ui.Muted;
        var contents = new MarkedRow();
        var icon = Ui.Icon(worktree.Kind switch { WorkspaceKinds.Default => "homeFill", WorkspaceKinds.External => "folderOpen", _ => "gitBranch" }, color, 14);
        contents.Add(icon, RowRole.Leading);
        var label = Ui.Text(name, color); label.LineHeight = 17.5; label.Name = "WorkspaceName";
        label.Classes.Add("workspace-description");
        var unreadDot = new Border { Name = "WorkspaceUnread", Width = 8, Height = 8, CornerRadius = new(4), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        contents.Add(label, RowRole.Title);
        void ShowUnread()
        {
            var reason = state.Current.UnreadWorkspaces.GetValueOrDefault(worktree.Path);
            unreadDot.IsVisible = reason is not null;
            if (reason is null) return;
            var (brush, _) = UnreadLook(reason);
            var tip = UnreadTip(worktree.Path, reason);
            unreadDot.Background = brush; ToolTip.SetTip(unreadDot, tip); AutomationProperties.SetName(unreadDot, tip);
        }
        ShowUnread();
        // A further agent failing in this workspace changes the tooltip without changing the host mark.
        void FailedChanged(string session) { if (workbench.Signals.FailedTabs(worktree.Path).Count > 0) ShowUnread(); }
        unreadDot.AttachedToVisualTree += (_, _) => workbench.Signals.Changed += FailedChanged;
        unreadDot.DetachedFromVisualTree += (_, _) => workbench.Signals.Changed -= FailedChanged;
        var added = Ui.Text("", Ui.Success, RailTitleBudget.SecondFontSize); added.LineHeight = RailTitleBudget.SecondLineHeight; added.VerticalAlignment = VerticalAlignment.Center;
        var removed = Ui.Text("", Ui.Danger, RailTitleBudget.SecondFontSize); removed.LineHeight = RailTitleBudget.SecondLineHeight; removed.VerticalAlignment = VerticalAlignment.Center;
        var totals = new StackPanel { Name = "WorkspaceDiffStats", Orientation = Orientation.Horizontal, Spacing = 4, Children = { added, removed } };
        void ShowTotals()
        {
            var stats = workspaceStats.GetValueOrDefault(worktree.Path);
            totals.IsVisible = stats is { Added: > 0 } or { Removed: > 0 };
            added.Text = "+" + (stats?.Added ?? 0); removed.Text = "−" + (stats?.Removed ?? 0);
        }
        ShowTotals();
        railStats[worktree.Path] = ShowTotals;
        var branch = Ui.Text("", Ui.Hint, RailTitleBudget.SecondFontSize); branch.LineHeight = RailTitleBudget.SecondLineHeight; branch.VerticalAlignment = VerticalAlignment.Center; branch.Name = "WorkspaceBranch";
        branch.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        // The totals sit right of the branch, or alone under the name when the row has none.
        var second = new DockPanel { Children = { totals } };
        contents.Add(second, RowRole.Below);
        // A branch that moves under its checkout is redrawn in place: the row keeps its focus and pointer target.
        void ShowBranch()
        {
            var text = removingWorkspaces.Contains(worktree.Path) ? "Removing…"
                : RailWorkspaces(worktree.ProjectRoot).FirstOrDefault(workspace => workspace.Path == worktree.Path)?.Branch ?? worktree.Branch;
            branch.Text = text;
            var twoLines = text.Length > 0;
            if (!twoLines) second.Children.Remove(branch);
            else if (branch.Parent is null) second.Children.Add(branch);
            DockPanel.SetDock(totals, twoLines ? Dock.Right : Dock.Left);
            totals.Margin = twoLines ? new(8, 0, 0, 0) : default;
            icon.VerticalAlignment = twoLines ? VerticalAlignment.Top : VerticalAlignment.Center;
            icon.Margin = twoLines ? new(0, 2, 0, 0) : default;
        }
        ShowBranch();
        // The dot follows the name, centred on the whole row; the button's padding keeps it clear of the settle button.
        contents.Add(unreadDot, RowRole.Mark);
        var button = new Button
        {
            Name = "WorkspaceSelect",
            Tag = worktree.Path,
            Content = contents,
            Background = active ? Ui.Selected : Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(4, 4, RailDotInset, 4),
            MinHeight = 28,
            CornerRadius = new(4),
            BorderThickness = new(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        // The title takes the row's whole width; the dot's column is reserved only while the dot shows.
        void PadForDot() => button.Padding = new Thickness(4, 4, unreadDot.IsVisible ? RailDotInset : 4, 4);
        unreadDot.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) PadForDot(); };
        PadForDot();
        railSelection["workspace:" + worktree.Path] = () =>
        {
            var selected = !atHome && worktree.Path == workspaceRoot;
            label.Text = WorkspaceName(worktree.Path);
            AutomationProperties.SetName(button, label.Text);
            ToolTip.SetTip(button, RailTitleBudget.Tip(label.Text, worktree.Path, label.MaxLines));
            button.Background = selected ? Ui.Selected : Avalonia.Media.Brushes.Transparent;
            ((Border)icon).Background = label.Foreground = selected ? Ui.Accent : Ui.Muted;
            // A workspace being removed neither opens nor offers its actions; its second line says why.
            button.IsEnabled = !removingWorkspaces.Contains(worktree.Path);
            contents.Opacity = button.IsEnabled ? 1 : 0.5;
            ShowBranch();
            ShowUnread();
        };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, worktree.Path);
        button.Click += (_, _) => _ = OpenWorkspaceAsync(worktree.Path, worktree.ProjectRoot != projectRoot);
        var item = new Grid { Name = "WorkspaceItem", Tag = worktree.Path, Margin = new Thickness(24, 0, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Avalonia.Media.Brushes.Transparent };
        // The line tying a grouped workspace to the creator above it; shown only while it is grouped.
        var nestGuide = new Border { Name = "WorkspaceNestGuide", Width = 1, Margin = new Thickness(-8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.BorderBrush, IsHitTestVisible = false, IsVisible = false };
        item.Children.Add(nestGuide);
        // The line runs under the middle of the creator tab's icon and is one line with its siblings' lines: it starts at the icon
        // for the first child and reaches the next child of the same tab across the space between rows.
        Control? creatorRow = null;
        item.LayoutUpdated += (_, _) =>
        {
            if (!nestGuide.IsVisible) return;
            if (creatorRow?.IsEffectivelyVisible != true || TopLevel.GetTopLevel(creatorRow) is null) creatorRow = FindCreatorRow(worktree.Path);
            if (creatorRow?.GetVisualDescendants().OfType<MarkedRow>().FirstOrDefault() is not { } marked || marked.TranslatePoint(default, item) is not { } at) return;
            var left = at.X + MarkedRow.LeadingWidth / 2 - nestGuide.Width / 2;
            var creator = state.Current.WorkspaceCreators.FirstOrDefault(entry => entry.Workspace == worktree.Path)?.Creator;
            Grid? Sibling(IEnumerable<Control> others) => others.OfType<Grid>().FirstOrDefault(other => other.Name == "WorkspaceItem" && other.Tag is string path && path != worktree.Path &&
                state.Current.WorkspaceCreators.FirstOrDefault(entry => entry.Workspace == path)?.Creator == creator &&
                other.Children.OfType<Border>().Any(guide => guide.Name == "WorkspaceNestGuide" && guide.IsVisible));
            var top = 0.0; var bottom = 0.0;
            if (item.Parent is Panel panel)
            {
                var index = panel.Children.IndexOf(item);
                if (Sibling(panel.Children.Take(index)) is null)
                {
                    if (marked.Children.FirstOrDefault(child => child.GetValue(MarkedRow.RoleProperty) == RowRole.Leading) is { } icon && icon.TranslatePoint(new Point(0, icon.Bounds.Height), item) is { } iconBottom) top = Math.Min(0, iconBottom.Y);
                }
                if (Sibling(panel.Children.Skip(index + 1)) is { } next && next.TranslatePoint(default, item) is { } nextAt) bottom = -Math.Max(0, nextAt.Y - item.Bounds.Height);
            }
            var margin = new Thickness(left, top, 0, bottom);
            if (Math.Abs(margin.Left - nestGuide.Margin.Left) > 0.01 || Math.Abs(margin.Top - nestGuide.Margin.Top) > 0.01 || Math.Abs(margin.Bottom - nestGuide.Margin.Bottom) > 0.01) nestGuide.Margin = margin;
        };
        var menu = WorkspaceActions(worktree, button);
        button.ContextMenu = menu;
        // The menu key and Shift+F10 open the actions from the focused row, since the row has no actions button.
        button.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Apps && !(e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift) || !button.IsEnabled) return;
            MenuFill.Open(menu, button);
            e.Handled = true;
        };
        item.PointerEntered += (_, _) => { if (ProjectsHoverEnabled) Prewarm(worktree.Path); };
        item.GotFocus += (_, _) => { if (ProjectsHoverEnabled) Prewarm(worktree.Path); };
        var main = renaming == worktree.Path ? RenameBox(new Thickness(4, 0, 32, 0), 28) : button;
        Ui.Place(item, main);
        Grid.SetColumnSpan(main, 2);
        return item;
    }

    /// <summary>Pointing at or focusing another workspace asks the host to start watching it before it is opened.</summary>
    private async void Prewarm(string path)
    {
        if (path == workspaceRoot || !prewarmed.Add(path)) return;
        try { await Task.Run(async () => await host.PrewarmWorkspaceAsync(path, lifetime.Token), lifetime.Token); }
        // Only a hint: the subscription made on opening starts the watcher itself.
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException or ArgumentException || RemoteErrors.IsTransport(error))
        { prewarmed.Remove(path); }
    }

    private readonly HashSet<string> prewarmed = [];


}