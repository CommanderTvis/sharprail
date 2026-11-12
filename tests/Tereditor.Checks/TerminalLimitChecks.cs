using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// What the host refuses before it touches a session, and what it tells a client whose shell cannot start.
internal static partial class TerminalHostChecks
{
    private static async Task Limits(string root, string workspace)
    {
        await using var local = new PtyTerminalService();
        await Grids(new LocalTerminalAdapter(local), workspace, "local");
        await using (var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "limits-token", terminals: local))
        {
            await app.StartAsync();
            try
            {
                using var remote = new RemoteTerminalAdapter(Address(app), "limits-token");
                await Grids(remote, workspace, "remote");
            }
            finally { await app.StopAsync(); }
        }
        Console.WriteLine("PASS host terminal grids: attach and resize outside 1–32,767 are refused before any shell starts, locally and remotely");

        var broken = Path.Combine(root, "not-a-shell");
        File.WriteAllText(broken, "not executable");
        await using var missing = new PtyTerminalService(shell: Path.Combine(root, "no-such-shell"));
        const string configured = "Couldn’t start the configured shell. Check the host’s shell installation, then retry.";
        await StartFails(new LocalTerminalAdapter(missing), workspace, configured, root);
        await using (var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "limits-token", terminals: missing))
        {
            await app.StartAsync();
            try
            {
                using var remote = new RemoteTerminalAdapter(Address(app), "limits-token");
                await StartFails(remote, workspace, configured, root);
            }
            finally { await app.StopAsync(); }
        }
        var previous = Environment.GetEnvironmentVariable("SHELL");
        Environment.SetEnvironmentVariable("SHELL", broken);
        try
        {
            await using var environment = new PtyTerminalService();
            await StartFails(new LocalTerminalAdapter(environment), workspace,
                "Couldn’t start the shell configured by SHELL. Fix or clear SHELL in the host environment, restart SharpRail, then retry.", root);
        }
        finally { Environment.SetEnvironmentVariable("SHELL", previous); }
        Console.WriteLine("PASS host terminal start guidance: an unusable shell reports fixed guidance without the native error, path or SHELL value, and a retry starts nothing");
    }

    private static Uri Address(Microsoft.AspNetCore.Builder.WebApplication app) =>
        new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private static async Task Grids(ITerminalService terminals, string workspace, string mode)
    {
        foreach (var (columns, rows) in new[] { (0, 24), (80, 0), (-1, 24), (TerminalGrid.Max + 1, 24), (80, TerminalGrid.Max + 1) })
        {
            var id = mode + "-grid-" + Guid.NewGuid().ToString("N");
            try
            {
                await using var refused = await terminals.AttachAsync(new(id, workspace, "client", columns, rows));
                throw new InvalidOperationException($"{mode}: a {columns}x{rows} grid was attached.");
            }
            catch (ArgumentOutOfRangeException) { }
            // The refusal precedes session lookup, so no shell exists to resume.
            try
            {
                await using var resumed = await terminals.AttachAsync(new(id, workspace, "client", 80, 24, 1));
                throw new InvalidOperationException($"{mode}: a refused grid left a session behind.");
            }
            catch (IOException) { }
        }
        var live = mode + "-grid-live-" + Guid.NewGuid().ToString("N");
        var session = await terminals.AttachAsync(new(live, workspace, "client", TerminalGrid.Max, 1));
        var screen = new Screen(session);
        try
        {
            foreach (var (columns, rows) in new[] { (0, 10), (10, 0), (TerminalGrid.Max + 1, 10) })
                try { await session.ResizeAsync(columns, rows); throw new InvalidOperationException($"{mode}: a {columns}x{rows} resize was accepted."); }
                catch (ArgumentOutOfRangeException) { }
            await session.ResizeAsync(90, 20);
            await screen.Run("printf 'GRID_%s\\n' \"$(stty size | tr ' ' x)\"");
            await screen.WaitFor("GRID_20x90", mode);
        }
        finally { await session.DisposeAsync(); await terminals.CloseAsync(live); }
    }

    private static async Task StartFails(ITerminalService terminals, string workspace, string guidance, string root)
    {
        for (var attempt = 0; attempt < 2; attempt++)
            try
            {
                await using var started = await terminals.AttachAsync(new("unstartable", workspace, "client"));
                throw new InvalidOperationException("A terminal started with an unusable shell.");
            }
            catch (IOException error)
            {
                Require(error.Message == guidance, "Unexpected start guidance: " + error.Message);
                Require(!error.Message.Contains(root, StringComparison.Ordinal), "Start guidance must not disclose host paths.");
            }
    }
}