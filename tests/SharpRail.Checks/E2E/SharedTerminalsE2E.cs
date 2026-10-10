using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Terminal;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.TerminalsE2E;
using static SharpRail.Checks.E2E.TerminalSessionsE2E;

namespace SharpRail.Checks.E2E;

// Two clients of one real gRPC host, each with its own profile, connection and grid: which terminals exist is
// the host's catalog, and a terminal is held by one client, and watched by the others, until another takes it over.
internal static class SharedTerminalsE2E
{
    internal static void Run(string root)
    {
        Shared(root);
        InProjects(root);
        Renderers(root);
    }

    // The Android phone client: centre tabs live in Projects, a page of its own, and the bottom panel starts hidden.
    private static void InProjects(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "terminals-listed"));
        using var host = new GrpcHost(project);
        var a = Client(host, project, "desk", project, host.Client());
        E2eWorkspace? b = null;
        try
        {
            Until(() => a.Window.WorkspaceMounted);
            var workspace = WorkspaceTabsE2E.CreateWorkspace(a, "workspace-1");
            var initial = TerminalTabs(a).Single();
            var first = Ready(a, initial);
            first.Run("echo LISTED_$((40+2))");
            var bottom = a.Window.Layout.State.Groups.Single(group => a.Window.Layout.Tabs(group.Id).Any(tab => tab.Kind == "terminal")).Id;
            a.Click(a.Find<Button>("NewTerminal_" + bottom));
            Until(() => TerminalTabs(a).Length == 2);
            Ready(a, TerminalTabs(a)[1]);

            b = new E2eWorkspace(host.Address, Token, project, project + "-phone-profile", project, terminals: host.Client(), tabsInProjects: true, compact: true,
                prepare: profile => { profile.Data.Windows[0].Layout = DockState.Preset("focus"); profile.Data.Windows[0].DefaultPreset = "focus"; });
            Until(() => b.Window.WorkspaceMounted);
            b.Click(b.Find<Button>("PageProjects"));
            Until(() => Listed(b, workspace).Select(AutomationProperties.GetName).SequenceEqual(["Terminal 1", "Terminal 2"]));
            Console.WriteLine("PASS shared terminals: a client that keeps tabs in Projects lists a workspace's terminals before it ever opened it");

            b.Click(Listed(b, workspace)[0]);
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace && Ids(b).SequenceEqual(Ids(a)) && Central(b));
            var watched = Watched(View(b, initial));
            Expect(watched, "LISTED_42");
            Until(() => b.Window.Layout.Selected(b.Window.Layout.State.Center.Leaves().First())?.Id == initial.Id && View(b, initial).IsWatching && !b.Window.Layout.State.LeftVisible);
            b.Click(b.Find<Button>("PageProjects"));
            Until(() => Strips(b).SequenceEqual(Ids(b)));
            Require(!b.Window.Layout.State.BottomVisible && host.Host.Started.Count(request => request.SessionId == TerminalTab.SessionFor(workspace, initial.Id)) == 1,
                "The terminal chosen in Projects must open in the centre, watched, without revealing the hidden bottom or starting another shell.");
            a.Click(a.Find<Button>("NewTerminal_" + bottom));
            Until(() => TerminalTabs(a).Length == 3 && Ids(b).SequenceEqual(Ids(a)) && Central(b) && Strips(b).SequenceEqual(Ids(b)));
            Console.WriteLine("PASS shared terminals: with tabs in Projects every terminal of the workspace sits in the centre and its strip there, whoever holds it");
        }
        finally { b?.Dispose(); a.Dispose(); }
    }

    private static Button[] Listed(E2eWorkspace app, string workspace) => [.. app.Window.GetLogicalDescendants().OfType<ContentControl>()
        .Where(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, workspace))
        .SelectMany(host => host.GetLogicalDescendants().OfType<Button>()).Where(button => button.Name == "WorkspaceTabPreview")];

    private static bool Central(E2eWorkspace app) =>
        TerminalTabs(app).All(tab => app.Window.Layout.State.Center.Leaves().Any(group => app.Window.Layout.Tabs(group).Any(item => item.Id == tab.Id)));

    // The live strips under the active workspace in Projects.
    private static string[] Strips(E2eWorkspace app) => [.. app.Window.GetLogicalDescendants().OfType<StackPanel>().Where(panel => panel.Name == "CenterTabsInProjects")
        .SelectMany(panel => panel.GetLogicalDescendants().OfType<Button>()).Select(button => button.Name ?? "").Where(name => name.StartsWith("Tab_terminal", StringComparison.Ordinal))
        .Select(name => TerminalTabs(app).FirstOrDefault(tab => "Tab_" + tab.Id.Replace(':', '_') == name)?.Id ?? name)];

    // A renderer that cannot watch (Metal) yields first and the view then watches with one that can; a host that
    // predates watching leaves the offer over an empty body, as before.
    private static void Renderers(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "terminals-renderers"));
        using var host = new GrpcHost(project);
        var metal = host.Client();
        metal.WatchesWhenAsked = true;
        var a = Client(host, project, "desk", project, host.Client());
        E2eWorkspace? b = null;
        try
        {
            Until(() => a.Window.WorkspaceMounted);
            var workspace = WorkspaceTabsE2E.CreateWorkspace(a, "workspace-1");
            var initial = TerminalTabs(a).Single();
            Ready(a, initial).Run("echo METAL_$((40+2))");
            b = Client(host, project, "metal", workspace, metal);
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace);
            Expect(Watched(View(b, initial)), "METAL_42");
            Require(metal.Attaches.Select(request => (request.Yield, request.Watch)).SequenceEqual([(true, false), (true, true)]) && View(a, initial) is { IsWatching: false, IsDetached: false },
                "A renderer that cannot watch must yield, and the view must then watch with one that can.");
            Console.WriteLine("PASS shared terminals: a view whose renderer cannot watch falls back to one that can");

            host.Host.Legacy = true;
            b.Dispose();
            b = Client(host, project, "metal", workspace, metal);
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace);
            var view = View(b, initial);
            Until(() => view.IsDetached);
            Require(!view.IsWatching && !view.FindControl<ContentControl>("TerminalBody")!.IsVisible && (string?)TakeBack(view).Content == "Take over",
                "A host that cannot show a held terminal must leave it offered over a hidden body.");
            b.Click(TakeBack(view));
            Until(() => !view.IsDetached);
            var taken = Ready(b, initial);
            taken.Run("echo LEGACY_$((40+2))");
            Expect(taken, "LEGACY_42");
            var lost = View(a, initial);
            Until(() => lost.IsDetached);
            Require((string?)TakeBack(lost).Content == "Take it back", "On a host that cannot show a held terminal its previous holder must be offered it back.");
            Console.WriteLine("PASS shared terminals: a host that predates watching keeps the take-over offer without a live view");
        }
        finally { b?.Dispose(); a.Dispose(); }
    }

    private static void Shared(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "terminals-shared"));
        using var host = new GrpcHost(project);
        var desk = host.Client();
        var phone = host.Client();
        phone.Grid = (61, 17);
        var a = Client(host, project, "desk", project, desk);
        E2eWorkspace? b = null;
        try
        {
            Until(() => a.Window.WorkspaceMounted);
            var workspace = WorkspaceTabsE2E.CreateWorkspace(a, "workspace-1");
            var initial = TerminalTabs(a).Single();
            Require(initial is { Id: TerminalTab.InitialKey, Title: TerminalTab.InitialTitle }, "A workspace's first terminal must have the key every client gives it.");
            var first = Ready(a, initial);
            var held = View(a, initial);
            first.Run("TR_HELD=desk");
            var bottom = a.Window.Layout.State.Groups.Single(group => a.Window.Layout.Tabs(group.Id).Any(tab => tab.Kind == "terminal")).Id;
            a.Click(a.Find<Button>("NewTerminal_" + bottom));
            Until(() => TerminalTabs(a).Length == 2);
            Ready(a, TerminalTabs(a)[1]);

            b = Client(host, project, "phone", workspace, phone);
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace && Ids(b).SequenceEqual(Ids(a)));
            Require(TerminalTabs(b).Select(tab => tab.Title).SequenceEqual(["Terminal 1", "Terminal 2"]), "A second client must show the first client's terminals under their titles.");
            Console.WriteLine("PASS shared terminals: a second client shows the terminals the first client opened");

            var passive = View(b, initial);
            var watched = Watched(passive);
            Settle(500);
            Require((string?)TakeBack(passive).Content == "Take over" && !held.IsDetached && !held.IsWatching && phone.Started.Count == 0,
                "A terminal another client holds must be offered, not taken and not started again.");
            Require(watched.Text.Contains("TR_HELD=desk", StringComparison.Ordinal) && passive.FindControl<ContentControl>("TerminalBody")!.IsEffectivelyVisible,
                "A terminal another client holds must show its current screen.");
            passive.Write("echo GHOST_$((40+2))\r");
            passive.FocusTerminal();
            Require(ReferenceEquals(b.Window.FocusManager?.GetFocusedElement(), TakeBack(passive)), "A watched terminal's keyboard focus must be its offer, not its screen.");
            first.Run("echo \"SIZE_$(stty size | tr ' ' x)_HELD\"");
            Expect(first, "SIZE_30x120_HELD");
            Expect(watched, "SIZE_30x120_HELD");
            Require(watched.ShownGrid == (120, 30) && !first.Text.Contains("GHOST_42", StringComparison.Ordinal) && !held.IsWatching,
                "Watching must show the holder's grid and send it nothing.");
            Console.WriteLine("PASS shared terminals: a terminal another client holds is shown live and read-only, without detaching, resizing or typing into it");

            var group = b.Window.Layout.State.Groups.Single(item => b.Window.Layout.Tabs(item.Id).Any(tab => tab.Id == initial.Id)).Id;
            var focusedGroup = b.Window.Layout.View.FocusedGroup;
            a.Click(a.Find<Button>("NewTerminal_" + bottom));
            Until(() => TerminalTabs(a).Length == 3);
            var third = TerminalTabs(a)[2];
            Ready(a, third);
            Until(() => Ids(b).SequenceEqual(Ids(a)));
            Settle(500);
            Require(b.Window.Layout.Tabs(group).Any(tab => tab.Id == third.Id) && b.Window.Layout.Selected(group)?.Id == initial.Id &&
                b.Window.Layout.View.FocusedGroup == focusedGroup && TopLevel.GetTopLevel(a.Window.FocusManager?.GetFocusedElement() as Control) == a.Window &&
                b.Window.GetLogicalDescendants().OfType<TerminalView>().Single() == passive && View(a, third) is { IsDetached: false, IsWatching: false },
                "A terminal opened elsewhere must arrive beside the others without selection, focus or an attachment.");
            Console.WriteLine("PASS shared terminals: a terminal opened in one client appears passively in the other");

            CloseTab(b, third);
            Until(() => TerminalTabs(a).Length == 2 && TerminalTabs(b).Length == 2);
            CloseTab(a, TerminalTabs(a)[1]);
            Until(() => TerminalTabs(a).Length == 1 && TerminalTabs(b).Length == 1);
            Require(!a.Window.OwnedWindows.Any() && Catalog(host, workspace).SequenceEqual([TerminalTab.InitialKey]),
                "A terminal closed in one client must leave every client and the host's catalog without asking again.");
            Console.WriteLine("PASS shared terminals: closing a terminal in one client removes it from the other");

            var second = TakeOver(b, initial);
            second.Run("echo \"TAKEN=$TR_HELD SIZE_$(stty size | tr ' ' x)_TAKEN\"");
            Expect(second, "TAKEN=desk SIZE_17x61_TAKEN");
            var lost = Watched(held);
            second.Run("echo \"STILL=$TR_HELD\"");
            Expect(lost, "STILL=desk");
            Require((string?)TakeBack(held).Content == "Take it back" && lost.ShownGrid == (61, 17), "The client that held the terminal must keep watching it, at the new holder's grid, and be offered it back.");
            Console.WriteLine("PASS shared terminals: taking a terminal over gives the shell the new holder's size and leaves its holder watching");

            a.Click(TakeBack(held));
            Until(() => !held.IsWatching);
            var back = Ready(a, initial);
            back.Run("echo \"BACK=$TR_HELD SIZE_$(stty size | tr ' ' x)_BACK\"");
            Expect(back, "BACK=desk SIZE_30x120_BACK");
            Expect(Watched(View(b, initial)), "BACK=desk SIZE_30x120_BACK");
            Require((string?)TakeBack(View(b, initial)).Content == "Take it back", "The client the terminal was taken back from must watch it in turn.");
            Require(host.Host.Started.Count(request => request.SessionId == TerminalTab.SessionFor(workspace, initial.Id)) == 1, "Taking a terminal over and back must keep one shell.");
            Console.WriteLine("PASS shared terminals: the first holder takes the terminal back at its own size");

            // A relaunch restores the tab from this client's own layout; that is no more a reason to take it.
            b.Dispose();
            b = Client(host, project, "phone", workspace, phone);
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace && TerminalTabs(b).Length == 1);
            Until(() => View(b, initial).IsWatching);
            Settle(300);
            Require(held is { IsDetached: false, IsWatching: false } && (string?)TakeBack(View(b, initial)).Content == "Take over", "A relaunched client must not take a terminal it merely restored.");
            a.Dispose();
            b.Dispose();
            b = Client(host, project, "phone", workspace, phone);
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace);
            var free = Ready(b, TerminalTabs(b).Single());
            free.Run("echo \"FREE=$TR_HELD\"");
            Expect(free, "FREE=desk");
            Console.WriteLine("PASS shared terminals: a restored tab watches its holder's terminal and attaches once nobody holds it");
        }
        finally { b?.Dispose(); if (a.Workbench.Windows.Count > 0) a.Dispose(); }
    }

    private static E2eWorkspace Client(GrpcHost host, string project, string name, string start, E2eTerminals terminals) =>
        new(host.Address, Token, project, project + "-" + name + "-profile", start, terminals: terminals);

    private static string[] Ids(E2eWorkspace app) => [.. TerminalTabs(app).Select(tab => tab.Id)];

    private static string[] Catalog(GrpcHost host, string workspace) =>
        [.. (host.Host.HostService!.Catalog.Workspaces.GetValueOrDefault(workspace) ?? []).Select(tab => tab.Key)];
}