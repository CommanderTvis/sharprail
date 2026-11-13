using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;

using Tereditor.UI.State;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

namespace Tereditor.Checks.E2E;

internal static class WorkspaceActionsE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "workspace-actions-git"));
        OpenIn(Path.Combine(root, "actions-open-in"));
        CopyPath(Path.Combine(root, "actions-copy-path"));
        Rename(Path.Combine(root, "actions-rename"));
        DefaultMenu(Path.Combine(root, "actions-default"));
        RightClick(Path.Combine(root, "actions-right-click"));
        KeyboardMenu(Path.Combine(root, "actions-keyboard"));
        OpenChanges(Path.Combine(root, "actions-open-changes"));
    }

    private static void OpenIn(string directory)
    {
        var bin = Path.Combine(directory, "bin");
        var log = Path.Combine(directory, "editor.log");
        Directory.CreateDirectory(bin);
        var code = Path.Combine(bin, "code");
        File.WriteAllText(code, $"#!/bin/sh\necho \"$@\" >> '{log}'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(code, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + path);
        try
        {
            using var app = OpenFixtureProject(directory);
            var workspace = CreateWorkspaceViaDialog(app);
            Until(() => Item(app, workspace).GetLogicalChildren().OfType<Button>().Any(button => button.Name == "WorkspaceSelect"));
            var probe = MenuFillProbe.Watch(Item(app, workspace).GetLogicalChildren().OfType<Button>().Single(button => button.Name == "WorkspaceSelect").ContextMenu!, "workspace actions menu");
            var menu = OpenWorkspaceMenu(app, workspace);
            var openIn = MenuEntry(menu, "WorkspaceOpenIn")!;
            app.Click(openIn, freshGesture: false);
            Until(() => openIn.IsSubMenuOpen && openIn.Items.OfType<MenuItem>().Any(item => item.Name == "WorkspaceOpenInEditor" && Equals(item.Header, "VS Code")));
            probe.RequireOpenedFilled();
            var vsCode = openIn.Items.OfType<MenuItem>().Single(item => item.Name == "WorkspaceOpenInEditor" && Equals(item.Header, "VS Code"));
            TopLevel.GetTopLevel(vsCode)!.UpdateLayout();
            app.Click(vsCode, freshGesture: false);
            Until(() => File.Exists(log) && File.ReadAllText(log).Trim().Length > 0);
            Require(File.ReadAllText(log).Trim() == workspace && workspace.Contains("/worktrees/sample-project-", StringComparison.Ordinal),
                "Open in must launch the detected editor at the worktree path.");
        }
        finally { Environment.SetEnvironmentVariable("PATH", path); }
        Console.WriteLine("PASS upstream workspace-actions.spec.ts: Open in launches the detected editor detached at the worktree path");
    }

    private static void CopyPath(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        Choose(app, OpenWorkspaceMenu(app, workspace), "WorkspaceCopyPath");
        var copied = app.Window.Clipboard!.TryGetTextAsync();
        Until(() => copied.IsCompleted);
        Require(copied.Result == workspace && Path.IsPathFullyQualified(copied.Result) && copied.Result.Contains("/worktrees/sample-project-", StringComparison.Ordinal),
            "Copy path must copy the worktree's absolute path.");
        Console.WriteLine("PASS upstream workspace-actions.spec.ts: Copy path copies the worktree's absolute path to the clipboard");
        Choose(app, OpenWorkspaceMenu(app, workspace), "WorkspaceCopyName");
        var name = app.Window.Clipboard!.TryGetTextAsync();
        Until(() => name.IsCompleted);
        Require(name.Result == Path.GetFileName(workspace), "Copy name must copy the workspace's display name.");
        Console.WriteLine("PASS fork workspace menu: Copy name copies the workspace's display name");

        // With another project open, this one's workspaces still copy their path; renaming and removal stay with their project.
        var second = IsolatedGit.Repository(Path.Combine(directory, "second-project"));
        AddProject(app, "Open project", second);
        Until(() => app.Window.ProjectRoot == second);
        var foreign = OpenWorkspaceMenu(app, workspace);
        Require(MenuEntry(foreign, "WorkspaceRename") is null or { IsVisible: false } && MenuEntry(foreign, "WorkspaceRemove") is null or { IsVisible: false },
            "Another project's workspace offers no rename or removal.");
        Choose(app, foreign, "WorkspaceCopyPath");
        var foreignPath = app.Window.Clipboard!.TryGetTextAsync();
        Until(() => foreignPath.IsCompleted);
        Require(foreignPath.Result == workspace, "Another project's workspace copies its absolute path.");
        Console.WriteLine("PASS workspace menu: another project's workspace copies its path without focusing that project");
    }

    private static TextBox StartRename(E2eWorkspace app, string workspace)
    {
        Choose(app, OpenWorkspaceMenu(app, workspace), "WorkspaceRename");
        Until(() => Item(app, workspace).GetLogicalChildren().OfType<TextBox>().Any(box => box.IsFocused));
        return Item(app, workspace).GetLogicalChildren().OfType<TextBox>().Single();
    }

    private static void Rename(string directory)
    {
        var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var name = Path.GetFileName(workspace);
        var branch = ItemText(app, workspace, "WorkspaceBranch");
        var input = StartRename(app, workspace);
        Require(input.Text == name && AutomationProperties.GetName(input) == "Workspace name" &&
            input.SelectionStart == 0 && input.SelectionEnd == name.Length, "Rename must start with the whole current name selected.");
        input.Text = "   ";
        app.Click(ProjectName(app, app.Root));
        Until(() => !Item(app, workspace).GetLogicalChildren().OfType<TextBox>().Any());
        Require(ItemText(app, workspace, "WorkspaceName") == name, "A blank rename must keep the current name.");

        input = StartRename(app, workspace);
        input.Text = "Cancelled Rename";
        Press(input, Avalonia.Input.Key.Escape);
        Until(() => !Item(app, workspace).GetLogicalChildren().OfType<TextBox>().Any());
        Require(ItemText(app, workspace, "WorkspaceName") == name, "Escape must cancel a rename.");

        input = StartRename(app, workspace);
        input.Text = "Manual Workspace Name";
        app.Click(ProjectName(app, app.Root));
        Until(() => ItemText(app, workspace, "WorkspaceName") == "Manual Workspace Name");
        Require(ItemText(app, workspace, "WorkspaceBranch") == branch && Git(workspace, "symbolic-ref", "--short", "HEAD") == branch &&
            Directory.Exists(workspace), "Renaming the display label must not change Git.");
        app.Dispose();
        Require(new ProfileStore(app.Root + "-profile").OpenState().Current.WorkspaceLabels.GetValueOrDefault(workspace) == "Manual Workspace Name",
            "The display label must persist in the local host state.");
        Console.WriteLine("PASS upstream workspace-actions.spec.ts: a managed workspace can rename its display label inline without changing Git");
    }

    private static void DefaultMenu(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var menu = OpenWorkspaceMenu(app, app.Root);
        Require(MenuEntry(menu, "WorkspaceOpenIn") is { IsVisible: true } && MenuEntry(menu, "WorkspaceCopyPath") is { IsVisible: true } &&
            MenuEntry(menu, "WorkspaceRename") is null && MenuEntry(menu, "WorkspaceRemove") is null,
            "The Default workspace menu must offer only non-mutating actions.");
        Console.WriteLine("PASS upstream workspace-actions.spec.ts: the Default workspace's kebab menu offers only non-mutating actions");
    }

    private static void RightClick(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        Require(Active(app, workspace) && !Active(app, app.Root), "The created workspace must be active.");
        var row = Select(app, app.Root);
        app.Click(row, mouseButton: MouseButton.Right);
        var menu = row.ContextMenu!;
        Until(() => menu.IsOpen);
        Settle(100);
        Require(MenuEntry(menu, "WorkspaceCopyPath") is not null && MenuEntry(menu, "WorkspaceRename") is null && MenuEntry(menu, "WorkspaceRemove") is null,
            "The Default workspace menu must offer only non-mutating actions.");
        Require(Active(app, workspace) && !Active(app, app.Root), "Right-click must not activate the workspace.");
        CloseMenu(menu);
        Console.WriteLine("PASS upstream workspace-actions.spec.ts: right-click opens the workspace menu without activating it");
    }

    private static void KeyboardMenu(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var row = Select(app, workspace);
        var menu = row.ContextMenu!;
        Require(!Item(app, workspace).GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "WorkspaceMenu"), "A workspace row has no three-dots button.");
        row.Focus();
        Until(() => row.IsFocused);
        app.Window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        Until(() => menu.IsOpen);
        Require(MenuEntry(menu, "WorkspaceRename") is not null && MenuEntry(menu, "WorkspaceRemove") is not null && MenuEntry(menu, "WorkspaceCopyPath") is not null, "Shift+F10 on the focused row opens the full workspace menu.");
        CloseMenu(menu);
        Console.WriteLine("PASS workspace actions: the row has no three-dots button; Shift+F10 on the focused row opens its menu");
    }

    private static void OpenChanges(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        File.AppendAllText(Path.Combine(workspace, "README.md"), "An edit to review.\n");
        bool Badged() => app.Window.GetLogicalDescendants().OfType<Grid>().Where(item => item.Name == "WorkspaceItem" && Equals(item.Tag, workspace))
            .SelectMany(item => item.GetLogicalDescendants().OfType<StackPanel>()).Any(panel => panel.Name == "WorkspaceDiffStats" && panel.IsVisible);
        Until(Badged);
        app.Click(Select(app, app.Root));
        Until(() => Active(app, app.Root));
        Settle(300);

        var clean = OpenWorkspaceMenu(app, app.Root);
        var entries = clean.Items.OfType<MenuItem>().ToArray();
        var index = Array.FindIndex(entries, item => item.Name == "WorkspaceOpenChanges");
        Require(index > 0 && entries[index - 1].Name == "WorkspaceOpenIn" && Equals(entries[index].Header, "Open changes"), "Open changes follows Open in.");
        Require(!entries[index].IsEnabled, "A workspace without changes offers Open changes disabled.");
        CloseMenu(clean);

        var dirty = OpenWorkspaceMenu(app, workspace);
        Require(MenuEntry(dirty, "WorkspaceOpenChanges") is { IsEnabled: true }, "Another workspace with changes offers Open changes.");
        Choose(app, dirty, "WorkspaceOpenChanges");
        Until(() => Active(app, workspace) && app.Window.Layout.Selected(app.Center) is { Kind: "changes", Scope: "all" or "branch" } &&
            app.Window.GetLogicalDescendants().OfType<Tereditor.UI.ChangesReviewPane>().Any(pane => pane.DiffFor("README.md") is not null));
        Console.WriteLine("PASS workspace menu: Open changes switches to that workspace and opens its changes review; without changes it is disabled");
    }
}