#if !ANDROID && !BROWSER
using System.Diagnostics;
using System.Runtime;

using Avalonia.Input;
using Avalonia.Interactivity;

namespace SharpRail.UI;

/// <summary>
/// Returns managed memory to the OS once after the app has sat idle: a fragmented large-object heap and Gen2 garbage
/// otherwise stay committed for hours. Idle means no input for <see cref="IdleFor"/> and the process using little CPU
/// (a terminal or agent streaming output is not idle), and never within <see cref="StartupGrace"/> of launch.
/// Times are passed in so a check can drive it with a fake clock.
/// </summary>
internal sealed class IdleMemoryTrim
{
    internal static readonly TimeSpan IdleFor = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);
    internal const double BusyCpu = 0.05;

    private long lastActivity;
    private TimeSpan lastTick, lastCpu;
    private bool trimmed;

    /// <summary>Records user input at <paramref name="now"/> (time since launch); safe from any thread.</summary>
    internal void Activity(TimeSpan now)
    {
        Volatile.Write(ref lastActivity, now.Ticks);
        Volatile.Write(ref trimmed, false);
    }

    /// <summary>True once per idle period when a compaction should run now. Called periodically from one thread.</summary>
    internal bool ShouldTrim(TimeSpan now, TimeSpan cpu)
    {
        var elapsed = now - lastTick;
        if (elapsed > TimeSpan.Zero && (cpu - lastCpu) / elapsed > BusyCpu) Activity(now);
        lastTick = now;
        lastCpu = cpu;
        if (Volatile.Read(ref trimmed) || now < StartupGrace || now - TimeSpan.FromTicks(Volatile.Read(ref lastActivity)) < IdleFor) return false;
        Volatile.Write(ref trimmed, true);
        return true;
    }

    /// <summary>Discounts the CPU the compaction itself used, so it does not read as activity.</summary>
    internal void Rebase(TimeSpan now, TimeSpan cpu)
    {
        lastTick = now;
        lastCpu = cpu;
    }

    internal static void Start()
    {
        var trim = new IdleMemoryTrim();
        var process = Process.GetCurrentProcess();
        var launched = Stopwatch.StartNew();
        // The timer runs on a pool thread, so a compaction never occupies the UI thread.
        var timer = new Timer(_ =>
        {
            try
            {
                process.Refresh();
                if (!trim.ShouldTrim(launched.Elapsed, process.TotalProcessorTime)) return;
                var before = GC.GetTotalMemory(false);
                var committed = GC.GetGCMemoryInfo().TotalCommittedBytes;
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                Trace.WriteLine($"Idle memory trim: heap {before >> 20} MB -> {GC.GetTotalMemory(false) >> 20} MB, committed {committed >> 20} MB -> {GC.GetGCMemoryInfo().TotalCommittedBytes >> 20} MB");
                process.Refresh();
                trim.Rebase(launched.Elapsed, process.TotalProcessorTime);
            }
            catch (Exception error) { Trace.WriteLine("Idle memory trim: " + error.Message); }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        // Any input to any window counts, handled or not; the handlers only stamp a time.
        void Stamp(object? sender, RoutedEventArgs e) => trim.Activity(launched.Elapsed);
        foreach (var routed in new RoutedEvent[] { InputElement.KeyDownEvent, InputElement.PointerPressedEvent, InputElement.PointerMovedEvent, InputElement.PointerWheelChangedEvent })
            routed.AddClassHandler(typeof(InputElement), Stamp, RoutingStrategies.Tunnel, true);
        Rooted = timer;
    }

    private static Timer? Rooted;
}
#endif