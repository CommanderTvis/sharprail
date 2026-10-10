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
// the host's catalog, and a terminal is held by one client until another takes it over.
internal static class SharedTerminalsE2E
{
    internal static void Run(string root)
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
            Until(() => passive.IsYielded);
            Settle(500);
            Require((string?)TakeBack(passive).Content == "Take over" && !held.IsDetached && phone.Started.Count == 0,
                "A terminal another client holds must be offered, not taken and not started again.");
            first.Run("echo \"SIZE_$(stty size | tr ' ' x)_HELD\"");
            Expect(first, "SIZE_30x120_HELD");
            Console.WriteLine("PASS shared terminals: showing a terminal another client holds neither detaches it nor resizes it");

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
                b.Window.GetLogicalDescendants().OfType<TerminalView>().Single() == passive && !View(a, third).IsDetached,
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
            Until(() => held.IsDetached);
            Require(!held.IsYielded && (string?)TakeBack(held).Content == "Take it back", "The client that held the terminal must be told and offered it back.");
            Console.WriteLine("PASS shared terminals: taking a terminal over detaches its holder and gives the shell the new holder's size");

            a.Click(TakeBack(held));
            var back = Ready(a, initial);
            back.Run("echo \"BACK=$TR_HELD SIZE_$(stty size | tr ' ' x)_BACK\"");
            Expect(back, "BACK=desk SIZE_30x120_BACK");
            Until(() => View(b, initial) is { IsDetached: true, IsYielded: false });
            Require(host.Host.Started.Count(request => request.SessionId == TerminalTab.SessionFor(workspace, initial.Id)) == 1, "Taking a terminal over and back must keep one shell.");
            Console.WriteLine("PASS shared terminals: the first holder takes the terminal back at its own size");

            // A relaunch restores the tab from this client's own layout; that is no more a reason to take it.
            b.Dispose();
            b = Client(host, project, "phone", workspace, phone);
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace && TerminalTabs(b).Length == 1);
            Until(() => View(b, initial).IsYielded);
            Settle(300);
            Require(!held.IsDetached, "A relaunched client must not take a terminal it merely restored.");
            a.Dispose();
            b.Dispose();
            b = Client(host, project, "phone", workspace, phone);
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace);
            var free = Ready(b, TerminalTabs(b).Single());
            free.Run("echo \"FREE=$TR_HELD\"");
            Expect(free, "FREE=desk");
            Console.WriteLine("PASS shared terminals: a restored tab yields to its holder and attaches once nobody holds it");
        }
        finally { b?.Dispose(); if (a.Workbench.Windows.Count > 0) a.Dispose(); }
    }

    private static E2eWorkspace Client(GrpcHost host, string project, string name, string start, E2eTerminals terminals) =>
        new(host.Address, Token, project, project + "-" + name + "-profile", start, terminals: terminals);

    private static string[] Ids(E2eWorkspace app) => [.. TerminalTabs(app).Select(tab => tab.Id)];

    private static string[] Catalog(GrpcHost host, string workspace) =>
        [.. (host.Host.HostService!.Catalog.Workspaces.GetValueOrDefault(workspace) ?? []).Select(tab => tab.Key)];
}