using Avalonia.Controls;
using Avalonia.LogicalTree;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class BranchListE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "branch-list-git"));
        GuardsDeletion(Path.Combine(root, "branch-list"));
        Fetches(Path.Combine(root, "branch-list-fetch"));
    }

    private static StackPanel Open(E2eWorkspace app)
    {
        var button = app.Find<Button>("BranchButton");
        app.Click(button);
        Until(() => button.Flyout is Flyout { IsOpen: true });
        var list = (StackPanel)((Flyout)button.Flyout!).Content!;
        Until(() => Rows(list).Length > 0);
        return list;
    }

    private static void Close(E2eWorkspace app)
    {
        ((Flyout)app.Find<Button>("BranchButton").Flyout!).Hide();
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
        Require(!spare.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "BranchWorktree"),
            "A branch nothing has checked out has no path.");
        app.Click(Named<Button>(spare, "BranchDelete"));
        var confirm = Dialog(app);
        app.Click(Named<Button>(confirm, "BranchDeleteConfirm"));
        Until(() => !app.Window.OwnedWindows.Any(window => window.IsVisible));
        Until(() => Git(app.Root, "branch", "--list", "spare-branch").Length == 0);
        Until(() => Row(list, "spare-branch") is null && Row(list, branch) is not null);
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