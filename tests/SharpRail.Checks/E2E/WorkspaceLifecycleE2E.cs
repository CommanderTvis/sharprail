using Avalonia.Controls;
using Avalonia.LogicalTree;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class WorkspaceLifecycleE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "workspace-lifecycle-git"));
        CreateRemoveRecreate(Path.Combine(root, "workspaces-recreate"));
        RemoveActive(Path.Combine(root, "lifecycle-remove-active"));
        RemoveDirty(Path.Combine(root, "lifecycle-remove-dirty"));
    }

    private static void RemoveDirty(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var path = CreateWorkspaceViaDialog(app);
        var branch = Git(path, "branch", "--show-current").Trim();
        var dirty = Path.Combine(path, "unsaved.txt");
        File.WriteAllText(dirty, "keep until approved\n");
        RemoveWorkspace(app, path);
        var confirm = Dialog(app);
        Require(confirm.Title == "Delete uncommitted changes?" && Text(confirm).Contains("permanently loses", StringComparison.Ordinal),
            "Dirty removal must explain the loss before offering force deletion.");
        var cancel = confirm.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Cancel" });
        Require(cancel.IsFocused && !Named<Button>(confirm, "WorkspaceForceRemove").IsDefault, "Cancel owns focus; Delete anyway is never an implicit default.");
        app.Click(cancel);
        Until(() => !app.Window.OwnedWindows.Any());
        Require(File.ReadAllText(dirty) == "keep until approved\n" && WorktreePaths(app).Contains(path), "Cancelling preserves dirty files and the workspace row.");
        RemoveWorkspace(app, path);
        confirm = Dialog(app);
        Require(confirm.Title == "Delete uncommitted changes?", "A second removal must ask again.");
        app.Click(Named<Button>(confirm, "WorkspaceForceRemove"));
        Until(() => !Directory.Exists(path) && !WorktreePaths(app).Contains(path));
        Require(Git(app.Root, "branch", "--list", branch).Length > 0, "Force deletion retains the committed branch.");
        Console.WriteLine("PASS dirty worktree deletion: Cancel preserves files; explicit Delete anyway removes checkout and keeps branch");
    }

    private static int WorktreeCount(string project)
    {
        try { return Git(project, "worktree", "list").Split('\n').Length; }
        catch (InvalidOperationException) { return -1; }
    }

    private static void CreateRemoveRecreate(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var first = CreateWorkspaceViaDialog(app);
        Require(WorktreePaths(app).Count() == 1, "One worktree row after creation.");
        var list = Git(app.Root, "worktree", "list");
        Require(WorktreeCount(app.Root) >= 2 && list.Contains("/worktrees/sample-project-", StringComparison.Ordinal),
            "Creating a workspace must add a Git worktree.");
        RemoveWorkspace(app, first);
        Until(() => !WorktreePaths(app).Any() && app.Window.AtProjectHome && HasWelcome(app));
        Require(app.Tabs.Count == 0, "Removing the only workspace returns to Project Home.");
        Until(() => WorktreeCount(app.Root) == 1);
        var second = CreateWorkspaceViaDialog(app);
        Require(WorktreePaths(app).Count() == 1 && second != first, "Re-creating must not collide with the retained branch.");
        Console.WriteLine("PASS upstream workspaces.spec.ts: creates, removes, and re-creates worktree workspaces (no branch collision)");
    }

    private static void RemoveActive(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var previous = CreateWorkspaceViaDialog(app);
        var removed = CreateWorkspaceViaDialog(app);
        app.Click(Select(app, previous));
        Until(() => Active(app, previous));
        app.Click(Select(app, removed));
        Until(() => Active(app, removed));
        RemoveWorkspace(app, removed);
        Until(() => !WorktreePaths(app).Contains(removed));
        Until(() => Active(app, previous));
        Require(!HasWelcome(app), "Removing the active workspace must restore the previous one, not Project Home.");
        Console.WriteLine("PASS upstream workspace-lifecycle.spec.ts: removing the active workspace restores the previously selected workspace");
    }
}