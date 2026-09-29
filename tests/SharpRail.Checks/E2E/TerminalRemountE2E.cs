using Avalonia.Controls;
using Avalonia.LogicalTree;
using SharpRail.UI.Docking;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class TerminalRemountE2E
{
    internal static void Run(string root)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("SKIP upstream workspace-tabs terminal remount: embedded terminals require macOS.");
            return;
        }
        using var git = new IsolatedGit(Path.Combine(root, "terminal-remount-git"));
        var directory = IsolatedGit.Repository(Path.Combine(root, "terminal-remount"));
        using var app = new E2eWorkspace(directory, openFiles: false);
        var first = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-2");
        var shared = new DockTab("terminal:shared", "Terminal", "terminal");
        app.Window.Layout.Open(shared, keep: true);
        Until(() => Surface(app) is not null);
        var carried = Surface(app)!;
        carried.Tag = "workspace-2";
        WorkspaceTabsE2E.Switch(app, first, "workspace-1");
        app.Window.Layout.Open(shared, keep: true);
        Until(() => Surface(app) is not null);
        var remounted = Surface(app)!;
        Require(!ReferenceEquals(remounted, carried) && !Equals(remounted.Tag, "workspace-2"),
            "A terminal tab with the same id must mount a fresh body in another workspace.");
        Console.WriteLine("PASS upstream workspace-tabs.spec.ts: a same-id terminal body remounts instead of carrying across workspaces");
    }

    private static Control? Surface(E2eWorkspace app) => app.Window.GetLogicalDescendants().OfType<Control>()
        .SingleOrDefault(control => control.Name == "TerminalSurface_terminal_shared");
}
