using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private readonly Dictionary<string, IReadOnlyList<ProjectFile>> folderCache = [];
    private readonly HashSet<string> expandedFolders = [];

    public Func<Task<string?>>? FolderPicker { get; set; }
    private int projectPicker;
    private string? railSignature;

    private async Task PickProjectAsync()
    {
        var picker = ++projectPicker;
        if (remote) { await EnterHostPathAsync(null); return; }
        string? path;
        try
        {
            path = FolderPicker is { } custom ? await custom() : (await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Open project", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (picker == projectPicker) await EnterHostPathAsync(error.Message);
            return;
        }
        if (picker == projectPicker && path is not null) await OpenPickedProjectAsync(path);
    }

    private async Task EnterHostPathAsync(string? pickerError)
    {
        var picker = ++projectPicker;
        var path = await Dialogs.HostPath(this, workspaceRoot, pickerError, remote);
        if (path is null || picker != projectPicker) return;
        projectPicker++;
        await OpenPickedProjectAsync(path);
    }

    private async Task CreateProjectAsync()
    {
        var parent = Path.GetDirectoryName(projectRoot.Length > 0 ? projectRoot : workspaceRoot) ?? "";
        Func<Task<string?>>? pick = remote ? null : async () => FolderPicker is { } custom ? await custom()
            : (await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Create project in", AllowMultiple = false }))
                .FirstOrDefault()?.TryGetLocalPath();
        var path = await Dialogs.CreateProject(this, parent, pick,
            (into, name) => Task.Run(async () => await host.CreateProjectAsync(into, name, lifetime.Token), lifetime.Token));
        if (path is not null) await OpenPickedProjectAsync(path);
    }

    private async Task CloneProjectAsync()
    {
        var parent = Path.GetDirectoryName(projectRoot.Length > 0 ? projectRoot : workspaceRoot) ?? "";
        Func<Task<string?>>? pick = remote ? null : async () => FolderPicker is { } custom ? await custom()
            : (await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Clone into", AllowMultiple = false }))
                .FirstOrDefault()?.TryGetLocalPath();
        // The clone runs git for up to ten minutes; it never runs on the dispatcher.
        var path = await Dialogs.CloneProject(this, parent, pick,
            (url, into, name, depth) => Task.Run(async () => await host.CloneProjectAsync(url, into, name, depth, lifetime.Token), lifetime.Token));
        if (path is not null) await OpenPickedProjectAsync(path);
    }

    private async Task OpenPickedProjectAsync(string path)
    {
        try
        {
            var picker = projectPicker;
            var kind = await host.InspectProjectPathAsync(path, lifetime.Token);
            if (picker != projectPicker) return;
            // An open the user asked for by path has no surface of its own to fail on, so it says so in a notice.
            Exception? failure = kind switch
            {
                ProjectPathKind.Missing => new IOException("No such folder: " + path),
                ProjectPathKind.NotDirectory => new IOException("Not a folder: " + path),
                _ => null
            };
            if (failure is null) await OpenWorkspaceAsync(path, true, home: true, failed: error => failure = error);
            if (failure is not null)
            {
                Console.Error.WriteLine(failure);
                await Dialogs.Notice(this, "Couldn't open project", failure.Message);
                return;
            }
            if (!WorkspaceMounted) return;
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    /// <summary>Restyles the rail's active project and workspace, and what Git allows, without rebuilding it, so switching keeps rows and focus.</summary>
    private readonly List<Action> railSelection = [];

    private void UpdateRailSelection() { foreach (var update in railSelection) update(); }

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

    private Control ProjectsPanel()
    {
        railSignature = RailSignature();
        railSelection.Clear();
        railStats.Clear();
        workspaceTabHosts.Clear();
        var panel = new Grid { Name = "ProjectsPanel", Margin = new Thickness(12, 12, 0, 12), RowDefinitions = new RowDefinitions("28,8,*") };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 0, 16, 0) };
        var title = Ui.Text("PROJECTS", size: 12); title.FontWeight = Avalonia.Media.FontWeight.Medium;
        Ui.Place(toolbar, title);
        var open = Ui.IconButton("add", "Add project", () => { });
        open.Name = "AddProjectMenu";
        var addMenu = ProjectMenu();
        open.ContextMenu = addMenu;
        open.Click += (_, _) => addMenu.Open(open);
        open.Width = open.Height = 28;
        Ui.Place(toolbar, open, 0, 1);
        Ui.Place(panel, toolbar);
        // The right gutter is inside the scroller, so its overlay scrollbar never covers the rows' trailing buttons.
        var tree = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 12, 0) };
        foreach (var project in state.Current.Projects)
        {
            var row = new Grid { Name = "ProjectRow", ColumnDefinitions = new ColumnDefinitions("16,4,*,Auto"), Height = 28, Margin = new Thickness(4, 0), Background = Avalonia.Media.Brushes.Transparent, Tag = project };
            // Like the reference rail, the selected project is one rounded highlight across the whole row.
            var highlight = new Border
            {
                Name = "ProjectHighlight",
                Tag = project,
                Child = row
            };
            highlight.Classes.Add("project-row");
            highlight.Classes.Set("active", atHome && project == projectRoot);
            railSelection.Add(() => highlight.Classes.Set("active", atHome && project == projectRoot));
            var collapsed = profile.Data.CollapsedProjects.Contains(project);
            var workspaces = RailWorkspaces(project);
            var toggle = Ui.IconButton(collapsed ? "arrowRight" : "arrowDown", collapsed ? "Expand project" : "Collapse project", () =>
            {
                // Expanding is the gesture that re-reads a background project's workspaces.
                if (!profile.Data.CollapsedProjects.Add(project)) { profile.Data.CollapsedProjects.Remove(project); _ = SyncWorkspacesAsync(project); }
                SaveProfile();
                toolContent.Remove("projects"); surface.RefreshContents();
                Dispatcher.UIThread.Post(() => surface.GetLogicalDescendants().OfType<Button>()
                    .FirstOrDefault(button => button.Name == "ProjectExpand" && Equals(button.Tag, project))?.Focus());
            });
            toggle.Name = "ProjectExpand"; toggle.Tag = project;
            toggle.Width = toggle.Height = 16; toggle.Padding = new(0);
            Ui.Place(row, toggle);
            var select = new Button { Name = "ProjectName", Tag = project };
            select.Classes.Add("project-name");
            select.Click += (_, _) => _ = OpenProjectHomeAsync(project);
            AutomationProperties.SetName(select, new DirectoryInfo(project).Name);
            var projectLabel = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
            Ui.Place(projectLabel, Ui.Icon("folderFill", project == projectRoot ? Ui.Accent : Ui.Muted, 14));
            Ui.Place(projectLabel, Ui.Text(new DirectoryInfo(project).Name, project == projectRoot ? Ui.TextBrush : Ui.Muted), 0, 2);
            select.Content = projectLabel;
            select.Background = Avalonia.Media.Brushes.Transparent;
            select.BorderThickness = new(0); select.Padding = new Thickness(0);
            select.HorizontalAlignment = HorizontalAlignment.Stretch; select.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ToolTip.SetTip(select, project); Ui.Place(row, select, 0, 2);
            row.ContextMenu = ProjectActions(project);
            select.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                { row.ContextMenu.Open(row); e.Handled = true; }
            };
            var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            Ui.Place(row, trailing, 0, 3);
            var count = workspaces.Count(workspace => workspace.Kind != WorkspaceKinds.Default);
            if (collapsed && count > 0)
            {
                var badge = Ui.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), Ui.Hint, 12);
                badge.Name = "ProjectWorkspaceCount"; badge.VerticalAlignment = VerticalAlignment.Center;
                trailing.Children.Add(badge);
            }
            if (project == projectRoot)
            {
                var tip = $"Create workspace ({Shortcut("N")})";
                var add = Ui.IconButton("add", tip, () => _ = CreateWorkspaceDialogAsync());
                add.Name = "AddWorkspace";
                add.Width = add.Height = 28; add.Padding = new(7);
                add.IsEnabled = WorkspaceMounted;
                trailing.Children.Add(add);
            }
            tree.Children.Add(highlight);
            if (collapsed) continue;
            foreach (var workspace in workspaces)
            {
                tree.Children.Add(WorkspaceItem(workspace));
                tree.Children.Add(WorkspaceTabsHost(workspace.Path));
            }
        }
        Ui.Place(panel, new ScrollViewer { Content = tree, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 2);
        return panel;
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
        var foreign = worktree.ProjectRoot != projectRoot;
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
            var text = RailWorkspaces(worktree.ProjectRoot).FirstOrDefault(workspace => workspace.Path == worktree.Path)?.Branch ?? worktree.Branch;
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
            Padding = new Thickness(24, 4, 32, 4),
            MinHeight = 28,
            CornerRadius = new(4),
            BorderThickness = new(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        railSelection.Add(() =>
        {
            var selected = !atHome && worktree.Path == workspaceRoot;
            button.Background = selected ? Ui.Selected : Avalonia.Media.Brushes.Transparent;
            ((Border)icon).Background = label.Foreground = selected ? Ui.Accent : Ui.Muted;
            ShowBranch();
        });
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
        var item = new Grid { Name = "WorkspaceItem", Tag = worktree.Path, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Avalonia.Media.Brushes.Transparent };
        var menu = WorkspaceActions(worktree, kebab);
        button.ContextMenu = menu;
        kebab.Click += (_, _) => menu.Open(button);
        void Reveal() => kebab.Opacity = item.IsPointerOver || item.IsKeyboardFocusWithin || menu.IsOpen ? 1 : 0;
        item.PointerEntered += (_, _) => { Reveal(); Prewarm(worktree.Path); };
        item.PointerExited += (_, _) => Reveal();
        item.GotFocus += (_, _) => { Reveal(); Prewarm(worktree.Path); };
        item.LostFocus += (_, _) => Reveal();
        menu.Opened += (_, _) => Reveal();
        menu.Closed += (_, _) => Reveal();
        var main = renaming == worktree.Path && !renameInHeader ? RenameBox(new Thickness(20, 0, 32, 0), 28) : button;
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

    private Control FilesPanel()
    {
        var panel = new Grid { Name = "FilesPanel", IsHitTestVisible = WorkspaceMounted && !switchingWorkspace };
        var tree = new TreeView { Name = "FilesTree", Background = Ui.Sidebar, Margin = new Thickness(4, 12, 12, 12) };
        // A rail narrower than its names scrolls sideways rather than cutting them off.
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
        foreach (var file in folderCache.GetValueOrDefault("") ?? []) tree.Items.Add(FileNode(file));
        Ui.Place(panel, tree);
        return panel;
    }

    private TreeViewItem FileNode(ProjectFile file)
    {
        var node = new TreeViewItem
        {
            Header = FileRow(file),
            MinHeight = 24,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = file
        };
        AutomationProperties.SetName(node, file.Name);
        AutomationProperties.SetHelpText(node, file.Path);
        node.ContextMenu = FileActions(file, node);
        if (file.IsDirectory)
        {
            if (folderCache.TryGetValue(file.Path, out var children))
                foreach (var child in children) node.Items.Add(FileNode(child));
            else node.Items.Add(new TreeViewItem { Header = Ui.Text("Loading…", Ui.Hint) });
            var prefix = file.Path + Path.DirectorySeparatorChar;
            node.IsExpanded = expandedFolders.Contains(file.Path) || expandedFolders.Any(path => path.StartsWith(prefix, StringComparison.Ordinal));
            async Task Expand()
            {
                expandedFolders.Add(file.Path);
                if (folderCache.ContainsKey(file.Path)) return;
                var request = projectRequest;
                try
                {
                    if (!WorkspaceMounted) return;
                    var entries = await Task.Run(async () => await host.ListFilesAsync(file.Path, lifetime.Token), lifetime.Token);
                    if (request != projectRequest) return;
                    folderCache[file.Path] = entries;
                    ReconcileFileNodes(node.Items, entries);
                }
                catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
            }
            node.PropertyChanged += (_, e) =>
            {
                if (e.Property != TreeViewItem.IsExpandedProperty) return;
                if (node.IsExpanded) _ = Expand();
                else expandedFolders.Remove(file.Path);
            };
            if (node.IsExpanded) _ = Expand();
            // Like the reference tree, every click on the folder's own row toggles it, so two quick clicks open and
            // close it. The tree's built-in double-tap toggle is suppressed there so it does not toggle a third time.
            node.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.Source is not Control source || !IsOwnHeader(node, source) || !e.GetCurrentPoint(node).Properties.IsLeftButtonPressed) return;
                // The chevron's own toggle already expands and collapses the folder.
                if (source is ToggleButton || source.GetVisualAncestors().OfType<ToggleButton>().Any()) return;
                node.IsExpanded = !node.IsExpanded;
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            // Double taps only bubble; handling them on the row content keeps them from the header's own toggle.
            ((Control)node.Header!).DoubleTapped += (_, e) => e.Handled = true;
        }
        else node.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Control source || !IsOwnHeader(node, source) || !e.GetCurrentPoint(node).Properties.IsLeftButtonPressed) return;
            _ = BrowseDocumentAsync(file.Path, e.ClickCount == 2);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        node.KeyDown += (_, e) =>
        {
            if (!WorkspaceMounted || toolContent.GetValueOrDefault("files")?.IsHitTestVisible != true) return;
            if (e.Key == Key.Enter && !file.IsDirectory) { _ = BrowseDocumentAsync(file.Path, true); e.Handled = true; }
        };
        return node;
    }

    // Each platform's file manager has its own name, and the wrong one reads as a bug.
    private static readonly string RevealLabel = OperatingSystem.IsMacOS() ? "Reveal in Finder"
        : OperatingSystem.IsWindows() ? "Show in Explorer" : "Open containing folder";

    private ContextMenu FileActions(ProjectFile file, TreeViewItem node)
    {
        // New creates inside a folder row and beside a file row; a compact chain's row stands for its deepest folder.
        var parent = Path.GetDirectoryName(file.Path) ?? "";
        var into = file.IsDirectory ? file.Path : parent;
        var menu = new ContextMenu { Name = "FileNodeActions" };
        void Add(string header, string name, Action action, string? icon = null)
        {
            var item = Ui.Menu(header, action);
            item.Name = name;
            if (icon is not null) item.Icon = Ui.Icon(icon, null, 14);
            menu.Items.Add(item);
        }
        Add("New file…", "FileNodeNewFile", () => _ = CreatePathAsync(into, false, node), "fileText");
        Add("New folder…", "FileNodeNewFolder", () => _ = CreatePathAsync(into, true, node), "folder");
        menu.Items.Add(new Separator());
        Add(RevealLabel, "FileNodeReveal", () => _ = FileActionAsync(new("reveal", file.Path)), "folderOpen");
        Add("Copy absolute path", "FileNodeCopyPath", () => _ = CopyTextAsync(Path.Combine(workspaceRoot, file.Path)));
        Add("Rename…", "FileNodeRename", () => _ = RenamePathAsync(file));
        Add(file.IsDirectory ? "Delete folder" : "Delete file", "FileNodeDelete", () => _ = TrashPathAsync(file), "trash");
        return menu;
    }

    private static bool PathListed(IReadOnlyList<ProjectFile> entries, string path) => entries.Any(entry =>
        entry.Path == path || entry.Path.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private async Task CreatePathAsync(string folder, bool directory, TreeViewItem node)
    {
        var workspace = workspaceRoot;
        string Target(string name) => Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
        // A collapsed folder has not been listed yet; its names are needed to warn about a collision as it is typed.
        if (!folderCache.TryGetValue(folder, out var entries))
        {
            try { entries = await host.ListFilesAsync(folder, lifetime.Token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { entries = []; }
            catch (Exception error) when (error is not OperationCanceledException) { Report(error); return; }
            if (workspace != workspaceRoot) return;
        }
        await Dialogs.PathName(this, directory ? "New folder" : "New file", "", "Create",
            name => PathListed(entries, Target(name)), async name =>
            {
                await host.ApplyFileActionAsync(new(directory ? "create-folder" : "create-file", Target(name)), lifetime.Token);
                if (workspace != workspaceRoot) return;
                if (node.Tag is ProjectFile { IsDirectory: true }) node.IsExpanded = true;
                await RefreshFilesAsync(projectRequest);
                if (!directory) await BrowseDocumentAsync(Target(name), true);
            });
    }

    private async Task RenamePathAsync(ProjectFile file)
    {
        var parent = Path.GetDirectoryName(file.Path) ?? "";
        string Target(string name) => Path.Combine(parent, name.Replace('/', Path.DirectorySeparatorChar));
        await Dialogs.PathName(this, "Rename " + Path.GetFileName(file.Path), Path.GetFileName(file.Path), "Rename",
            name => !string.Equals(Target(name), file.Path, StringComparison.OrdinalIgnoreCase) &&
                PathListed(folderCache.GetValueOrDefault(parent) ?? [], Target(name)),
            async name =>
            {
                await host.ApplyFileActionAsync(new("rename", file.Path, Target(name)), lifetime.Token);
                await RefreshFilesAsync(projectRequest);
            });
    }

    private async Task TrashPathAsync(ProjectFile file)
    {
        var name = Path.GetFileName(file.Path);
        var kind = file.IsDirectory ? "Delete folder" : "Delete file";
        if (!await Dialogs.Confirm(this, $"Delete {name}?",
            $"{name} moves to the {(OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash")}, where it can be restored.", kind, "FileNodeDeleteConfirm")) return;
        await FileActionAsync(new("trash", file.Path));
    }

    private async Task FileActionAsync(FileAction action)
    {
        try
        {
            await host.ApplyFileActionAsync(action, lifetime.Token);
            // The local watcher refreshes the tree too; a remote workspace has only this refresh.
            if (action.Kind != "reveal") await RefreshFilesAsync(projectRequest);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    private static bool IsOwnHeader(TreeViewItem node, Control source)
    {
        return ReferenceEquals(source is TreeViewItem ? source : source.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), node);
    }

    private readonly HashSet<string> specsReseated = [];
    private TreeView? specsTree;
    private StackPanel? specsFailure;

    private Control SpecsPanel()
    {
        var panel = new Grid { Name = "SpecsPanel", RowDefinitions = new RowDefinitions("Auto,*") };
        var tree = new TreeView { Name = "SpecsTree", Background = Ui.Sidebar, Margin = new Thickness(4, 12, 12, 12) };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        var message = Ui.Text("", Ui.Hint, 12);
        message.Name = "SpecsError"; message.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var failure = new StackPanel { Name = "SpecsFailure", Spacing = 4, Margin = new Thickness(12, 8, 12, 0), IsVisible = false, Children = { message } };
        var retry = Ui.Button("Retry", () => _ = PopulateSpecsAsync(tree, failure));
        retry.Name = "SpecsRetry"; retry.HorizontalAlignment = HorizontalAlignment.Left;
        failure.Children.Add(retry);
        Ui.Place(panel, failure); Ui.Place(panel, tree, 1);
        specsTree = tree; specsFailure = failure;
        if (WorkspaceMounted) _ = PopulateSpecsAsync(tree, failure);
        return panel;
    }

    /// <summary>Re-reads the mounted Specs panel in place; a panel that is not built yet reads when it is.</summary>
    private void RefreshSpecs()
    {
        if (toolContent.ContainsKey("specs") && specsTree is { } tree && specsFailure is { } failure) _ = PopulateSpecsAsync(tree, failure);
    }

    private async Task PopulateSpecsAsync(TreeView tree, StackPanel failure)
    {
        var request = projectRequest; var workspace = workspaceRoot;
        try
        {
            var specs = await Task.Run(async () => await host.ListSpecsAsync(lifetime.Token), lifetime.Token);
            if (request != projectRequest || workspace != workspaceRoot) return;
            var nodes = new List<TreeViewItem>();
            if (specs.Count == 0 && specsReseated.Add(workspace)) ReseatEmptySpecs();
            var byParent = specs.GroupBy(spec => spec.Parent).ToDictionary(group => group.Key, group => group.ToArray());
            var ids = specs.Select(spec => spec.Id).ToHashSet();
            var roots = specs.Where(spec => spec.Parent.Length == 0 || !ids.Contains(spec.Parent)).ToArray();
            var placed = new HashSet<string>();
            TreeViewItem Build(SpecDocument spec, int depth)
            {
                placed.Add(spec.Id);
                var main = depth == 0 && spec.Type == "goal-and-requirements";
                var row = (Grid)TreeRow(spec.Type.Contains("goal", StringComparison.Ordinal) ? "bookFill" :
                    spec.Type.Contains("module", StringComparison.Ordinal) ? "box" : "stack",
                    System.Text.RegularExpressions.Regex.Replace(spec.Title, @"\s+[—–]\s+", " · "), main);
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var role = Ui.Text(main ? "Main spec" : spec.Type switch
                {
                    "goal-and-requirements" => "GOAL",
                    "architecture-design" => "ARCH",
                    "module-design" => "MODULE",
                    "submodule-design" => "SUBMODULE",
                    "task-spec" => "TASK",
                    _ => System.Text.RegularExpressions.Regex.Replace(spec.Type, @"[-_\s]+", " ").Trim().ToUpperInvariant() is { Length: > 0 } tag ? tag : "SPEC"
                }, main ? Ui.Accent : Ui.Hint, 12);
                role.Name = "SpecRole"; role.IsVisible = false; role.Margin = new Thickness(8, 0, 0, 0);
                Ui.Place(row, role, 0, 3);
                var node = new TreeViewItem
                {
                    Header = row,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsExpanded = depth < 3,
                    Tag = spec.Path
                };
                void UpdateRole() => role.IsVisible = row.IsPointerOver || node.IsKeyboardFocusWithin;
                row.Background = Avalonia.Media.Brushes.Transparent;
                row.PointerEntered += (_, _) => role.IsVisible = true;
                row.PointerExited += (_, _) => role.IsVisible = node.IsKeyboardFocusWithin;
                node.GotFocus += (_, _) => UpdateRole();
                node.LostFocus += (_, _) => UpdateRole();
                AutomationProperties.SetName(node, spec.Title);
                AutomationProperties.SetHelpText(node, spec.Path + " · " + role.Text);
                node.AddHandler(PointerPressedEvent, (_, e) =>
                {
                    if (e.Source is not Control source || !IsOwnHeader(node, source) ||
                        !e.GetCurrentPoint(node).Properties.IsLeftButtonPressed || source is ToggleButton ||
                        source.GetVisualAncestors().OfType<ToggleButton>().Any()) return;
                    _ = BrowseDocumentAsync(spec.Path, e.ClickCount == 2);
                }, RoutingStrategies.Bubble, handledEventsToo: true);
                node.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = BrowseDocumentAsync(spec.Path, true); e.Handled = true; } };
                if (depth < 32 && byParent.TryGetValue(spec.Id, out var children))
                    foreach (var child in children.Where(child => !placed.Contains(child.Id))) node.Items.Add(Build(child, depth + 1));
                return node;
            }
            foreach (var spec in roots) nodes.Add(Build(spec, 0));
            foreach (var spec in specs.Where(spec => !placed.Contains(spec.Id))) nodes.Add(Build(spec, 0));
            if (nodes.Count == 0) nodes.Add(new TreeViewItem { Header = Ui.Text("No specifications in this project", Ui.Hint, 12) });
            tree.Items.Clear();
            foreach (var node in nodes) tree.Items.Add(node);
            failure.IsVisible = false;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The tree keeps what it last showed; the hint says it may be stale and offers the read again.
            if (request != projectRequest) return;
            ((TextBlock)failure.Children[0]).Text = "Specs could not be loaded: " + error.Message;
            failure.IsVisible = true;
            Console.Error.WriteLine("Specs could not be loaded: " + error.Message);
        }
    }

    /// <summary>
    /// A workspace without a spec graph opens its rail on the next tool rather than on the empty Specs panel.
    /// Applied once per workspace, and only to a group still on the seeded first tool; Specs stays docked.
    /// </summary>
    private void ReseatEmptySpecs()
    {
        foreach (var group in Layout.State.Groups.Where(group => group.Region != "center" && group.Tools.Count > 1 && group.Tools[0].Id == "specs"))
            if (Layout.Selected(group.Id)?.Id == "specs") Layout.Reseat(group.Id, group.Tools[1].Id);
    }

    private Control FileRow(ProjectFile file)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
        Ui.Place(row, FileIcon(file.Path, file.IsDirectory, file.IsDirectory ? "folder" : "fileText"));
        Ui.Place(row, Ui.Text(file.Name), 0, 2);
        return row;
    }
    private static Control TreeRow(string icon, string title, bool primary = false)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
        Ui.Place(row, Ui.Icon(icon, primary ? Ui.Accent : null, 14));
        Ui.Place(row, Ui.Text(title), 0, 2);
        return row;
    }
}
