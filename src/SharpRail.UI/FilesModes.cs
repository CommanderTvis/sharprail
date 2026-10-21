using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;

namespace SharpRail.UI;

/// <summary>
/// Files' All | Changed switch (ThinkRail #779): Changed is the Changes view unchanged, All is the file tree with
/// VS Code-like Git marks. One Git snapshot feeds the count, the marks and the Changed list, so they cannot disagree.
/// </summary>
public sealed partial class WorkbenchWindow
{
    // App-wide in the window and session-only, like the Changes view's List | Tree choice.
    private bool filesChanged;
    private readonly HashSet<string> changedCollapsed = [];
    // The last marks a successful Git read produced: a failed refresh keeps them, a failed first read has none.
    private Dictionary<string, (string Letter, IBrush Brush)> fileMarks = [];
    private HashSet<string> markedFolders = [];
    private string marksSignature = "";

    internal bool FilesShowChanged => filesChanged;

    /// <summary>The changes the Changed view lists for the current scope; the count and the All marks read the same list.</summary>
    private GitChange[] ScopedChanges() => git.Changes.Where(change => changeScope switch
    {
        "Staged" => change.IndexStatus is not (" " or "?"),
        _ => true
    }).ToArray();

    /// <summary>The one status letter every Files surface uses: untracked reads U, added A, renamed R, anything else M; deleted has none.</summary>
    internal static (string Letter, IBrush Brush)? ChangeMark(GitChange change)
    {
        if (change.IndexStatus == "?" || change.WorktreeStatus == "?") return ("U", Ui.Success);
        if (change.WorktreeStatus == "D" || change.IndexStatus == "D" && change.WorktreeStatus != "A") return null;
        if (change.IndexStatus == "R" || change.WorktreeStatus == "R") return ("R", Ui.Info);
        if (change.IndexStatus == "A" || change.WorktreeStatus == "A") return ("A", Ui.Success);
        return ("M", Ui.Warning);
    }

    private static string MarkKey(string path) => path.Replace('\\', '/');

    private void UpdateFileMarks()
    {
        // A switched workspace starts loading with no changes, so its predecessor's marks go; a failed refresh keeps the last.
        if (gitLoading && git.Changes.Count == 0) { fileMarks = []; markedFolders = []; }
        else if (!gitLoading && gitError is null)
        {
            var marks = new Dictionary<string, (string, IBrush)>(StringComparer.Ordinal);
            var folders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in ScopedChanges())
            {
                if (ChangeMark(change) is not { } mark) continue;
                var path = MarkKey(change.Path);
                marks[path] = mark;
                for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1)) folders.Add(path[..slash]);
            }
            fileMarks = marks; markedFolders = folders;
        }
        var signature = string.Join("\n", fileMarks.Select(pair => pair.Key + "\t" + pair.Value.Letter)) + "\0" + string.Join("\n", markedFolders);
        if (signature != marksSignature)
        {
            marksSignature = signature;
            if (toolContent.GetValueOrDefault("files") is FilesPaneFrame panel)
            {
                var tree = panel.Children.OfType<DrawnTree>().Single();
                foreach (var root in tree.Roots) MarkEntry(root);
                tree.RefreshRows();
            }
        }
        UpdateFilesHeader();
    }

    private void MarkEntry(DrawnTreeEntry entry)
    {
        if (entry.Tag is ProjectFile file)
        {
            var key = MarkKey(file.Path);
            if (file.IsDirectory)
            {
                var marked = markedFolders.Contains(key);
                entry.Tint = marked ? Ui.Warning : null; entry.Dot = marked; entry.Badge = null;
            }
            else if (fileMarks.TryGetValue(key, out var mark)) { entry.Tint = mark.Brush; entry.Badge = mark.Letter; entry.Dot = false; }
            else { entry.Tint = null; entry.Badge = null; entry.Dot = false; }
            entry.Help = file.Path + (entry.Badge is { } letter ? " · " + letter : entry.Dot ? " · contains changes" : "") + (entry.Tip is { Length: > 0 } tip ? " · " + tip : "");
        }
        foreach (var child in entry.Children) MarkEntry(child);
    }

    private Grid FilesHeader()
    {
        var header = new Grid { Name = "FilesHeader", ColumnDefinitions = new ColumnDefinitions("Auto,4,Auto,*,Auto"), Height = 32, Margin = new Thickness(12, 0, 8, 0) };
        Ui.Place(header, FilesModeToggle("All", false));
        Ui.Place(header, FilesModeToggle("Changed", true), 0, 2);
        var collapse = new Button
        {
            Name = "FilesCollapseAll",
            Content = Ui.Icon("collapseVertical", null, 14),
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        collapse.Classes.Add("quiet");
        AutomationProperties.SetName(collapse, "Collapse all folders");
        ToolTip.SetTip(collapse, "Collapse all folders");
        collapse.Click += (_, _) => CollapseAllFolders();
        Ui.Place(header, collapse, 0, 4);
        return header;
    }

    private ToggleButton FilesModeToggle(string label, bool changed)
    {
        var button = new ToggleButton
        {
            Name = "FilesMode" + label,
            Content = Ui.Text(label, Ui.Muted, 12),
            Height = 20,
            Padding = new Thickness(8, 2),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, label + " files");
        button.Click += (_, _) => SetFilesMode(changed);
        return button;
    }

    private void UpdateFilesHeader()
    {
        // Built in code, so the header is not in the XAML frame's name scope.
        if (toolContent.GetValueOrDefault("files") is not FilesPaneFrame panel || panel.Children.OfType<Grid>().FirstOrDefault(child => child.Name == "FilesHeader") is not { } header) return;
        var count = gitLoading || gitError is not null || !git.IsRepository ? 0 : ScopedChanges().Length;
        foreach (var toggle in header.Children.OfType<ToggleButton>())
        {
            var changed = toggle.Name == "FilesModeChanged";
            var selected = changed == filesChanged;
            toggle.IsChecked = selected;
            toggle.Background = selected ? Ui.Selected : Brushes.Transparent;
            var text = (TextBlock)toggle.Content!;
            text.Text = changed && count > 0 ? $"Changed {count}" : changed ? "Changed" : "All";
            text.Foreground = selected ? Ui.TextBrush : Ui.Muted;
        }
        // Collapse all applies to All, and to Changed only in its Tree view with at least one change.
        if (header.Children.OfType<Button>().FirstOrDefault(child => child.Name == "FilesCollapseAll") is { } collapse) collapse.IsEnabled = !filesChanged || changeTree && count > 0;
    }

    /// <summary>Shows All or Changed in the Files tool; Changed is the Changes view itself, built on first use.</summary>
    internal void SetFilesMode(bool changed)
    {
        filesChanged = changed;
        if (toolContent.GetValueOrDefault("files") is FilesPaneFrame panel) ApplyFilesMode(panel);
    }

    private void ApplyFilesMode(FilesPaneFrame panel)
    {
        panel.Children.OfType<DrawnTree>().Single().IsVisible = !filesChanged;
        var changes = toolContent.GetValueOrDefault("changes");
        if (filesChanged && changes is null && WorkspaceMounted) toolContent["changes"] = changes = ChangesPanel();
        if (changes is not null)
        {
            if (!ReferenceEquals(changes.Parent, panel))
            {
                (changes.Parent as Panel)?.Children.Remove(changes);
                Grid.SetRow(changes, 2);
                panel.Children.Add(changes);
            }
            changes.IsVisible = filesChanged;
        }
        UpdateFilesHeader();
    }

    /// <summary>Reveals Files in Changed mode, which is where the changed-file list lives.</summary>
    private void RevealChanges()
    {
        RevealFiles();
        SetFilesMode(true);
    }

    private void CollapseAllFolders()
    {
        if (!filesChanged)
        {
            if (toolContent.GetValueOrDefault("files") is not FilesPaneFrame panel) return;
            expandedFolders.Clear();
            var tree = panel.Children.OfType<DrawnTree>().Single();
            void Collapse(DrawnTreeEntry entry) { entry.Expanded = false; foreach (var child in entry.Children) Collapse(child); }
            foreach (var root in tree.Roots) Collapse(root);
            tree.SetRoots(tree.Roots);
        }
        else if (toolContent.GetValueOrDefault("changes")?.GetLogicalDescendants().OfType<TreeView>().FirstOrDefault(tree => tree.Name == "ChangesTree") is { } changes)
        {
            void Collapse(ItemCollection items)
            {
                foreach (var node in items.OfType<TreeViewItem>().Where(node => node.Tag is string))
                {
                    node.IsExpanded = false;
                    Collapse(node.Items);
                }
            }
            Collapse(changes.Items);
        }
        SaveFilesTree();
    }

    /// <summary>
    /// Restores the entered workspace's remembered tree. One with no memory yet keeps the retained tree of the same
    /// project's previous workspace (the Files pane stays as it was while listings load) and starts empty otherwise.
    /// </summary>
    private void RestoreFilesTree(bool sameProject)
    {
        if (profile.Data.FilesTrees.GetValueOrDefault(workspaceRoot) is not { } memory)
        {
            if (!sameProject) { expandedFolders.Clear(); changedCollapsed.Clear(); }
            return;
        }
        expandedFolders.Clear(); changedCollapsed.Clear();
        expandedFolders.UnionWith(memory.Expanded);
        changedCollapsed.UnionWith(memory.ChangedCollapsed);
    }

    private void RememberFilesTree()
    {
        if (!WorkspaceMounted || atHome || workspaceRoot.Length == 0 || removedWorkspaces.Contains(workspaceRoot)) return;
        if (expandedFolders.Count == 0 && changedCollapsed.Count == 0) profile.Data.FilesTrees.Remove(workspaceRoot);
        else profile.Data.FilesTrees[workspaceRoot] = new() { Expanded = [.. expandedFolders], ChangedCollapsed = [.. changedCollapsed] };
    }

    private void SaveFilesTree() { RememberFilesTree(); SaveProfile(); }

    /// <summary>Remembers a Changed-tree folder's collapse for every scope of this workspace.</summary>
    private void TrackChangedFolder(TreeViewItem node, string path)
    {
        node.IsExpanded = !changedCollapsed.Contains(path);
        node.PropertyChanged += (_, e) =>
        {
            if (e.Property != TreeViewItem.IsExpandedProperty) return;
            if (node.IsExpanded ? changedCollapsed.Remove(path) : changedCollapsed.Add(path)) SaveFilesTree();
        };
    }
}