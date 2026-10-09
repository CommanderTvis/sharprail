using System.Net;
using System.Text;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// A reader that stops reading must cost the host a bounded amount of memory and still see the newest output
// once it reads again; a connection that goes silent without closing must be noticed and resumed.
internal static partial class TerminalHostChecks
{
    private static async Task Backpressure(string root, string workspace)
    {
        await using var local = new PtyTerminalService();
        var direct = await Stalled(new LocalTerminalAdapter(local), workspace, "local", 8_000_000);
        Require(direct <= Attachment.BacklogBytes + 128 * 1024, $"local: a stalled reader was handed {direct} bytes, more than the backlog bound.");
        await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "flow-token", terminals: local);
        await app.StartAsync();
        try
        {
            using (var remote = new RemoteTerminalAdapter(Address(app), "flow-token"))
            {
                // The transport's own flow-control windows hold some output on top of the host's backlog.
                const int flood = 32_000_000;
                var relayed = await Stalled(remote, workspace, "remote", flood);
                Require(relayed <= flood / 2, $"remote: a stalled reader was handed {relayed} of {flood} bytes.");
            }
            Console.WriteLine("PASS host terminal backpressure: a reader that stops reading holds a bounded backlog, loses only the oldest output, and receives the newest and later output once it reads again, locally and remotely");
            await Silence(Address(app), workspace);
        }
        finally { await app.StopAsync(); }
    }

    // Floods a session nobody reads, then reads it; returns how many bytes of the flood arrived.
    private static async Task<long> Stalled(ITerminalService terminals, string workspace, string mode, int flood)
    {
        var id = mode + "-flood-" + Guid.NewGuid().ToString("N");
        var session = await terminals.AttachAsync(new(id, workspace, "client", 100, 30));
        try
        {
            await session.WriteAsync(Encoding.UTF8.GetBytes($"head -c {flood} /dev/zero | tr '\\0' x; printf 'FLOOD_%s\\n' DONE\r"));
            await Until(async () => await terminals.IsBusyAsync(id), mode + " flood started");
            // Bounded by PTY throughput on a busy machine, not by the host.
            for (var deadline = DateTime.UtcNow.AddMinutes(3); await terminals.IsBusyAsync(id); await Task.Delay(100))
                Require(DateTime.UtcNow < deadline, $"{mode}: the flood did not finish without a reader.");
            var text = new StringBuilder();
            long received = 0;
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var reader = session.ReadAsync(limit.Token).GetAsyncEnumerator();
            async Task ReadUntil(string marker)
            {
                while (!text.ToString().Contains(marker, StringComparison.Ordinal))
                {
                    Require(await reader.MoveNextAsync(), $"{mode}: output ended before {marker}.");
                    var chunk = Encoding.UTF8.GetString(reader.Current.Span);
                    received += chunk.Count(letter => letter == 'x');
                    text.Append(chunk.Replace("x", "", StringComparison.Ordinal));
                }
            }
            await ReadUntil("FLOOD_DONE");
            Require(session.Position >= flood, $"{mode}: positions must advance past dropped output, at {session.Position}.");
            await session.WriteAsync("printf 'AFTER_%s\\n' FLOOD\r"u8.ToArray());
            await ReadUntil("AFTER_FLOOD");
            await reader.DisposeAsync();
            return received;
        }
        finally { await session.DisposeAsync(); await terminals.CloseAsync(id); }
    }

    private static async Task Silence(Uri address, string workspace)
    {
        await using var proxy = new TcpProxy(address);
        using var terminals = new RemoteTerminalAdapter(proxy.Address, "flow-token", keepAlive: TimeSpan.FromSeconds(1));
        var id = "silent-" + Guid.NewGuid().ToString("N");
        var session = await terminals.AttachAsync(new(id, workspace, "window-a", 100, 30));
        var screen = new Screen(session);
        try
        {
            await screen.Run("printf 'BEFORE_%s\\n' SLEEP");
            await screen.WaitFor("BEFORE_SLEEP", "silent connection");
            proxy.Stall();
            await Until(() => Task.FromResult(proxy.Connections > 1), "a silent connection replaced");
            await Task.Delay(500);
            await screen.Run("printf 'AFTER_%s\\n' SLEEP");
            await screen.WaitFor("AFTER_SLEEP", "silent connection");
            Require(Count(screen.Text, "BEFORE_SLEEP") == 1, "Resuming after a silent connection must not replay output twice.");
        }
        finally { await session.DisposeAsync(); await terminals.CloseAsync(id); }
        Console.WriteLine("PASS host terminal sleep recovery: a connection that stays open but carries nothing is detected by keepalive, and the session reconnects and resumes without duplicates");
    }
}