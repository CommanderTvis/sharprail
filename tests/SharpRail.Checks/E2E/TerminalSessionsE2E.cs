using System.Net;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.TerminalsE2E;

namespace SharpRail.Checks.E2E;

// Translates the host-owned session rows of upstream e2e/terminals.spec.ts. A reload is a new window of the
// same app over the same profile and terminal host. A second browser is a second window of the app with its
// own connection to a real gRPC host; a dropped socket is a TCP proxy that severs the connection.
internal static class TerminalSessionsE2E
{
    internal const string Token = "e2e-terminal-token";

    internal static void Run(string root)
    {
        ReloadSurvives(root);
        OutputStaysAddressed(root);
        DiesWhileDetached(root);
        ReconnectSurvives(root);
        LostAttachReply(root);
        FinalOutputAfterReconnect(root);
        SecondClientTakesOver(root);
        DiesDuringReclaim(root);
        SharedTerminalsE2E.Run(root);
    }

    private static string Repository(string root, string name) => IsolatedGit.Repository(Path.Combine(root, name));

    private static E2eWorkspace Window(string directory, E2eTerminals terminals) =>
        new(directory, openFiles: false, profileRoot: directory + "-profile", terminals: terminals);

    private static void Enter(E2eWorkspace app, string workspace)
    {
        if (app.Window.WorkspaceRoot != workspace) WorkspaceTabsE2E.Switch(app, workspace, Path.GetFileName(workspace));
    }

    private static int Count(string text, string marker) => text.Split(marker).Length - 1;

    internal static Button TakeBack(SharpRail.UI.Terminal.TerminalView view) =>
        view.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TerminalTakeBack");

    // A client that finds a terminal held elsewhere is offered it; taking it is the user's gesture.
    internal static HostTerminal TakeOver(E2eWorkspace app, SharpRail.UI.Docking.DockTab tab)
    {
        var view = View(app, tab);
        Until(() => view.IsYielded);
        Require((string?)TakeBack(view).Content == "Take over", "A terminal never attached here must offer to take it over.");
        app.Click(TakeBack(view));
        return Ready(app, tab);
    }

    private static void ReloadSurvives(string root)
    {
        var directory = Repository(root, "terminals-reload");
        var terminals = new E2eTerminals();
        try
        {
            string workspace;
            using (var app = Window(directory, terminals))
            {
                workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
                var terminal = Ready(app, TerminalTabs(app).Single());
                terminal.Run("TR_RELOAD=survived");
                terminal.Run("echo \"BEFORE=$TR_RELOAD\"");
                Expect(terminal, "BEFORE=survived\n");
            }
            using (var app = Window(directory, terminals))
            {
                Enter(app, workspace);
                Require(TerminalTabs(app).Length == 1, "A reload must restore exactly one terminal tab.");
                var terminal = Ready(app, TerminalTabs(app).Single());
                Require(Count(terminal.Text, "BEFORE=survived\n") == 1, "A reload must replay the shell's earlier output once.");
                terminal.Run("echo \"AFTER=$TR_RELOAD\"");
                Expect(terminal, "AFTER=survived\n");
                Require(terminals.StartedIn(workspace) == 1, "A reload must reattach the same shell rather than start another.");
            }
        }
        finally { terminals.Quit(); }
        Console.WriteLine("PASS upstream terminals.spec.ts: a shell survives a page reload");
    }

    private static void DiesWhileDetached(string root)
    {
        var directory = Repository(root, "terminals-detached-death");
        var terminals = new E2eTerminals();
        try
        {
            string workspace;
            using (var app = Window(directory, terminals))
            {
                workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
                var terminal = Ready(app, TerminalTabs(app).Single());
                terminal.Run("(sleep 2; kill -9 $$) &");
                terminal.Run("printf 'TR_%s\\n' BEFORE_DEATH");
                Expect(terminal, "TR_BEFORE_DEATH");
            }
            Settle(3500);
            using (var app = Window(directory, terminals))
            {
                Enter(app, workspace);
                var tab = TerminalTabs(app).Single();
                var view = View(app, tab);
                Until(() => view.IsExited);
                var notice = view.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "TerminalExited");
                var terminal = (HostTerminal)view.Backend!;
                Require(notice.Text == "[process exited with code 137]" && terminal.Text.Contains("TR_BEFORE_DEATH", StringComparison.Ordinal) &&
                    terminals.StartedIn(workspace) == 1 && !view.IsBusyAsync().AsTask().GetAwaiter().GetResult(),
                    "A shell that died while detached must show its final output and exit, not a live or new shell.");
                // SharpRail keeps the exited tab; a new terminal is the way back to a live shell.
                CloseTab(app, tab);
                Until(() => TerminalTabs(app).Length == 0);
                app.Click(app.Find<Button>("NewTerminal_" + app.Window.Layout.State.Groups.Single(group => group.Region == "bottom").Id));
                Until(() => TerminalTabs(app).Length == 1);
                var fresh = Ready(app, TerminalTabs(app).Single());
                fresh.Run("echo TR_REATTACH_$((7 * 6))");
                Expect(fresh, "TR_REATTACH_42");
            }
        }
        finally { terminals.Quit(); }
        Console.WriteLine("PASS upstream terminals.spec.ts: a shell that dies while detached is not re-attached as if alive");
    }

    // One gRPC host that windows reach through their own connections.
    internal sealed class GrpcHost : IDisposable
    {
        private readonly PtyTerminalService pty = new("/bin/sh");
        private readonly WebApplication server;
        private readonly List<IDisposable> clients = [];
        internal E2eTerminals Host { get; }
        internal Uri Address { get; }

        internal GrpcHost(string root)
        {
            Host = new E2eTerminals(pty);
            server = RemoteServer.Create(root, IPAddress.Loopback, 0, Token, terminals: Host);
            Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
            Address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }

        internal E2eTerminals Client(Uri? address = null, AttachFaults? faults = null)
        {
            var adapter = new RemoteTerminalAdapter(address ?? Address, Token, faults);
            // The catalog keeps a connection of its own, so a proxied address carries the terminal alone.
            var catalog = new RemoteTerminalCatalogAdapter(Address, Token);
            clients.AddRange([adapter, catalog]);
            return new E2eTerminals(adapter, catalog);
        }

        public void Dispose()
        {
            foreach (var client in clients) client.Dispose();
            Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); await pty.DisposeAsync(); }).GetAwaiter().GetResult();
        }
    }

    private static void OutputStaysAddressed(string root)
    {
        var directory = Repository(root, "terminals-addressed");
        using var host = new GrpcHost(directory);
        using var a = Window(directory, host.Client());
        var first = WorkspaceTabsE2E.CreateWorkspace(a, "workspace-1");
        Ready(a, TerminalTabs(a).Single());
        var second = WorkspaceTabsE2E.CreateWorkspace(a, "workspace-2");
        Ready(a, TerminalTabs(a).Single());
        WorkspaceTabsE2E.Switch(a, first, "workspace-1");
        var one = Ready(a, TerminalTabs(a).Single());

        using var b = Window(directory, host.Client());
        Enter(b, second);
        var two = TakeOver(b, TerminalTabs(b).Single());
        one.Run("printf 'TR_SECRET_%s\\n' FROM_A");
        Expect(one, "TR_SECRET_FROM_A");
        two.Run("printf 'TR_SECRET_%s\\n' FROM_B");
        Expect(two, "TR_SECRET_FROM_B");
        Settle(500);
        Require(b.Terminals.Views.Any(view => view.Text.Contains("TR_SECRET_FROM_B", StringComparison.Ordinal)) &&
            !b.Terminals.Views.Any(view => view.Text.Contains("TR_SECRET_FROM_A", StringComparison.Ordinal)),
            "A terminal's output must reach only the client attached to it.");
        Console.WriteLine("PASS upstream terminals.spec.ts: a terminal's output never reaches another client");
    }

    private static void ReconnectSurvives(string root)
    {
        var directory = Repository(root, "terminals-reconnect");
        using var host = new GrpcHost(directory);
        var proxy = new TcpProxy(host.Address);
        try
        {
            using var app = Window(directory, host.Client(proxy.Address));
            var workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
            var terminal = Ready(app, TerminalTabs(app).Single());
            terminal.Run("TR_RECONNECT=survived");
            terminal.Run("echo \"BEFORE=$TR_RECONNECT\"");
            Expect(terminal, "BEFORE=survived\n");
            Require(proxy.Connections == 1, "The window must share one connection before the drop.");
            proxy.Sever();
            Until(() => proxy.Connections > 1);
            Settle(500);
            terminal.Run("echo \"AFTER=$TR_RECONNECT\"");
            Expect(terminal, "AFTER=survived\n");
            Require(Count(terminal.Text, "BEFORE=survived\n") == 1 && host.Host.StartedIn(workspace) == 1,
                "A reconnect must resume the same shell without replaying its output twice.");
        }
        finally { Task.Run(() => proxy.DisposeAsync().AsTask()).GetAwaiter().GetResult(); }
        Console.WriteLine("PASS upstream terminals.spec.ts: a shell survives losing the connection and reconnecting");
    }

    private static void LostAttachReply(string root)
    {
        var directory = Repository(root, "terminals-lost-attach");
        using var host = new GrpcHost(directory);
        var faults = new AttachFaults();
        using var app = Window(directory, host.Client(faults: faults));
        Ready(app, TerminalTabs(app).Single());
        var before = faults.Calls;
        faults.DropNextReply = true;
        var workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var terminal = Ready(app, TerminalTabs(app).Single());
        terminal.Run("printf 'TR_%s\\n' REPLAYED_CREATE_WORKS");
        Expect(terminal, "TR_REPLAYED_CREATE_WORKS");
        var attaches = host.Host.Attaches.Where(request => request.WorkspaceRoot == workspace).ToArray();
        Require(faults.Calls - before > 1 && attaches.Length > 1 && attaches.Select(request => request.SessionId).Distinct().Count() == 1 &&
            host.Host.StartedIn(workspace) == 1 && Count(terminal.Text, "TR_REPLAYED_CREATE_WORKS") == 1,
            "A lost attach reply must be retried for the same session and start exactly one shell.");
        Console.WriteLine("PASS upstream terminals.spec.ts: a terminal attach response lost with its socket is replayed exactly once");
    }

    private static void FinalOutputAfterReconnect(string root)
    {
        var directory = Repository(root, "terminals-final-output");
        using var host = new GrpcHost(directory);
        var proxy = new TcpProxy(host.Address);
        try
        {
            using var app = Window(directory, host.Client(proxy.Address));
            WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
            var tab = TerminalTabs(app).Single();
            var terminal = Ready(app, tab);
            var view = View(app, tab);
            terminal.Run("M=TR_FINAL; sleep 1; printf '\\n%s_%s\\n' \"$M\" DURING_DROP; exit 7");
            Expect(terminal, "M=TR_FINAL");
            var release = proxy.Hold();
            proxy.Sever();
            Settle(1500);
            Require(!view.IsExited, "The exit must not be reported while the connection is down.");
            release.SetResult();
            Until(() => view.IsExited);
            var notice = view.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "TerminalExited");
            Require(proxy.Connections > 1 && Count(terminal.Text, "TR_FINAL_DURING_DROP") == 1 && notice.Text == "[process exited with code 7]",
                "Final shell output must arrive once, before the exit status, after a reconnect.");
        }
        finally { Task.Run(() => proxy.DisposeAsync().AsTask()).GetAwaiter().GetResult(); }
        Console.WriteLine("PASS upstream terminals.spec.ts: final shell output is delivered before exit after reconnect");
    }

    private static void SecondClientTakesOver(string root)
    {
        var directory = Repository(root, "terminals-takeover");
        using var host = new GrpcHost(directory);
        using var a = Window(directory, host.Client());
        var workspace = WorkspaceTabsE2E.CreateWorkspace(a, "workspace-1");
        var tab = TerminalTabs(a).Single();
        var first = Ready(a, tab);
        first.Run("TR_SHARED=yes");
        first.Run("echo \"FIRST=$TR_SHARED\"");
        Expect(first, "FIRST=yes\n");

        using var b = Window(directory, host.Client());
        Enter(b, workspace);
        Require(TerminalTabs(b).Length == 1, "The second client must show the one terminal.");
        var second = TakeOver(b, TerminalTabs(b).Single());
        second.Run("echo \"SECOND=$TR_SHARED\"");
        Expect(second, "SECOND=yes\n");

        var view = View(a, tab);
        Until(() => view.IsDetached);
        Require(!view.FindControl<ContentControl>("TerminalBody")!.IsVisible && view.FindControl<Control>("TerminalDetached")!.IsEffectivelyVisible,
            "The first client must be told, over a hidden stale surface.");
        a.Click(TakeBack(view));
        var back = Ready(a, tab);
        back.Run("echo \"BACK=$TR_SHARED\"");
        Expect(back, "BACK=yes\n");
        Until(() => View(b, TerminalTabs(b).Single()).IsDetached);
        Require(host.Host.StartedIn(workspace) == 1, "Taking a terminal over and back must keep one shell.");
        Console.WriteLine("PASS upstream terminals.spec.ts: a second client takes a terminal over and the first is told");
    }

    private static void DiesDuringReclaim(string root)
    {
        var directory = Repository(root, "terminals-reclaim");
        using var host = new GrpcHost(directory);
        var faults = new AttachFaults();
        using var a = Window(directory, host.Client(faults: faults));
        var workspace = WorkspaceTabsE2E.CreateWorkspace(a, "workspace-1");
        var tab = TerminalTabs(a).Single();
        Ready(a, tab);

        using var b = Window(directory, host.Client());
        Enter(b, workspace);
        var second = TakeOver(b, TerminalTabs(b).Single());
        var view = View(a, tab);
        Until(() => view.IsDetached);
        second.Run("(sleep 2; kill -9 $$) &");
        Settle(300);

        faults.Delay = TimeSpan.FromSeconds(4);
        a.Click(TakeBack(view));
        var deadline = Awake.Now.AddSeconds(20);
        while (!view.IsExited && Awake.Now < deadline) Settle(100);
        Require(view.IsExited && !view.IsDetached && !view.IsFailed, "A shell that dies during a reclaim must be presented as exited.");
        Console.WriteLine("PASS upstream terminals.spec.ts: a shell that dies during a reclaim is not presented as alive");
    }
}