using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Blueprint;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class BlueprintWorktreeStartChecks
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "blueprint-worktree-start");
        var project = IsolatedGit.Repository(Path.Combine(directory, "project"));
        var worktree = Path.Combine(directory, "feature");
        IsolatedGit.Run(project, "worktree", "add", "-b", "feature", worktree);
        using var app = new E2eWorkspace(project, openFiles: false, startPath: worktree);
        Until(() => app.Workbench.PluginRegistry.Active.Contains("blueprint") && app.Window.WorkspaceMounted);
        Require(app.Window.WorkspaceRoot == worktree && app.Window.ProjectRoot == project,
            "The fixture starts in its linked worktree, with the main project identity.");
        var launches = new List<LauncherCommandOptions>();
        app.Workbench.PluginRegistry.AddLauncher("fixture", new("claude", "Fixture author", "terminal", options =>
        {
            launches.Add(options);
            return ":";
        }, () => new(true)));
        Until(() => app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "WorkspaceDraftBlueprint"));
        app.Click(app.Find<Button>("WorkspaceDraftBlueprint"));
        Until(() => app.Window.OwnedWindows.Any(window => Equals(window.Tag, "BlueprintStart")));
        var dialog = app.Window.OwnedWindows.Single(window => Equals(window.Tag, "BlueprintStart"));
        T Find<T>(string name) where T : Control => dialog.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        Find<TextBox>("Brief").Text = "  A project blueprint launched from another worktree.  ";
        app.Click(Find<Button>("BlueprintStart"));
        Until(() => !app.Window.OwnedWindows.Contains(dialog) && launches.Count == 1 && app.Window.WorkspaceRoot == project &&
            app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "Blueprint"));
        BlueprintState? State(string workspace)
        {
            var pending = app.Workbench.Plugins!.CallAsync(new("blueprint", BlueprintContract.Get.Name, new BlueprintScope(workspace), "worktree-start-check")).AsTask();
            Until(() => pending.IsCompleted);
            return PluginJson.Convert<BlueprintChangedPayload>(pending.GetAwaiter().GetResult()).State;
        }
        Require(State(project) is
        { Source: BlueprintIdea { Brief: "A project blueprint launched from another worktree." }, Author: BlueprintTerminalAuthor { TabKey: "blueprint-author" } },
            "The workspace action authors the project's Default blueprint with the normalized idea and recorded terminal.");
        Require(State(worktree) is null, "Starting from a linked worktree does not create a separate Blueprint record there.");
        Require(app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Any(tab => tab.Kind == "terminal" && tab.Id == "blueprint-author"),
            "The author is visible in the Default workspace that received the blueprint.");
        Console.WriteLine("PASS Blueprint workspace action: linked worktree routes to project Default with one author and normalized idea");
    }
}