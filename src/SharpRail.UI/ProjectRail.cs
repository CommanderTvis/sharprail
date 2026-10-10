using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI;

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
    private Action? placeProjectTrailing;

    private void UpdateRailSelection()
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
        foreach (var project in state.Current.Projects)
        {
            if (!projectRailRows.TryGetValue(project, out var entry))
            {
                var workspaces = new StackPanel { Name = "ProjectWorkspaces", Tag = project, Spacing = 4 };
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
            foreach (var worktree in trees)
            {
                if (!entry.Rows.TryGetValue(worktree.Path, out var pair))
                    pair = (null, WorkspaceTabsHost(worktree.Path));
                if (showWorkspaces) pair.Row ??= WorkspaceItem(worktree);
                else if (pair.Row is not null) { pair.Row = null; railSelection.Remove("workspace:" + worktree.Path); }
                pair.Tabs.Margin = new Thickness(showWorkspaces ? 40 : 20, 0, 4, 4);
                entry.Rows[worktree.Path] = pair;
                if (pair.Row is not null) rows.Add(pair.Row);
                rows.Add(pair.Tabs);
            }
            ReconcileRailChildren(entry.Workspaces, rows);
        }
        ReconcileRailChildren(projectsTree, children);
        foreach (var update in railSelection.Values) update();
        placeProjectTrailing?.Invoke();
    }

    private void RefreshRailWorkspace(string path)
    {
        foreach (var entry in projectRailRows.Values)
            if (entry.Rows.TryGetValue(path, out var pair)) entry.Rows[path] = (null, pair.Tabs);
        railSelection.Remove("workspace:" + path);
        UpdateRailSelection();
    }

    private void ForgetWorkspaceRow(string path)
    {
        railSelection.Remove("workspace:" + path);
        workspaceTabHosts.Remove(path);
        workspaceTabPreviewSignatures.Remove(path);
    }

    private static void ReconcileRailChildren(Panel panel, IReadOnlyList<Control> desired)
    {
        var keep = desired.ToHashSet();
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

    private Control ProjectsPanel()
    {
        railSignature = RailSignature();
        railStats.Clear();
        railSelection.Clear(); projectRailRows.Clear(); workspaceTabHosts.Clear(); workspaceTabPreviewSignatures.Clear();
        var panel = new Grid { Name = "ProjectsPanel", Margin = new Thickness(12, 12, 0, 12), RowDefinitions = new RowDefinitions("28,8,*") };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 0, 16, 0) };
        var title = Ui.Text("PROJECTS", size: 12); title.FontWeight = Avalonia.Media.FontWeight.Medium;
        Ui.Place(toolbar, title);
        var open = Ui.IconButton("add", "Add project", () => { });
        open.Name = "AddProjectMenu";
        var addMenu = ProjectMenu();
        open.ContextMenu = addMenu;
        open.Click += (_, _) => { FillProjectMenu(addMenu); addMenu.Open(open); };
        open.Width = open.Height = 28;
        Ui.Place(toolbar, open, 0, 1);
        Ui.Place(panel, toolbar);
        // The gutter belongs inside the scroller, beside the rows' trailing buttons.
        projectsTree = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 12, 0) };
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var add = Ui.IconButton("add", $"Start work ({Shortcut("N")})", () => _ = CreateWorkspaceDialogAsync());
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
        Ui.Place(panel, new ScrollViewer { Content = projectsTree, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 2);
        return panel;
    }

    private Border ProjectHeader(string project, StackPanel workspaces)
    {
        var row = new Grid { Name = "ProjectRow", ColumnDefinitions = new ColumnDefinitions("16,4,*,Auto,Auto"), Height = 28, Margin = new Thickness(4, 0), Background = Avalonia.Media.Brushes.Transparent, Tag = project };
        var highlight = new Border { Name = "ProjectHighlight", Tag = project, Child = row };
        highlight.Classes.Add("project-row");
        var collapsed = profile.Data.CollapsedProjects.Contains(project);
        var toggle = Ui.IconButton(collapsed ? "arrowRight" : "arrowDown", collapsed ? "Expand project" : "Collapse project", () =>
        {
            if (!profile.Data.CollapsedProjects.Add(project)) { profile.Data.CollapsedProjects.Remove(project); _ = SyncWorkspacesAsync(project); }
            SaveProfile(); UpdateRailSelection();
        });
        toggle.Name = "ProjectExpand"; toggle.Tag = project;
        toggle.Width = toggle.Height = 16; toggle.Padding = new(0);
        Ui.Place(row, toggle);
        var select = new Button { Name = "ProjectName", Tag = project };
        select.Classes.Add("project-name");
        select.Click += (_, _) => _ = RailWorkspaces(project) is { } trees && !HasWorkspaceRows(project, trees)
            ? OpenProjectAsync(project) : OpenProjectHomeAsync(project);
        AutomationProperties.SetName(select, new DirectoryInfo(project).Name);
        var projectLabel = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
        var folder = Ui.Icon("folderFill", Ui.Muted, 14);
        var name = Ui.Text(new DirectoryInfo(project).Name, Ui.Muted);
        var countText = Ui.Text("", Ui.Hint, 12);
        countText.Name = "ProjectWorkspaceCount";
        countText.VerticalAlignment = VerticalAlignment.Center;
        Ui.Place(row, countText, 0, 3);
        Ui.Place(projectLabel, folder); Ui.Place(projectLabel, name, 0, 2);
        railSelection["project:" + project] = () =>
        {
            var plain = RailWorkspaces(project) is { } trees && !HasWorkspaceRows(project, trees);
            highlight.Classes.Set("active", project == projectRoot && (atHome || plain));
            ((Border)folder).Background = project == projectRoot ? Ui.Accent : Ui.Muted;
            name.Foreground = project == projectRoot ? Ui.TextBrush : Ui.Muted;
            var folded = profile.Data.CollapsedProjects.Contains(project);
            if (folded != collapsed) toggle.Content = Ui.Icon(folded ? "arrowRight" : "arrowDown");
            collapsed = folded;
            var count = RailWorkspaces(project).Count(tree => tree.Kind != WorkspaceKinds.Default);
            countText.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            countText.IsVisible = folded && count > 0;
            ToolTip.SetTip(toggle, folded ? "Expand project" : "Collapse project");
            workspaces.IsVisible = !folded;
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

    private void RefreshProjectWorkspaces()
    {
        foreach (var project in state.Current.Projects.Where(project => !profile.Data.CollapsedProjects.Contains(project)))
            _ = SyncWorkspacesAsync(project);
    }

    // Change totals per workspace path, read after the rail is up and applied to the rows in place.
    private readonly Dictionary<string, DiffStats> workspaceStats = [];
    private readonly List<Action> railStats = [];
    private CancellationTokenSource? statsRefresh;

    private async Task RefreshWorkspaceStatsAsync(long request)
    {
        statsRefresh?.Cancel();
        statsRefresh?.Dispose();
        statsRefresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = statsRefresh.Token;
        var read = new Dictionary<string, DiffStats>();
        foreach (var worktree in git.Worktrees)
        {
            try
            {
                if (await Task.Run(async () => await host.GetDiffStatsAsync(worktree.Path, token), token) is { } stats) read[worktree.Path] = stats;
            }
            catch (OperationCanceledException) { return; }
            // A badge is a convenience: a workspace whose totals cannot be read simply shows none.
            catch (Exception error) { Console.Error.WriteLine($"Change totals of {worktree.Path} are unavailable: {error.Message}"); }
            if (token.IsCancellationRequested || request != projectRequest) return;
        }
        workspaceStats.Clear();
        foreach (var (path, stats) in read) workspaceStats[path] = stats;
        foreach (var update in railStats) update();
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

    private Control WorkspaceItem(WorkspaceRecord worktree)
    {
        // Another project's workspace opens that project; renaming and removal stay with the project that is open, while
        // opening it elsewhere and copying its path or name work from any row.
        var name = WorkspaceName(worktree.Path);
        var active = !atHome && worktree.Path == workspaceRoot;
        var color = active ? Ui.Accent : Ui.Muted;
        var contents = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
        var icon = Ui.Icon(worktree.Kind switch { WorkspaceKinds.Default => "homeFill", WorkspaceKinds.External => "folderOpen", _ => "gitBranch" }, color, 14);
        Ui.Place(contents, icon);
        var labels = new StackPanel();
        var label = Ui.Text(name, color); label.LineHeight = 17.5; label.Name = "WorkspaceName";
        labels.Children.Add(label);
        var added = Ui.Text("", Ui.Success, 12);
        var removed = Ui.Text("", Ui.Danger, 12);
        var totals = new StackPanel { Name = "WorkspaceDiffStats", Orientation = Orientation.Horizontal, Spacing = 4, Children = { added, removed } };
        void ShowTotals()
        {
            var stats = workspaceStats.GetValueOrDefault(worktree.Path);
            totals.IsVisible = stats is { Added: > 0 } or { Removed: > 0 };
            added.Text = "+" + (stats?.Added ?? 0); removed.Text = "−" + (stats?.Removed ?? 0);
        }
        ShowTotals();
        railStats.Add(ShowTotals);
        var branch = Ui.Text("", Ui.Hint, 12); branch.LineHeight = 15; branch.Name = "WorkspaceBranch";
        branch.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        // The totals sit right of the branch, or alone under the name when the row has none.
        var second = new DockPanel { Children = { totals } };
        labels.Children.Add(second);
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
        Ui.Place(contents, labels, 0, 2);
        var button = new Button
        {
            Name = "WorkspaceSelect",
            Tag = worktree.Path,
            Content = contents,
            Background = active ? Ui.Selected : Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(4, 4, 32, 4),
            MinHeight = 28,
            CornerRadius = new(4),
            BorderThickness = new(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        railSelection["workspace:" + worktree.Path] = () =>
        {
            var selected = !atHome && worktree.Path == workspaceRoot;
            label.Text = WorkspaceName(worktree.Path);
            AutomationProperties.SetName(button, label.Text);
            button.Background = selected ? Ui.Selected : Avalonia.Media.Brushes.Transparent;
            ((Border)icon).Background = label.Foreground = selected ? Ui.Accent : Ui.Muted;
            // A workspace being removed neither opens nor offers its actions; its second line says why.
            button.IsEnabled = !removingWorkspaces.Contains(worktree.Path);
            contents.Opacity = button.IsEnabled ? 1 : 0.5;
            ShowBranch();
        };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, worktree.Path);
        button.Click += (_, _) => _ = OpenWorkspaceAsync(worktree.Path, worktree.ProjectRoot != projectRoot);
        var kebab = new Button
        {
            Name = "WorkspaceMenu",
            Content = Ui.Icon("moreHorizontal", null, 14),
            Width = 24,
            Height = 24,
            Padding = new(0),
            Margin = new(0, 0, 4, 0),
            Opacity = 0,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(kebab, "Workspace actions");
        ToolTip.SetTip(kebab, "Workspace actions");
        var item = new Grid { Name = "WorkspaceItem", Tag = worktree.Path, Margin = new Thickness(24, 0, 0, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Avalonia.Media.Brushes.Transparent };
        var menu = WorkspaceActions(worktree, kebab);
        button.ContextMenu = menu;
        kebab.Click += (_, _) => { if (button.IsEnabled) menu.Open(button); };
        void Reveal() => kebab.Opacity = button.IsEnabled && (item.IsPointerOver || item.IsKeyboardFocusWithin || menu.IsOpen) ? 1 : 0;
        item.PointerEntered += (_, _) => { Reveal(); Prewarm(worktree.Path); };
        item.PointerExited += (_, _) => Reveal();
        item.GotFocus += (_, _) => { Reveal(); Prewarm(worktree.Path); };
        item.LostFocus += (_, _) => Reveal();
        menu.Opened += (_, _) => Reveal();
        menu.Closed += (_, _) => Reveal();
        var main = renaming == worktree.Path && !renameInHeader ? RenameBox(new Thickness(4, 0, 32, 0), 28) : button;
        Ui.Place(item, main);
        Grid.SetColumnSpan(main, 2);
        Ui.Place(item, kebab, 0, 1);
        return item;
    }

    /// <summary>Pointing at or focusing another workspace asks the host to start watching it before it is opened.</summary>
    private async void Prewarm(string path)
    {
        if (path == workspaceRoot || !prewarmed.Add(path)) return;
        try { await Task.Run(async () => await host.PrewarmWorkspaceAsync(path, lifetime.Token), lifetime.Token); }
        // Only a hint: the subscription made on opening starts the watcher itself.
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException or ArgumentException or Grpc.Core.RpcException)
        { prewarmed.Remove(path); }
    }

    private readonly HashSet<string> prewarmed = [];


}