using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private string comparison = "";
    private string changeScope = "All changes";
    private GitCommit? selectedCommit;
    private IReadOnlyList<GitCommit> gitCommits = [];
    private bool changeTree;
    private readonly List<(Border Frame, GitChange Change)> changeFrames = [];

    private Control ChangesPanel()
    {
        changeFrames.Clear();
        var panel = new Grid { Name = "ChangesPanel", RowDefinitions = new RowDefinitions("32,*") };
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,4,Auto,4,Auto"),
            Margin = new Thickness(12, 0),
            ClipToBounds = true
        };
        var selectors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, ClipToBounds = true };
        var scope = ChangesDropdown("ChangesScope", "Diff scope", selectedCommit?.ShortSha ?? changeScope, "fileDiff");
        if (selectedCommit is not null) ToolTip.SetTip(scope, selectedCommit.Subject);
        foreach (var label in new[] { "All changes", "Uncommitted", "Staged", "Branch" })
        {
            var item = Ui.Menu(label, () =>
            {
                changeScope = label;
                selectedCommit = null;
                if (label == "Branch" && comparison.Length == 0) comparison = git.Branches.FirstOrDefault(branch => branch != git.Branch) ?? "HEAD";
                SaveGitSelection();
                _ = RefreshAsync();
            });
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = changeScope == label;
            scope.ContextMenu!.Items.Add(item);
        }
        scope.ContextMenu!.Items.Add(new Separator());
        if (gitCommits.Count == 0)
        {
            var none = Ui.Menu("No commits on this branch", () => { }, false);
            none.Name = "ChangesNoCommits";
            scope.ContextMenu.Items.Add(none);
        }
        foreach (var commit in gitCommits)
        {
            var item = Ui.Menu(commit.Subject, () =>
            {
                selectedCommit = commit; changeScope = "Commit";
                SaveGitSelection();
                _ = RefreshAsync();
            });
            item.Name = "ChangesCommit_" + commit.Sha;
            item.Header = new StackPanel
            {
                Children = { Ui.Text(commit.Subject.Length > 0 ? commit.Subject : commit.ShortSha),
                    Ui.Text(commit.ShortSha + " · " + commit.Author, Ui.Muted, 12) }
            };
            ToolTip.SetTip(item, commit.Subject);
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = selectedCommit?.Sha == commit.Sha;
            scope.ContextMenu!.Items.Add(item);
        }
        selectors.Children.Add(scope);
        if (git.IsRepository)
        {
            var branches = ChangesDropdown("ChangesBranch", "Comparison branch", "vs " + (comparison.Length > 0 ? comparison : git.Branch), "gitBranch");
            branches.MaxWidth = 200;
            var local = new MenuItem { Header = "Local" };
            var remote = new MenuItem { Header = "Remote" };
            if (gitBranches.Local.Count > 0) branches.ContextMenu!.Items.Add(local);
            if (gitBranches.Remote.Count > 0) branches.ContextMenu!.Items.Add(remote);
            void AddBranch(MenuItem parent, string name, string branch)
            {
                var parts = name.Split('/');
                foreach (var folder in parts[..^1])
                {
                    var group = parent.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag is null && Equals(item.Header, folder));
                    if (group is null)
                    {
                        group = new MenuItem { Header = folder };
                        parent.Items.Add(group);
                    }
                    parent = group;
                }
                var item = Ui.Menu(parts[^1], () =>
                {
                    changeScope = "All changes"; selectedCommit = null; comparison = branch;
                    RetargetDiffTabs();
                    SaveGitSelection(); _ = RefreshAsync();
                });
                item.ToggleType = MenuItemToggleType.Radio;
                item.IsChecked = comparison == branch;
                item.Tag = branch;
                ToolTip.SetTip(item, branch);
                parent.Items.Add(item);
            }
            foreach (var branch in gitBranches.Local) AddBranch(local, branch, branch);
            foreach (var group in gitBranches.Remote.GroupBy(branch => branch.Remote))
            {
                var owner = new MenuItem { Header = group.Key };
                remote.Items.Add(owner);
                foreach (var branch in group) AddBranch(owner, branch.Name, branch.Ref);
            }
            branches.ContextMenu!.Items.Add(new Separator());
            branches.ContextMenu.Items.Add(Ui.Menu("Refresh git", () => _ = RefreshAsync()));
            selectors.Children.Add(branches);
        }
        Ui.Place(toolbar, selectors);
        Ui.Place(toolbar, ChangesViewToggle("List", false), 0, 2);
        Ui.Place(toolbar, ChangesViewToggle("Tree", true), 0, 4);
        Ui.Place(panel, Ui.Frame(toolbar));
        var changes = git.Changes.Where(change => changeScope switch
        {
            "Staged" => change.IndexStatus is not (" " or "?"),
            _ => true
        }).ToArray();
        if (gitLoading || gitError is not null)
        {
            var message = Ui.Text(gitError ?? "Loading Git…", Ui.Hint, 12);
            message.Name = gitError is null ? "ChangesLoading" : "ChangesError";
            var notice = new StackPanel { Spacing = 4, Margin = new Thickness(12, 4), Children = { message } };
            if (gitError is not null)
            {
                var retry = Ui.Button("Retry", () =>
                {
                    gitError = null; gitLoading = true; RefreshGitPanels();
                    _ = RefreshGitAsync(projectRequest);
                });
                retry.Name = "ChangesRetry";
                retry.HorizontalAlignment = HorizontalAlignment.Left;
                notice.Children.Add(retry);
            }
            Ui.Place(panel, notice, 1); return panel;
        }
        if (!git.IsRepository)
        {
            Ui.Place(panel, Ui.Text("This project is not a git repository.", Ui.Hint, 12), 1); return panel;
        }
        if (changeTree)
        {
            Ui.Place(panel, ChangesTree(changes), 1);
        }
        else
        {
            var rows = new StackPanel { Margin = new Thickness(12, 12), Spacing = 2 };
            foreach (var change in changes) rows.Children.Add(ChangeRow(change, change.Path));
            if (changes.Length == 0) rows.Children.Add(Ui.Text("Working tree clean", Ui.Hint, 12));
            Ui.Place(panel, new ScrollViewer { Content = rows }, 1);
        }
        return panel;
    }

    private static Button ChangesDropdown(string name, string description, string label, string icon)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*,4,16") };
        Ui.Place(content, Ui.Icon(icon, size: 14));
        Ui.Place(content, Ui.Text(label, size: 12), 0, 2);
        Ui.Place(content, Ui.Icon("arrowDown"), 0, 4);
        var button = new Button
        {
            Name = name,
            Content = content,
            Height = 24,
            Padding = new Thickness(4, 0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Avalonia.Media.Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            ContextMenu = new ContextMenu { Placement = PlacementMode.Bottom }
        };
        AutomationProperties.SetName(button, description);
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => button.ContextMenu.Open(button);
        return button;
    }

    private ToggleButton ChangesViewToggle(string label, bool tree)
    {
        var selected = changeTree == tree;
        var button = new ToggleButton
        {
            Name = "Changes" + label,
            Content = Ui.Text(label, selected ? Ui.TextBrush : Ui.Muted, 12),
            IsChecked = selected,
            Height = 20,
            Padding = new Thickness(8, 2),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = selected ? Ui.Hover : Avalonia.Media.Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, label + " view");
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed", "PointerOver", "Pressed" })
            button.Resources["ToggleButtonBackground" + state] = Ui.Hover;
        button.Click += (_, _) =>
        {
            changeTree = tree;
            toolContent.Remove("changes");
            surface.RefreshContents();
        };
        return button;
    }

    private Control ChangeRow(GitChange change, string label)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto"), Height = 28 };
        var color = change.IndexStatus is "?" or "A" || change.WorktreeStatus is "?" or "A" ? Ui.Success :
            change.IndexStatus == "D" || change.WorktreeStatus == "D" ? Ui.Danger : Ui.Muted;
        var separator = label.LastIndexOf('/');
        var path = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        var directory = Ui.Text(separator < 0 ? "" : label[..(separator + 1)], Ui.Muted);
        directory.Classes.Add("change-path-dir");
        Ui.Place(path, directory);
        var filename = Ui.Text(label[(separator + 1)..], color);
        filename.Classes.Add("change-path-base");
        Ui.Place(path, filename, 0, 1);
        Ui.Place(row, path);
        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        numbers.Children.Add(Ui.Text("+" + change.Added, Ui.Success, 12));
        numbers.Children.Add(Ui.Text("−" + change.Removed, Ui.Danger, 12));
        Ui.Place(row, numbers, 0, 2);
        row.SizeChanged += (_, _) =>
        {
            path.MaxWidth = Math.Max(0, row.Bounds.Width - numbers.DesiredSize.Width - 8);
            filename.MaxWidth = path.MaxWidth;
        };
        var button = new Button
        {
            Content = row,
            Padding = new Thickness(4, 0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        foreach (var state in new[] { "PointerOver", "Pressed" }) button.Resources["ButtonBackground" + state] = Avalonia.Media.Brushes.Transparent;
        AutomationProperties.SetName(button, change.Path);
        ToolTip.SetTip(button, change.OriginalPath is null ? $"{change.Path}  [{change.IndexStatus}{change.WorktreeStatus}]" : $"{change.OriginalPath} → {change.Path}");
        button.Click += (_, _) => _ = OpenDiffAsync(change);
        var actions = new Button
        {
            Content = Ui.Icon("arrowDown"),
            Width = 20,
            Height = 20,
            Padding = new(0),
            Margin = new(0, 0, 4, 0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Classes.Add("change-actions");
        AutomationProperties.SetName(actions, "Actions for " + change.Path);
        button.ContextMenu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight, PlacementTarget = actions };
        button.ContextMenu.Items.Add(Ui.Menu("View", () => _ = OpenDiffAsync(change)));
        button.ContextMenu.Items.Add(Ui.Menu("Copy path", () => _ = CopyChangePathAsync(change.Path)));
        var canStage = selectedCommit is null && (comparison.Length == 0 || changeScope is "Uncommitted" or "Staged");
        button.ContextMenu.Items.Add(Ui.Menu("Stage file", () => _ = GitActionAsync(new("stage", change.Path)), canStage && change.WorktreeStatus != " "));
        button.ContextMenu.Items.Add(Ui.Menu("Unstage file", () => _ = GitActionAsync(new("unstage", change.Path)), canStage && change.IndexStatus is not (" " or "?")));
        var wrapper = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Ui.Place(wrapper, button); Ui.Place(wrapper, actions, 0, 1);
        var frame = new Border { Child = wrapper };
        frame.Classes.Add("change-row");
        changeFrames.Add((frame, change));
        frame.Classes.Set("active", IsActiveDiff(change));
        actions.Click += (_, _) => button.ContextMenu.Open(button);
        button.ContextMenu.Opened += (_, _) => frame.Classes.Add("menu-open");
        button.ContextMenu.Closed += (_, _) => frame.Classes.Remove("menu-open");
        return frame;
    }

    private async Task CopyChangePathAsync(string path)
    {
        if (Clipboard is not null) await Clipboard.SetTextAsync(path);
    }

    private DockTab DiffTab(GitChange change)
    {
        var reference = selectedCommit?.Sha ?? comparison;
        var scope = selectedCommit is not null ? "commit" : changeScope == "Uncommitted" ? "uncommitted" : comparison.Length > 0 && changeScope != "Staged" ? "branch" :
            change.IndexStatus == "?" ? "untracked" : changeScope == "Staged" ? "staged" : "all";
        var identity = scope == "branch" ? "" : reference;
        return new($"diff:{scope}:{identity}:{change.Path}", Path.GetFileName(change.Path) + " · Diff", "diff", change.Path, Scope: scope, Comparison: reference);
    }

    private bool IsActiveDiff(GitChange change) =>
        Layout.Selected(Layout.View.FocusedCenter) is { Kind: "diff" } selected && selected.Id == DiffTab(change).Id;

    private void UpdateActiveChangeRows()
    {
        foreach (var (frame, change) in changeFrames) frame.Classes.Set("active", IsActiveDiff(change));
    }

    private void RetargetDiffTabs() => Layout.Geometry(state =>
    {
        if (!state.Workspaces.TryGetValue(workspaceRoot, out var view)) return;
        foreach (var tabs in view.Documents.Values)
            for (var index = 0; index < tabs.Count; index++)
                if (tabs[index] is { Kind: "diff", Scope: "branch" } tab && tab.Comparison != comparison) tabs[index] = tab with { Comparison = comparison };
    });

    private async Task OpenDiffAsync(GitChange change)
    {
        var request = BeginNavigation();
        var tab = DiffTab(change);
        try
        {
            var diff = await host.GetDiffAsync(change.Path, tab.Scope, tab.Comparison, lifetime.Token);
            var destination = AcceptNavigation(request);
            if (destination is null) return;
            var key = workspaceRoot + ":" + tab.Id;
            documents[key] = new(change.Path, diff); DropDocumentContent(key);
            Layout.Open(tab, true, destination);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    private async Task GitActionAsync(GitAction action)
    {
        gitRefresh?.Cancel();
        try
        {
            var request = projectRequest;
            var snapshot = await host.ApplyGitActionAsync(action, lifetime.Token);
            if (request != projectRequest) return;
            gitRefresh?.Cancel();
            errorText.IsVisible = false;
            if (changeScope == "Staged" || selectedCommit is not null || comparison.Length > 0) await RefreshGitAsync(request);
            else
            {
                git = snapshot; gitLoading = false; gitError = null;
                RefreshGitPanels();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    private Control ReviewPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12, Name = "ReviewPanel" };
        panel.Children.Add(Ui.Text("Repository review", Ui.TextBrush, 16));
        panel.Children.Add(Ui.Text(git.IsRepository ? $"{git.Changes.Count} changed files on {git.Branch}" : "No repository selected"));
        panel.Children.Add(Ui.Text("Select a file in Changes to inspect its diff.", Ui.Hint, 12));
        panel.Children.Add(Ui.Button("Show Changes", () =>
        {
            var group = Layout.State.Groups.FirstOrDefault(item => item.Tools.Any(tab => tab.Id == "changes"));
            if (group is null) Layout.RestoreTool("changes"); else Layout.Select(group.Id, "changes");
        }, "fileDiff"));
        return panel;
    }
}