using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

using Tereditor.Host.Abstractions;
using Tereditor.UI;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

using ThemeCatalog = Tereditor.UI.Rendering.Themes;

namespace Tereditor.Checks.E2E;

internal static class BranchListE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "branch-list-git"));
        GuardsDeletion(Path.Combine(root, "branch-list"));
        Fetches(Path.Combine(root, "branch-list-fetch"));
        ReconcilesInPlace(Path.Combine(root, "branch-list-in-place"));
        CompactList(Path.Combine(root, "branch-list-compact"));
    }

    /// <summary>
    /// The card keeps its header compact and gives the branch list the rest of its height: rows stay within one or two compact lines, six
    /// or more are visible at 800 px with ten branches, the list is over half the card, and the card stays inside the window.
    /// </summary>
    private static void CompactList(string directory)
    {
        using var git = new IsolatedGit(directory + "-git");
        using var app = OpenFixtureProject(directory);
        CreateWorkspaceViaDialog(app);
        for (var index = 1; index < 9; index++) Git(app.Root, "branch", "extra-" + index);
        foreach (var height in new[] { 600, 800, 1000 })
        {
            app.Window.Height = height; Settle(300); app.Window.UpdateLayout();
            var list = Open(app);
            Until(() => Rows(list).Length >= 10);
            var card = list.GetLogicalAncestors().OfType<Border>().First(border => border.Name == "ScopeBranchCard");
            var scroll = list.GetLogicalDescendants().OfType<ScrollViewer>().Single();
            Until(() => card.Bounds.Height > 0 && scroll.Bounds.Height > 0);
            Settle(100);
            foreach (var row in Rows(list))
            {
                var budget = row.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "BranchWorktree") ? WorkbenchWindow.TwoLineRow : WorkbenchWindow.OneLineRow;
                Require(row.Bounds.Height <= budget + 0.5, $"A branch row is {row.Bounds.Height:0.#} tall at window height {height}, over its {budget} budget.");
            }
            var visible = Rows(list).Count(row => row.TranslatePoint(new Point(0, row.Bounds.Height), scroll) is { } bottom && bottom.Y <= scroll.Bounds.Height + 0.5);
            if (height == 800) Require(visible >= 6, $"Only {visible} branch rows are fully visible at window height 800.");
            Require(scroll.Bounds.Height > card.Bounds.Height / 2, $"The list is {scroll.Bounds.Height:0.#} of a {card.Bounds.Height:0.#} card at window height {height}.");
            var top = card.TranslatePoint(default, app.Window)!.Value.Y;
            Require(top >= 0 && top + card.Bounds.Height <= app.Window.Bounds.Height + 0.5, $"The branch card leaves the window at height {height}.");
            Close(app);
        }
        Console.WriteLine("PASS branch card: compact rows, the list takes the card's height, inside the window at 600, 800 and 1000");
    }

    private static string[] Names(StackPanel rows) => [.. rows.Children.OfType<Grid>().Select(row => (string)row.Tag!)];

    /// <summary>
    /// Branch rows are reconciled by name, panes keep their instances through a theme switch, and 50 small updates leave
    /// exactly the model's rows alive.
    /// </summary>
    private static void ReconcilesInPlace(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var window = app.Window;
        var rows = window.BranchRowPanel;
        string[] first = ["main", "alpha", "beta", "gamma"];
        window.RenderBranches(first);
        var kept = rows.Children.ToArray();
        Require(Names(rows).SequenceEqual(first), "The branch list must show the model's branches in order.");
        window.RenderBranches(["main", "beta", "alpha", "delta"]);
        Require(Names(rows).SequenceEqual(["main", "beta", "alpha", "delta"]) && ReferenceEquals(rows.Children[0], kept[0]) &&
            ReferenceEquals(rows.Children[1], kept[2]) && ReferenceEquals(rows.Children[2], kept[1]),
            "Kept branches must keep their row instances when others are added, removed or moved.");
        var removed = new List<WeakReference> { new(kept[3]) };
        kept = null;
        for (var index = 0; index < 50; index++)
        {
            var name = "temp-" + index;
            window.RenderBranches(["main", "beta", "alpha", name]);
            removed.Add(new WeakReference(rows.Children[3]));
        }
        window.RenderBranches(["main", "beta", "alpha"]);
        var fresh = app.Window.BranchRowPanel.Children.Select(row => (Grid)row).ToArray();
        Require(fresh.Length == 3 && Names(rows).SequenceEqual(["main", "beta", "alpha"]), "The rows must equal the model after 50 updates.");
        Require(fresh[0].GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "BranchWorktree"),
            "The checked-out branch must keep showing its worktree after in-place updates.");
        for (var round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); Settle(50); }
        var live = removed.Count(reference => reference.IsAlive);
        Require(live <= 2, $"Removed branch rows must be released ({live} of {removed.Count} alive).");

        app.ShowTool("files");
        window.SetFilesMode(true);
        Until(() => app.Window.GetLogicalDescendants().OfType<Grid>().Any(panel => panel.Name == "ChangesPanel"));
        Control Find(string name) => app.Window.GetLogicalDescendants().OfType<Control>().First(control => control.Name == name);
        Control[] panes = [Find("ProjectsPanel"), Find("FilesPanel"), Find("ChangesPanel")];
        window.Preferences.ThemeMode = "fixed";
        var original = window.Preferences.Theme;
        var other = ThemeCatalog.All.First(theme => theme.Id != original && theme.IsLight != Ui.Theme.IsLight).Id;
        window.Preferences.Theme = other;
        window.RefreshAppearance();
        Settle(200);
        Require(Ui.Theme.Id == other, "The theme switch must apply.");
        Require(ReferenceEquals(panes[0], Find("ProjectsPanel")) && ReferenceEquals(panes[1], Find("FilesPanel")) && ReferenceEquals(panes[2], Find("ChangesPanel")),
            "A theme switch must keep the rail, Files and Changes instances.");
        window.Preferences.Theme = original;
        window.RefreshAppearance();
        Settle(200);
        Require(ReferenceEquals(panes[1], Find("FilesPanel")), "Switching back must keep the panes too.");
        Console.WriteLine("PASS branch list, rail, Files and Changes update in place; theme switch keeps their instances");
    }

    private static Grid Open(E2eWorkspace app)
    {
        var button = app.Find<Button>("ScopeBranch");
        app.Click(button);
        Until(() => button.Flyout is Flyout { IsOpen: true });
        var list = ((Control)((Flyout)button.Flyout!).Content!).GetLogicalDescendants().OfType<Grid>().Single(panel => panel.Name == "BranchList");
        Until(() => Rows(list).Length > 0);
        return list;
    }

    private static void Close(E2eWorkspace app)
    {
        ((Flyout)app.Find<Button>("ScopeBranch").Flyout!).Hide();
        Settle(100);
    }

    private static Grid[] Rows(Grid list) => list.GetLogicalDescendants().OfType<Grid>().Where(row => row.Name == "BranchRow").ToArray();
    private static Grid? Row(Grid list, string name) => Rows(list).SingleOrDefault(row => Equals(row.Tag, name));

    private static void GuardsDeletion(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var branch = Path.GetFileName(workspace);
        Git(app.Root, "branch", "spare-branch");
        // Two commits only this branch has, made without checking anything out.
        var tree = Git(app.Root, "rev-parse", "HEAD^{tree}");
        var first = Git(app.Root, "commit-tree", tree, "-p", "HEAD", "-m", "unmerged one");
        Git(app.Root, "branch", "unmerged-branch", Git(app.Root, "commit-tree", tree, "-p", first, "-m", "unmerged two"));

        var list = Open(app);
        var mine = Row(list, branch)!;
        Require(ToolTip.GetTip(Named<TextBlock>(mine, "BranchWorktree")) is string full && full.Contains("worktrees", StringComparison.Ordinal),
            "The workspace's branch must show where it is checked out.");
        Require(!Named<Button>(mine, "BranchDelete").IsEnabled && ToolTip.GetTip(Named<Button>(mine, "BranchDelete")) is string,
            "The workspace's branch must refuse deletion and say why.");
        var main = Row(list, "main")!;
        Require(!Named<Button>(main, "BranchDelete").IsEnabled, "The checked-out branch must refuse deletion.");

        var spare = Row(list, "spare-branch")!;
        var variant = app.Window.RequestedThemeVariant;
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            app.Window.RequestedThemeVariant = theme;
            Settle(100);
            foreach (var branchRow in new[] { mine, main, spare })
            {
                var delete = Named<Button>(branchRow, "BranchDelete");
                var presenter = delete.GetVisualDescendants().OfType<ContentPresenter>().Single(item => item.Name == "PART_ContentPresenter");
                Require(presenter.Background is ISolidColorBrush { Color.A: 0 },
                    "Idle and disabled branch delete buttons must have transparent backgrounds in both themes.");
                Require(delete.IsEnabled || delete.Content is Border { Background: ISolidColorBrush disabled } && disabled.Color == Ui.ControlDisabledText.Color,
                    "A disabled branch delete icon must use the theme's disabled text color.");
            }
        }
        app.Window.RequestedThemeVariant = variant;
        Require(!spare.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "BranchWorktree"),
            "A branch nothing has checked out has no path.");
        // The shared flow's refusals and wording, from the model alone: the checked-out branch, a branch a workspace holds, the default branch.
        var catalog = Task.Run(async () => await app.Host.ListBranchesAsync(false)).GetAwaiter().GetResult();
        var trees = app.Window.Worktrees;
        var spareTip = Git(app.Root, "rev-parse", "spare-branch");
        Require(WorkbenchWindow.PlanBranchDeletion("main", catalog, trees).Refusal is { } checkedOut && checkedOut.Contains("currently checked out", StringComparison.Ordinal) &&
            WorkbenchWindow.PlanBranchDeletion(branch, catalog, trees).Refusal is { } held && held.Contains("workspace", StringComparison.Ordinal) &&
            WorkbenchWindow.PlanBranchDeletion("gone", catalog, trees).Refusal is not null,
            "The shared flow must refuse the checked-out branch, a workspace's branch and an unknown one, each with a message.");
        var other = new BranchCatalog(["main", "dev"], [], "main");
        Require(WorkbenchWindow.PlanBranchDeletion("main", other, [new("/p", "dev", true, false)]).Refusal?.Contains("default branch", StringComparison.Ordinal) == true,
            "The shared flow must refuse the project's default branch.");
        var merged = WorkbenchWindow.PlanBranchDeletion("spare-branch", catalog, trees);
        var unmerged = WorkbenchWindow.PlanBranchDeletion("unmerged-branch", catalog, trees);
        Require(merged.Refusal is null && merged.Sha == spareTip && merged.Text.Contains(spareTip[..7], StringComparison.Ordinal) && merged.Text.Contains("already contained", StringComparison.Ordinal) &&
            merged.ConfirmLabel == "Delete branch", "A merged branch must get the plain confirmation naming its tip.");
        Require(unmerged.Refusal is null && unmerged.Text.Contains("2 commits", StringComparison.Ordinal) && unmerged.Text.Contains(unmerged.Sha[..7], StringComparison.Ordinal) &&
            unmerged.ConfirmLabel == "Delete unmerged branch", "An unmerged branch must count its commits and label the button differently.");

        void DeleteThroughList(string name, string label)
        {
            app.Click(Named<Button>(Row(list, name)!, "BranchDelete"));
            var dialog = Dialog(app);
            Require(Named<Button>(dialog, "BranchDeleteConfirm").GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == label), "The confirmation button must read " + label + ".");
            // A real window loses focus to the dialog and dismisses the card; the headless one has to be told.
            Close(app);
            app.Click(Named<Button>(dialog, "BranchDeleteConfirm"));
            Until(() => !Tereditor.Plugins.UI.Kit.DialogWindow.On(app.Window).Any(window => window is Tereditor.Plugins.UI.Kit.DialogWindow { InOverlay: true } || window.IsVisible));
            Until(() => Git(app.Root, "branch", "--list", name).Length == 0);
        }
        var oldTip = Git(app.Root, "rev-parse", "unmerged-branch");
        DeleteThroughList("unmerged-branch", "Delete unmerged branch");
        Until(() => app.Window.Toasts.Items.Any(toast => toast.Message.Contains(oldTip[..7], StringComparison.Ordinal) && toast.Action?.Label == "Undo"));
        var undo = app.Window.Toasts.Items.Single(toast => toast.Action?.Label == "Undo").Action!;
        Until(() => app.Find<Button>("ScopeBranch").Flyout is Flyout { IsOpen: true });
        undo.Invoke();
        Until(() => Git(app.Root, "branch", "--list", "unmerged-branch").Length > 0);
        Require(Git(app.Root, "rev-parse", "unmerged-branch") == oldTip, "Undo must recreate the branch at its old tip.");

        app.Click(Named<Button>(spare, "BranchDelete"));
        var confirm = Dialog(app);
        // A real window loses focus to the dialog and dismisses the card; the headless one has to be told.
        Close(app);
        app.Click(Named<Button>(confirm, "BranchDeleteConfirm"));
        Until(() => !Tereditor.Plugins.UI.Kit.DialogWindow.On(app.Window).Any(window => window is Tereditor.Plugins.UI.Kit.DialogWindow { InOverlay: true } || window.IsVisible));
        Until(() => Git(app.Root, "branch", "--list", "spare-branch").Length == 0);
        Until(() => Row(list, "spare-branch") is null && Row(list, branch) is not null);
        Until(() => app.Find<Button>("ScopeBranch").Flyout is Flyout { IsOpen: true });
        Require(ReferenceEquals(list.GetLogicalAncestors().OfType<Border>().FirstOrDefault(card => card.Name == "ScopeBranchCard"),
            ((Flyout)app.Find<Button>("ScopeBranch").Flyout!).Content), "The branch card must come back after a deletion, so the next branch can be deleted.");
        Close(app);

        // The host refuses on its own, whichever client asks.
        var refused = false;
        try { Task.Run(async () => await app.Host.ApplyGitActionAsync(new("delete-branch", "", branch))).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused && Git(app.Root, "branch", "--list", branch).Length > 0, "The host must refuse to delete a workspace's branch.");
        Console.WriteLine("PASS upstream branch-list.spec.ts: the branch chip lists branches with their worktrees, and guards deletion");
    }

    private static void Fetches(string directory)
    {
        using var app = OpenFixtureProject(directory);
        CreateWorkspaceViaDialog(app);
        var origin = Path.Combine(directory, "origin.git");
        var writer = Path.Combine(directory, "writer");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "--bare", "-q", "-b", "main");
        Git(app.Root, "remote", "add", "fetch-origin", origin);
        Git(app.Root, "push", "-q", "fetch-origin", "main");
        Git(directory, "clone", "-q", origin, writer);
        Git(writer, "commit", "--allow-empty", "-q", "-m", "written on the remote");
        Git(writer, "push", "-q", "origin", "main");
        var before = Git(app.Root, "rev-parse", "fetch-origin/main");

        var list = Open(app);
        app.Click(Named<Button>(list, "BranchFetch"));
        Until(() => Git(app.Root, "rev-parse", "fetch-origin/main") != before);
        Until(() => Rows(list).Length > 0);
        Close(app);
        Console.WriteLine("PASS upstream branch-list.spec.ts: Fetch brings the remotes up to date from the branch list");
    }
}