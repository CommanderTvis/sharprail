using System.Diagnostics;
using System.Net;
using System.Text;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// Managed garbage the host makes per byte of PTY output: two shells stream at a fixed rate through the
// embedded host (and, measured separately, the remote WebSocket host in the same process) to readers that
// only count bytes. The embedded path must allocate far less than the output it moves.
internal static class PtyOutputAllocationChecks
{
    // Each shell prints 64 KiB every 10 ms.
    private const string Stream = "perl -e '$|=1; $l=(\"x\" x 1023).\"\\n\"; while (1) { print $l x 64; select(undef, undef, undef, 0.01) }'\r";
    // Allocated bytes per delivered byte the embedded host may spend; copying every chunk twice costs about 2.
    private const double EmbeddedBudget = 0.3;

    internal static async Task Run(string root, TimeSpan duration, bool remote)
    {
        var workspace = Path.Combine(root, "pty-output-allocations");
        Directory.CreateDirectory(workspace);
        await using var local = new PtyTerminalService();
        var embedded = await Measure(new LocalTerminalAdapter(local), workspace, "embedded", duration);
        Report("embedded", embedded);
        if (remote)
        {
            await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "allocations-token", terminals: local);
            await app.StartAsync();
            try
            {
                using var adapter = new RemoteTerminalAdapter(new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "allocations-token");
                Report("remote", await Measure(adapter, workspace, "remote", duration));
            }
            finally { await app.StopAsync(); }
        }
        var ratio = embedded.Allocated / (double)Math.Max(1, embedded.Delivered);
        if (ratio > EmbeddedBudget)
            throw new InvalidOperationException($"embedded: allocated {ratio:F3} bytes per delivered byte, over the {EmbeddedBudget} budget.");
        Console.WriteLine($"PASS PTY output allocations: the embedded host allocates {ratio:F3} bytes per byte of output (budget {EmbeddedBudget})");
    }

    private static void Report(string mode, (long Allocated, long Delivered, int Gen0, double Seconds) result) =>
        Console.WriteLine(FormattableString.Invariant(
            $"{mode}: {result.Delivered / result.Seconds / 1048576:F1} MiB/s delivered, {result.Allocated / result.Seconds / 1048576:F2} MiB/s allocated, {result.Gen0 / result.Seconds:F2} gen0 GCs/s, {result.Allocated / (double)Math.Max(1, result.Delivered):F3} bytes allocated per byte"));

    private static async Task<(long Allocated, long Delivered, int Gen0, double Seconds)> Measure(ITerminalService terminals, string workspace, string mode, TimeSpan duration)
    {
        var sessions = new List<ITerminalSession>();
        var delivered = new long[2];
        using var stop = new CancellationTokenSource();
        var readers = new List<Task>();
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var session = await terminals.AttachAsync(new($"{mode}-allocations-{index}-{Guid.NewGuid():N}", workspace, "allocations", 120, 40));
                sessions.Add(session);
                var slot = index;
                readers.Add(Task.Run(async () =>
                {
                    try { await foreach (var chunk in session.ReadAsync(stop.Token)) Interlocked.Add(ref delivered[slot], chunk.Length); }
                    catch (OperationCanceledException) { }
                }));
                await session.WriteAsync(Encoding.UTF8.GetBytes(Stream));
            }
            // Let the shells start streaming before measuring.
            await Task.Delay(TimeSpan.FromSeconds(2));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var deliveredBefore = Interlocked.Read(ref delivered[0]) + Interlocked.Read(ref delivered[1]);
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var gen0Before = GC.CollectionCount(0);
            var clock = Stopwatch.StartNew();
            await Task.Delay(duration);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var gen0 = GC.CollectionCount(0) - gen0Before;
            var seconds = clock.Elapsed.TotalSeconds;
            var moved = Interlocked.Read(ref delivered[0]) + Interlocked.Read(ref delivered[1]) - deliveredBefore;
            if (moved < 1048576 * seconds) throw new InvalidOperationException($"{mode}: only {moved} bytes streamed in {seconds:F0} s.");
            return (allocated, moved, gen0, seconds);
        }
        finally
        {
            foreach (var session in sessions)
            {
                try { await session.KillAsync(); } catch (Exception) { }
            }
            await stop.CancelAsync();
            try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            foreach (var session in sessions) await session.DisposeAsync();
        }
    }
}