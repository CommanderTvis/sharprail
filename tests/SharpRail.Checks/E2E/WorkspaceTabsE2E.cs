using System.Diagnostics;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using SharpRail.UI.Docking;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class WorkspaceTabsE2E
{
    internal static void Run(string root)
    {
        var source = Environment.GetEnvironmentVariable("SHARPRAIL_TEST_GIT_SOURCE");
        if (string.IsNullOrEmpty(source))
        {
            Console.WriteLine("SKIP upstream workspace-tabs: set SHARPRAIL_TEST_GIT_SOURCE.");
            return;
        }
        DocumentIsolation(Repository(root, "workspace-tabs-documents", source));
        SideTools(Repository(root, "workspace-tabs-tools", source));
        MountedWorkbench(Repository(root, "workspace-tabs-mounted", source));
    }

    private static void MountedWorkbench(string root)
    {
        using var app = new E2eWorkspace(root);
        var first = CreateWorkspace(app, "workspace-1");
        app.Open("README.md", true);
        var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom");
        var add = app.Find<Button>("AddToGroup_" + bottom.Id);
        app.Click(add); Until(() => add.ContextMenu!.IsOpen);
        app.Click(add.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "New terminal")), freshGesture: false);
        Until(() => app.Window.Layout.Tabs(bottom.Id).Any(tab => tab.Kind == "terminal"));
        var terminalCount = app.Window.Layout.Tabs(bottom.Id).Count(tab => tab.Kind == "terminal");
        CreateWorkspace(app, "workspace-2");
        var names = new[] { "WorkspaceWorkbench", "CenterRegion", "AuxiliaryRegion_left" };
        var frames = names.Select(app.Find<Control>).ToArray();
        var detached = 0;
        foreach (var frame in frames) frame.DetachedFromVisualTree += (_, _) => detached++;
        void Mounted()
        {
            Require(names.Select(app.Find<Control>).Zip(frames).All(pair => ReferenceEquals(pair.First, pair.Second)) && detached == 0,
                "Workspace switches must retain the mounted workbench, center region and left navigation region.");
        }
        Switch(app, first, "workspace-1");
        Require(app.Tab("README.md").IsVisible && app.Window.Layout.Tabs(bottom.Id).Count(tab => tab.Kind == "terminal") == terminalCount,
            "Retargeting the workbench must restore the first workspace's document and terminal tabs.");
        Mounted();
        Switch(app, root); Mounted();
        Console.WriteLine("PASS upstream workspace-tabs.spec.ts: switching workspaces re-targets the mounted workbench instead of remounting it");
    }

    internal static string Repository(string root, string name, string source)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        Git(root, "clone", "--no-local", "--depth=1", "--no-checkout", new Uri(Path.GetFullPath(source)).AbsoluteUri, path);
        Git(path, "sparse-checkout", "init", "--cone");
        Git(path, "sparse-checkout", "set", ".sharprail-test-only");
        Git(path, "checkout", "HEAD");
        return path;
    }

    internal static void Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        output.GetAwaiter().GetResult();
        Require(process.ExitCode == 0, error.GetAwaiter().GetResult());
    }

    internal static string CreateWorkspace(E2eWorkspace app, string name)
    {
        var parent = app.Root + "-worktrees";
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, name);
        Until(() => app.Window.GetLogicalDescendants().OfType<Button>().Any(button => Equals(ToolTip.GetTip(button), "Create worktree") && button.IsEnabled));
        app.Click(app.Window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), "Create worktree")));
        Until(() => app.Window.OwnedWindows.Any(window => window.Title == "Create worktree"));
        var prompt = app.Window.OwnedWindows.Single(window => window.Title == "Create worktree");
        var fields = prompt.GetLogicalDescendants().OfType<TextBox>().ToArray();
        fields[0].Text = path; fields[1].Text = name;
        app.Click(prompt.GetLogicalDescendants().OfType<Button>().Single(button => button.IsDefault));
        Until(() => app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == path && app.Find<TextBlock>("BranchLabel").Text == name);
        Require(app.Find<TextBlock>("WorkspaceLabel").Text == name, "The title bar must show the active workspace name.");
        return path;
    }

    internal static void Switch(E2eWorkspace app, string path, string? branch = null)
    {
        Until(() => app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.ContextMenu is not null && Equals(ToolTip.GetTip(button), path)));
        app.Click(app.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.ContextMenu is not null && Equals(ToolTip.GetTip(button), path)));
        Until(() => app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == path && (branch is null || app.Find<TextBlock>("BranchLabel").Text == branch));
        Require(app.Find<TextBlock>("WorkspaceLabel").Text == (path == app.Root ? "Default workspace" : Path.GetFileName(path)),
            "Switching workspaces must update the title bar scope.");
    }

    private static void DocumentIsolation(string root)
    {
        using var app = new E2eWorkspace(root);
        var first = CreateWorkspace(app, "workspace-1");
        Require(app.Tabs.Count == 0, "A new workspace must have no document tabs; AI tabs are excluded.");
        app.Click(app.Find<Button>("Tab_files")); app.Open("README.md", true);
        Require(app.Tabs.Count == 1, "Opening a kept document must add one tab.");
        CreateWorkspace(app, "workspace-2");
        Require(app.Tabs.Count == 0 && !app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "Tab_markdown_README.md"),
            "A document from another workspace must not remain visible.");
        Switch(app, first, "workspace-1");
        Require(app.Tabs.Count == 1 && app.Tabs.Single().Path == "README.md" && app.Tab("README.md").IsVisible,
            "Returning to a workspace must restore its resource tabs.");
        Console.WriteLine("PASS upstream workspace-tabs.spec.ts: editor tabs are scoped to the active workspace");
    }

    private static void SideTools(string root)
    {
        using var app = new E2eWorkspace(root, openFiles: false);
        var first = CreateWorkspace(app, "workspace-1");
        var second = CreateWorkspace(app, "workspace-2");
        Switch(app, first, "workspace-1");
        var specs = app.Window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "specs"));
        var projects = app.Find<Button>("Tab_projects");
        var menu = projects.ContextMenu!;
        app.ContextAction(projects, "Move to pane");
        var move = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move to pane"));
        Until(() => move.IsSubMenuOpen);
        app.Click(move.Items.OfType<MenuItem>().Single(item => Equals(item.Header, specs.Region + ": " + app.Window.Layout.Selected(specs.Id)!.Title)), freshGesture: false);
        app.Click(app.Find<Button>("Tab_projects"));
        Require(Selected(app, "projects"), "Moving Projects must leave it selected in the Specs group.");
        Switch(app, second, "workspace-2");
        Require(Selected(app, "projects"), "Selected side tools must follow a workspace switch.");
        Switch(app, root);
        Require(Selected(app, "projects"), "Selected side tools must follow the return to the default workspace.");
        app.Click(app.Find<Button>("Tab_review"));
        Require(Selected(app, "review"), "Review must become the selected tool.");
        Switch(app, first, "workspace-1");
        Require(Selected(app, "projects") && Selected(app, "review"), "Both side groups must retain their selected tools across switches.");
        Console.WriteLine("PASS upstream workspace-tabs.spec.ts: the selected side tool follows workspace switches");
    }

    private static bool Selected(E2eWorkspace app, string id) =>
        ((ISelectionItemProvider)ControlAutomationPeer.CreatePeerForElement(app.Find<Button>("Tab_" + id))!).IsSelected;
}
