using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Terminal;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.TerminalsE2E;
using static SharpRail.Checks.E2E.TerminalSessionsE2E;

namespace SharpRail.Checks.E2E;

// Two clients of one real WebSocket RPC host, each with its own profile, connection and grid: which terminals exist is
// the host's catalog, and a terminal is held by one client, and watched by the others, until another takes it over.
internal static class SharedTerminalsE2E
{
    internal static void Run(string root)
    {
        Shared(root);
        UndoClose(root);
        InProjects(root);
        PhoneToDesktop(root);
        Renderers(root);
        ExitCloses(root);
    }

    // As in Ghostty, a shell that exits closes its tab like a user close, in every client; the host setting keeps it instead.
    private static void ExitCloses(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "terminals-exit-closes"));
        var keep = false;
        using var host = new RemoteHost(project, () => keep);
        var desk = host.Client();
        var a = Client(host, project, "desk", project, desk);
        E2eWorkspace? b = null;
        try
        {
            Until(() => a.Window.WorkspaceMounted);
            var workspace = CreateTerminalWorkspace(a, "workspace-1");
            var initial = TerminalTabs(a).Single();
            Ready(a, initial);
            var group = a.Window.Layout.State.Groups.Single(item => a.Window.Layout.Tabs(item.Id).Any(tab => tab.Kind == "terminal")).Id;
            a.Click(a.Find<Button>("NewTerminal_" + group));
            Until(() => TerminalTabs(a).Length == 2);
            var second = TerminalTabs(a)[1];
            var shell = Ready(a, second);
            b = Client(host, project, "phone", workspace, host.Client());
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace && Ids(b).SequenceEqual(Ids(a)));
            View(a, second).FocusTerminal();
            Until(() => View(a, second).IsKeyboardFocusWithin);
            Settle(300);
            shell.Run("exit 0");
            Until(() => TerminalTabs(a).Length == 1 && TerminalTabs(b).Length == 1);
            Until(() => View(a, initial).IsKeyboardFocusWithin);
            Require(!a.Window.OwnedWindows.Any() && a.Window.Layout.Tabs(group).Select(tab => tab.Id).SequenceEqual([initial.Id]) &&
                a.Window.Layout.Selected(group)?.Id == initial.Id && Catalog(host, workspace).SequenceEqual([initial.Id]),
                "An exited shell's tab must leave its group, the host's catalog and every client without asking, and hand focus to the tab beside it.");
            Console.WriteLine("PASS terminal exit: a shell that exits closes its tab in every client by default, without confirmation, and focus moves on in its group");

            keep = true;
            var last = Ready(a, initial);
            Settle(300);
            last.Run("exit 0");
            var kept = View(a, initial);
            Until(() => kept.IsExited);
            Settle(500);
            Require(TerminalTabs(a).Length == 1 && TerminalTabs(b).Length == 1 && Catalog(host, workspace).SequenceEqual([initial.Id]) &&
                kept.FindControl<TextBlock>("TerminalExited")!.Text == "[process exited with code 0]",
                "Keeping exited tabs must leave the tab with its exit notice.");
            Console.WriteLine("PASS terminal exit: with the setting to keep, an exited shell's tab stays with its exit notice");

            keep = false;
            desk.Fail("no shell here");
            a.Click(a.Find<Button>("NewTerminal_" + group));
            Until(() => TerminalTabs(a).Length == 2);
            var failed = View(a, TerminalTabs(a)[1]);
            Until(() => failed.IsFailed);
            Settle(500);
            Require(TerminalTabs(a).Length == 2 && failed.IsFailed, "A shell that fails to start must keep its tab and its Retry.");
            Console.WriteLine("PASS terminal exit: a shell that fails to start keeps its tab and Retry");
        }
        finally { b?.Dispose(); a.Dispose(); }
    }

    // Ghostty's undo close: a closed terminal comes back in place with its shell, for every client, within the host's timeout.
    private static void UndoClose(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "terminals-undo"));
        using var host = new RemoteHost(project);
        host.UndoCloseSeconds = 60;
        var a = Client(host, project, "desk", project, host.Client());
        E2eWorkspace? b = null;
        try
        {
            Until(() => a.Window.WorkspaceMounted);
            var workspace = CreateTerminalWorkspace(a, "workspace-1");
            var initial = TerminalTabs(a).Single();
            Ready(a, initial);
            var bottom = a.Window.Layout.State.Groups.Single(group => a.Window.Layout.Tabs(group.Id).Any(tab => tab.Kind == "terminal")).Id;
            a.Click(a.Find<Button>("NewTerminal_" + bottom));
            Until(() => TerminalTabs(a).Length == 2);
            a.Click(a.Find<Button>("NewTerminal_" + bottom));
            Until(() => TerminalTabs(a).Length == 3);
            var middle = TerminalTabs(a)[1];
            a.Window.Layout.Select(bottom, middle.Id);
            var shell = Ready(a, middle);
            shell.Run("echo \"PID_$$_SHELL\"");
            var pid = System.Text.RegularExpressions.Regex.Match(WaitFor(shell, @"PID_(\d+)_SHELL"), @"PID_(\d+)_SHELL").Groups[1].Value;
            b = Client(host, project, "phone", workspace, host.Client());
            Until(() => b.Window.WorkspaceMounted && b.Window.WorkspaceRoot == workspace && Ids(b).SequenceEqual(Ids(a)));
            var order = Ids(a);
            var session = TerminalTab.SessionFor(workspace, middle.Id);

            // In the background, so the shell is idle and closes without asking.
            shell.Run("(sleep 1; echo WHILE_$((40+1))_CLOSED) &");
            Settle(200);
            CloseTab(a, middle);
            Until(() => TerminalTabs(a).Length == 2 && TerminalTabs(b).Length == 2);
            Require(a.Window.Toasts.Items.Any(toast => toast is { Countdown: true, Message: "Closed Terminal 2", Action.Label: "Undo" }) && host.Pty.ClosingCount == 1 &&
                !Catalog(host, workspace).Contains(middle.Id), "Closing a live terminal must offer undo while the host keeps its shell out of the catalog.");
            Settle(1500);
            // A focused terminal keeps Ctrl+Z for its shell (SIGTSTP): off macOS, where Ctrl is the command key, the undo
            // chord stands aside for it; Ghostty's terminal input then encodes it (GhosttySkiaChecks covers the encoding).
            a.Window.Layout.Select(bottom, TerminalTabs(a)[0].Id);
            Ready(a, TerminalTabs(a)[0]);
            View(a, TerminalTabs(a)[0]).FocusTerminal();
            a.Window.KeyPress(Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Z, null);
            Settle(300);
            Require(TerminalTabs(a).Length == 2 && a.Window.CanUndoClose() && !a.Window.UndoCloseFocusAllows(control: true) && a.Window.UndoCloseFocusAllows(control: false),
                "Ctrl+Z in a focused terminal must stay the shell's and reopen nothing.");
            Console.WriteLine("PASS undo close: Ctrl+Z in a focused terminal is left to the shell");

            // Off macOS the command key is Ctrl, which the focused terminal keeps, so undo is pressed with focus on the group's chrome.
            a.Find<Button>("NewTerminal_" + bottom).Focus();
            Settle(100);
            Require(a.Window.UndoCloseFocusAllows(control: true), "Focusing the group chrome must take focus out of the terminal.");
            a.Window.KeyPress(Avalonia.Input.Key.Z, Command, Avalonia.Input.PhysicalKey.Z, "z");
            Until(() => Ids(a).SequenceEqual(order));
            Until(() => Ids(b).Order().SequenceEqual(order.Order()));
            Require(a.Window.Layout.Selected(bottom)?.Id == middle.Id && Catalog(host, workspace).Contains(middle.Id) && host.Pty.ClosingCount == 0,
                "Undo must reopen the terminal at its place, selected, and restore it to the host's catalog.");
            var restored = Ready(a, middle);
            Expect(restored, "WHILE_41_CLOSED");
            restored.Run("echo \"PID_$$_AGAIN\"");
            Expect(restored, "PID_" + pid + "_AGAIN");
            Require(host.Host.Started.Count(request => request.SessionId == session) == 1, "Undo must reattach the live session rather than start a shell.");
            Console.WriteLine("PASS undo close: Command-Z reopens a closed terminal in place for every client, with its shell and what it wrote while closed");

            a.Find<Button>("NewTerminal_" + bottom).Focus();
            a.Window.KeyPress(Avalonia.Input.Key.Z, Command | Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.Z, "Z");
            Until(() => TerminalTabs(a).Length == 2 && TerminalTabs(b).Length == 2 && host.Pty.ClosingCount == 1);
            a.Window.UndoClose();
            Until(() => Ids(a).SequenceEqual(order));
            Until(() => Ids(b).Order().SequenceEqual(order.Order()));
            Expect(Ready(a, middle), "PID_" + pid + "_AGAIN");
            Console.WriteLine("PASS undo close: redo closes the reopened terminal again, and undo brings it back once more");

            host.UndoCloseSeconds = 0;
            Settle(1000);
            CloseTab(a, middle);
            Until(() => TerminalTabs(a).Length == 2 && !Running(int.Parse(pid)));
            Require(!a.Window.CanUndoClose() && host.Pty.ClosingCount == 0, "With undo off a close must be final at once.");
            Console.WriteLine("PASS undo close: a timeout of zero turns undo off and ends the shell at once");
        }
        finally { b?.Dispose(); a.Dispose(); }
    }

    private static string WaitFor(HostTerminal terminal, string pattern)
    {
        Until(() => System.Text.RegularExpressions.Regex.IsMatch(terminal.Text, pattern));
        return terminal.Text;
    }

    private static bool Running(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    // The Android phone client keeps Projects on its own page.
    private static void InProjects(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "terminals-listed"));
        using var host = new RemoteHost(project);
        var a = Client(host, project, "desk", project, host.Client());
        E2eWorkspace? b = null;
        try
        {
            Until(() => a.Window.WorkspaceMounted);
            var workspace = CreateTerminalWorkspace(a, "workspace-1");
            var initial = TerminalTabs(a).Single();
            var first = Ready(a, initial);
            first.Run("echo LISTED_$((40+2))");
            var bottom = a.Window.Layout.State.Groups.Single(group => a.Window.Layout.Tabs(group.Id).Any(tab => tab.Kind == "terminal")).Id;
            a.Click(a.Find<Button>("NewTerminal_" + bottom));
            Until(() => TerminalTabs(a).Length == 2);
            Ready(a, TerminalTabs(a)[1]);

            b = new E2eWorkspace(host.Address, Token, project, project + "-phone-profile", project, terminals: host.Client(), compact: true,
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
            Require(!b.Window.Layout.State.Groups.Any(legacyGroup => legacyGroup.Region == "bottom") && host.Host.Started.Count(request => request.SessionId == TerminalTab.SessionFor(workspace, initial.Id)) == 1,
                "The terminal chosen in Projects must open in the centre, watched, without revealing the hidden bottom or starting another shell.");
            a.Click(a.Find<Button>("NewTerminal_" + bottom));
            Until(() => TerminalTabs(a).Length == 3 && Ids(b).SequenceEqual(Ids(a)) && Central(b) && Strips(b).SequenceEqual(Ids(b)));
            Console.WriteLine("PASS shared terminals: with tabs in Projects every terminal of the workspace sits in the centre and its strip there, whoever holds it");
        }
        finally { b?.Dispose(); a.Dispose(); }
    }

    private static void PhoneToDesktop(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "phone-to-desktop"));
        using var host = new RemoteHost(project);
        using var desktop = Client(host, project, "desktop", project, host.Client());
        Until(() => desktop.Window.WorkspaceMounted);
        using var phone = new E2eWorkspace(host.Address, Token, project, project + "-phone-profile", project,
            terminals: host.Client(), compact: true);
        Until(() => phone.Window.WorkspaceMounted);
        phone.Click(phone.Find<Button>("PageProjects"));
        var workspace = CreateTerminalWorkspace(phone, "workspace-1");
        var tab = TerminalTabs(phone).Single();
        var held = Ready(phone, tab);
        held.Run("echo PHONE_$((40+2))");
        Until(() => Listed(desktop, workspace).Any(button => Equals(button.Tag, tab.Id)));
        Require(desktop.Window.WorkspaceRoot == project, "Listing a phone terminal must not switch the desktop workspace.");
        desktop.Click(Listed(desktop, workspace).Single(button => Equals(button.Tag, tab.Id)));
        Until(() => desktop.Window.WorkspaceMounted && desktop.Window.WorkspaceRoot == workspace && View(desktop, tab).IsWatching);
        Expect(Watched(View(desktop, tab)), "PHONE_42");
        Require(!View(phone, tab).IsWatching && host.Host.Started.Count(request => request.SessionId == TerminalTab.SessionFor(workspace, tab.Id)) == 1,
            "Opening a phone terminal on desktop must watch the existing shell without taking ownership.");
        held.Run("echo LIVE_$((40+3))");
        Expect(Watched(View(desktop, tab)), "LIVE_43");
        Console.WriteLine("PASS phone-to-desktop terminals: an unopened workspace lists its phone-owned terminal and opens its live read-only view without takeover");
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
        using var host = new RemoteHost(project);
        var metal = host.Client();
        metal.WatchesWhenAsked = true;
        var a = Client(host, project, "desk", project, host.Client());
        E2eWorkspace? b = null;
        try
        {
            Until(() => a.Window.WorkspaceMounted);
            var workspace = CreateTerminalWorkspace(a, "workspace-1");
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
        using var host = new RemoteHost(project);
        var desk = host.Client();
        var phone = host.Client();
        phone.Grid = (61, 17);
        var a = Client(host, project, "desk", project, desk);
        E2eWorkspace? b = null;
        try
        {
            Until(() => a.Window.WorkspaceMounted);
            var workspace = CreateTerminalWorkspace(a, "workspace-1");
            var initial = TerminalTabs(a).Single();
            Require(initial.Title == TerminalTab.InitialTitle, "The first explicit terminal starts with the first number.");
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
            Require(!a.Window.OwnedWindows.Any() && Catalog(host, workspace).SequenceEqual([initial.Id]),
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

    private static E2eWorkspace Client(RemoteHost host, string project, string name, string start, E2eTerminals terminals) =>
        new(host.Address, Token, project, project + "-" + name + "-profile", start, terminals: terminals);

    private static string[] Ids(E2eWorkspace app) => [.. TerminalTabs(app).Select(tab => tab.Id)];

    private static string[] Catalog(RemoteHost host, string workspace) =>
        [.. (host.Host.HostService!.Catalog.Workspaces.GetValueOrDefault(workspace) ?? []).Select(tab => tab.Key)];
}