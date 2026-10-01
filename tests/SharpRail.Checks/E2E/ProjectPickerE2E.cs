using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class ProjectPickerE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "projects-picker-git"));
        Picker(root);
        HostPath(root);
        PickerFailure(root);
        ManualSupersedes(root);
        InitialiseNonGit(root);
        RailExpansion(root);
        UnusablePaths(root);
    }

    private static void UnusablePaths(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "projects-unusable-welcome"), openFiles: false);
        var file = Path.Combine(app.Root, "README.md");
        foreach (var (path, message) in new[] { (Path.Combine(root, "projects-unusable-absent"), "No such folder: "), (file, "Not a folder: ") })
        {
            app.Window.FolderPicker = () => Task.FromResult<string?>(path);
            AddProject(app, "Open project");
            var notice = Dialog(app);
            Require(Equals(notice.Tag, "NoticeDialog") && notice.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "DialogExplanation" && text.Text == message + path) &&
                !app.Find<TextBlock>("WorkspaceError").IsVisible, "An unusable path must be refused in the notice dialog: " + message);
            app.Click(notice.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "NoticeDismiss"));
            Until(() => !app.Window.OwnedWindows.Any());
            Require(app.Window.WorkspaceRoot == app.Root && ProjectNames(app).Count() == 1, "An unusable path must leave the open workspace and the project list alone.");
        }
        Console.WriteLine("PASS SharpRail: a missing folder or a file picked as a project is refused by inspection before anything opens");
    }

    private static void Picker(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "projects-picker-welcome"), openFiles: false);
        var repo = IsolatedGit.Repository(Path.Combine(root, "projects-picker-repo"));
        app.Window.FolderPicker = () => Task.FromResult<string?>(repo);
        AddProject(app, "Open project");
        WaitForProject(app, "projects-picker-repo");
        Console.WriteLine("PASS upstream projects.spec.ts: opens a git repo as a project via the directory picker");
    }

    private static void HostPath(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "projects-path-welcome"), openFiles: false);
        var repo = IsolatedGit.Repository(Path.Combine(root, "projects-path-repo"));
        AddProject(app, "Enter path…");
        var dialog = Dialog(app);
        // On the desktop the host is this computer, so the copy does not call it the host.
        Require(Named<TextBlock>(dialog, "DialogExplanation").Text == "Enter the absolute path of a folder.",
            "The local path dialog must not speak of another computer.");
        Require(!dialog.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "OpenProjectPickerError"),
            "A deliberate host-path entry must not show a picker error.");
        var input = Named<TextBox>(dialog, "OpenProjectPathInput");
        Until(() => input.IsFocused);
        input.Text = repo;
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Until(() => !app.Window.OwnedWindows.OfType<DialogWindow>().Any());
        WaitForProject(app, "projects-path-repo");
        Console.WriteLine("PASS upstream projects.spec.ts: opens a project from an explicit host path");
    }

    private static void PickerFailure(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "projects-failure-welcome"), openFiles: false);
        var repo = IsolatedGit.Repository(Path.Combine(root, "projects-failure-repo"));
        const string failure = "Deterministic picker failure from the e2e host";
        app.Window.FolderPicker = () => Task.FromException<string?>(new InvalidOperationException(failure));
        AddProject(app, "Open project");
        var dialog = Dialog(app);
        Require(Named<TextBlock>(dialog, "OpenProjectPickerError").Text!.Contains(failure, StringComparison.Ordinal),
            "A failed picker must fall back to host-path entry and show its error.");
        Named<TextBox>(dialog, "OpenProjectPathInput").Text = repo;
        app.Click(Named<Button>(dialog, "OpenProjectPathSubmit"));
        Until(() => !app.Window.OwnedWindows.OfType<DialogWindow>().Any());
        WaitForProject(app, "projects-failure-repo");
        Console.WriteLine("PASS upstream projects.spec.ts: picker failure falls back to host-path entry on every host platform");
    }

    private static void ManualSupersedes(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "projects-supersede-welcome"), openFiles: false);
        var stale = IsolatedGit.Repository(Path.Combine(root, "projects-supersede-picked"));
        var manual = IsolatedGit.Repository(Path.Combine(root, "projects-supersede-manual"));
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = false;
        app.Window.FolderPicker = () => { asked = true; return reply.Task; };
        AddProject(app, "Open project");
        Until(() => asked);
        AddProject(app, "Enter path…");
        var dialog = Dialog(app);
        Named<TextBox>(dialog, "OpenProjectPathInput").Text = manual;
        app.Click(Named<Button>(dialog, "OpenProjectPathSubmit"));
        Until(() => !app.Window.OwnedWindows.OfType<DialogWindow>().Any());
        WaitForProject(app, "projects-supersede-manual");
        reply.SetResult(stale);
        Settle(800);
        Require(Path.GetFileName(app.Window.WorkspaceRoot) == "projects-supersede-manual" && !ProjectNames(app).Contains("projects-supersede-picked"),
            "A late picker reply must not replace a manual path entered afterwards.");
        Console.WriteLine("PASS upstream projects.spec.ts: manual path from the rail supersedes a picker started from Welcome");
    }

    private static void InitialiseNonGit(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "projects-init-welcome"), openFiles: false);
        var plain = Path.Combine(root, "projects-init-plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "notes.txt"), "not a repository yet\n");
        app.Window.FolderPicker = () => Task.FromResult<string?>(plain);
        AddProject(app, "Open project");
        WaitForProject(app, "projects-init-plain");
        Settle(500);
        Require(!app.Window.OwnedWindows.Any() && !Directory.Exists(Path.Combine(plain, ".git")),
            "A plain folder opens directly, without offering or running git init.");
        Require(app.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "AddWorkspace").IsEnabled,
            "A plain folder must keep Start work available to enter its project folder.");
        Console.WriteLine("PASS fork gitless.spec.ts: a plain folder opens as a project, with no git required");
    }

    private static void RailExpansion(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "projects-activation-first"), openFiles: false);
        var second = IsolatedGit.Repository(Path.Combine(root, "projects-activation-second"));
        app.Window.FolderPicker = () => Task.FromResult<string?>(second);
        AddProject(app, "Open project");
        WaitForProject(app, "projects-activation-second");
        Require(Expanded(app, "projects-activation-first") && Expanded(app, "projects-activation-second"),
            "Both projects must start expanded.");
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "main"); Settle();
        var first = app.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ProjectName" && Equals(ToolTip.GetTip(button), app.Root));
        app.Click(first);
        Until(() => app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == app.Root);
        Settle(800);
        WorkspaceTabsE2E.Switch(app, app.Root);
        Require(Expanded(app, "projects-activation-first") && Expanded(app, "projects-activation-second"),
            "Activating a workspace in one project must keep the other project's rail expansion.");
        Console.WriteLine("PASS upstream projects.spec.ts: activating a workspace in one project keeps the other project's rail expansion");
    }

    private static void AddProject(E2eWorkspace app, string item)
    {
        app.Click(app.Find<Button>("AddProjectMenu"));
        Until(() => app.Find<Button>("AddProjectMenu").ContextMenu!.IsOpen);
        app.Click(app.Find<Button>("AddProjectMenu").ContextMenu!.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, item)), freshGesture: false);
    }

    private static DialogWindow Dialog(E2eWorkspace app)
    {
        Until(() => app.Window.OwnedWindows.OfType<DialogWindow>().Any());
        var dialog = app.Window.OwnedWindows.OfType<DialogWindow>().Single();
        Settle(100);
        return dialog;
    }

    private static T Named<T>(Control container, string name) where T : Control =>
        container.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);


    private static IEnumerable<string> ProjectNames(E2eWorkspace app) => app.Window.GetLogicalDescendants().OfType<Button>()
        .Where(button => button.Name == "ProjectExpand").Select(button => Path.GetFileName((string)button.Tag!));

    private static bool Expanded(E2eWorkspace app, string name) => Equals(ToolTip.GetTip(app.Window.GetLogicalDescendants().OfType<Button>()
        .Single(button => button.Name == "ProjectExpand" && Path.GetFileName((string)button.Tag!) == name)), "Collapse project");

    private static void WaitForProject(E2eWorkspace app, string name)
    {
        Until(() => app.Window.WorkspaceMounted && Path.GetFileName(app.Window.WorkspaceRoot) == name && ProjectNames(app).Contains(name));
        Require(app.Window.GetLogicalDescendants().OfType<Button>().Any(button => AutomationProperties.GetName(button) == name),
            "The opened project must appear in the rail.");
    }
}