using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;

using Tereditor.Host.Abstractions;
using Tereditor.UI.Panels;

namespace Tereditor.UI;

public sealed partial class WorkbenchWindow
{
    private Grid? branchList;
    private readonly StackPanel branchRows = new() { Name = "BranchRows" };
    internal StackPanel BranchRowPanel => branchRows;
    private readonly ScrollViewer branchScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    /// <summary>
    /// The topbar branch is a control: it lists the project's local branches with the worktree occupying each,
    /// so which ones are live workspaces is answered by looking rather than by remembering.
    /// </summary>
    private void WireBranchList()
    {
        var title = Ui.Text("BRANCHES", size: 12);
        title.VerticalAlignment = VerticalAlignment.Center;
        var fetch = Ui.Button("Fetch", () => _ = FetchBranchesAsync(), "refresh");
        fetch.Name = "BranchFetch";
        ToolTip.SetTip(fetch, "Bring every remote up to date; no local branch moves");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 4) };
        Ui.Place(header, title); Ui.Place(header, fetch, 0, 1);
        // The header keeps its natural height; the rows take whatever the popup leaves and scroll inside it.
        var list = new Grid { Name = "BranchList", RowDefinitions = new RowDefinitions("Auto,*"), Width = 380, Margin = new Thickness(LayoutRoles.RowInset) };
        Ui.Place(list, header);
        branchScroll.Content = branchRows;
        Ui.Place(list, branchScroll, 1);
        branchList = list;
        branchCard.Opened += (_, _) => _ = LoadBranchesAsync();
    }

    private async Task LoadBranchesAsync()
    {
        if (!WorkspaceMounted) return;
        var request = projectRequest;
        try
        {
            var catalog = await host.ListBranchesAsync(false, lifetime.Token);
            if (request != projectRequest) return;
            RenderBranches(catalog.Local);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    private sealed class BranchRowParts(Grid row, StackPanel text, Button delete)
    {
        public Grid Row { get; } = row;
        public StackPanel Text { get; } = text;
        public Button Delete { get; } = delete;
        public TextBlock? Worktree { get; set; }
    }

    private readonly Dictionary<string, BranchRowParts> branchParts = [];

    /// <summary>Reconciles the rows by branch name: kept rows are updated in place and reordered, only added or removed branches create or drop controls.</summary>
    internal void RenderBranches(IReadOnlyList<string> local)
    {
        foreach (var gone in branchParts.Keys.Where(name => !local.Contains(name)).ToArray())
        {
            branchRows.Children.Remove(branchParts[gone].Row);
            branchParts.Remove(gone);
        }
        for (var index = 0; index < local.Count; index++)
        {
            var name = local[index];
            var holder = git.Worktrees.FirstOrDefault(tree => tree.Branch == name);
            if (!branchParts.TryGetValue(name, out var parts)) branchParts[name] = parts = NewBranchRow(name);
            if (holder is not null)
            {
                if (parts.Worktree is null)
                {
                    var path = Ui.Text("", Ui.Hint, 12);
                    path.Name = "BranchWorktree";
                    parts.Text.Children.Add(path);
                    parts.Worktree = path;
                    parts.Row.MinHeight = TwoLineRow;
                }
                if (!Equals(ToolTip.GetTip(parts.Worktree), ScopedSetting.AbbreviateHomePath(holder.Path)))
                {
                    parts.Worktree.Text = PathLine.Label(holder.Path);
                    ToolTip.SetTip(parts.Worktree, ScopedSetting.AbbreviateHomePath(holder.Path));
                }
            }
            else if (parts.Worktree is not null)
            {
                parts.Text.Children.Remove(parts.Worktree);
                parts.Worktree = null;
                parts.Row.MinHeight = OneLineRow;
            }
            // The host refuses these too; the reason lives on the control so nobody has to try to find out.
            parts.Delete.IsEnabled = holder is null;
            ToolTip.SetShowOnDisabled(parts.Delete, holder is not null);
            ToolTip.SetTip(parts.Delete, holder is null ? "Delete branch" : holder.IsMain ? "The branch currently checked out can't be deleted" : "A workspace lives on this branch");
            if (index < branchRows.Children.Count && ReferenceEquals(branchRows.Children[index], parts.Row)) continue;
            branchRows.Children.Remove(parts.Row);
            branchRows.Children.Insert(index, parts.Row);
        }
    }

    /// <summary>A branch row is one text line, or two when a workspace occupies it; the card's rows share these heights.</summary>
    internal const double OneLineRow = LayoutRoles.PanelHeaderRow, TwoLineRow = 40;

    private BranchRowParts NewBranchRow(string name)
    {
        var row = new Grid { Name = "BranchRow", Tag = name, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(LayoutRoles.RowInset, 0), MinHeight = OneLineRow };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(name, Ui.TextBrush));
        Ui.Place(row, text);
        var delete = Ui.IconButton("trash", "Delete branch", () => _ = DeleteBranchAsync(name));
        delete.Name = "BranchDelete"; delete.Width = delete.Height = LayoutRoles.PanelHeaderControl;
        AutomationProperties.SetName(delete, "Delete " + name);
        Ui.Place(row, delete, 0, 1);
        return new BranchRowParts(row, text, delete);
    }

    private async Task DeleteBranchAsync(string name)
    {
        // The card is a popup window that would cover the dialog, so it yields first and comes back where it was, so several
        // branches can be deleted in a row.
        var offset = branchScroll.Offset;
        branchCard.Hide();
        await DeleteBranchWithConfirmationAsync(name);
        if (!branchCard.IsOpen && WorkspaceMounted) branchCard.ShowAt(this.FindControl<Button>("ScopeBranch")!);
        await LoadBranchesAsync();
        Dispatcher.UIThread.Post(() => branchScroll.Offset = offset, DispatcherPriority.Loaded);
    }

    private async Task FetchBranchesAsync()
    {
        await GitActionAsync(new("fetch"));
        await LoadBranchesAsync();
    }
}