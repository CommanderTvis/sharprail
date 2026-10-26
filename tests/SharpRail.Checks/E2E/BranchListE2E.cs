using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

using ThemeCatalog = SharpRail.UI.Rendering.Themes;

namespace SharpRail.Checks.E2E;

internal static class BranchListE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "branch-list-git"));
        GuardsDeletion(Path.Combine(root, "branch-list"));
        Fetches(Path.Combine(root, "branch-list-fetch"));
        ReconcilesInPlace(Path.Combine(root, "branch-list-in-place"));
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

    private static StackPanel Open(E2eWorkspace app)
    {
        var button = app.Find<Button>("ScopeBranch");
        app.Click(button);
        Until(() => button.Flyout is Flyout { IsOpen: true });
        var list = ((Control)((Flyout)button.Flyout!).Content!).GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "BranchList");
        Until(() => Rows(list).Length > 0);
        return list;
    }

    private static void Close(E2eWorkspace app)
    {
        ((Flyout)app.Find<Button>("ScopeBranch").Flyout!).Hide();
        Settle(100);
    }

    private static Grid[] Rows(StackPanel list) => list.GetLogicalDescendants().OfType<Grid>().Where(row => row.Name == "BranchRow").ToArray();
    private static Grid? Row(StackPanel list, string name) => Rows(list).SingleOrDefault(row => Equals(row.Tag, name));

    private static void GuardsDeletion(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var branch = Path.GetFileName(workspace);
        Git(app.Root, "branch", "spare-branch");

        var list = Open(app);
        var mine = Row(list, branch)!;
        Require(Named<TextBlock>(mine, "BranchWorktree").Text!.Contains("worktrees", StringComparison.Ordinal),
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
        app.Click(Named<Button>(spare, "BranchDelete"));
        var confirm = Dialog(app);
        // A real window loses focus to the dialog and dismisses the card; the headless one has to be told.
        Close(app);
        app.Click(Named<Button>(confirm, "BranchDeleteConfirm"));
        Until(() => !app.Window.OwnedWindows.Any(window => window.IsVisible));
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