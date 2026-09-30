using Avalonia.Controls;
using SharpRail.UI.State;
using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class ReloadNavigationE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "reload-navigation-git"));
        MissingFallback(Path.Combine(root, "reload-missing"));
        NavigationWins(Path.Combine(root, "reload-navigation-wins"));
        FileTab(Path.Combine(root, "reload-file-tab"));
        RowsAfterRestore(Path.Combine(root, "reload-rows"));
    }

    private static E2eWorkspace Reload(E2eWorkspace app, Action<E2eHost>? prepare = null)
    {
        var project = app.Root;
        app.Dispose();
        var saved = new ProfileStore(project + "-profile").Data;
        return new E2eWorkspace(project, openFiles: false, startPath: saved.Windows[0].LastProject, prepare: prepare);
    }

    private static void MissingFallback(string directory)
    {
        var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var project = app.Root;
        app.Dispose();
        Git(project, "worktree", "remove", "--force", workspace);
        var profile = new ProfileStore(project + "-profile");
        Require(profile.Data.Windows[0].LastProject == workspace, "The profile must remember the active workspace.");
        using (app = new E2eWorkspace(project, openFiles: false, startPath: profile.Data.Windows[0].LastProject))
        {
            Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == project && HasWelcome(app));
            Require(!WorktreePaths(app).Any(path => Active(app, path)), "A missing workspace falls back to its Project Home.");
        }
        profile = new ProfileStore(project + "-profile");
        Require(profile.Data.Windows[0].LastAtHome && profile.Data.Windows[0].LastProjectRoot == project, "The fallback must be remembered as Project Home.");
        profile.Data.Windows[0].LastProject = Path.Combine(directory, "gone", "workspace");
        profile.Data.Windows[0].LastProjectRoot = Path.Combine(directory, "gone");
        profile.Save();
        using (app = new E2eWorkspace(project, openFiles: false, startPath: profile.Data.Windows[0].LastProject))
        {
            Until(() => HasWelcome(app));
            Require(!app.Window.WorkspaceMounted && WelcomeTitle(app) == "SharpRail", "A missing project falls back to the clean Welcome.");
        }
        Console.WriteLine("PASS upstream reload-navigation.spec.ts: missing chat, workspace, and project fall back to the nearest valid location");
    }

    private static void NavigationWins(string directory)
    {
        var app = OpenFixtureProject(directory);
        CreateWorkspaceViaDialog(app);
        TaskCompletionSource? held = null;
        using (app = Reload(app, host => held = host.HoldOpen()))
        {
            app.Click(ProjectName(app, app.Root));
            Settle(200);
            Require(!app.Window.WorkspaceMounted, "The restore read must still be held.");
            held!.SetResult();
            Until(() => app.Window.AtProjectHome && HasWelcome(app));
            Settle(500);
            Require(app.Window.AtProjectHome && HasWelcome(app) && !WorktreePaths(app).Any(path => Active(app, path)),
                "A late restore response must not override the user's navigation.");
        }
        Console.WriteLine("PASS upstream reload-navigation.spec.ts: user navigation while the restore read is delayed wins over the late response");
    }

    private static void FileTab(string directory)
    {
        var app = OpenFixtureProject(directory);
        DefaultWorkspaceE2E.EnterDefaultWorkspace(app);
        app.Click(app.Find<Button>("Tab_files"));
        app.Open("README.md");
        Require(app.Tabs.Count(tab => tab.Kind is "markdown" or "file") == 1, "One file tab must be open.");
        using (app = Reload(app))
        {
            Until(() => Active(app, app.Root));
            Until(() => app.Window.Layout.Selected(app.Center)?.Path == "README.md");
            Require(app.Tabs.Count(tab => tab.Kind is "markdown" or "file") == 1, "Reload must restore exactly the open file tab.");
        }
        Console.WriteLine("PASS upstream reload-navigation.spec.ts: reload from a file tab restores its shared placement under the workspace route");
    }

    private static void RowsAfterRestore(string directory)
    {
        var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        using (app = Reload(app))
        {
            Until(() => Active(app, workspace) && WorktreePaths(app).Count() == 1);
            Require(Item(app, app.Root).IsVisible, "The Default row must be listed after a restore.");
        }
        Console.WriteLine("PASS upstream reload-navigation.spec.ts: workspace rows still list after a reload restore (the light list is complete)");
    }
}
