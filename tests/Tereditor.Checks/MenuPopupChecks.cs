using System.Diagnostics;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using Tereditor.Checks.E2E;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks;

/// <summary>
/// Menus as the desktop app shows them: each in its own popup window (<see cref="Avalonia.Controls.Primitives.PopupRoot"/>), which keeps
/// the frame it is born with, rather than the overlay the other headless checks use. Opens real menus (the rail's workspace menu, the
/// Files tree menu, a terminal tab menu, the chat model menu) and a workspace-shaped one, and requires every icon to lie inside the
/// popup's visible frame by at least the menu's padding, with the labels of rows with and without icons on one edge.
/// </summary>
internal static class MenuPopupChecks
{
    internal static void Run(string root)
    {
        WorkspaceShaped();
        RealMenus(root);
        FileAndTabMenus(root);
        ClaudeChatUiChecks.ModelMenu(menu => MenuIconAudit.Audit("chat model menu", menu));
        Console.WriteLine("PASS menu popups: in native popup windows the workspace, Files, terminal tab and chat model menus keep every icon inside the frame, labels on one edge");
    }

    /// <summary>The gate's process shows popups in the window's overlay, so the gate runs <c>--menu-popups</c> as its own process.</summary>
    internal static void InOwnProcess()
    {
        var self = Environment.ProcessPath!;
        var start = new ProcessStartInfo(self) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--menu-popups");
        foreach (var variable in new[] { Runner.OwnerVariable, Runner.JobShardVariable, Runner.ResultVariable }) start.Environment.Remove(variable);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Console.Write(output);
        Require(process.ExitCode == 0, $"--menu-popups exited {process.ExitCode}: {output}{error.Result}");
    }

    // The rail's workspace menu as built: icon rows around plain rows, a submenu row, a row relabelled as it opens, a separator, opened at the pointer.
    private static void WorkspaceShaped()
    {
        var anchor = new Button { Content = "workspace", Width = 120, Height = 24 };
        var window = new Window { Width = 900, Height = 600, Content = anchor };
        window.Show();
        try
        {
            var menu = new ContextMenu();
            menu.Items.Add(Ui.IconMenu("Copy absolute path", () => { }, ActionIcon.CopyAbsolutePath));
            menu.Items.Add(Ui.Menu("Copy link", () => { }, false));
            var openIn = new MenuItem { Header = "Open in" };
            openIn.Items.Add(new MenuItem { Header = "Looking for editors…", IsEnabled = false });
            menu.Items.Add(openIn);
            menu.Items.Add(Ui.Menu("Open changes", () => { }, false));
            menu.Items.Add(Ui.Menu("Copy name", () => { }));
            menu.Items.Add(Ui.Menu("Reveal in file manager", () => { }));
            var unread = Ui.Menu("", () => { });
            menu.Opening += (_, _) => unread.Header = "Mark as unread";
            menu.Opened += (_, _) => unread.Header = "Mark as unread";
            menu.Items.Add(unread);
            menu.Items.Add(Ui.Menu("Rename", () => { }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.IconMenu("Remove worktree…", () => { }, ActionIcon.Delete));
            anchor.ContextMenu = menu;
            menu.Open(anchor);
            Settle(menu);
            MenuIconAudit.Audit("workspace-shaped menu", menu);
            menu.Close();
        }
        finally { window.Close(); }
    }

    private static void RealMenus(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "menu-popups-git"));
        using var app = WorkspaceFixture.OpenFixtureProject(Path.Combine(root, "menu-popups"));
        var workspace = WorkspaceFixture.CreateWorkspaceViaDialog(app);
        var menu = WorkspaceFixture.OpenWorkspaceMenu(app, workspace);
        Settle(menu);
        MenuIconAudit.Audit("rail workspace menu", menu);
        menu.Close();
    }

    private static void FileAndTabMenus(string root)
    {
        using var files = new E2eWorkspace(Path.Combine(root, "menu-popups-files"));
        E2eWorkspace.Until(() => files.Window.WorkspaceMounted);
        var row = files.FileRow("notes.txt");
        files.Click(row, mouseButton: Avalonia.Input.MouseButton.Right);
        var fileMenu = row.ContextMenu!;
        E2eWorkspace.Until(() => fileMenu.IsOpen);
        Settle(fileMenu);
        MenuIconAudit.Audit("Files tree menu", fileMenu);
        fileMenu.Close();

        const string tab = "menu-popups-terminal";
        files.Window.Layout.NewTerminal(files.Center, tab);
        E2eWorkspace.Until(() => files.Terminals.Views.Any(view => view.Started.IsCompletedSuccessfully));
        var tabButton = files.Find<Button>("Tab_" + tab);
        files.Click(tabButton, mouseButton: Avalonia.Input.MouseButton.Right);
        var tabMenu = tabButton.ContextMenu!;
        E2eWorkspace.Until(() => tabMenu.IsOpen);
        Settle(tabMenu);
        MenuIconAudit.Audit("terminal tab menu", tabMenu);
        tabMenu.Close();
    }

    internal static void Settle(ContextMenu menu)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
        Require(menu.IsOpen, "The menu must still be open.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}