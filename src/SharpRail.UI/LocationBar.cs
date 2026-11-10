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
/// Lays the header's Project, Branch and Pull request segments in a row. As width shrinks the Pull request
/// yields first, then the branch; a segment too narrow to read is dropped, and the project takes what is left.
/// </summary>
public sealed class LocationStrip : Panel
{
    private const double Least = 72;
    // The Pull request yields first, then the branch; the project keeps what is left.
    private static readonly int[] YieldOrder = [2, 1];
    private double[] widths = [];

    /// <summary>The width every segment wants, before any yields; the header weighs it against the room it has.</summary>
    public double NaturalWidth { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        widths = new double[Children.Count];
        for (var index = 0; index < widths.Length; index++)
        {
            Children[index].Measure(availableSize.WithWidth(double.PositiveInfinity));
            widths[index] = Children[index].DesiredSize.Width;
        }
        NaturalWidth = widths.Sum();
        var over = NaturalWidth - availableSize.Width;
        foreach (var index in YieldOrder)
        {
            if (over <= 0 || index >= widths.Length) continue;
            var kept = widths[index] - over >= Least ? widths[index] - over : 0;
            over -= widths[index] - kept;
            widths[index] = kept;
        }
        if (over > 0 && widths.Length > 0) widths[0] = Math.Max(0, widths[0] - over);
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

/// <summary>The header's captioned location segments: the project switcher and the branch card.</summary>
public sealed partial class WorkbenchWindow
{
    private readonly Flyout branchCard = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    /// <summary>Raised after the window lands on another project, workspace or Project Home.</summary>
    private event Action? LocationChanged;

    private void WireLocationBar()
    {
        foreach (var name in new[] { "ProjectChevron", "BranchChevron" })
            this.FindControl<ContentControl>(name)!.Content = Ui.Icon("arrowDown", Ui.Hint, 14);
        Switcher("ScopeProject", FillProjectSwitcher);
        var branch = this.FindControl<Button>("ScopeBranch")!;
        branchCard.Opening += (_, _) => { branchCard.Content = BranchCard(); branch.Classes.Set("open", true); };
        branchCard.Closed += (_, _) => branch.Classes.Set("open", false);
        // Focus lands on the card, not its first button, so opening it shows no tooltip.
        branchCard.Opened += (_, _) => (branchCard.Content as Control)?.Focus();
        branch.Flyout = branchCard;
    }

    private void Switcher(string name, Action<ContextMenu> fill)
    {
        var pill = this.FindControl<Button>(name)!;
        var menu = new ContextMenu { Name = name + "Menu", Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = pill };
        void Fill() { menu.Items.Clear(); fill(menu); }
        menu.Opening += (_, _) => Fill();
        // The pill stays lit, chevron shown, while its menu is open.
        menu.Opened += (_, _) => pill.Classes.Set("open", true);
        menu.Closed += (_, _) => pill.Classes.Set("open", false);
        pill.ContextMenu = menu;
        pill.Click += (_, _) => { Fill(); menu.Open(pill); };
    }

    /// <summary>The project tile's glyph: the name's first letter or digit, in capitals.</summary>
    private static string InitialOf(string name) =>
        name.FirstOrDefault(char.IsLetterOrDigit) is var first && first != default ? char.ToUpperInvariant(first).ToString() : "";

    private static MenuItem MenuLabel(string text) => new() { Header = Ui.Text(text, Ui.Hint, 12), IsEnabled = false };

    private void FillProjectSwitcher(ContextMenu menu)
    {
        menu.Items.Add(MenuLabel("Projects"));
        foreach (var project in state.Current.Projects)
        {
            var item = Ui.Menu(state.Current.ProjectName(project), () => _ = OpenProjectHomeAsync(project));
            item.Name = "ScopeProjectOption"; item.Tag = project;
            item.ToggleType = MenuItemToggleType.Radio; item.IsChecked = project == projectRoot;
            ToolTip.SetTip(item, project);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var home = Ui.IconMenu("Project home", () => _ = OpenProjectHomeAsync(projectRoot), ActionIcon.Home, projectRoot.Length > 0 && !atHome);
        home.Name = "ScopeProjectHome";
        menu.Items.Add(home);
    }

    internal void SetBranch(string branch)
    {
        if (branchLabel.Text != branch) branchLabel.Text = branch;
        branchLabel.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        branchIcon.IsVisible = branch.Length > 0;
        if (branch.Length > 0 && !atHome && workspaceRoot.Length > 0) lastBranches[workspaceRoot] = branch;
        UpdateBranchSegment();
    }

    private void UpdateBranchSegment()
    {
        var branch = branchLabel.Text ?? "";
        this.FindControl<Border>("BranchSegment")!.IsVisible = projectRoot.Length > 0 && !atHome && (branch.Length > 0 || ShipExpected);
        AutomationProperties.SetName(this.FindControl<Button>("ScopeBranch")!, "Branch " + branch);
        ToolTip.SetTip(branchLabel, branch.Length > 0 ? branch : null);
        // A managed workspace names what it was cut from; the Default workspace is plain "Branch".
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