using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.UI.Docking;
using SharpRail.UI.Panels;
using SharpRail.UI.Terminal;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

// Translates upstream e2e/terminals.spec.ts. Headless tabs run real host PTY sessions (/bin/sh);
// commands print split markers so the typed command line never satisfies an output assertion.
internal static class TerminalsE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "terminals-git"));
        OpensAutomatically(root);
        StartFailure(root);
        WorkspaceScoped(root);
        MultipleTerminals(root);
        CountsCharacters(root);
        ProjectHomeTrip(root);
        RapidReentry(root);
        ShellExit(root);
        BusyClose(root);
        IdleClose(root);
        TerminalSessionsE2E.Run(root);
    }

    private static E2eWorkspace Open(string root, string name) =>
        new(IsolatedGit.Repository(Path.Combine(root, name)), openFiles: false);

    internal static DockTab[] TerminalTabs(E2eWorkspace app) =>
        app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Where(tab => tab.Kind == "terminal").ToArray();

    private static string GroupOf(E2eWorkspace app, DockTab tab) =>
        app.Window.Layout.State.Groups.Single(group => app.Window.Layout.Tabs(group.Id).Any(item => item.Id == tab.Id)).Id;

    internal static TerminalView View(E2eWorkspace app, DockTab tab) => app.Find<TerminalView>("TerminalSurface_" + tab.Id.Replace(':', '_'));

    internal static bool Presented(E2eWorkspace app, TerminalView view) => view.GetVisualAncestors().Contains(app.Window) && view.IsEffectivelyVisible;

    internal static HostTerminal Ready(E2eWorkspace app, DockTab tab)
    {
        var view = View(app, tab);
        Until(() => Presented(app, view) && view.Backend is HostTerminal { Started.IsCompletedSuccessfully: true } host && host.Text.Length > 0);
        return (HostTerminal)view.Backend!;
    }

    internal static void Expect(HostTerminal terminal, string text) => Until(() => terminal.Text.Contains(text, StringComparison.Ordinal));

    private static void NewTerminal(E2eWorkspace app, string group)
    {
        app.Click(app.Find<Button>("NewTerminal_" + group));
    }

    internal static void CloseTab(E2eWorkspace app, DockTab tab)
    {
        var chrome = app.Find<Grid>("DockTab_" + tab.Id.Replace(':', '_'));
        app.Click(chrome.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CloseTab"));
    }

    private static DialogWindow? Dialog(E2eWorkspace app) => app.Window.OwnedWindows.OfType<DialogWindow>().SingleOrDefault();

    private static bool Busy(TerminalView view) => view.IsBusyAsync().AsTask().GetAwaiter().GetResult();

    private static void OpensAutomatically(string root)
    {
        using var app = Open(root, "terminals-automatic");
        var workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var tabs = TerminalTabs(app);
        Require(tabs.Length == 1, "A new workspace must open exactly one terminal tab.");
        var terminal = Ready(app, tabs[0]);
        terminal.Run("printf 'BASE_%s\\n' \"$(basename \"$(pwd)\")\"");
        Expect(terminal, "BASE_workspace-1");
        terminal.Run("printf 'TR_%s\\n' MARKER_IO");
        Expect(terminal, "TR_MARKER_IO");
        Require(app.Terminals.StartedIn(workspace) == 1, "The workspace must start one shell in its worktree.");
        Console.WriteLine("PASS upstream terminals.spec.ts: a workspace opens a terminal automatically, rooted in the worktree, with working I/O");
    }

    private static void StartFailure(string root)
    {
        const string failure = "Couldn’t start PowerShell 7 (pwsh.exe). Make sure it is installed and available to ThinkRail.";
        using var app = Open(root, "terminals-failure");
        app.Terminals.Fail(failure, 2);
        var held = app.Terminals.Hold();
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var tab = TerminalTabs(app).Single();
        var view = View(app, tab);
        var focused = app.Find<Button>("Tab_changes");
        focused.Focus();
        Require(focused.IsFocused, "The Changes tab must hold focus while the terminal starts.");
        held.SetResult();
        Until(() => view.IsFailed);
        var retry = view.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TerminalStartRetry");
        Require(view.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(text => text.Name == "TerminalStartFailureText").Text == failure &&
            view.Backend is null, "A failed start must explain its reason and leave no terminal surface.");
        Require(focused.IsFocused, "A start failure must not steal focus.");

        held = app.Terminals.Hold();
        app.Click(retry);
        Require(!retry.IsEnabled, "Retry must be disabled while its attempt runs.");
        held.SetResult();
        Until(() => retry.IsEnabled);
        Require(view.IsFailed && retry.IsFocused, "A failed retry must re-enable and focus Retry.");

        held = app.Terminals.Hold();
        app.Click(retry);
        Require(!retry.IsEnabled, "Retry must be disabled while its attempt runs.");
        held.SetResult();
        var terminal = Ready(app, tab);
        Require(!view.IsFailed && !view.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "TerminalStartFailure").IsVisible &&
            ReferenceEquals(view, View(app, TerminalTabs(app).Single())) && TerminalTabs(app).Single().Id == tab.Id,
            "A successful retry must reuse the same tab and hide the failure.");
        Until(() => terminal.View.IsKeyboardFocusWithin);
        terminal.Run("printf 'RETRY_%s\\n' WORKS");
        Expect(terminal, "RETRY_WORKS");
        CloseTab(app, tab);
        Until(() => TerminalTabs(app).Length == 0);
        Console.WriteLine("PASS upstream terminals.spec.ts: a shell start failure explains recovery and retries the same tab");
    }

    private static void WorkspaceScoped(string root)
    {
        using var app = Open(root, "terminals-scoped");
        var first = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var one = Ready(app, TerminalTabs(app).Single());
        one.Run("printf 'TR_%s\\n' WS1_BUFFER");
        Expect(one, "TR_WS1_BUFFER");
        var second = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-2");
        Require(TerminalTabs(app).Length == 1, "The second workspace must show only its own terminal.");
        var two = Ready(app, TerminalTabs(app).Single());
        Require(!ReferenceEquals(one, two) && !two.Text.Contains("TR_WS1_BUFFER", StringComparison.Ordinal),
            "Another workspace must not show the first workspace's buffer.");
        WorkspaceTabsE2E.Switch(app, first, "workspace-1");
        Require(TerminalTabs(app).Length == 1, "Returning must show the first workspace's single terminal.");
        Require(ReferenceEquals(Ready(app, TerminalTabs(app).Single()), one) && one.Text.Contains("TR_WS1_BUFFER", StringComparison.Ordinal) &&
            app.Terminals.StartedIn(first) == 1 && app.Terminals.StartedIn(second) == 1,
            "Returning must present the same live session and buffer without a second shell.");
        Console.WriteLine("PASS upstream terminals.spec.ts: terminals are workspace-scoped and survive workspace switches");
    }

    private static void MultipleTerminals(string root)
    {
        using var app = Open(root, "terminals-multiple");
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var firstTab = TerminalTabs(app).Single();
        var one = Ready(app, firstTab);
        var firstView = View(app, firstTab);
        one.Run("printf 'TR_%s\\n' ONE");
        Expect(one, "TR_ONE");
        var bottom = GroupOf(app, firstTab);
        NewTerminal(app, bottom);
        Until(() => TerminalTabs(app).Length == 2);
        var secondTab = TerminalTabs(app)[1];
        var two = Ready(app, secondTab);
        var secondView = View(app, secondTab);
        two.Run("printf 'TR_%s\\n' TWO");
        Expect(two, "TR_TWO");
        Require(!two.Text.Contains("TR_ONE", StringComparison.Ordinal) && !Presented(app, firstView),
            "The second terminal must present its own buffer.");
        app.Click(app.Find<Button>("Tab_" + firstTab.Id.Replace(':', '_')));
        Until(() => Presented(app, firstView));
        Require(one.Text.Contains("TR_ONE", StringComparison.Ordinal) && !one.Text.Contains("TR_TWO", StringComparison.Ordinal) &&
            !Presented(app, secondView), "Selecting the first terminal must present only its buffer.");
        CloseTab(app, secondTab);
        Until(() => TerminalTabs(app).Length == 1);
        CloseTab(app, firstTab);
        Until(() => TerminalTabs(app).Length == 0);
        NewTerminal(app, app.Center);
        Until(() => TerminalTabs(app).Length == 1);
        var reopened = TerminalTabs(app).Single();
        Require(GroupOf(app, reopened) == app.Center, "The center group's New terminal must open the terminal in the center group.");
        Ready(app, reopened);
        Console.WriteLine("PASS upstream terminals.spec.ts: multiple terminals per workspace keep independent buffers and can be closed");
    }

    private static void CountsCharacters(string root)
    {
        using var app = Open(root, "terminals-characters");
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var terminal = Ready(app, TerminalTabs(app).Single());
        terminal.Run("echo \"LEN=$(printf %s привет | wc -m | tr -d ' ')\"");
        Expect(terminal, "LEN=6\n");
        terminal.Run("echo \"CHARMAP=$(locale charmap)\"");
        Expect(terminal, "CHARMAP=UTF-8\n");
        Console.WriteLine("PASS upstream terminals.spec.ts: the terminal's shell counts characters, not bytes");
    }

    // SharpRail has no Project Home page; its project row opens the default workspace, which unmounts
    // the worktree's terminal panel the same way.
    private static void ProjectHomeTrip(string root)
    {
        using var app = Open(root, "terminals-home");
        var workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var tab = TerminalTabs(app).Single();
        var terminal = Ready(app, tab);
        terminal.Run("TR_SURVIVOR=alive");
        terminal.Run("echo \"CHECK=$TR_SURVIVOR\"");
        Expect(terminal, "CHECK=alive\n");
        var view = View(app, tab);
        WorkspaceTabsE2E.Switch(app, app.Root);
        Require(!Presented(app, view), "Leaving the workspace must unmount its terminal panel.");
        WorkspaceTabsE2E.Switch(app, workspace, "workspace-1");
        Require(ReferenceEquals(Ready(app, tab), terminal) && terminal.Text.Contains("CHECK=alive", StringComparison.Ordinal), "Returning must present the same shell.");
        terminal.Run("echo \"AGAIN=$TR_SURVIVOR\"");
        Expect(terminal, "AGAIN=alive\n");
        Require(app.Terminals.StartedIn(workspace) == 1, "The trip must not start a second shell.");
        Console.WriteLine("PASS upstream terminals.spec.ts: a shell survives a trip to Project Home and back");
    }

    private static void RapidReentry(string root)
    {
        using var app = Open(root, "terminals-reentry");
        var workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var tab = TerminalTabs(app).Single();
        var terminal = Ready(app, tab);
        terminal.Run("TR_SURVIVOR=alive");
        terminal.Run("echo \"CHECK=$TR_SURVIVOR\"");
        Expect(terminal, "CHECK=alive\n");
        var view = View(app, tab);
        for (var round = 0; round < 2; round++)
        {
            WorkspaceTabsE2E.Switch(app, app.Root);
            Require(!Presented(app, view), "Leaving must unmount the terminal panel.");
            WorkspaceTabsE2E.Switch(app, workspace, "workspace-1");
            Require(ReferenceEquals(View(app, tab), view) && Presented(app, view), "Re-entry must present exactly the one terminal.");
        }
        terminal.Run("echo \"AFTER=$TR_SURVIVOR\"");
        Expect(terminal, "AFTER=alive\n");
        Require(app.Terminals.StartedIn(workspace) == 1, "Exactly one shell should ever have existed for this tab.");
        Console.WriteLine("PASS upstream terminals.spec.ts: rapid re-entry never spawns a second shell");
    }

    private static void ShellExit(string root)
    {
        using var app = Open(root, "terminals-exit");
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var tab = TerminalTabs(app).Single();
        var terminal = Ready(app, tab);
        var view = View(app, tab);
        Require(!view.IsExited, "A running shell must not be marked exited.");
        terminal.Run("exit");
        Until(() => view.IsExited);
        var notice = view.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "TerminalExited");
        Require(notice.IsEffectivelyVisible && Regex.IsMatch(notice.Text ?? "", @"\[process exited(?: with code \d+)?\]"),
            "An exited shell must say so in its tab.");
        Console.WriteLine("PASS upstream terminals.spec.ts: a tab says so when its shell exits");
    }

    private static void BusyClose(string root)
    {
        using var app = Open(root, "terminals-busy-close");
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var tab = TerminalTabs(app).Single();
        var terminal = Ready(app, tab);
        var view = View(app, tab);
        terminal.Run("sleep 45");
        Until(() => Busy(view));
        CloseTab(app, tab);
        Until(() => Dialog(app) is not null);
        Require(TerminalTabs(app).Length == 1, "The tab must stay open while the close is being confirmed.");
        var dialog = Dialog(app)!;
        app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Cancel" } || Equals(button.Content, "Cancel")));
        Until(() => Dialog(app) is null);
        Require(app.Window.Layout.Selected(GroupOf(app, tab))?.Id == tab.Id && ReferenceEquals(View(app, tab), view) && Presented(app, view) && Busy(view),
            "Canceling must keep the running terminal active.");
        CloseTab(app, tab);
        Until(() => Dialog(app) is not null);
        app.Click(Dialog(app)!.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TerminalCloseBusyConfirm"));
        Until(() => TerminalTabs(app).Length == 0);
        Console.WriteLine("PASS upstream terminals.spec.ts: closing a tab with a running process asks first");
    }

    private static void IdleClose(string root)
    {
        using var app = Open(root, "terminals-idle-close");
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var first = TerminalTabs(app).Single();
        Ready(app, first);
        NewTerminal(app, GroupOf(app, first));
        Until(() => TerminalTabs(app).Length == 2);
        var second = TerminalTabs(app)[1];
        Ready(app, second);
        CloseTab(app, second);
        Until(() => TerminalTabs(app).Length == 1);
        Settle();
        Require(Dialog(app) is null, "Closing an idle terminal must not ask.");
        Console.WriteLine("PASS upstream terminals.spec.ts: closing an idle tab does not ask");
    }
}