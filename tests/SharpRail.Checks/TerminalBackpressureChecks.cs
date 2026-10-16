using System.Net;
using System.Text;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// A reader that stops reading must cost the host a bounded amount of memory and preserve the byte stream
// once it reads again; a connection that goes silent without closing must be noticed and resumed.
internal static partial class TerminalHostChecks
{
    private static async Task Backpressure(string root, string workspace)
    {
        await using var local = new PtyTerminalService();
        var direct = await Stalled(new LocalTerminalAdapter(local), workspace, "local", 8_000_000);
        Require(direct == 8_000_000, $"local: a stalled reader lost output ({direct} bytes).");
        await StalledRelease(new LocalTerminalAdapter(local), workspace);
        await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "flow-token", terminals: local);
        await app.StartAsync();
        try
        {
            using (var remote = new RemoteTerminalAdapter(Address(app), "flow-token"))
            {
                // The transport's own flow-control windows hold some output on top of the host's backlog.
                const int flood = 32_000_000;
                var relayed = await Stalled(remote, workspace, "remote", flood);
                Require(relayed == flood, $"remote: a stalled reader lost output ({relayed} of {flood} bytes).");
            }
            Console.WriteLine("PASS host terminal backpressure: a reader that stops reading holds a bounded backlog, pauses the producer, and receives every byte and later output once it reads again, locally and remotely");
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
            await session.WriteAsync(Encoding.UTF8.GetBytes($"printf 'FLOOD_%s\\n' BEGIN; head -c {flood} /dev/zero | tr '\\0' '\\176'; printf 'FLOOD_%s\\n' DONE\r"));
            await Until(async () => await terminals.IsBusyAsync(id), mode + " flood started");
            await Task.Delay(1000);
            Require(await terminals.IsBusyAsync(id), $"{mode}: the producer must pause while its holder is not reading.");
            var text = new StringBuilder();
            long received = 0;
            var started = false;
            var finished = false;
            var position = session.Position;
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var reader = session.ReadAsync(limit.Token).GetAsyncEnumerator();
            async Task ReadUntil(string marker)
            {
                while (!text.ToString().Contains(marker, StringComparison.Ordinal))
                {
                    Require(await reader.MoveNextAsync(), $"{mode}: output ended before {marker}.");
                    Require(session.Position == position + reader.Current.Length,
                        $"{mode}: an output batch's position does not match its byte count.");
                    position = session.Position;
                    var chunk = Encoding.UTF8.GetString(reader.Current.Span);
                    text.Append(chunk);
                    var start = 0;
                    if (!started && text.ToString().IndexOf("FLOOD_BEGIN", StringComparison.Ordinal) is var begin && begin >= 0)
                    {
                        started = true;
                        start = begin + "FLOOD_BEGIN".Length;
                    }
                    if (started && !finished)
                    {
                        var end = text.ToString().IndexOf("FLOOD_DONE", start, StringComparison.Ordinal);
                        finished = end >= 0;
                        received += text.ToString(start, (finished ? end : text.Length) - start).Count(letter => letter == '~');
                        text.Replace("~", "");
                    }
                }
            }
            await ReadUntil("FLOOD_DONE");
            Require(session.Position >= flood, $"{mode}: positions must advance through all output, at {session.Position}.");
            await session.WriteAsync("printf 'AFTER_%s\\n' FLOOD\r"u8.ToArray());
            await ReadUntil("AFTER_FLOOD");
            await reader.DisposeAsync();
            return received;
        }
        finally { await session.DisposeAsync(); await terminals.CloseAsync(id); }
    }

    private static async Task StalledRelease(ITerminalService terminals, string workspace)
    {
        foreach (var action in new[] { "detach", "takeover", "close" })
        {
            var id = "stalled-" + action + "-" + Guid.NewGuid().ToString("N");
            await using var holder = await terminals.AttachAsync(new(id, workspace, "first", 80, 24));
            try
            {
                await holder.WriteAsync("head -c 4000000 /dev/zero | tr '\\0' x; printf 'RELEASE_%s\\n' DONE\r"u8.ToArray());
                await Until(async () => await terminals.IsBusyAsync(id), "stalled " + action);
                await Task.Delay(500);
                if (action == "close") await terminals.CloseAsync(id).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                else if (action == "detach")
                {
                    await holder.DisposeAsync();
                    await Until(async () => !await terminals.IsBusyAsync(id), "detaching releases the blocked producer");
                }
                else
                {
                    await using var taker = await terminals.AttachAsync(new(id, workspace, "second", 80, 24));
                    await holder.Detached.WaitAsync(TimeSpan.FromSeconds(10));
                    var screen = new Screen(taker);
                    await screen.WaitFor("RELEASE_DONE", action);
                }
            }
            finally { await terminals.CloseAsync(id); }
        }
        Console.WriteLine("PASS a backpressured holder can detach, be taken over or close without stranding the PTY pump");
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