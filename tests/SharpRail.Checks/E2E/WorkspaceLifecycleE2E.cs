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
        Require(WorktreeCount(app.Root) >= 2 && list.Contains("/sample-project-worktrees/", StringComparison.Ordinal),
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