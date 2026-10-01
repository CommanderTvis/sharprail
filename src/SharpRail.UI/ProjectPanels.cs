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
    private readonly HashSet<string> specsReseated = [];

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

    private async Task OpenPickedProjectAsync(string path)
    {
        try
        {
            await OpenProjectHomeAsync(path);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    /// <summary>Restyles the rail's active project and workspace without rebuilding it, so switching keeps rows and focus.</summary>
    private readonly List<Action> railSelection = [];

    private void UpdateRailSelection() { foreach (var update in railSelection) update(); }

    private Control ProjectsPanel()
    {
        railSignature = RailSignature();
        railSelection.Clear();
        var panel = new Grid { Name = "ProjectsPanel", Margin = new Thickness(12), RowDefinitions = new RowDefinitions("28,8,*") };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 0, 4, 0) };
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
        var tree = new StackPanel { Spacing = 4 };
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
            var toggle = Ui.IconButton(collapsed ? "arrowRight" : "arrowDown", collapsed ? "Expand project" : "Collapse project", () =>
            {
                if (!profile.Data.CollapsedProjects.Add(project)) profile.Data.CollapsedProjects.Remove(project);
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
            if (project == projectRoot)
            {
                var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                var count = git.Worktrees.Count(tree => !tree.IsMain);
                if (collapsed && count > 0)
                {
                    var badge = Ui.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), Ui.Hint, 12);
                    badge.Name = "ProjectWorkspaceCount";
                    trailing.Children.Add(badge);
                }
                var tip = $"Create workspace ({Shortcut("N")})";
                var add = Ui.IconButton("add", tip, () => _ = CreateWorkspaceDialogAsync());
                add.Name = "AddWorkspace";
                add.Width = add.Height = 28; add.Padding = new(7);
                add.IsEnabled = git.IsRepository;
                trailing.Children.Add(add);
                Ui.Place(row, trailing, 0, 3);
            }
            tree.Children.Add(highlight);
            if (project != projectRoot || collapsed) continue;
            var worktrees = git.Worktrees.Count > 0 ? git.Worktrees : new[] { new WorktreeInfo(projectRoot, "", true, false) };
            foreach (var worktree in worktrees) tree.Children.Add(WorkspaceItem(worktree));
        }
        Ui.Place(panel, new ScrollViewer { Content = tree, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 2);
        return panel;
    }

    private Control WorkspaceItem(WorktreeInfo worktree)
    {
        var name = WorkspaceName(worktree.Path);
        var active = !atHome && worktree.Path == workspaceRoot;
        var twoLines = worktree.Branch.Length > 0;
        var color = active ? Ui.Accent : Ui.Muted;
        var contents = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
        var icon = Ui.Icon(worktree.IsMain ? "homeFill" : "gitBranch", color, 14);
        if (twoLines) { icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new(0, 2, 0, 0); }
        Ui.Place(contents, icon);
        var labels = new StackPanel();
        var label = Ui.Text(name, color); label.LineHeight = 17.5; label.Name = "WorkspaceName";
        labels.Children.Add(label);
        if (twoLines)
        {
            var branch = Ui.Text(worktree.Branch, Ui.Hint, 12); branch.LineHeight = 15; branch.Name = "WorkspaceBranch";
            labels.Children.Add(branch);
        }
        Ui.Place(contents, labels, 0, 2);
        var button = new Button
        {
            Name = "WorkspaceSelect",
            Tag = worktree.Path,
            Content = contents,
            Background = active ? Ui.Hover : Avalonia.Media.Brushes.Transparent,
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
            button.Background = selected ? Ui.Hover : Avalonia.Media.Brushes.Transparent;
            ((Border)icon).Background = label.Foreground = selected ? Ui.Accent : Ui.Muted;
        });
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, worktree.Path);
        button.Click += (_, _) => _ = OpenWorkspaceAsync(worktree.Path, false);
        var kebab = new Button
        {
            Name = "WorkspaceMenu",
            Content = Ui.Icon("moreHorizontal", null, 14),
            Width = 24,
            Height = 24,
            Padding = new(5),
            Margin = new(0, 0, 4, 0),
            Opacity = 0,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(kebab, "Workspace actions");
        ToolTip.SetTip(kebab, "Workspace actions");
        var menu = WorkspaceActions(worktree, kebab);
        button.ContextMenu = menu;
        kebab.Click += (_, _) => menu.Open(button);
        var item = new Grid { Name = "WorkspaceItem", Tag = worktree.Path, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Avalonia.Media.Brushes.Transparent };
        void Reveal() => kebab.Opacity = item.IsPointerOver || item.IsKeyboardFocusWithin || menu.IsOpen ? 1 : 0;
        item.PointerEntered += (_, _) => Reveal();
        item.PointerExited += (_, _) => Reveal();
        item.GotFocus += (_, _) => Reveal();
        item.LostFocus += (_, _) => Reveal();
        menu.Opened += (_, _) => Reveal();
        menu.Closed += (_, _) => Reveal();
        var main = renaming == worktree.Path ? RenameBox(worktree.Path) : button;
        Ui.Place(item, main);
        Grid.SetColumnSpan(main, 2);
        Ui.Place(item, kebab, 0, 1);
        return item;
    }

    private Control FilesPanel()
    {
        var panel = new Grid { Name = "FilesPanel" };
        var tree = new TreeView { Name = "FilesTree", Background = Ui.Sidebar, Margin = new Thickness(4, 12, 12, 12) };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
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
            Tag = file,
            IsVisible = Preferences.ShowHiddenFiles || !file.Name.StartsWith('.')
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
                var workspace = workspaceRoot;
                try
                {
                    var entries = await host.ListFilesAsync(file.Path, lifetime.Token);
                    if (workspace != workspaceRoot) return;
                    folderCache[file.Path] = entries;
                    node.Items.Clear();
                    foreach (var entry in entries) node.Items.Add(FileNode(entry));
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
            // Like the reference tree, a single click on the folder's own row toggles it. The tree's built-in
            // double-tap toggle is suppressed there so a double click does not undo the first click.
            node.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.Source is not Control source || !IsOwnHeader(node, source) || !e.GetCurrentPoint(node).Properties.IsLeftButtonPressed) return;
                // The chevron's own toggle already expands and collapses the folder.
                if (source is ToggleButton || source.GetVisualAncestors().OfType<ToggleButton>().Any()) return;
                if (e.ClickCount == 1) node.IsExpanded = !node.IsExpanded;
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

    private Control SpecsPanel()
    {
        var tree = new TreeView { Name = "SpecsTree", Background = Ui.Sidebar, Margin = new Thickness(4, 12, 12, 12) };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        if (WorkspaceMounted) _ = PopulateSpecsAsync(tree);
        return tree;
    }

    private async Task PopulateSpecsAsync(TreeView tree)
    {
        var request = projectRequest; var workspace = workspaceRoot;
        try
        {
            var specs = await Task.Run(async () => await host.ListSpecsAsync(lifetime.Token), lifetime.Token);
            if (request != projectRequest || workspace != workspaceRoot) return;
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
            foreach (var spec in roots) tree.Items.Add(Build(spec, 0));
            foreach (var spec in specs.Where(spec => !placed.Contains(spec.Id))) tree.Items.Add(Build(spec, 0));
            if (tree.Items.Count == 0) tree.Items.Add(new TreeViewItem { Header = Ui.Text("No specifications in this project", Ui.Hint, 12) });
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
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