using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using SharpRail.UI.Panels;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Translates upstream's fixtures/app.ts helpers: a committed sample project opened at its
/// Project Home, workspace creation through the dialog and the workspace row menu.
/// </summary>
internal static class WorkspaceFixture
{
    internal static E2eWorkspace OpenFixtureProject(string directory)
    {
        var project = Path.Combine(directory, "sample-project");
        IsolatedGit.Repository(project);
        var app = new E2eWorkspace(project, openFiles: false);
        IsolatedGit.Run(project, "add", "-A");
        IsolatedGit.Run(project, "commit", "-m", "fixture");
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "main");
        GoProjectHome(app);
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "main");
        return app;
    }

    internal static E2eWorkspace OpenFresh(string directory, string? profile = null) =>
        new(Path.Combine(directory, "scratch"), openFiles: false, profileRoot: profile, startPath: "");

    internal static void GoProjectHome(E2eWorkspace app, string? project = null)
    {
        app.Click(ProjectName(app, project ?? app.Window.ProjectRoot));
        Until(() => app.Window.AtProjectHome && app.Window.WorkspaceMounted && HasWelcome(app));
    }

    internal static Button ProjectName(E2eWorkspace app, string project)
    {
        Until(() => Buttons(app).Any(button => button.Name == "ProjectName" && Equals(button.Tag, project)));
        return Buttons(app).Single(button => button.Name == "ProjectName" && Equals(button.Tag, project));
    }

    internal static bool HasWelcome(E2eWorkspace app) => Controls(app).Any(control => control.Name == "Welcome" && control.IsVisible);

    internal static string WelcomeTitle(E2eWorkspace app) => Controls(app).OfType<TextBlock>().Single(text => text.Name == "WelcomeTitle").Text!;

    internal static Window Dialog(E2eWorkspace app, string? name = null)
    {
        Until(() => app.Window.OwnedWindows.Any(window => name is null || Equals(window.Tag, name)));
        var dialog = app.Window.OwnedWindows.Single(window => name is null || Equals(window.Tag, name));
        Settle(100);
        return dialog;
    }

    internal static Window OpenNewWorkspaceDialog(E2eWorkspace app)
    {
        Until(() => Buttons(app).Any(button => button.Name == "AddWorkspace" && button.IsEnabled));
        app.Click(Buttons(app).Single(button => button.Name == "AddWorkspace"));
        return Dialog(app, "NewWorkspaceDialog");
    }

    internal static string CreateWorkspaceViaDialog(E2eWorkspace app)
    {
        var before = WorktreePaths(app).ToHashSet();
        var dialog = app.Window.OwnedWindows.SingleOrDefault(window => Equals(window.Tag, "NewWorkspaceDialog")) ?? OpenNewWorkspaceDialog(app);
        app.Click(Named<Button>(dialog, "WsCreate"));
        Until(() => !app.Window.OwnedWindows.Any() && app.Window.WorkspaceMounted && !app.Window.AtProjectHome &&
            app.Window.WorkspaceRoot != app.Window.ProjectRoot && !before.Contains(app.Window.WorkspaceRoot));
        var path = app.Window.WorkspaceRoot;
        Until(() => WorktreePaths(app).Contains(path) && app.Find<TextBlock>("BranchLabel").Text == Path.GetFileName(path));
        return path;
    }

    internal static IEnumerable<string> WorktreePaths(E2eWorkspace app) => Controls(app).OfType<Grid>()
        .Where(item => item.Name == "WorkspaceItem" && (string)item.Tag! != app.Window.ProjectRoot).Select(item => (string)item.Tag!);

    internal static Grid Item(E2eWorkspace app, string path)
    {
        Until(() => Controls(app).OfType<Grid>().Any(item => item.Name == "WorkspaceItem" && Equals(item.Tag, path)));
        return Controls(app).OfType<Grid>().Single(item => item.Name == "WorkspaceItem" && Equals(item.Tag, path));
    }

    internal static Button Select(E2eWorkspace app, string path) =>
        Item(app, path).GetLogicalChildren().OfType<Button>().Single(button => button.Name == "WorkspaceSelect");

    internal static string ItemText(E2eWorkspace app, string path, string name) =>
        Item(app, path).GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == name).Text!;

    internal static bool Active(E2eWorkspace app, string path) => !app.Window.AtProjectHome && app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == path;

    internal static ContextMenu OpenWorkspaceMenu(E2eWorkspace app, string path)
    {
        Until(() => Item(app, path).GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "WorkspaceBranch"));
        Settle(300);
        var item = Item(app, path);
        var kebab = item.GetLogicalChildren().OfType<Button>().Single(button => button.Name == "WorkspaceMenu");
        app.Window.MouseMove(item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), app.Window)!.Value);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var menu = item.GetLogicalChildren().OfType<Button>().Single(button => button.Name == "WorkspaceSelect").ContextMenu!;
        app.Click(kebab, freshGesture: false);
        Until(() => menu.IsOpen);
        return menu;
    }

    internal static MenuItem? MenuEntry(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == name);

    internal static void CloseMenu(ContextMenu menu)
    {
        TopLevel.GetTopLevel(menu.Items.OfType<MenuItem>().First())!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !menu.IsOpen);
    }

    internal static void Choose(E2eWorkspace app, ContextMenu menu, string name)
    {
        var entry = MenuEntry(menu, name) ?? throw new InvalidOperationException("Missing menu entry " + name);
        TopLevel.GetTopLevel(entry)!.UpdateLayout();
        app.Click(entry, freshGesture: false);
    }

    internal static void RemoveWorkspace(E2eWorkspace app, string path)
    {
        Choose(app, OpenWorkspaceMenu(app, path), "WorkspaceRemove");
        var confirm = Dialog(app);
        Require(confirm.Title == "Remove worktree?", "Removal must ask for confirmation.");
        app.Click(confirm.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Remove worktree" }));
    }

    internal static void AddProject(E2eWorkspace app, string item, string? path = null)
    {
        if (path is not null) app.Window.FolderPicker = () => Task.FromResult<string?>(path);
        var add = app.Find<Button>("AddProjectMenu");
        app.Click(add);
        Until(() => add.ContextMenu!.IsOpen);
        app.Click(add.ContextMenu!.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, item)), freshGesture: false);
    }

    internal static T Named<T>(Control container, string name) where T : Control =>
        container.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    internal static string Text(Control control) => string.Join("\n", control.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));

    internal static IEnumerable<Control> Controls(E2eWorkspace app)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); app.Window.UpdateLayout();
        return app.Window.GetLogicalDescendants().OfType<Control>();
    }

    internal static IEnumerable<Button> Buttons(E2eWorkspace app) => Controls(app).OfType<Button>();

    internal static void Press(Control target, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        TopLevel.GetTopLevel(target)!.KeyPress(key, modifiers, PhysicalKeyFor(key), null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static PhysicalKey PhysicalKeyFor(Key key) => key switch
    {
        Key.N => PhysicalKey.N,
        Key.Enter => PhysicalKey.Enter,
        Key.Escape => PhysicalKey.Escape,
        Key.Down => PhysicalKey.ArrowDown,
        Key.F10 => PhysicalKey.F10,
        Key.Apps => PhysicalKey.ContextMenu,
        _ => PhysicalKey.None
    };

    internal static string Git(string directory, params string[] arguments) => IsolatedGit.Run(directory, arguments).Trim();
}
