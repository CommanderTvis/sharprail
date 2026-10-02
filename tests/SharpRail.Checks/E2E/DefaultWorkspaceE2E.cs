using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class DefaultWorkspaceE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "default-workspace-git"));
        EnterDefault(Path.Combine(root, "default-enter"));
        BranchSwitch(Path.Combine(root, "default-branch"));
        Unique(Path.Combine(root, "default-unique"));
        RailSurvivesSwitch(Path.Combine(root, "default-rail"));
    }

    private static void RailSurvivesSwitch(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        app.Click(Select(app, app.Root));
        Until(() => Active(app, app.Root)); Settle(500);
        var rows = WorktreePaths(app).ToDictionary(path => path, path => Item(app, path));
        var rail = app.Find<Grid>("ProjectsPanel");
        var shrank = false;
        void Watch(object? sender, EventArgs e) =>
            shrank |= rail.GetLogicalDescendants().OfType<Grid>().Count(item => item.Name == "WorkspaceItem") < rows.Count;
        rail.LayoutUpdated += Watch;
        app.Click(Select(app, workspace));
        Until(() => Active(app, workspace)); Settle(500);
        rail.LayoutUpdated -= Watch;
        Require(ReferenceEquals(app.Find<Grid>("ProjectsPanel"), rail) && !shrank &&
            rows.All(pair => ReferenceEquals(Item(app, pair.Key), pair.Value)),
            "Switching workspaces within a project must keep the rail and its rows rather than rebuilding them.");
        Require(Select(app, workspace).Background == Ui.Hover && Select(app, app.Root).Background != Ui.Hover,
            $"The active highlight must move to the opened workspace: opened={Select(app, workspace).Background} default={Select(app, app.Root).Background}.");
        Console.WriteLine("PASS switching workspaces keeps the Projects rail rows and moves the highlight in place");
    }

    internal static void EnterDefaultWorkspace(E2eWorkspace app)
    {
        app.Click(app.Find<Button>("WelcomeAction"));
        Until(() => Active(app, app.Root) && !HasWelcome(app));
    }

    private static void EnterDefault(string directory)
    {
        using var app = OpenFixtureProject(directory);
        EnterDefaultWorkspace(app);
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "main");
        Require(app.Find<TextBlock>("WorkspaceLabel").Text == "Default", "The scope must name the Default workspace.");
        var first = Controls(app).OfType<Grid>().First(item => item.Name == "WorkspaceItem");
        Require(Equals(first.Tag, app.Root) && ItemText(app, app.Root, "WorkspaceName") == "Default" && ItemText(app, app.Root, "WorkspaceBranch") == "main",
            "The Default row comes first, named Default on main.");
        Until(() => Text(app.Find<StackPanel>("WorkspacePlaceholder")).Contains("on main", StringComparison.Ordinal));
        var ready = Text(app.Find<StackPanel>("WorkspacePlaceholder"));
        Require(ready.Contains("DEFAULT WORKSPACE", StringComparison.Ordinal) && ready.Contains("sample-project", StringComparison.Ordinal) &&
            ready.Contains("run directly in your project folder", StringComparison.Ordinal) && !ready.Contains("from ", StringComparison.Ordinal),
            "The Default workspace receipt must describe the project folder without a base.");
        app.Click(app.Find<Button>("Tab_files"));
        app.FileRow("README.md");
        app.Click(app.Find<Button>("Tab_changes"));
        Until(() => Text(app.Find<Control>("ChangesPanel")).Contains("Working tree clean", StringComparison.Ordinal));
        Console.WriteLine("PASS upstream default-workspace.spec.ts: the Welcome fork's “Work in project folder” enters the Default workspace — the project folder itself");
    }

    private static void BranchSwitch(string directory)
    {
        using var app = OpenFixtureProject(directory);
        EnterDefaultWorkspace(app);
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "main" && ItemText(app, app.Root, "WorkspaceBranch") == "main");
        Git(app.Root, "switch", "-c", "live-branch");
        Until(() => ItemText(app, app.Root, "WorkspaceBranch") == "live-branch" && app.Find<TextBlock>("BranchLabel").Text == "live-branch" &&
            Text(app.Find<StackPanel>("WorkspacePlaceholder")).Contains("on live-branch", StringComparison.Ordinal));
        Console.WriteLine("PASS upstream default-workspace.spec.ts: a terminal branch switch converges every Default branch label live");
    }

    private static void Unique(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var menu = OpenWorkspaceMenu(app, app.Root);
        Require(MenuEntry(menu, "WorkspaceRemove") is null, "The Default workspace cannot be removed.");
        CloseMenu(menu);
        menu = OpenWorkspaceMenu(app, workspace);
        Require(MenuEntry(menu, "WorkspaceRemove") is { IsEnabled: true }, "A worktree workspace can be removed.");
        CloseMenu(menu);
        AddProject(app, "Open project", app.Root);
        Until(() => app.Window.AtProjectHome && HasWelcome(app));
        Require(Controls(app).OfType<Grid>().Count(item => item.Name == "WorkspaceItem" && Equals(item.Tag, app.Root)) == 1,
            "Reopening the project must keep one Default row.");
        app.Click(Select(app, app.Root));
        Until(() => Active(app, app.Root) && !HasWelcome(app));
        GoProjectHome(app);
        Require(app.Tabs.Count == 0, "Project Home has no center tabs.");
        Console.WriteLine("PASS upstream default-workspace.spec.ts: the Default workspace is non-removable and unique; project home stays reachable");
    }
}