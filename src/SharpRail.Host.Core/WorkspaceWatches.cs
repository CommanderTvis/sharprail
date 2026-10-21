namespace SharpRail.Host.Core;

/// <summary>
/// The host's filesystem watchers, one set per workspace root however many subscriptions listen. A set is
/// rebuilt when its root was replaced by another directory or its watcher failed, released with its last
/// subscriber, and may be started ahead of the first subscriber from a bounded pre-warm pool.
/// </summary>
internal static partial class WorkspaceWatches
{
    internal const int PrewarmLimit = 8;

    private sealed class Entry(string root, Action<FileSystemWatcher, Action<Exception>>? startWatcher = null)
    {
        public string Root { get; } = root;
        public Action<FileSystemWatcher, Action<Exception>>? StartWatcher { get; } = startWatcher;
        public Timer? Retry { get; set; }
        public int Failures { get; set; }
        public bool Failed { get; set; }
        public long StartedAt { get; set; }
        public Dictionary<long, (string Owner, Action<string?> Changed)> Subscribers { get; } = [];
        public List<FileSystemWatcher> Watchers { get; set; } = [];
        public HashSet<FileSystemWatcher> Enabled { get; } = [];
        public Dictionary<FileSystemWatcher, long> Identities { get; } = [];
        public DateTime Identity { get; set; }
        public int Generation { get; set; }
        public long Warmed { get; set; }
    }

    private sealed class Subscription(Entry entry, long id) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
                if (entry.Subscribers.Remove(id, out var subscription))
                {
                    WatcherTelemetry.Log.Lease("release", entry.Root, id, subscription.Owner);
                    if (entry.Subscribers.Count == 0) Remove(entry);
                }
        }
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static long clock;
    private static long leaseId;
    private static long created;
    private static long disposed;

    /// <summary>Calls <paramref name="changed"/> with a workspace-relative path, or null when everything must be re-read.</summary>
    internal static IDisposable Subscribe(string root, Action<string?> changed, Action<FileSystemWatcher, Action<Exception>>? startWatcher = null, string owner = "project-stream")
    {
        root = Normalize(root);
        lock (Gate)
        {
            var entry = Ensure(root, startWatcher);
            var id = ++leaseId;
            entry.Subscribers.Add(id, (owner, changed));
            WatcherTelemetry.Log.Lease("acquire", root, id, owner);
            return new Subscription(entry, id);
        }
    }

    /// <summary>Starts watching a workspace a client is about to open. The pool keeps the most recent few without subscribers.</summary>
    internal static void Prewarm(string root)
    {
        root = Normalize(root);
        lock (Gate)
        {
            if (!Directory.Exists(root)) { Reap(); return; }
            try { Ensure(root).Warmed = ++clock; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"Could not pre-warm the watcher for {root}: {error.Message}");
                return;
            }
            var idle = Entries.Values.Where(entry => entry.Subscribers.Count == 0).OrderBy(entry => entry.Warmed).ToArray();
            foreach (var oldest in idle.Take(Math.Max(0, idle.Length - PrewarmLimit))) Remove(oldest);
        }
    }

    /// <summary>Drops a pre-warmed watcher of a workspace that is gone; one with subscribers ends with them.</summary>
    internal static void Forget(string root)
    {
        root = Normalize(root);
        lock (Gate)
            if (Entries.TryGetValue(root, out var entry) && entry.Subscribers.Count == 0) Remove(entry);
    }

    /// <summary>Subscribers and how many times the root's watchers were started, or null when it is not watched.</summary>
    internal static (int Subscribers, int Generation)? Inspect(string root)
    {
        root = Normalize(root);
        lock (Gate) return Entries.TryGetValue(root, out var entry) ? (entry.Subscribers.Count, entry.Generation) : null;
    }

    private static Entry Ensure(string root, Action<FileSystemWatcher, Action<Exception>>? startWatcher = null)
    {
        Reap();
        if (Entries.TryGetValue(root, out var entry))
        {
            // The same path can become another directory (a worktree removed and created again); its watchers follow the old one.
            if (entry.Retry is null && entry.Identity != Identity(root)) Restart(entry);
            return entry;
        }
        entry = new(root, startWatcher);
        Entries[root] = entry;
        try { Start(entry); }
        catch
        {
            Remove(entry);
            throw;
        }
        return entry;
    }

    private static void Reap()
    {
        foreach (var gone in Entries.Values.Where(entry => entry.Subscribers.Count == 0 && !Directory.Exists(entry.Root)).ToArray()) Remove(gone);
    }

    private static void Remove(Entry entry)
    {
        Entries.Remove(entry.Root);
        entry.Retry?.Dispose();
        entry.Retry = null;
        foreach (var watcher in entry.Watchers) Release(entry, watcher);
        entry.Watchers = [];
    }

    private static string Normalize(string root) => Path.TrimEndingDirectorySeparator(Posix.RealPath(ProjectPaths.Resolve(root)) ?? ProjectPaths.Resolve(root));

    private static void Release(Entry entry, FileSystemWatcher watcher)
    {
        watcher.Dispose();
        entry.Enabled.Remove(watcher);
        entry.Identities.Remove(watcher, out var id);
        disposed++;
        WatcherTelemetry.Log.Watcher("dispose", entry.Root, watcher.Path, entry.Generation, id);
    }

    private static DateTime Identity(string root) => Directory.GetCreationTimeUtc(root);

    private static void Restart(Entry entry)
    {
        try { Start(entry); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Failed(entry, error);
        }
        Notify(entry, null);
    }

    private static void Failed(Entry entry, Exception error)
    {
        if (entry.Failed) return;
        entry.Failed = true;
        WatcherTelemetry.Log.Watcher("failed", entry.Root, "", entry.Generation, 0);
        if (Environment.TickCount64 - entry.StartedAt >= 30_000) entry.Failures = 0;
        entry.Identity = default;
        Console.Error.WriteLine($"Workspace watcher error for {entry.Root}: {error.Message}");
        var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 250 * Math.Pow(2, Math.Min(entry.Failures++, 7))));
        entry.Retry = new Timer(_ =>
        {
            lock (Gate)
            {
                if (Entries.GetValueOrDefault(entry.Root) != entry) return;
                entry.Retry?.Dispose();
                entry.Retry = null;
                try { Start(entry); }
                catch (Exception retryError) when (retryError is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Failed(entry, retryError);
                }
            }
            Notify(entry, null);
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    private static void Start(Entry entry)
    {
        var previous = entry.Watchers;
        entry.Watchers = [];
        foreach (var watcher in previous) Release(entry, watcher);
        entry.Failed = false;
        entry.StartedAt = Environment.TickCount64;
        var root = entry.Root;
        entry.Identity = Identity(root);
        entry.Generation++;
        void Watch(string path, bool recursive, Func<string, bool> relevant, bool workspace)
        {
            if (entry.Failed || !Directory.Exists(path)) return;
            var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = recursive, InternalBufferSize = 64 * 1024 };
            entry.Watchers.Add(watcher);
            var id = ++created;
            entry.Identities.Add(watcher, id);
            WatcherTelemetry.Log.Watcher("allocate", root, watcher.Path, entry.Generation, id);
            void Raise(string fullPath)
            {
                if (relevant(fullPath)) Notify(entry, workspace ? Path.GetRelativePath(root, fullPath).Replace('\\', '/') : null);
            }
            watcher.Created += (_, e) => Raise(e.FullPath);
            watcher.Changed += (_, e) => Raise(e.FullPath);
            watcher.Deleted += (_, e) => Raise(e.FullPath);
            watcher.Renamed += (_, e) => { Raise(e.OldFullPath); Raise(e.FullPath); };
            void Error(Exception error)
            {
                lock (Gate)
                    if (Entries.GetValueOrDefault(root) == entry && entry.Watchers.Contains(watcher))
                    {
                        entry.Enabled.Remove(watcher);
                        Failed(entry, error);
                    }
            }
            watcher.Error += (_, e) => Error(e.GetException());
            try
            {
                if (entry.StartWatcher is { } start) start(watcher, Error);
                else watcher.EnableRaisingEvents = true;
                if (!entry.Failed && watcher.EnableRaisingEvents)
                {
                    entry.Enabled.Add(watcher);
                    WatcherTelemetry.Log.Watcher("enabled", root, watcher.Path, entry.Generation, id);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Error(error);
            }
        }
        // The project's .sharprail is reported like any directory: its config is edited in Files and its appearance refreshes Git.
        Watch(root, true, path =>
            !Path.GetRelativePath(root, path).Replace('\\', '/').Split('/').Any(part => part is ".git" or ".tools" or "node_modules" or ".DS_Store" || part.StartsWith(ProjectServices.FileTransferPrefix, StringComparison.Ordinal)), true);
        var (gitDirectory, commonDirectory) = ProjectServices.ResolveGitDirectories(root);
        if (gitDirectory is not null)
        {
            Watch(gitDirectory, false, path => Path.GetFileName(path) is "HEAD" or "index", false);
            Watch(commonDirectory!, false, path => Path.GetFileName(path) == "packed-refs", false);
            Watch(Path.Combine(commonDirectory!, "refs"), true, _ => true, false);
        }
    }

    private static void Notify(Entry entry, string? path)
    {
        Action<string?>[] subscribers;
        lock (Gate) subscribers = entry.Subscribers.Values.Select(subscription => subscription.Changed).ToArray();
        foreach (var subscriber in subscribers) subscriber(path);
    }
}