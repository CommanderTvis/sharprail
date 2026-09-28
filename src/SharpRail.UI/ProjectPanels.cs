using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Threading;
using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private readonly Dictionary<string, IReadOnlyList<ProjectFile>> folderCache = [];
    private readonly HashSet<string> expandedFolders = [];
    private readonly HashSet<string> collapsedProjects = [];

    private async Task PickProjectAsync()
    {
        try
        {
            if (remote)
            {
                var values = await Dialogs.Prompt(this, "Open project on remote host", ("Directory path", workspaceRoot));
                if (values is not null && values[0].Length > 0) await OpenProjectAsync(values[0]);
            }
            else
            {
                var paths = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open project", AllowMultiple = false });
                var path = paths.FirstOrDefault()?.TryGetLocalPath();
                if (path is not null) await OpenProjectAsync(path);
            }
        }
        catch (Exception error) { Report(error); }
    }

    private Control ProjectsPanel()
    {
        var panel = new Grid { Name = "ProjectsPanel", Margin = new Thickness(12), RowDefinitions = new RowDefinitions("28,8,*") };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 0, 4, 0) };
        var title = Ui.Text("PROJECTS", size: 12); title.FontWeight = Avalonia.Media.FontWeight.Medium;
        Ui.Place(toolbar, title);
        var open = Ui.IconButton("add", "Open project", () => _ = PickProjectAsync());
        open.Width = open.Height = 28;
        Ui.Place(toolbar, open, 0, 1);
        Ui.Place(panel, toolbar);
        var tree = new StackPanel { Spacing = 4 };
        foreach (var project in profile.Data.Projects)
        {
            var row = new Grid { Name = "ProjectRow", ColumnDefinitions = new ColumnDefinitions("16,4,*,Auto"), Height = 28, Margin = new Thickness(4, 0) };
            var collapsed = collapsedProjects.Contains(project);
            var toggle = Ui.IconButton(collapsed ? "arrowRight" : "arrowDown", collapsed ? "Expand project" : "Collapse project", () =>
            {
                if (!collapsedProjects.Add(project)) collapsedProjects.Remove(project);
                toolContent.Remove("projects"); surface.RefreshContents();
                Dispatcher.UIThread.Post(() => surface.GetLogicalDescendants().OfType<Button>()
                    .FirstOrDefault(button => button.Name == "ProjectExpand" && Equals(button.Tag, project))?.Focus());
            });
            toggle.Name = "ProjectExpand"; toggle.Tag = project;
            toggle.Width = toggle.Height = 16; toggle.Padding = new(0);
            Ui.Place(row, toggle);
            var select = new Button();
            select.Click += (_, _) => _ = OpenProjectAsync(project);
            AutomationProperties.SetName(select, new DirectoryInfo(project).Name);
            var projectLabel = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
            Ui.Place(projectLabel, Ui.Icon("folderFill", project == projectRoot ? Ui.Accent : Ui.Muted, 14));
            Ui.Place(projectLabel, Ui.Text(new DirectoryInfo(project).Name, project == projectRoot ? Ui.TextBrush : Ui.Muted), 0, 2);
            select.Content = projectLabel;
            select.Background = Avalonia.Media.Brushes.Transparent; select.BorderThickness = new(0); select.Padding = new Thickness(0);
            select.HorizontalAlignment = HorizontalAlignment.Stretch; select.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ToolTip.SetTip(select, project); Ui.Place(row, select, 0, 2);
            if (project == projectRoot)
            {
                var add = Ui.IconButton("add", "Create worktree", () => _ = CreateWorktreeAsync());
                add.Width = add.Height = 28; add.Padding = new(7);
                add.IsEnabled = git.IsRepository; Ui.Place(row, add, 0, 3);
            }
            tree.Children.Add(row);
            if (project != projectRoot || collapsed) continue;
            var worktrees = git.Worktrees.Count > 0 ? git.Worktrees : new[] { new WorktreeInfo(workspaceRoot, "", true, false) };
            foreach (var worktree in worktrees)
            {
                var name = worktree.IsMain ? "Default workspace" : new DirectoryInfo(worktree.Path).Name;
                var twoLines = worktree.Branch.Length > 0 && worktree.Branch != name;
                var color = worktree.Path == workspaceRoot ? Ui.Accent : Ui.Muted;
                var contents = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
                var icon = Ui.Icon(worktree.IsMain ? "homeFill" : "gitBranch", color, 14);
                if (twoLines) { icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new(0, 2, 0, 0); }
                Ui.Place(contents, icon);
                var labels = new StackPanel();
                var label = Ui.Text(name, color); label.LineHeight = 17.5;
                labels.Children.Add(label);
                if (twoLines)
                {
                    var branch = Ui.Text(worktree.Branch, Ui.Hint, 12); branch.LineHeight = 15;
                    labels.Children.Add(branch);
                }
                Ui.Place(contents, labels, 0, 2);
                var button = new Button
                {
                    Content = contents,
                    Background = worktree.Path == workspaceRoot ? Ui.Hover : Avalonia.Media.Brushes.Transparent,
                    Padding = new Thickness(24, 4, 4, 4),
                    MinHeight = 28,
                    CornerRadius = new(4),
                    BorderThickness = new(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetName(button, name);
                ToolTip.SetTip(button, worktree.Path);
                button.Click += (_, _) => _ = OpenWorkspaceAsync(worktree.Path, false);
                button.ContextMenu = new ContextMenu();
                button.ContextMenu.Items.Add(Ui.Menu("Open workspace", () => _ = OpenWorkspaceAsync(worktree.Path, false)));
                button.ContextMenu.Items.Add(Ui.Menu("Remove worktree…", () => _ = RemoveWorktreeAsync(worktree),
                    !worktree.IsMain && !worktree.IsLocked && worktree.Path != workspaceRoot));
                tree.Children.Add(button);
            }
        }
        Ui.Place(panel, new ScrollViewer { Content = tree, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 2);
        return panel;
    }

    private Control FilesPanel()
    {
        var panel = new Grid { Name = "FilesPanel" };
        var tree = new TreeView { Name = "FilesTree", Background = Ui.Sidebar, Margin = new Thickness(12) };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        foreach (var file in folderCache.GetValueOrDefault("") ?? []) tree.Items.Add(FileNode(file));
        Ui.Place(panel, tree);
        return panel;
    }

    private TreeViewItem FileNode(ProjectFile file)
    {
        var node = new TreeViewItem
        {
            Header = TreeRow(file.IsDirectory ? "folder" : "fileText", file.Name),
            MinHeight = 24,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = file,
            IsVisible = Preferences.ShowHiddenFiles || !file.Name.StartsWith('.')
        };
        AutomationProperties.SetName(node, file.Name);
        AutomationProperties.SetHelpText(node, file.Path);
        if (file.IsDirectory)
        {
            if (folderCache.TryGetValue(file.Path, out var children))
                foreach (var child in children) node.Items.Add(FileNode(child));
            else node.Items.Add(new TreeViewItem { Header = Ui.Text("Loading…", Ui.Hint) });
            node.IsExpanded = expandedFolders.Contains(file.Path);
            node.PropertyChanged += async (_, e) =>
            {
                if (e.Property != TreeViewItem.IsExpandedProperty) return;
                if (node.IsExpanded)
                {
                    expandedFolders.Add(file.Path);
                    if (!folderCache.ContainsKey(file.Path))
                    {
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
                }
                else expandedFolders.Remove(file.Path);
            };
        }
        else node.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Control source || !IsOwnHeader(node, source) || !e.GetCurrentPoint(node).Properties.IsLeftButtonPressed) return;
            _ = BrowseDocumentAsync(file.Path, e.ClickCount == 2);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        node.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !file.IsDirectory) { _ = OpenDocumentAsync(file.Path, true); e.Handled = true; }
        };
        return node;
    }

    private static bool IsOwnHeader(TreeViewItem node, Control source)
    {
        return ReferenceEquals(source is TreeViewItem ? source : source.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), node);
    }

    private Control SpecsPanel()
    {
        var tree = new TreeView { Name = "SpecsTree", Background = Ui.Sidebar, Margin = new Thickness(12) };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        _ = PopulateSpecsAsync(tree);
        return tree;
    }

    private async Task PopulateSpecsAsync(TreeView tree)
    {
        var request = projectRequest;
        try
        {
            var specs = await host.ListSpecsAsync(lifetime.Token);
            if (request != projectRequest) return;
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
                node.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = OpenDocumentAsync(spec.Path, true); e.Handled = true; } };
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

    private static Control TreeRow(string icon, string title, bool primary = false)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
        Ui.Place(row, Ui.Icon(icon, primary ? Ui.Accent : null, 14));
        Ui.Place(row, Ui.Text(title), 0, 2);
        return row;
    }
}
