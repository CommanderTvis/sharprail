using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;

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
        var confirm = Dialog(app);
        app.Click(confirm.GetLogicalDescendants().OfType<Button>().Single(button => Text(button) == "Initialise repository"));
        Until(() => !app.Window.OwnedWindows.Any() && app.Window.AtProjectHome && app.Window.ProjectRoot == plain && HasWelcome(app));
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "main");
        Require(Buttons(app).Any(button => button.Name == "ProjectName" && Equals(button.Tag, plain)), "The initialised folder must join the rail.");
        Require(app.Tabs.Count == 0 && WelcomeTitle(app) == "plain-folder", "An initialised folder must land on its Project Home.");
        Require(Buttons(app).Any(button => button.Name == "WelcomeAction" && Text(button).Contains("Work in project folder", StringComparison.Ordinal)),
            "Project Home must offer the project-folder fork.");
        Require(Git(plain, "ls-tree", "--name-only", "HEAD") == "notes.txt", "Initialising must commit the folder's files.");
        Console.WriteLine("PASS upstream welcome.spec.ts: opening a non-git folder from the Welcome screen offers to initialise a repo");
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