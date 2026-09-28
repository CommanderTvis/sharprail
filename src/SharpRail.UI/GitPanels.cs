using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private bool changeTree;

    private Control ChangesPanel()
    {
        var panel = new Grid { Name = "ChangesPanel", RowDefinitions = new RowDefinitions("32,*") };
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,4,Auto,4,Auto"),
            Margin = new Thickness(12, 0),
            ClipToBounds = true
        };
        var selectors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, ClipToBounds = true };
        var scope = ChangesDropdown("ChangesScope", "Diff scope", changeScope, "fileDiff");
        foreach (var label in new[] { "All changes", "Uncommitted", "Staged", "Branch" })
        {
            var item = Ui.Menu(label, () =>
            {
                changeScope = label;
                comparison = label == "Branch" ? git.Branches.FirstOrDefault(branch => branch != git.Branch) ?? "HEAD" : "";
                _ = RefreshAsync();
            });
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = changeScope == label;
            scope.ContextMenu!.Items.Add(item);
        }
        selectors.Children.Add(scope);
        if (git.IsRepository)
        {
            var branches = ChangesDropdown("ChangesBranch", "Comparison branch", "vs " + (comparison.Length > 0 ? comparison : git.Branch), "gitBranch");
            branches.MaxWidth = 200;
            foreach (var branch in git.Branches)
            {
                var item = Ui.Menu(branch, () => { changeScope = "Branch"; comparison = branch; _ = RefreshAsync(); });
                item.ToggleType = MenuItemToggleType.Radio;
                item.IsChecked = comparison == branch;
                branches.ContextMenu!.Items.Add(item);
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
            "Uncommitted" => change.WorktreeStatus != " ",
            _ => true
        }).ToArray();
        if (gitLoading || gitError is not null)
        {
            Ui.Place(panel, Ui.Text(gitError ?? "Loading Git…", Ui.Hint, 12), 1); return panel;
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
        var path = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), HorizontalAlignment = HorizontalAlignment.Left };
        Ui.Place(path, Ui.Text(separator < 0 ? "" : label[..(separator + 1)], Ui.Muted));
        var filename = Ui.Text(label[(separator + 1)..], color);
        Ui.Place(path, filename, 0, 1);
        Ui.Place(row, path);
        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        numbers.Children.Add(Ui.Text("+" + change.Added, Ui.Success, 12));
        numbers.Children.Add(Ui.Text("−" + change.Removed, Ui.Danger, 12));
        Ui.Place(row, numbers, 0, 2);
        row.SizeChanged += (_, _) => filename.MaxWidth = Math.Max(0, row.Bounds.Width - numbers.DesiredSize.Width - 8);
        var button = new Button
        {
            Content = row,
            Padding = new Thickness(4, 0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(button, change.Path);
        ToolTip.SetTip(button, change.OriginalPath is null ? $"{change.Path}  [{change.IndexStatus}{change.WorktreeStatus}]" : $"{change.OriginalPath} → {change.Path}");
        button.Click += (_, _) => _ = OpenDiffAsync(change);
        button.ContextMenu = new ContextMenu();
        button.ContextMenu.Items.Add(Ui.Menu("Open diff", () => _ = OpenDiffAsync(change)));
        button.ContextMenu.Items.Add(Ui.Menu("Stage file", () => _ = GitActionAsync(new("stage", change.Path)), comparison.Length == 0 && change.WorktreeStatus != " "));
        button.ContextMenu.Items.Add(Ui.Menu("Unstage file", () => _ = GitActionAsync(new("unstage", change.Path)), comparison.Length == 0 && change.IndexStatus is not (" " or "?")));
        return button;
    }

    private async Task OpenDiffAsync(GitChange change)
    {
        var request = BeginNavigation();
        var scope = comparison.Length > 0 ? "branch" : change.IndexStatus == "?" ? "untracked" :
            changeScope == "Staged" || change.WorktreeStatus == " " ? "staged" : changeScope == "Uncommitted" ? "working" : "all";
        try
        {
            var diff = await host.GetDiffAsync(change.Path, scope, comparison, lifetime.Token);
            var destination = AcceptNavigation(request);
            if (destination is null) return;
            var tab = new DockTab($"diff:{scope}:{comparison}:{change.Path}", Path.GetFileName(change.Path) + " · Diff", "diff", change.Path, Scope: scope, Comparison: comparison);
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
            git = snapshot; gitLoading = false; gitError = null;
            errorText.IsVisible = false; RefreshGitPanels();
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    private async Task CreateWorktreeAsync()
    {
        var values = await Dialogs.Prompt(this, "Create worktree",
            ("Directory path", projectRoot + "-worktree"), ("New branch", ""), ("Base branch", git.Branch.Length > 0 ? git.Branch : "HEAD"));
        if (values is null) return;
        gitRefresh?.Cancel();
        try
        {
            git = await host.ApplyGitActionAsync(new("create-worktree", values[0], values[1], values[2]), lifetime.Token);
            await OpenWorkspaceAsync(values[0], false);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    private async Task RemoveWorktreeAsync(WorktreeInfo worktree)
    {
        if (!await Dialogs.Confirm(this, "Remove worktree?", $"Remove {worktree.Path}? Git will refuse if it has uncommitted changes. The branch will be retained.")) return;
        await GitActionAsync(new("remove-worktree", worktree.Path));
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
