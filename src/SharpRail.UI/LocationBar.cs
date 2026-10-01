using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

/// <summary>
/// Lays the header's Project, Workspace and Branch segments in a row. As width shrinks the branch yields
/// first, then the project; a segment too narrow to read is dropped, and the workspace takes what is left.
/// </summary>
public sealed class LocationStrip : Panel
{
    private const double Least = 72;
    private static readonly int[] YieldOrder = [2, 0];
    private double[] widths = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        widths = new double[Children.Count];
        for (var index = 0; index < widths.Length; index++)
        {
            Children[index].Measure(availableSize.WithWidth(double.PositiveInfinity));
            widths[index] = Children[index].DesiredSize.Width;
        }
        var over = widths.Sum() - availableSize.Width;
        foreach (var index in YieldOrder)
        {
            if (over <= 0 || index >= widths.Length) continue;
            var kept = widths[index] - over >= Least ? widths[index] - over : 0;
            over -= widths[index] - kept;
            widths[index] = kept;
        }
        if (over > 0 && widths.Length > 1) widths[1] = Math.Max(0, widths[1] - over);
        var height = 0.0;
        for (var index = 0; index < widths.Length; index++)
        {
            Children[index].Measure(availableSize.WithWidth(widths[index]));
            height = Math.Max(height, Children[index].DesiredSize.Height);
        }
        return new Size(widths.Sum(), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        for (var index = 0; index < Children.Count; index++)
        {
            Children[index].Arrange(new Rect(x, 0, widths[index], finalSize.Height));
            x += widths[index];
        }
        return finalSize;
    }
}

/// <summary>The header's captioned location segments: project and workspace switchers and the branch card.</summary>
public sealed partial class WorkbenchWindow
{
    private readonly Flyout branchCard = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    // The header and the Projects rail share one rename; this says which of them shows its input.
    private bool renameInHeader;

    /// <summary>Raised after the window lands on another project, workspace or Project Home.</summary>
    private event Action? LocationChanged;

    private void WireLocationBar()
    {
        foreach (var name in new[] { "ProjectChevron", "WorkspaceChevron", "BranchChevron" })
            this.FindControl<ContentControl>(name)!.Content = Ui.Icon("arrowDown", Ui.Hint, 14);
        Switcher("ScopeProject", FillProjectSwitcher);
        Switcher("ScopeWorkspace", FillWorkspaceSwitcher);
        branchCard.Opening += (_, _) => branchCard.Content = BranchCard();
        // Focus lands on the card, not its first button, so opening it shows no tooltip.
        branchCard.Opened += (_, _) => (branchCard.Content as Control)?.Focus();
        this.FindControl<Button>("ScopeBranch")!.Flyout = branchCard;
    }

    private void Switcher(string name, Action<ContextMenu> fill)
    {
        var pill = this.FindControl<Button>(name)!;
        var menu = new ContextMenu { Name = name + "Menu", Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = pill };
        void Fill() { menu.Items.Clear(); fill(menu); }
        menu.Opening += (_, _) => Fill();
        pill.ContextMenu = menu;
        pill.Click += (_, _) => { Fill(); menu.Open(pill); };
    }

    private static MenuItem MenuLabel(string text) => new() { Header = Ui.Text(text, Ui.Hint, 12), IsEnabled = false };

    private void FillProjectSwitcher(ContextMenu menu)
    {
        menu.Items.Add(MenuLabel("Projects"));
        foreach (var project in state.Current.Projects)
        {
            var item = Ui.Menu(DirectoryName(project), () => _ = OpenProjectHomeAsync(project));
            item.Name = "ScopeProjectOption"; item.Tag = project;
            item.ToggleType = MenuItemToggleType.Radio; item.IsChecked = project == projectRoot;
            ToolTip.SetTip(item, project);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var home = Ui.Menu("Project home", () => _ = OpenProjectHomeAsync(projectRoot), projectRoot.Length > 0 && !atHome);
        home.Name = "ScopeProjectHome"; home.Icon = Ui.Icon("homeFill", null, 14);
        menu.Items.Add(home);
        var add = Ui.Menu("Add project…", () => _ = PickProjectAsync());
        add.Name = "ScopeProjectAdd"; add.Icon = Ui.Icon("add", null, 14);
        menu.Items.Add(add);
    }

    private void FillWorkspaceSwitcher(ContextMenu menu)
    {
        // The same rows as the Projects rail: the host's registry, with the folder standing in until it is listed.
        var worktrees = RailWorkspaces(projectRoot);
        var active = atHome ? null : worktrees.FirstOrDefault(tree => tree.Path == workspaceRoot)
            ?? new WorkspaceRecord("", projectRoot, workspaceRoot == projectRoot ? WorkspaceKinds.Default : WorkspaceKinds.Managed, workspaceRoot, branchLabel.Text ?? "", "");
        if (active is not null)
        {
            menu.Items.Add(MenuLabel(WorkspaceName(active.Path)));
            _ = LoadEditorsAsync(AddWorkspaceActions(menu, active, header: true), active.Path);
            var link = Ui.Menu("Copy link", () => _ = Clipboard?.SetTextAsync(Location.Serialize()));
            link.Name = "ScopeCopyLink";
            menu.Items.Insert(menu.Items.IndexOf(menu.Items.OfType<MenuItem>().Single(item => item.Name == "WorkspaceCopyPath")) + 1, link);
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(MenuLabel("Switch to"));
        var others = worktrees.Where(tree => tree.Path != active?.Path).ToArray();
        if (others.Length == 0)
            menu.Items.Add(new MenuItem { Header = gitLoading ? "Loading…" : "No other workspaces", IsEnabled = false });
        foreach (var worktree in others)
        {
            var name = WorkspaceName(worktree.Path);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Ui.Text(name, Ui.TextBrush) } };
            if (worktree.Branch.Length > 0 && worktree.Branch != name)
                label.Children.Add(new TextBlock { Text = worktree.Branch, Foreground = Ui.Hint, FontSize = 12, FontFamily = Ui.CodeFont, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem
            {
                Name = "ScopeWorkspaceOption",
                Tag = worktree.Path,
                Header = label,
                Icon = Ui.Icon(worktree.Kind switch { WorkspaceKinds.Default => "homeFill", WorkspaceKinds.External => "folderOpen", _ => "gitBranch" }, null, 14)
            };
            item.Click += (_, _) => _ = OpenWorkspaceAsync(worktree.Path, false);
            AutomationProperties.SetName(item, name);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var create = Ui.Menu("New workspace", () => _ = CreateWorkspaceDialogAsync(), git.IsRepository);
        create.Name = "ScopeWorkspaceNew"; create.Icon = Ui.Icon("add", null, 14);
        create.InputGesture = new KeyGesture(Key.N, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        menu.Items.Add(create);
    }

    private void SetBranch(string branch)
    {
        branchLabel.Text = branch; branchIcon.IsVisible = branch.Length > 0;
        UpdateBranchSegment();
    }

    private void UpdateBranchSegment()
    {
        var branch = branchLabel.Text ?? "";
        this.FindControl<Border>("BranchSegment")!.IsVisible = projectRoot.Length > 0 && !atHome && branch.Length > 0;
        AutomationProperties.SetName(this.FindControl<Button>("ScopeBranch")!, "Branch " + branch);
        // A managed workspace names what it was cut from; the Default workspace is plain "BRANCH".
        this.FindControl<TextBlock>("ScopeBase")!.Text = workspaceRoot != projectRoot && comparison.Length > 0 ? "· from " + comparison : "";
    }

    private Border BranchCard()
    {
        var branch = branchLabel.Text ?? "";
        var name = new TextBlock { Text = branch, FontFamily = Ui.CodeFont, Foreground = Ui.TextBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        var copy = Ui.Button("Copy", () => _ = CopyBranchAsync(branch));
        copy.Name = "ScopeBranchCopy"; copy.Padding = new(8, 2);
        ToolTip.SetTip(copy, "Copy branch name"); AutomationProperties.SetName(copy, "Copy branch name");
        var rows = new StackPanel { Children = { CardRow("Branch", name, copy), CardRow("Compare to", ComparisonPicker("ScopeDiffBase", "")) } };
        if (branchList is not null)
        {
            (branchList.Parent as Panel)?.Children.Remove(branchList);
            rows.Children.Add(branchList);
        }
        var card = new Border { Name = "ScopeBranchCard", Width = 400, Child = rows, Focusable = true };
        AutomationProperties.SetName(card, "Branch " + branch);
        return card;
    }

    private static Grid CardRow(string label, Control value, Control? action = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("88,*,Auto"), MinHeight = 28 };
        Ui.Place(row, Ui.Text(label, Ui.Hint, 12));
        value.HorizontalAlignment = HorizontalAlignment.Left;
        Ui.Place(row, value, 0, 1);
        if (action is not null) Ui.Place(row, action, 0, 2);
        return row;
    }

    private async Task CopyBranchAsync(string branch)
    {
        if (Clipboard is null) return;
        await Clipboard.SetTextAsync(branch);
        ShowNotification($"Copied {branch}");
    }

    /// <summary>The comparison changed through either picker: an open card shows the new target.</summary>
    private void RefreshBranchCard() => Dispatcher.UIThread.Post(() =>
    {
        UpdateBranchSegment();
        if (branchCard.IsOpen) branchCard.Content = BranchCard();
    });
}