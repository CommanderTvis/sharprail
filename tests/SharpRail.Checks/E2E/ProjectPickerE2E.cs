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
        AddProject(app, "Enter host path…");
        var dialog = Dialog(app);
        Require(Named<TextBlock>(dialog, "DialogExplanation").Text!.Contains("computer running SharpRail", StringComparison.Ordinal),
            "The host-path dialog must explain that the path is on the computer running SharpRail.");
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
        AddProject(app, "Enter host path…");
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
        var dialog = Dialog(app);
        app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => Label(button, "Initialise repository")));
        Until(() => !app.Window.OwnedWindows.OfType<DialogWindow>().Any());
        WaitForProject(app, "projects-init-plain");
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "main");
        Require(IsolatedGit.Run(plain, "ls-tree", "--name-only", "HEAD").Trim() == "notes.txt", "Initialising a folder must commit its existing files.");
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        Console.WriteLine("PASS upstream projects.spec.ts: opening a non-git folder offers to initialise a repo, then opens it end-to-end");
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
        var first = app.Window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), app.Root));
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

    private static bool Label(Button button, string label) => button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == label);

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