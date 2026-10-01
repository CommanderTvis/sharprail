using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private StackPanel? branchList;
    private readonly StackPanel branchRows = new() { Name = "BranchRows", Spacing = 2 };

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
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Ui.Place(header, title); Ui.Place(header, fetch, 0, 1);
        var list = new StackPanel { Name = "BranchList", Spacing = 8, Width = 380, Margin = new Thickness(4) };
        list.Children.Add(header);
        list.Children.Add(new ScrollViewer { Content = branchRows, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
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

    private void RenderBranches(IReadOnlyList<string> local)
    {
        branchRows.Children.Clear();
        foreach (var name in local)
        {
            var holder = git.Worktrees.FirstOrDefault(tree => tree.Branch == name);
            var row = new Grid { Name = "BranchRow", Tag = name, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 2) };
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.Text(name, Ui.TextBrush));
            if (holder is not null)
            {
                var path = Ui.Text(holder.Path, Ui.Hint, 12);
                path.Name = "BranchWorktree"; path.TextTrimming = Avalonia.Media.TextTrimming.PrefixCharacterEllipsis;
                text.Children.Add(path);
            }
            Ui.Place(row, text);
            var delete = Ui.IconButton("trash", "Delete branch", () => _ = DeleteBranchAsync(name));
            delete.Name = "BranchDelete"; delete.Width = delete.Height = 28;
            if (holder is not null)
            {
                // The host refuses these too; the reason lives on the control so nobody has to try to find out.
                delete.IsEnabled = false;
                ToolTip.SetShowOnDisabled(delete, true);
                ToolTip.SetTip(delete, holder.IsMain ? "The branch currently checked out can't be deleted" : "A workspace lives on this branch");
            }
            AutomationProperties.SetName(delete, "Delete " + name);
            Ui.Place(row, delete, 0, 1);
            branchRows.Children.Add(row);
        }
    }

    private async Task DeleteBranchAsync(string name)
    {
        if (!await Dialogs.Confirm(this, $"Delete branch {name}?",
            "Commits that only this branch points at will no longer be reachable from any branch.", "Delete branch", "BranchDeleteConfirm")) return;
        await GitActionAsync(new("delete-branch", "", name));
        await LoadBranchesAsync();
    }

    private async Task FetchBranchesAsync()
    {
        await GitActionAsync(new("fetch"));
        await LoadBranchesAsync();
    }
}
