using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
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
        var contributed = new List<Control>();
        menu.Opening += (_, _) =>
        {
            foreach (var item in contributed) menu.Items.Remove(item);
            contributed.Clear();
            var offered = FileActionItems(new(workspaceRoot, file.Path, file.IsDirectory));
            if (offered.Count == 0) return;
            contributed.Add(new Separator());
            contributed.AddRange(offered);
            foreach (var item in contributed) menu.Items.Add(item);
        };
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