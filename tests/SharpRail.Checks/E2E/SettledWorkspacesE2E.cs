using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>Settled workspaces in the rail, the header switcher and Settings, with a client clock moved past the idle window.</summary>
internal static class SettledWorkspacesE2E
{
    private const long Day = 24L * 60 * 60 * 1000;

    internal static void Run(string root)
    {
        Partition();
        using var git = new IsolatedGit(Path.Combine(root, "settled-git"));
        using var app = OpenFixtureProject(Path.Combine(root, "settled"));
        Until(() => app.Window.Workbench.State.SettlingSupported);
        var older = CreateWorkspaceViaDialog(app);
        var newer = CreateWorkspaceViaDialog(app);
        Require(RailOrder(app).SequenceEqual([app.Window.ProjectRoot, newer, older]), "The rail lists Default first, then newest created first: " + string.Join(", ", RailOrder(app)));

        // Settle by hand: the row goes to a collapsed shelf, and its chip says why once the shelf is open.
        Choose(app, OpenWorkspaceMenu(app, older), "WorkspaceSettle");
        Until(() => Shelf(app) is { } shelf && Text(shelf) == "Settled · 1" && !WorktreePaths(app).Contains(older));
        app.Click(Shelf(app)!);
        Until(() => WorktreePaths(app).Contains(older) && Chip(app, older) == "by you");
        Console.WriteLine("PASS settled workspaces: Settle moves a row to the collapsed Settled shelf with its reason");

        app.Click(Toggle(app, older), freshGesture: false);
        Until(() => Shelf(app) is null && app.Window.Workbench.State.Current.Workspaces.Single(workspace => workspace.Path == older).SettledOverride == SettledOverrides.Active);
        Console.WriteLine("PASS settled workspaces: Keep active returns a row to the live list and pins it");

        // Idle: past the window a row without an override settles; a pinned row and the workspace you are in stay live.
        var host = app.Window.Workbench.State;
        var quiet = CreateWorkspaceViaDialog(app);
        app.Click(Select(app, newer));
        Until(() => Active(app, newer));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        app.Window.SettlingClock = () => now + 8 * Day;
        app.Window.RefreshSettling();
        Until(() => Shelf(app) is { } shelf && Text(shelf) == "Settled · 1" && WorktreePaths(app).Contains(older));
        Until(() => app.Window.Toasts.Items.Any(toast => toast.Title == "Moved 1 quiet workspace to Settled"));
        // The shelf stays open as this window left it.
        Until(() => Chip(app, quiet) == "idle 8d" && Chip(app, older) is null);
        Console.WriteLine("PASS settled workspaces: idle rows settle on the client clock while pinned and active ones stay live, announced once");

        // The switcher lists live siblings and the shelf in a submenu.
        var pill = app.Find<Button>("ScopeWorkspace");
        app.Click(pill);
        var menu = pill.ContextMenu!;
        Until(() => menu.IsOpen);
        var settled = MenuEntry(menu, "ScopeWorkspaceSettled") ?? throw new InvalidOperationException("The switcher has no Settled submenu.");
        Require(Equals(settled.Header, "Settled · 1") && settled.Items.OfType<MenuItem>().Single().Tag as string == quiet &&
            menu.Items.OfType<MenuItem>().Where(item => item.Name == "ScopeWorkspaceOption").Select(item => item.Tag as string).SequenceEqual([app.Window.ProjectRoot, older]),
            "The switcher lists live siblings, then the shelf.");
        CloseMenu(menu);

        // Opening a settled workspace keeps it on the shelf and says so in the header.
        app.Click(Select(app, quiet));
        Until(() => Active(app, quiet) && app.Find<TextBlock>("ScopeSettled").IsVisible && Shelf(app) is not null);
        Console.WriteLine("PASS settled workspaces: the switcher's Settled submenu, and an opened settled workspace stays settled with its caption");

        // Never settles nothing automatically; the header follows.
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OpenSettingsViews.Any());
        var settings = app.Window.OpenSettingsViews.Single();
        app.Click(Named<Button>(settings, "Settings_Workspaces"));
        Until(() => settings.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "SettleIdle_Never"));
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SettleIdle_Never"));
        Until(() => host.Current.Settings.SettleIdleDays is null && Shelf(app) is null && !app.Find<TextBlock>("ScopeSettled").IsVisible);
        settings.Close();
        Console.WriteLine("PASS settled workspaces: Settings › Workspaces sets the shared idle window, Never included");
    }

    private static void Partition()
    {
        var now = 100 * Day;
        static WorkspaceRecord Row(string path, long? active, string? settled = null, string kind = WorkspaceKinds.Managed) =>
            new(path, "/p", kind, path, path, "main") { LastActiveAt = active, SettledOverride = settled };
        var rows = new[]
        {
            Row("/p", 0, kind: WorkspaceKinds.Default), Row("/a", now - 30 * Day), Row("/b", now - Day, SettledOverrides.Settled),
            Row("/c", now - 30 * Day, SettledOverrides.Active), Row("/d", now - 2 * Day), Row("/e", now - 20 * Day)
        };
        var partition = WorkspaceSettling.Partition(rows, true, 7, now, latchedLive: "/e");
        Require(partition.Live.Select(row => row.Path).SequenceEqual(["/p", "/e", "/d", "/c"]) &&
            partition.Settled.Select(row => row.Workspace.Path + ":" + row.Reason).SequenceEqual(["/b:ByYou", "/a:Idle"]),
            "Partition: Default first and never settled, overrides first, the latch keeps a row live, idle rows settle.");
        var recent = WorkspaceSettling.Partition(rows, true, null, now, order: WorkspaceOrder.Recent);
        Require(recent.Live.Select(row => row.Path).SequenceEqual(["/p", "/d", "/e", "/c", "/a"]), "Recent order sorts by activity and Never settles only overrides.");
        Require(WorkspaceSettling.Partition(rows, false, 7, now).Live.SequenceEqual(rows), "An older host keeps its flat order and stale fields settle nothing.");
        Console.WriteLine("PASS settled workspaces: one partition with override, latch, idle and order rules");
    }

    private static string[] RailOrder(E2eWorkspace app) => Controls(app).OfType<Grid>()
        .Where(item => item.Name == "WorkspaceItem" && item.IsEffectivelyVisible).Select(item => (string)item.Tag!).ToArray();

    private static Button? Shelf(E2eWorkspace app) => Controls(app).OfType<Button>().SingleOrDefault(button => button.Name == "SettledShelf" && button.IsEffectivelyVisible);

    private static string? Chip(E2eWorkspace app, string path) =>
        Item(app, path).GetLogicalDescendants().OfType<Border>().Single(border => border.Name == "WorkspaceSettledChip") is { IsVisible: true } chip
            ? ((TextBlock)chip.Child!).Text : null;

    private static Button Toggle(E2eWorkspace app, string path) =>
        Item(app, path).GetLogicalChildren().OfType<Button>().Single(button => button.Name == "WorkspaceSettleToggle");
}