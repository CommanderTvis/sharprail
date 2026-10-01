using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class WelcomeE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "welcome-git"));
        Clean(Path.Combine(root, "welcome-clean"));
        ProjectHome(Path.Combine(root, "welcome-project"));
        InitialiseFromWelcome(Path.Combine(root, "welcome-init"));
        ProjectClick(Path.Combine(root, "welcome-project-click"));
    }

    private static void Clean(string directory)
    {
        using var app = OpenFresh(directory);
        Until(() => HasWelcome(app));
        Require(!app.Window.WorkspaceMounted && app.Tabs.Count == 0, "A fresh start must not mount a workspace or open center tabs.");
        Require(WelcomeTitle(app) == "SharpRail", "The clean Welcome must show the product title.");
        var cta = app.Find<Button>("WelcomeCta");
        Require(Text(cta).Contains("Open project", StringComparison.Ordinal) && !Buttons(app).Any(button => button.Name == "WelcomeAction"),
            "The clean Welcome offers only Open project.");
        Require(!Buttons(app).Any(button => button.Name == "ProjectName"), "No projects may be imported on a fresh start.");
        app.Click(cta);
        Until(() => cta.ContextMenu!.IsOpen);
        Require(cta.ContextMenu!.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Open project") && item.IsVisible),
            "The Welcome call to action must offer Open project.");
        CloseMenu(cta.ContextMenu);
        Console.WriteLine("PASS upstream welcome.spec.ts: opens a clean ThinkRail with no projects imported");
    }

    private static void ProjectHome(string directory)
    {
        using var app = OpenFixtureProject(directory);
        Require(WelcomeTitle(app) == "sample-project", "Project Home must be titled with the project name.");
        Require(app.Find<TextBlock>("ProjectLabel").Text == "sample-project" && app.Find<TextBlock>("WorkspaceLabel").Text == "Project home" &&
            app.Find<TextBlock>("WelcomeScope").Text == "PROJECT HOME", "The scope context must say Project home.");
        var actions = Buttons(app).Where(button => button.Name == "WelcomeAction").ToArray();
        Require(actions.Length == 1 && Text(actions[0]).Contains("Work in project folder", StringComparison.Ordinal),
            "Project Home offers exactly one fork: Work in project folder.");
        // The reference's cards: one size, the primary filled, the rest quiet.
        var cta = app.Find<Button>("WelcomeCta");
        Require(new[] { cta, actions[0] }.All(card => card.Bounds.Width == 220 && card.Bounds.Height == 150) &&
            cta.Background == Ui.PrimarySubtle && actions[0].Background == Ui.Sidebar, "Welcome actions are the reference's 220×150 cards.");
        var welcome = app.Find<StackPanel>("Welcome");
        Require(!Text(welcome).Contains("Set up project", StringComparison.Ordinal) && !Text(welcome).Contains("Open project", StringComparison.Ordinal),
            "Project Home must not offer set-up or Open project.");
        Console.WriteLine("PASS upstream welcome.spec.ts: a project with specs offers Start building over Set up, beside the project-folder fork");
    }

    private static void InitialiseFromWelcome(string directory)
    {
        using var app = OpenFresh(directory);
        Until(() => HasWelcome(app));
        var plain = Path.Combine(directory, "plain-folder");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "notes.txt"), "not a repository yet\n");
        app.Window.FolderPicker = () => Task.FromResult<string?>(plain);
        var cta = app.Find<Button>("WelcomeCta");
        app.Click(cta);
        Until(() => cta.ContextMenu!.IsOpen);
        app.Click(cta.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open project")), freshGesture: false);
        Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == plain && HasWelcome(app));
        Settle(500);
        Require(!app.Window.OwnedWindows.Any() && !Directory.Exists(Path.Combine(plain, ".git")), "A plain folder opens without git init.");
        Require(Buttons(app).Any(button => button.Name == "ProjectName" && Equals(button.Tag, plain)), "The plain folder must join the rail.");
        Require(app.Tabs.Count == 0 && WelcomeTitle(app) == "plain-folder", "A plain folder must land on its Project Home.");
        Require(!app.Find<Button>("WelcomeCta").IsVisible, "A plain folder's Project Home must hide Create workspace.");
        Require(Buttons(app).Any(button => button.Name == "WelcomeAction" && Text(button).Contains("Work in project folder", StringComparison.Ordinal)),
            "Project Home must offer the project-folder fork.");
        var row = Controls(app).OfType<Grid>().Single(control => control.Name == "ProjectRow" && Equals(control.Tag, plain));
        Require(!row.ContextMenu!.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Create workspace")),
            "A plain folder's project menu must not advertise Git workspace creation.");
        app.ContextAction(row, "Start work");
        var menuDialog = Dialog(app, "NewWorkspaceDialog");
        Require(Named<Avalonia.Controls.Primitives.ToggleButton>(menuDialog, "WsTargetDefault").IsChecked == true,
            "The project context menu must open the same folder-mode Start work dialog.");
        Press(menuDialog, Avalonia.Input.Key.Escape);
        Until(() => !app.Window.OwnedWindows.Any());
        app.Click(app.Find<Button>("AddWorkspace"));
        var dialog = Dialog(app, "NewWorkspaceDialog");
        Require(!Named<Avalonia.Controls.Primitives.ToggleButton>(dialog, "WsTargetWorktree").IsVisible &&
            Named<Avalonia.Controls.Primitives.ToggleButton>(dialog, "WsTargetDefault").IsChecked == true,
            "A plain folder must offer Start work in Project folder mode with New worktree hidden.");
        Require(!Named<Button>(dialog, "WsBranchPicker").IsVisible && !Named<TextBox>(dialog, "WsName").IsVisible &&
            Text(Named<Button>(dialog, "WsCreate")) == "Start", "Folder mode must offer Start without branch or name fields.");
        app.Click(Named<Button>(dialog, "WsCreate"));
        Until(() => !app.Window.AtProjectHome && app.Window.WorkspaceRoot == plain && !app.Window.OwnedWindows.Any());
        var terminal = app.Find<Button>("NewTerminal_" + app.Center);
        app.Click(terminal);
        Until(() => app.Tabs.Any(tab => tab.Kind == "terminal"));
        app.Open("notes.txt", keep: true);
        Require(!Directory.Exists(Path.Combine(plain, ".git")), "Starting work and opening tabs must not initialize Git.");
        Console.WriteLine("PASS fork gitless.spec.ts: a plain folder opened from the Welcome screen lands on its Project Home, with no git required");
    }

    private static void ProjectClick(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        Require(!HasWelcome(app) && Active(app, workspace), "The created workspace must be active.");
        app.Click(ProjectName(app, app.Root));
        Until(() => app.Window.AtProjectHome && HasWelcome(app));
        Require(app.Tabs.Count == 0 && !Active(app, workspace) && !Active(app, app.Root), "Project Home deselects every workspace.");
        app.Click(Select(app, workspace));
        Until(() => Active(app, workspace) && !HasWelcome(app));
        Console.WriteLine("PASS upstream welcome.spec.ts: clicking a project returns to its Welcome, deselecting the active workspace");
    }
}