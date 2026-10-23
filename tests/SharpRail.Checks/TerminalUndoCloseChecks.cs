using System.Net;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// Ghostty's undo close on the host: a tab the user closed keeps its shell, detached, for the undo timeout.
internal static partial class TerminalHostChecks
{
    private static async Task UndoClose(string root, string workspace)
    {
        var seconds = 30;
        await using (var local = new PtyTerminalService(undoCloseSeconds: () => seconds))
        {
            await UndoCloseOn(new LocalTerminalCatalogAdapter(local), new LocalTerminalAdapter(local), local, workspace, "local", value => seconds = value);
            await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "undo-token", terminals: local);
            await app.StartAsync();
            try
            {
                using var catalog = new RemoteTerminalCatalogAdapter(Address(app), "undo-token");
                using var terminals = new RemoteTerminalAdapter(Address(app), "undo-token");
                seconds = 30;
                await UndoCloseOn(catalog, terminals, local, workspace, "remote", value => seconds = value);
            }
            finally { await app.StopAsync(); }
        }

        // Stopping the host ends a shell still waiting for undo, and saves nothing to revive it.
        var recordings = Path.Combine(root, "undo-recordings");
        var pid = 0;
        await using (var stopping = new PtyTerminalService(recordingsDirectory: recordings, undoCloseSeconds: () => 30))
        {
            await stopping.ReserveAsync(workspace, new("undo-stop", "Stop"));
            await using var session = await stopping.AttachAsync(new(TerminalTab.SessionFor(workspace, "undo-stop"), workspace, "client", 100, 30) { TabKey = "undo-stop" });
            var screen = new Screen(session);
            await screen.Run("printf 'PID_%s_\\n' \"$$\"");
            pid = int.Parse(await screen.WaitForMatch(@"PID_(\d+)_", "stop"));
            await stopping.CloseTabForUndoAsync(workspace, "undo-stop");
            Require(Running(pid) && stopping.ClosingCount == 1, "A tab closed for undo must keep its shell.");
        }
        await Until(() => Task.FromResult(!Running(pid)), "stopping the host ends a shell kept for undo");
        Require(!File.Exists(Path.Combine(recordings, TerminalTab.SessionFor(workspace, "undo-stop"))), "A shell kept for undo must not be saved for revival.");
        Console.WriteLine("PASS host undo close: stopping the host ends shells kept for undo without saving them");
    }

    private static async Task UndoCloseOn(ITerminalCatalogService catalog, ITerminalService terminals, PtyTerminalService host, string workspace, string mode, Action<int> timeout)
    {
        var area = Path.Combine(workspace, mode + "-undo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        using var watching = new CancellationTokenSource();
        var seen = TerminalCatalog.Empty;
        var watcher = Task.Run(async () =>
        {
            try { await foreach (var snapshot in catalog.WatchAsync(watching.Token)) Volatile.Write(ref seen, snapshot); }
            catch (Exception) when (watching.IsCancellationRequested) { }
        });
        bool Listed(TerminalCatalog snapshot, string key) => snapshot.Workspaces.GetValueOrDefault(area)?.Any(tab => tab.Key == key) == true;

        async Task<(ITerminalSession Session, Screen Screen, int Pid)> Start(string key, string client)
        {
            await catalog.ReserveAsync(area, new(key, "Terminal"));
            var session = await terminals.AttachAsync(new(TerminalTab.SessionFor(area, key), area, client, 100, 30) { TabKey = key });
            var screen = new Screen(session);
            await screen.Run("printf 'PID_%s_\\n' \"$$\"");
            return (session, screen, int.Parse(await screen.WaitForMatch(@"PID_(\d+)_", mode)));
        }

        // Within the timeout: the tab leaves every client's catalog, its shell keeps running and writing, and reserving
        // the tab again brings back the same session with what it wrote while closed.
        var (first, firstScreen, pid) = await Start("undo-a", "client-a");
        await firstScreen.Run("sleep 1; printf 'WHILE_%s_CLOSED\\n' undone");
        Require(!Listed(await catalog.CloseTabForUndoAsync(area, "undo-a"), "undo-a"), $"{mode}: an undoable close must remove the tab from the catalog.");
        await Until(() => Task.FromResult(!Listed(Volatile.Read(ref seen), "undo-a")), $"{mode}: watchers see the closed tab gone");
        await first.DisposeAsync();
        Require(Running(pid), $"{mode}: a tab closed for undo must keep its shell running.");
        Require(Listed(await catalog.ReserveAsync(area, new("undo-a", "Terminal")), "undo-a"), $"{mode}: reserving the closed tab must restore it.");
        await Until(() => Task.FromResult(Listed(Volatile.Read(ref seen), "undo-a")), $"{mode}: watchers see the restored tab");
        await Task.Delay(1500);
        var restored = await terminals.AttachAsync(new(TerminalTab.SessionFor(area, "undo-a"), area, "client-b", 100, 30) { TabKey = "undo-a" });
        Require(!restored.Created, $"{mode}: the restored tab must reattach to its live session.");
        var restoredScreen = new Screen(restored);
        await restoredScreen.WaitFor("WHILE_undone_CLOSED", mode);
        await restoredScreen.Run("printf 'PID_%s_AGAIN\\n' \"$$\"");
        Require(int.Parse(await restoredScreen.WaitForMatch(@"PID_(\d+)_AGAIN", mode)) == pid, $"{mode}: the restored tab must run the same shell.");
        await catalog.CloseTabAsync(area, "undo-a");
        await Until(() => Task.FromResult(!Running(pid)), $"{mode}: an ordinary close ends the restored shell");
        await restored.DisposeAsync();

        // After the timeout the shell ends as a close ends it, and the tab comes back only as a new shell.
        timeout(1);
        var (second, _, expired) = await Start("undo-b", "client-a");
        await catalog.CloseTabForUndoAsync(area, "undo-b");
        await second.DisposeAsync();
        await Until(() => Task.FromResult(!Running(expired)), $"{mode}: the undo timeout ends the shell");
        Require(host.ClosingCount == 0, $"{mode}: an expired close must be forgotten.");

        // Zero turns undo off: the close is final at once.
        timeout(0);
        var (third, _, immediate) = await Start("undo-c", "client-a");
        await catalog.CloseTabForUndoAsync(area, "undo-c");
        await Until(() => Task.FromResult(!Running(immediate)), $"{mode}: with undo off a close ends the shell");
        Require(host.ClosingCount == 0, $"{mode}: with undo off nothing is kept.");
        await third.DisposeAsync();

        await catalog.CloseWorkspaceAsync(area);
        await watching.CancelAsync();
        await watcher;
        Console.WriteLine($"PASS host undo close ({mode}): the closed tab leaves every client's catalog, keeps its shell writing, returns with the same session on reserve, ends after the timeout, and closes outright at zero");
    }
}