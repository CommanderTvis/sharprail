namespace SharpRail.Host.Core;

/// <summary>
/// The host's filesystem watchers, one set per workspace root however many subscriptions listen. A set is
/// rebuilt when its root was replaced by another directory or its watcher failed, released with its last
/// subscriber, and may be started ahead of the first subscriber from a bounded pre-warm pool.
/// </summary>
internal static class WorkspaceWatches
{
    internal const int PrewarmLimit = 8;

    private sealed class Entry(string root)
    {
        public string Root { get; } = root;
        public List<Action<string?>> Subscribers { get; } = [];
        public List<FileSystemWatcher> Watchers { get; set; } = [];
        public DateTime Identity { get; set; }
        public int Generation { get; set; }
        public long Warmed { get; set; }
    }

    private sealed class Subscription(Entry entry, Action<string?> changed) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
                if (entry.Subscribers.Remove(changed) && entry.Subscribers.Count == 0) Remove(entry);
        }
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static long clock;

    /// <summary>Calls <paramref name="changed"/> with a workspace-relative path, or null when everything must be re-read.</summary>
    internal static IDisposable Subscribe(string root, Action<string?> changed)
    {
        lock (Gate)
        {
            var entry = Ensure(root);
            entry.Subscribers.Add(changed);
            return new Subscription(entry, changed);
        }
    }

    /// <summary>Starts watching a workspace a client is about to open. The pool keeps the most recent few without subscribers.</summary>
    internal static void Prewarm(string root)
    {
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
        lock (Gate)
            if (Entries.TryGetValue(root, out var entry) && entry.Subscribers.Count == 0) Remove(entry);
    }

    /// <summary>Subscribers and how many times the root's watchers were started, or null when it is not watched.</summary>
    internal static (int Subscribers, int Generation)? Inspect(string root)
    {
        lock (Gate) return Entries.TryGetValue(root, out var entry) ? (entry.Subscribers.Count, entry.Generation) : null;
    }

    private static Entry Ensure(string root)
    {
        Reap();
        if (Entries.TryGetValue(root, out var entry))
        {
            // The same path can become another directory (a worktree removed and created again); its watchers follow the old one.
            if (entry.Identity != Identity(root)) Restart(entry);
            return entry;
        }
        entry = new(root);
        try { Start(entry); }
        catch
        {
            foreach (var watcher in entry.Watchers) watcher.Dispose();
            throw;
        }
        Entries[root] = entry;
        return entry;
    }

    private static void Reap()
    {
        foreach (var gone in Entries.Values.Where(entry => entry.Subscribers.Count == 0 && !Directory.Exists(entry.Root)).ToArray()) Remove(gone);
    }

    private static void Remove(Entry entry)
    {
        Entries.Remove(entry.Root);
        foreach (var watcher in entry.Watchers) watcher.Dispose();
        entry.Watchers = [];
    }

    private static DateTime Identity(string root) => Directory.GetCreationTimeUtc(root);

    private static void Restart(Entry entry)
    {
        try { Start(entry); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without watchers the workspace is read on demand; the next subscription or pre-warm tries again.
            Console.Error.WriteLine($"Could not restart the watcher for {entry.Root}: {error.Message}");
            entry.Identity = default;
        }
        Notify(entry, null);
    }

    private static void Start(Entry entry)
    {
        foreach (var watcher in entry.Watchers) watcher.Dispose();
        entry.Watchers = [];
        var root = entry.Root;
        entry.Identity = Identity(root);
        entry.Generation++;
        void Watch(string path, bool recursive, Func<string, bool> relevant, bool workspace)
        {
            if (!Directory.Exists(path)) return;
            var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = recursive, InternalBufferSize = 64 * 1024 };
            entry.Watchers.Add(watcher);
            void Raise(string fullPath)
            {
                if (relevant(fullPath)) Notify(entry, workspace ? Path.GetRelativePath(root, fullPath).Replace('\\', '/') : null);
            }
            watcher.Created += (_, e) => Raise(e.FullPath);
            watcher.Changed += (_, e) => Raise(e.FullPath);
            watcher.Deleted += (_, e) => Raise(e.FullPath);
            watcher.Renamed += (_, e) => { Raise(e.OldFullPath); Raise(e.FullPath); };
            watcher.Error += (_, e) =>
            {
                Console.Error.WriteLine("Workspace watcher error: " + e.GetException().Message);
                lock (Gate)
                    if (Entries.GetValueOrDefault(root) == entry && entry.Watchers.Contains(watcher) && Directory.Exists(root)) { Restart(entry); return; }
                Notify(entry, null);
            };
            watcher.EnableRaisingEvents = true;
        }
        Watch(root, true, path => !Path.GetRelativePath(root, path).Replace('\\', '/').Split('/').Any(part => part is ".git" or ".sharprail" or ".tools" or "node_modules" or ".DS_Store"), true);
        var (gitDirectory, commonDirectory) = ProjectServices.ResolveGitDirectories(root);
        if (gitDirectory is null) return;
        Watch(gitDirectory, false, path => Path.GetFileName(path) is "HEAD" or "index", false);
        Watch(commonDirectory!, false, path => Path.GetFileName(path) == "packed-refs", false);
        Watch(Path.Combine(commonDirectory!, "refs"), true, _ => true, false);
    }

    private static void Notify(Entry entry, string? path)
    {
        Action<string?>[] subscribers;
        lock (Gate) subscribers = entry.Subscribers.ToArray();
        foreach (var subscriber in subscribers) subscriber(path);
    }
}