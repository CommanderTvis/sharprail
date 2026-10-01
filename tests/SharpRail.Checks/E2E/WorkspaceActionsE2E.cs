using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;

using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

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
        HoverKebab(Path.Combine(root, "actions-hover"));
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
            var menu = OpenWorkspaceMenu(app, workspace);
            var openIn = MenuEntry(menu, "WorkspaceOpenIn")!;
            app.Click(openIn, freshGesture: false);
            Until(() => openIn.IsSubMenuOpen && openIn.Items.OfType<MenuItem>().Any(item => item.Name == "WorkspaceOpenInEditor" && Equals(item.Header, "VS Code")));
            var vsCode = openIn.Items.OfType<MenuItem>().Single(item => item.Name == "WorkspaceOpenInEditor" && Equals(item.Header, "VS Code"));
            TopLevel.GetTopLevel(vsCode)!.UpdateLayout();
            app.Click(vsCode, freshGesture: false);
            Until(() => File.Exists(log) && File.ReadAllText(log).Trim().Length > 0);
            Require(File.ReadAllText(log).Trim() == workspace && workspace.Contains("/sample-project-worktrees/", StringComparison.Ordinal),
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
        Require(copied.Result == workspace && Path.IsPathFullyQualified(copied.Result) && copied.Result.Contains("/sample-project-worktrees/", StringComparison.Ordinal),
            "Copy path must copy the worktree's absolute path.");
        Console.WriteLine("PASS upstream workspace-actions.spec.ts: Copy path copies the worktree's absolute path to the clipboard");
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
        var kebab = Item(app, app.Root).GetLogicalChildren().OfType<Button>().Single(button => button.Name == "WorkspaceMenu");
        var menuCorner = menu.PointToScreen(new Point(menu.Bounds.Width, 0));
        var kebabCorner = kebab.PointToScreen(new Point(kebab.Bounds.Width, kebab.Bounds.Height));
        Require(Math.Abs(menuCorner.X - kebabCorner.X) < 8 && Math.Abs(menuCorner.Y - kebabCorner.Y) < 12,
            $"The right-click menu must anchor below the row's kebab (menu {menuCorner}, kebab {kebabCorner}).");
        Require(MenuEntry(menu, "WorkspaceCopyPath") is not null && MenuEntry(menu, "WorkspaceRename") is null && MenuEntry(menu, "WorkspaceRemove") is null,
            "The Default workspace menu must offer only non-mutating actions.");
        Require(Active(app, workspace) && !Active(app, app.Root), "Right-click must not activate the workspace.");
        CloseMenu(menu);
        Console.WriteLine("PASS upstream workspace-actions.spec.ts: right-click opens the workspace's kebab menu without activating it");
    }

    private static void HoverKebab(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var item = Item(app, workspace);
        var kebab = item.GetLogicalChildren().OfType<Button>().Single(button => button.Name == "WorkspaceMenu");
        app.Window.MouseMove(new Point(app.Window.Bounds.Width - 2, app.Window.Bounds.Height - 2));
        Settle(100);
        Require(kebab.Opacity == 0, "The kebab must stay quiet at rest on a pointer device.");
        app.Window.MouseMove(item.TranslatePoint(new Point(8, item.Bounds.Height / 2), app.Window)!.Value);
        Settle(100);
        Require(kebab.Opacity == 1, "Hovering the row must reveal its kebab.");
        Console.WriteLine("PASS upstream workspace-actions.spec.ts: the kebab is hover-only ONLY on devices that actually have hover — never invisible by default");
    }
}