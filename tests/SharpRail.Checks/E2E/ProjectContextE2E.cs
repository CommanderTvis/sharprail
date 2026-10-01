using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class ProjectContextE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "project-context-git"));
        var directory = Path.Combine(root, "project-context");
        using var app = OpenFixtureProject(directory);
        // SharpRail regression: the selected project is highlighted as one rounded row, chevron and add button included.
        var highlight = Controls(app).OfType<Border>().Single(border => border.Name == "ProjectHighlight" && Equals(border.Tag, app.Root));
        Require(highlight.Child is Grid { Name: "ProjectRow" } && highlight.Background is Avalonia.Media.ISolidColorBrush { Color: var fill } &&
            fill == Ui.Hover.Color && highlight.CornerRadius.TopLeft > 0 &&
            ProjectName(app, app.Root).Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 },
            "The selected project must highlight its whole row, not only its name.");
        var workspace = CreateWorkspaceViaDialog(app);
        var fixture = app.Root;

        Grid Row(string project) => Controls(app).OfType<Grid>().Single(row => row.Name == "ProjectRow" && Equals(row.Tag, project));
        Button Name(string project) => ProjectName(app, project);
        ContextMenu Menu(string project) => Row(project).ContextMenu!;
        MenuItem Entry(string project, string name) => Menu(project).Items.OfType<MenuItem>().Single(item => item.Name == name);

        var fixtureRow = Row(fixture);
        Require(fixtureRow.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "ProjectExpand" && button.IsVisible && button.Opacity == 1) &&
            fixtureRow.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "AddWorkspace" && button.IsVisible && button.Opacity == 1),
            "Expand and add-workspace controls stay visible.");
        Require(!fixtureRow.GetLogicalDescendants().OfType<Button>().Any(button => button.Name is "CloseProject" or "ProjectActionsButton"),
            "Project rows keep close and actions in the context menu.");
        app.Click(fixtureRow.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ProjectExpand"));
        Until(() => Controls(app).OfType<TextBlock>().Any(text => text.Name == "ProjectWorkspaceCount"));
        var count = Controls(app).OfType<TextBlock>().Single(text => text.Name == "ProjectWorkspaceCount");
        var trailing = (StackPanel)count.Parent!;
        Require(count.Text == "1" && trailing.Children.IndexOf(count) + 1 < trailing.Children.Count &&
            trailing.Children[trailing.Children.IndexOf(count) + 1].Name == "AddWorkspace", "A collapsed project shows its workspace count beside Add workspace.");
        app.Click(Row(fixture).GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ProjectExpand"));
        Until(() => WorktreePaths(app).Contains(workspace));

        foreach (var (key, modifiers) in new[] { (Avalonia.Input.Key.F10, RawInputModifiers.Shift), (Avalonia.Input.Key.Apps, RawInputModifiers.None) })
        {
            Name(fixture).Focus();
            Press(Name(fixture), key, modifiers);
            Until(() => Menu(fixture).IsOpen);
            CloseMenu(Menu(fixture));
            Until(() => Name(fixture).IsFocused);
        }

        var name = Name(fixture);
        var pointer = name.TranslatePoint(new Point(52, name.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(pointer);
        app.Window.MouseDown(pointer, MouseButton.Right); app.Window.MouseUp(pointer, MouseButton.Right);
        Until(() => Menu(fixture).IsOpen);
        Require(app.Window.AtProjectHome == false && Active(app, workspace), "Opening the project menu must not navigate.");
        var menuOrigin = Menu(fixture).TranslatePoint(default, app.Window)!.Value;
        Require(Math.Abs(menuOrigin.X - pointer.X) < 8 && Math.Abs(menuOrigin.Y - pointer.Y) < 8, $"The project menu opens at the pointer ({menuOrigin} vs {pointer}).");
        var parts = Menu(fixture).Items.Cast<object>().ToArray();
        Require(parts.Length == 4 && parts[0] is MenuItem { Header: "Create workspace", Icon: not null } && parts[1] is Separator &&
            parts[2] is MenuItem { Header: "Copy absolute path" } && parts[3] is MenuItem { Header: "Close project", Icon: not null },
            "Project actions stay compact: Create workspace, separator, Copy absolute path, Close project.");
        Press(Entry(fixture, "ProjectMenuCreateWorkspace"), Avalonia.Input.Key.Down);
        Until(() => Entry(fixture, "ProjectMenuCreateWorkspace").IsFocused);
        Press(Entry(fixture, "ProjectMenuCreateWorkspace"), Avalonia.Input.Key.Down);
        Until(() => Entry(fixture, "ProjectMenuCopyPath").IsFocused);
        Press(Entry(fixture, "ProjectMenuCopyPath"), Avalonia.Input.Key.Down);
        Until(() => Entry(fixture, "ProjectMenuClose").IsFocused);
        CloseMenu(Menu(fixture));
        Until(() => Name(fixture).IsFocused);

        app.Click(Name(fixture), mouseButton: MouseButton.Right);
        Until(() => Menu(fixture).IsOpen);
        app.Click(Entry(fixture, "ProjectMenuCopyPath"), freshGesture: false);
        var copied = app.Window.Clipboard!.TryGetTextAsync();
        Until(() => copied.IsCompleted && !Menu(fixture).IsOpen);
        Require(copied.Result == fixture && !app.Window.AtProjectHome && Active(app, workspace),
            "Copy absolute path copies the project's path without changing the active workspace.");
        Console.WriteLine("PASS fork projects.spec.ts: project context menu copies its absolute path without changing the active workspace");
        app.Click(Name(fixture), mouseButton: MouseButton.Right);
        Until(() => Menu(fixture).IsOpen);
        CloseMenu(Menu(fixture));
        Until(() => Name(fixture).IsFocused);

        app.Click(Name(fixture), mouseButton: MouseButton.Right);
        Until(() => Menu(fixture).IsOpen);
        app.Click(Entry(fixture, "ProjectMenuCreateWorkspace"), freshGesture: false);
        var dialog = Dialog(app, "NewWorkspaceDialog");
        Press(dialog, Avalonia.Input.Key.Escape);
        Until(() => !app.Window.OwnedWindows.Any() && Name(fixture).IsFocused);

        var second = IsolatedGit.Repository(Path.Combine(directory, "second-project"));
        AddProject(app, "Open project", second);
        Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == second && HasWelcome(app) && WelcomeTitle(app) == "second-project");

        void CloseChoice(string project)
        {
            app.Click(Name(project), mouseButton: MouseButton.Right);
            Until(() => Menu(project).IsOpen);
            app.Click(Entry(project, "ProjectMenuClose"), freshGesture: false);
        }
        CloseChoice(second);
        var confirm = Dialog(app);
        Require(confirm.Title == "Close second-project?" && Text(confirm).Contains(
            "Removes this project from the open projects list. Its repository and workspaces are kept. Reopen it from Add project → Recents.", StringComparison.Ordinal),
            "Closing a project asks for confirmation.");
        var cancel = confirm.GetLogicalDescendants().OfType<Button>().Single(button => Text(button) == "Cancel");
        Until(() => cancel.IsFocused);
        app.Click(cancel);
        Until(() => !app.Window.OwnedWindows.Any() && Name(second).IsFocused);
        CloseChoice(second);
        Press(Dialog(app), Avalonia.Input.Key.Escape);
        Until(() => !app.Window.OwnedWindows.Any() && Name(second).IsFocused);
        // A second window of the app observes the shared project list.
        using var observer = app.NewWindow();
        Until(() => observer.Window.AtProjectHome && HasWelcome(observer) && WelcomeTitle(observer) == "second-project");
        bool Listed(E2eWorkspace client, string project) => Controls(client).OfType<Grid>().Any(row => row.Name == "ProjectRow" && Equals(row.Tag, project));
        Require(Listed(observer, fixture) && Listed(observer, second), "The observer lists both open projects.");
        app.Window.Activate();
        CloseChoice(second);
        confirm = Dialog(app);
        app.Click(confirm.GetLogicalDescendants().OfType<Button>().Single(button => Text(button) == "Close project"));
        Until(() => !Controls(app).OfType<Grid>().Any(row => row.Name == "ProjectRow" && Equals(row.Tag, second)));
        Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == fixture && HasWelcome(app) && WelcomeTitle(app) == "sample-project");
        Require(app.Tabs.Count == 0, "Closing the active project shows the next project's home.");
        Until(() => Name(fixture).IsFocused);
        Until(() => !Listed(observer, second) && observer.Window.AtProjectHome && observer.Window.ProjectRoot == fixture &&
            HasWelcome(observer) && WelcomeTitle(observer) == "sample-project");

        CloseChoice(fixture);
        confirm = Dialog(app);
        app.Click(confirm.GetLogicalDescendants().OfType<Button>().Single(button => Text(button) == "Close project"));
        Until(() => !Controls(app).OfType<Grid>().Any(row => row.Name == "ProjectRow") && HasWelcome(app) && WelcomeTitle(app) == "SharpRail");
        Until(() => app.Find<Button>("AddProjectMenu").IsFocused);
        Until(() => !Controls(observer).OfType<Grid>().Any(row => row.Name == "ProjectRow") && HasWelcome(observer) && WelcomeTitle(observer) == "SharpRail");

        var add = app.Find<Button>("AddProjectMenu");
        app.Click(add);
        Until(() => add.ContextMenu!.IsOpen);
        var recents = add.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.Name == "RecentProject").ToArray();
        Require(recents.Any(item => Equals(item.Header, fixture)) && recents.Any(item => Equals(item.Header, second)), "Closed projects appear under Recents.");
        app.Click(recents.Single(item => Equals(item.Header, fixture)), freshGesture: false);
        Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == fixture && HasWelcome(app) && WelcomeTitle(app) == "sample-project");
        Require(app.Tabs.Count == 0, "Reopening lands on Project Home.");
        Until(() => WorktreePaths(app).Contains(workspace));
        Until(() => Listed(observer, fixture) && !Listed(observer, second));

        // SharpRail regression: a project whose folder was deleted closes without the confirmation.
        var deleted = IsolatedGit.Repository(Path.Combine(directory, "deleted-project"));
        AddProject(app, "Open project", deleted);
        Until(() => app.Window.ProjectRoot == deleted && Listed(app, deleted));
        app.Window.Activate();
        Directory.Delete(deleted, recursive: true);
        CloseChoice(deleted);
        Until(() => !Listed(app, deleted));
        Require(!app.Window.OwnedWindows.Any(), "Closing a project whose folder is gone does not ask for confirmation.");
        Console.WriteLine("PASS upstream projects.spec.ts: project context actions stay compact and close/reopen is lossless across clients");
    }
}