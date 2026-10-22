using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.Versioning;
using System.Text;

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Ghostty.Avalonia;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

/// <summary>
/// Two Metal terminals stream output at a fixed rate for a fixed time, one shown and one hidden, and the run reports
/// the UI cost of their frame notifications: dispatcher posts, GCs, allocation, pause time and the longest UI stall.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class NativeFramePumpBenchmark
{
    private const int ChunkBytes = 8 * 1024;
    private static readonly TimeSpan ChunkInterval = TimeSpan.FromMilliseconds(2);

    public static async Task Run(Window window)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("SHARPRAIL_FRAME_PUMP_SECONDS"), out var configured) ? configured : 20;
        await InProcess(window, seconds);
        await Hosted(window, seconds);
    }

    // Output written straight into Ghostty from feeder threads: only the renderer's notifications reach the UI.
    private static async Task InProcess(Window window, int seconds)
    {
        using var shown = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), ExternalIo: new GhosttyExternalIo()));
        using var hidden = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), ExternalIo: new GhosttyExternalIo()));
        window.Content = Pair(shown, hidden);
        await shown.Ready;
        await hidden.Ready;
        hidden.IsVisible = false;
        await Task.Delay(500);

        // Prepared output, so the feeders allocate nothing of their own.
        var chunks = Enumerable.Range(0, 16).Select(index =>
        {
            var line = new StringBuilder();
            while (line.Length < ChunkBytes - 100) line.Append("\e[3").Append(index % 8).Append("mline ").Append(index).Append(' ').Append(new string((char)('a' + index), 60)).Append("\e[0m\r\n");
            return Encoding.UTF8.GetBytes(line.ToString());
        }).ToArray();
        using var stop = new CancellationTokenSource();
        Task<long>[] feeders = [];
        await Measure("in-process", shown, hidden, seconds,
            () => { feeders = [Feed(shown, chunks, stop.Token), Feed(hidden, chunks, stop.Token)]; return Task.CompletedTask; },
            async () => { stop.Cancel(); await Task.WhenAll(feeders); });

        // No lost final frame: the last write after streaming stops must reach the shown terminal's next composited frame.
        var frames = shown.Frames;
        shown.WriteOutput("\e[2J\e[Hframe-pump-final"u8);
        await Until(() => shown.ReadScreen().Contains("frame-pump-final", StringComparison.Ordinal) && shown.Frames > frames, "The final frame after streaming stopped was not composited.");
        var posts = hidden.UiNotifications;
        frames = hidden.Frames;
        hidden.WriteOutput("\e[2J\e[Hhidden-final"u8);
        await Task.Delay(300);
        var hiddenPosts = hidden.UiNotifications - posts;
        hidden.IsVisible = true;
        await Until(() => hidden.Frames > frames, "A hidden terminal did not draw once shown.");
        Console.WriteLine($"frame-pump in-process: final frame composited; hidden terminal posts while hidden after its last write: {hiddenPosts}; drew once shown");
        window.Content = null;
    }

    // The app's path: an embedded host's PTYs streaming a fixed-rate producer through the direct Metal backend.
    private static async Task Hosted(Window window, int seconds)
    {
        SharpRail.Plugins.UI.Kit.Ui.Apply(SharpRail.UI.Rendering.Themes.All[0]);
        var root = CheckRoot.Create("frame-pump");
        Directory.CreateDirectory(root);
        try
        {
            await using var pty = new PtyTerminalService();
            var factory = TerminalBackends.Ghostty(new LocalTerminalAdapter(pty), () => TerminalRenderers.Texture);
            using var shownTab = new TerminalView(factory, new TerminalLaunch(root, "pump-shown-" + Guid.NewGuid().ToString("N"), Path.Combine(root, "clipboard"), "frame-pump"));
            using var hiddenTab = new TerminalView(factory, new TerminalLaunch(root, "pump-hidden-" + Guid.NewGuid().ToString("N"), Path.Combine(root, "clipboard"), "frame-pump"));
            window.Content = Pair(shownTab, hiddenTab);
            window.Show();
            await Until(() => shownTab.Backend is not null && hiddenTab.Backend is not null, "No renderer backends were created.");
            await shownTab.Backend!.Started.WaitAsync(TimeSpan.FromSeconds(20));
            await hiddenTab.Backend!.Started.WaitAsync(TimeSpan.FromSeconds(20));
            var shown = shownTab.GetVisualDescendants().OfType<GhosttyTextureView>().Single();
            var hidden = hiddenTab.GetVisualDescendants().OfType<GhosttyTextureView>().Single();
            hiddenTab.IsVisible = false;
            await Task.Delay(1000);
            // 8 KB every 2 ms per terminal; the shell prompt returns once the producer is interrupted.
            const string producer = "perl -e '$|=1; $l=(\"x\" x 79).\"\\n\"; $c=$l x 100; while(1){print $c; select(undef,undef,undef,0.002)}'\r";
            await Measure("host", shown, hidden, seconds,
                () => { shownTab.Backend.Write(producer); hiddenTab.Backend.Write(producer); return Task.CompletedTask; },
                () => { shownTab.Backend.Write("\x03"); hiddenTab.Backend.Write("\x03"); return Task.CompletedTask; });
            shownTab.Backend.Write("clear; echo host-pump-final\r");
            try { await Until(() => shown.ReadScreen().Split('\n').Any(line => line.Trim() == "host-pump-final"), "The hosted final frame was not shown."); }
            catch { Console.WriteLine(shown.ReadScreen()); throw; }
            Console.WriteLine("frame-pump host: final output shown after the producer stopped");
            await shownTab.Backend.CloseAsync();
            await hiddenTab.Backend.CloseAsync();
            window.Content = null;
        }
        finally { Directory.Delete(root, true); }
    }

    private static Grid Pair(Control shown, Control hidden)
    {
        var grid = new Grid { ColumnDefinitions = new("*,*") };
        Grid.SetColumn(hidden, 1);
        grid.Children.Add(shown);
        grid.Children.Add(hidden);
        return grid;
    }

    private static async Task Measure(string name, GhosttyTextureView shown, GhosttyTextureView hidden, int seconds, Func<Task> start, Func<Task> stop)
    {
        using var allocations = new AllocationListener();
        using var stopStall = new CancellationTokenSource();
        var stall = Task.Run(() => MeasureStall(stopStall.Token));
        var started = Stopwatch.GetTimestamp();
        var (gen0, gen1, gen2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        var paused = GC.GetTotalPauseDuration();
        var (shownPosts, hiddenPosts, shownFrames, hiddenFrames) = (shown.UiNotifications, hidden.UiNotifications, shown.Frames, hidden.Frames);
        await start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var result = new
        {
            seconds = Math.Round(elapsed, 2),
            shownPostsPerSecond = Math.Round((shown.UiNotifications - shownPosts) / elapsed, 1),
            hiddenPostsPerSecond = Math.Round((hidden.UiNotifications - hiddenPosts) / elapsed, 1),
            shownFramesPerSecond = Math.Round((shown.Frames - shownFrames) / elapsed, 1),
            hiddenFramesPerSecond = Math.Round((hidden.Frames - hiddenFrames) / elapsed, 1),
            gen0PerSecond = Math.Round((GC.CollectionCount(0) - gen0) / elapsed, 1),
            gen1 = GC.CollectionCount(1) - gen1,
            gen2 = GC.CollectionCount(2) - gen2,
            allocatedMiBPerSecond = Math.Round((GC.GetTotalAllocatedBytes(precise: false) - allocated) / elapsed / (1 << 20), 2),
            pauseMilliseconds = Math.Round((GC.GetTotalPauseDuration() - paused).TotalMilliseconds, 1),
            maxDispatcherStallMilliseconds = Math.Round(await StopStall(), 1),
            topAllocations = allocations.Top(8),
        };
        await stop();
        Console.WriteLine($"frame-pump {name} " + System.Text.Json.JsonSerializer.Serialize(result));

        async Task<double> StopStall() { stopStall.Cancel(); return await stall; }
    }

    private static Task<long> Feed(GhosttyTextureView view, byte[][] chunks, CancellationToken token) => Task.Factory.StartNew(() =>
    {
        long written = 0;
        var next = Stopwatch.GetTimestamp();
        var interval = (long)(ChunkInterval.TotalSeconds * Stopwatch.Frequency);
        for (var index = 0; !token.IsCancellationRequested; index++)
        {
            view.WriteOutput(chunks[index % chunks.Length]);
            written += chunks[index % chunks.Length].Length;
            next += interval;
            var wait = next - Stopwatch.GetTimestamp();
            if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
        }
        return written;
    }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // The longest time a posted Send-priority probe waited for the UI thread.
    private static async Task<double> MeasureStall(CancellationToken token)
    {
        var worst = 0.0;
        while (!token.IsCancellationRequested)
        {
            var posted = Stopwatch.GetTimestamp();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Send);
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
            try { await Task.Delay(5, token); } catch (OperationCanceledException) { }
        }
        return worst;
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(20);
        }
    }

    // Sampled allocation sites by type (one event per ~100 KB allocated).
    private sealed class AllocationListener : EventListener
    {
        private readonly Dictionary<string, long> bytes = [];

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || e.Payload is null || e.PayloadNames is null) return;
            var type = e.Payload[e.PayloadNames.IndexOf("TypeName")] as string ?? "?";
            var amount = Convert.ToInt64(e.Payload[e.PayloadNames.IndexOf("AllocationAmount64")]);
            lock (bytes) bytes[type] = bytes.GetValueOrDefault(type) + amount;
        }

        public string[] Top(int count)
        {
            lock (bytes) return [.. bytes.OrderByDescending(pair => pair.Value).Take(count).Select(pair => $"{pair.Key} {pair.Value / (1 << 20)} MiB")];
        }
    }
}