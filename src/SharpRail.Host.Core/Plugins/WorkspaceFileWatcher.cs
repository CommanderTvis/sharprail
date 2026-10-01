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
    private readonly FileSystemWatcher watcher;
    private readonly Action<WorkspaceFilesChanged> deliver;
    private readonly string root;
    private readonly Lock gate = new();
    private readonly Timer flush;
    private SortedSet<string> pending = new(StringComparer.Ordinal);
    private bool truncated;

    public WorkspaceFileWatcher(string root, Action<WorkspaceFilesChanged> deliver)
    {
        this.root = root; this.deliver = deliver;
        flush = new Timer(_ => Flush());
        watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += (_, change) => Record(change.FullPath);
        watcher.Created += (_, change) => Record(change.FullPath);
        watcher.Deleted += (_, change) => Record(change.FullPath);
        watcher.Renamed += (_, change) => { Record(change.OldFullPath); Record(change.FullPath); };
        watcher.Error += (_, _) => { lock (gate) truncated = true; flush.Change(Quiet, Timeout.InfiniteTimeSpan); };
        watcher.EnableRaisingEvents = true;
    }

    private void Record(string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        if (relative is ".git" || relative.StartsWith(".git/", StringComparison.Ordinal)) return;
        lock (gate)
        {
            if (pending.Count < BatchLimit) pending.Add(relative);
            else truncated = true;
        }
        flush.Change(Quiet, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        SortedSet<string> batch;
        bool overflowed;
        lock (gate)
        {
            if (pending.Count == 0 && !truncated) return;
            (batch, pending, overflowed, truncated) = (pending, new(StringComparer.Ordinal), truncated, false);
        }
        deliver(new(root, batch.ToArray(), overflowed));
    }

    public void Dispose()
    {
        watcher.Dispose();
        flush.Dispose();
    }
}