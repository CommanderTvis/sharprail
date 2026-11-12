using System.Net;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// Like Ghostty (wait-after-command = false), a catalogued tab closes for every client when its shell exits, unless the
// host setting keeps it or the shell exited abnormally: within 250 ms, with any code on macOS and a non-zero one elsewhere.
internal static partial class TerminalHostChecks
{
    private static async Task ExitClosesTab(string root, string workspace)
    {
        var directory = Path.Combine(root, "exit-state");
        var store = new HostStateStore(directory);
        Require(!store.Current.Settings.TerminalKeepAfterExit, "An exited shell must close its tab by default.");
        try { await store.ChangeAsync([HostStateChange.Setting("terminal-keep-after-exit", "maybe")]); throw new InvalidOperationException("An invalid exit setting was accepted."); }
        catch (ArgumentException) { }
        await store.ChangeAsync([HostStateChange.Setting("terminal-keep-after-exit", "true")]);
        Require(new HostStateStore(directory).Current.Settings.TerminalKeepAfterExit, "Keeping exited tabs must persist.");

        Require(PtyTerminalService.ExitedAbnormally(TimeSpan.FromMilliseconds(100), 1) && !PtyTerminalService.ExitedAbnormally(TimeSpan.FromSeconds(1), 1) &&
            PtyTerminalService.ExitedAbnormally(TimeSpan.FromMilliseconds(100), 0) == OperatingSystem.IsMacOS(),
            "Only a shell that ends within 250 ms, with a non-zero code off macOS, exits abnormally.");

        var keep = false;
        await using (var local = new PtyTerminalService("/bin/sh", keepAfterExit: () => keep))
        {
            Require(!await Survives(local, local, workspace, "exit 0"), "A shell that exits must close its tab.");
            Require(!await Survives(local, local, workspace, "exit 3"), "A shell that exits with an error after starting must close its tab, as in Ghostty.");
            keep = true;
            Require(await Survives(local, local, workspace, "exit 0"), "Keeping exited tabs must leave the tab with its exit notice.");
        }
        // A remote host reads its own shared setting; the default closes, and keeping applies from then on.
        var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "exit-token", Path.Combine(root, "exit-remote"));
        await app.StartAsync();
        try
        {
            using var settings = new RemoteStateAdapter(Address(app), "exit-token");
            using var terminals = new RemoteTerminalAdapter(Address(app), "exit-token");
            using var catalog = new RemoteTerminalCatalogAdapter(Address(app), "exit-token");
            Require(!(await settings.GetStateAsync()).Settings.TerminalKeepAfterExit, "A remote host must report closing exited tabs by default.");
            Require(!await Survives(terminals, catalog, workspace, "exit 0"), "A remote host must close an exited shell's tab.");
            Require((await settings.ChangeAsync([HostStateChange.Setting("terminal-keep-after-exit", "true")])).Settings.TerminalKeepAfterExit, "Keeping exited tabs must survive the wire.");
            Require(await Survives(terminals, catalog, workspace, "exit 0"), "A remote host must keep an exited shell's tab once asked.");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
        Console.WriteLine("PASS host terminal exit: an exited shell's tab closes for every client by default, like Ghostty, whatever its code; keeping it is a persisted host setting, locally and remotely; only an exit within 250 ms (any code on macOS, non-zero elsewhere) counts as abnormal and keeps the tab");
    }

    // Opens a catalogued tab, ends its shell with the command, and returns whether the tab is still catalogued.
    private static async Task<bool> Survives(ITerminalService terminals, ITerminalCatalogService catalog, string workspace, string command)
    {
        var key = "exit-" + Guid.NewGuid().ToString("N");
        var id = TerminalTab.SessionFor(workspace, key);
        await catalog.ReserveAsync(workspace, new(key, "Exit"));
        await using var session = await terminals.AttachAsync(new(id, workspace, "client", 100, 30) { TabKey = key });
        var screen = new Screen(session);
        await screen.Run("echo EXIT_$((40+2))");
        await screen.WaitFor("EXIT_42", "terminal exit");
        await Task.Delay(PtyTerminalService.AbnormalExitRuntime);
        await screen.Run(command);
        await session.Exit.WaitAsync(TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        bool listed;
        do
        {
            listed = (await catalog.OpenWorkspaceAsync(workspace, [])).Workspaces.GetValueOrDefault(workspace)?.Any(tab => tab.Key == key) == true;
            if (listed) await Task.Delay(50);
        }
        while (listed && DateTime.UtcNow < deadline);
        if (listed) await terminals.CloseAsync(id);
        return listed;
    }
}