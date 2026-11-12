using System.Diagnostics;

using Avalonia.Threading;

using Tereditor.Host.Abstractions;

namespace Tereditor.UI;

/// <summary>
/// Tells a stalled UI thread from a stuck pane: a background timer posts a no-op to the UI thread every second and logs a WARN line
/// to the Code Intelligence log when the no-op ran later than the threshold, with the lag. The timer never touches the UI thread itself.
/// </summary>
internal sealed class UiLagWatchdog : IDisposable
{
    private readonly Timer timer;
    private readonly TimeSpan threshold;
    private int outstanding;

    internal UiLagWatchdog(TimeSpan interval, TimeSpan threshold)
    {
        this.threshold = threshold;
        timer = new Timer(_ => Probe(), null, interval, interval);
    }

    private static UiLagWatchdog? running;

    /// <summary>Starts the app-wide watchdog once (kept referenced so its timer lives as long as the app).</summary>
    internal static void Start() => running ??= new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250));

    private void Probe()
    {
        // One probe at a time: a blocked UI thread must not queue a probe per second.
        if (Interlocked.Exchange(ref outstanding, 1) != 0) return;
        var posted = Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref outstanding, 0);
            var lag = Stopwatch.GetElapsedTime(posted);
            if (lag > threshold) CodeIntelligenceLog.Warn("ui-lag", ("lagMs", (long)lag.TotalMilliseconds));
        }, DispatcherPriority.Normal);
    }

    public void Dispose() => timer.Dispose();
}