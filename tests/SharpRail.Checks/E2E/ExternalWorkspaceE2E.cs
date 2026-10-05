using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>The registry-backed rail: attaching and forgetting an existing worktree, background project rows and removed-workspace cleanup.</summary>
internal static class ExternalWorkspaceE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "external-workspace-git"));
        AttachAndForget(Path.Combine(root, "external-attach"));
        BackgroundProjectRows(Path.Combine(root, "external-background"));
        Reveal(Path.Combine(root, "external-reveal"));
    }

    private static WorkspaceRecord Record(E2eWorkspace app, string path) => app.State!.Current.Workspaces.Single(workspace => workspace.Path == path);

    private static Window OpenExistingDialog(E2eWorkspace app, string project)
    {
        // The rail is rebuilt by broadcasts, so the row and its menu are looked up afresh rather than held.
        ContextMenu Menu() => Controls(app).OfType<Grid>().Single(item => item.Name == "ProjectRow" && Equals(item.Tag, project)).ContextMenu!;
        Settle(600);
        app.Click(ProjectName(app, project), mouseButton: Avalonia.Input.MouseButton.Right);
        Until(() => Menu().IsOpen);
        Choose(app, Menu(), "ProjectMenuOpenExisting");
        return Dialog(app, "ExistingWorktreeDialog");
    }

    private static Button[] Candidates(Window dialog) =>
        dialog.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "ExistingWorktreeCandidate").ToArray();

    private static void AttachAndForget(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var project = app.Root;
        Until(() => app.State!.Current.WorkspacesOf(project).Any(workspace => workspace.Kind == WorkspaceKinds.Default));
        var outside = Path.Combine(directory, "outside");
        var loose = Path.Combine(directory, "loose");
        Git(project, "worktree", "add", outside, "-b", "outside-branch");
        Git(project, "worktree", "add", "--detach", loose);
        _ = app.Window.RefreshAsync();
        Settle(600);
        Require(!WorktreePaths(app).Any(), "A worktree made behind SharpRail is not a workspace until it is attached.");

        // A checkout made in a terminal reaches the rail through the host's registry, without a reload.
        Git(project, "switch", "-c", "moved");
        Until(() => ItemText(app, project, "WorkspaceBranch") == "moved");

        var dialog = OpenExistingDialog(app, project);
        Until(() => Candidates(dialog).Length == 2);
        Require(Candidates(dialog).Single(row => Equals(row.Tag, loose)) is { IsEnabled: false } detached && Text(detached).Contains("Detached HEAD", StringComparison.Ordinal) &&
            Text(Candidates(dialog).Single(row => Equals(row.Tag, outside))).Contains("outside-branch", StringComparison.Ordinal),
            "The chooser lists unattached worktrees by branch and path and keeps detached ones disabled.");
        app.Click(Candidates(dialog).Single(row => Equals(row.Tag, outside)));
        Until(() => !app.Window.OwnedWindows.Any() && Active(app, outside) && WorktreePaths(app).Contains(outside));
        Require(Record(app, outside).Kind == WorkspaceKinds.External && ItemText(app, outside, "WorkspaceBranch") == "outside-branch" &&
            ItemText(app, outside, "WorkspaceName") == "outside" && !Directory.Exists(Path.Combine(outside, ".sharprail")),
            "An attached worktree is an external workspace shown by its directory and branch, left untouched.");
        Until(() => app.Window.Layout.State.Workspaces.ContainsKey(outside));

        var menu = OpenWorkspaceMenu(app, outside);
        Require(MenuEntry(menu, "WorkspaceReveal") is { IsVisible: true } && MenuEntry(menu, "WorkspaceRename") is null &&
            Equals(MenuEntry(menu, "WorkspaceRemove")!.Header, "Remove from SharpRail"), "An external row can be revealed and removed from SharpRail, never renamed.");
        Choose(app, menu, "WorkspaceRemove");
        var confirm = Dialog(app);
        Require(confirm.Title == "Remove outside from SharpRail?" && Text(confirm).Contains("stay untouched", StringComparison.Ordinal),
            "Forgetting an external workspace promises to leave the checkout alone.");
        app.Click(confirm.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Remove from SharpRail" }));
        Until(() => !WorktreePaths(app).Any() && app.Window.AtProjectHome && HasWelcome(app));
        Require(Directory.Exists(outside) && Git(project, "worktree", "list").Contains(outside, StringComparison.Ordinal) &&
            Git(outside, "rev-parse", "--abbrev-ref", "HEAD") == "outside-branch", "Removing from SharpRail keeps the worktree and its branch.");
        Until(() => !app.Window.Layout.State.Workspaces.ContainsKey(outside));
        Require(!app.Workbench.Profile.Data.GitSelections.ContainsKey(outside) && !app.Window.Slot.Layout.Workspaces.ContainsKey(outside),
            "A removed workspace leaves no view or selection behind, even though it was the one shown.");
        Console.WriteLine("PASS external workspaces: Open existing worktree attaches in place, Remove from SharpRail forgets it and drops its view");
    }

    private static void BackgroundProjectRows(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var first = app.Root;
        var workspace = CreateWorkspaceViaDialog(app);
        var second = IsolatedGit.Repository(Path.Combine(directory, "second-project"));
        AddProject(app, "Open project", second);
        Until(() => app.Window.WorkspaceMounted && app.Window.ProjectRoot == second);
        Until(() => app.State!.Current.WorkspacesOf(second).Any());
        Require(ItemText(app, workspace, "WorkspaceBranch") == Path.GetFileName(workspace) && ItemText(app, first, "WorkspaceName") == "Default" &&
            ItemText(app, second, "WorkspaceName") == "Default", "Every open project lists its own workspaces, whichever one is shown.");

        var expand = Buttons(app).Single(button => button.Name == "ProjectExpand" && Equals(button.Tag, first));
        app.Click(expand);
        Until(() => !Controls(app).OfType<Grid>().Any(item => item.Name == "WorkspaceItem" && Equals(item.Tag, workspace) && item.IsEffectivelyVisible));
        var row = Controls(app).OfType<Grid>().Single(item => item.Name == "ProjectRow" && Equals(item.Tag, first));
        Require(row.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ProjectWorkspaceCount").Text == "1" &&
            !row.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "AddWorkspace"),
            "A collapsed background project shows its workspace count; only the shown project offers Add workspace.");
        app.Click(Buttons(app).Single(button => button.Name == "ProjectExpand" && Equals(button.Tag, first)));
        app.Click(Select(app, workspace));
        Until(() => Active(app, workspace) && app.Window.ProjectRoot == first);
        Until(() => app.Window.Layout.State.Workspaces.ContainsKey(workspace));

        // Removed by another client while this window shows a different project: the row, the view and the selection go.
        app.Click(Select(app, second));
        Until(() => Active(app, second) && app.Window.ProjectRoot == second);
        var id = Record(app, workspace).Id;
        // Off the UI thread, whose synchronisation context this call must not wait on.
        Task.Run(async () => await app.Host.ApplyWorkspaceActionAsync(WorkspaceAction.Remove(id))).GetAwaiter().GetResult();
        Until(() => !Controls(app).OfType<Grid>().Any(item => item.Name == "WorkspaceItem" && Equals(item.Tag, workspace)) &&
            !app.Window.Layout.State.Workspaces.ContainsKey(workspace));
        Require(Active(app, second) && !Directory.Exists(workspace), "Removing a background project's workspace leaves the shown one alone.");
        Console.WriteLine("PASS external workspaces: background projects list and open their own workspaces, and a removed one leaves the frame");
    }

    private static void Reveal(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        var bin = Path.Combine(directory, "bin");
        var log = Path.Combine(directory, "reveal.log");
        Directory.CreateDirectory(bin);
        var opener = Path.Combine(bin, OperatingSystem.IsMacOS() ? "open" : "xdg-open");
        File.WriteAllText(opener, $"#!/bin/sh\necho \"$@\" >> '{log}'\n");
        File.SetUnixFileMode(opener, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + path);
        try
        {
            using var app = OpenFixtureProject(directory);
            var workspace = CreateWorkspaceViaDialog(app);
            Choose(app, OpenWorkspaceMenu(app, workspace), "WorkspaceReveal");
            Until(() => File.Exists(log) && File.ReadAllText(log).Trim().Length > 0);
            Require(File.ReadAllText(log).Trim() == workspace, "Reveal must open the workspace folder in the host's file manager.");
        }
        finally { Environment.SetEnvironmentVariable("PATH", path); }
        Console.WriteLine("PASS external workspaces: Reveal in file manager opens the workspace folder");
    }
}