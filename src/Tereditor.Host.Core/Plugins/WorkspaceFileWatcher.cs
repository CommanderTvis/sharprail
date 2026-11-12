using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// Watches one workspace for a plugin activation and delivers coalesced batches of changed paths, relative to
/// the workspace root. Git's own bookkeeping under <c>.git</c> is not a workspace change.
/// </summary>
internal sealed class WorkspaceFileWatcher : IDisposable
{
    private const int BatchLimit = 512;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);
    private readonly IDisposable watcher;
    private readonly Action<WorkspaceFilesChanged> deliver;
    private readonly string root;
    private readonly Lock gate = new();
    private readonly Timer flush;
    private SortedSet<string> pending = new(StringComparer.Ordinal);
    private bool truncated;
    private bool disposed;

    public WorkspaceFileWatcher(string root, string owner, Action<WorkspaceFilesChanged> deliver)
    {
        this.root = root; this.deliver = deliver;
        flush = new Timer(_ => Flush());
        try { watcher = WorkspaceWatches.Subscribe(root, Record, owner: owner); }
        catch { flush.Dispose(); throw; }
    }

    private void Record(string? relative)
    {
        lock (gate)
        {
            if (disposed) return;
            if (relative is null || pending.Count >= BatchLimit) truncated = true;
            else pending.Add(relative);
            flush.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        SortedSet<string> batch;
        bool overflowed;
        lock (gate)
        {
            if (disposed || pending.Count == 0 && !truncated) return;
            (batch, pending, overflowed, truncated) = (pending, new(StringComparer.Ordinal), truncated, false);
        }
        deliver(new(root, batch.ToArray(), overflowed));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending.Clear();
            flush.Dispose();
        }
        watcher.Dispose();
    }
}