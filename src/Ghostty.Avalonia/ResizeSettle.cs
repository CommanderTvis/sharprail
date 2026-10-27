using Avalonia.Threading;

namespace Ghostty.Avalonia;

/// <summary>
/// Tells a resize gesture from a single size change: the first size and any jump flagged with <see cref="ApplyNext"/>
/// apply at once; while sizes keep changing the caller holds its current grid, and the last size applies once
/// <see cref="Quiet"/> passes without another. A pending wait is dropped on <see cref="Cancel"/> and <see cref="Dispose"/>.
/// </summary>
internal sealed class ResizeSettle<T>(Action settled) : IDisposable where T : struct
{
    /// <summary>How long a size must stay unchanged to count as the end of a resize gesture.</summary>
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMilliseconds(140);
    private T? pending, ready;
    private bool started, immediate, disposed, waiting;
    private int generation;
    public TimeSpan Quiet { get; set; } = DefaultQuiet;

    /// <summary>The next size applies without waiting, such as after a window state change.</summary>
    public void ApplyNext() => immediate = true;

    /// <summary>The caller has a first size; later changes are gestures.</summary>
    public void Applied() => started = true;

    /// <summary>True when <paramref name="target"/> should not be applied yet; the caller keeps what it has.</summary>
    public bool Hold(T target)
    {
        if (disposed) return false;
        if (ready is { } done && EqualityComparer<T>.Default.Equals(done, target)) { ready = null; return false; }
        if (!started || immediate)
        {
            immediate = false; Cancel();
            return false;
        }
        pending = target; ready = null; generation++;
        if (!waiting) Wait(generation);
        return true;
    }

    public void Cancel() { generation++; pending = null; ready = null; }

    // One delay is in flight per gesture; it re-arms itself while sizes keep arriving.
    private void Wait(int seen)
    {
        waiting = true;
        Task.Delay(Quiet).ContinueWith(_ => Dispatcher.UIThread.Post(() => Elapsed(seen)), TaskScheduler.Default);
    }

    private void Elapsed(int seen)
    {
        waiting = false;
        if (disposed || pending is not { } target) return;
        if (seen != generation) { Wait(generation); return; }
        pending = null; ready = target;
        settled();
    }

    public void Dispose() { disposed = true; Cancel(); }
}