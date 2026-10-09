using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// The host's terminal catalog: persisted to <c>catalog.json</c> in its directory on every change (or kept
/// in memory without one) and published as complete snapshots. It knows nothing about shells; the terminal
/// service ends them.
/// </summary>
public sealed class TerminalCatalogStore
{
    public const string FileName = "catalog.json";

    private sealed class Stored
    {
        public Dictionary<string, List<TerminalTab>> Workspaces { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock gate = new();
    private readonly string? path;
    private readonly List<Channel<TerminalCatalog>> watchers = [];
    private readonly Dictionary<string, List<TerminalTab>> workspaces = [];
    private TerminalCatalog current = TerminalCatalog.Empty;

    /// <summary>Whether a catalog file was read, as opposed to a host that never kept one.</summary>
    public bool Loaded { get; }
    public string? LastError { get; private set; }
    public TerminalCatalog Current { get { lock (gate) return current; } }

    public TerminalCatalogStore(string? directory = null)
    {
        path = directory is null ? null : Path.Combine(directory, FileName);
        try
        {
            if (path is null || !File.Exists(path)) return;
            Loaded = true;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
            foreach (var (root, tabs) in stored?.Workspaces ?? [])
            {
                if (string.IsNullOrEmpty(root) || tabs is null) continue;
                // An oversized catalog is truncated, an invalid key dropped and an invalid title repaired.
                workspaces[root] = tabs.Take(TerminalTab.MaxPerWorkspace).Where(tab => tab is not null && ValidKey(tab.Key)).DistinctBy(tab => tab.Key)
                    .Select(tab => ValidTitle(tab.Title) ? tab : tab with { Title = "Terminal" }).ToList();
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { workspaces.Clear(); LastError = error.Message; }
        current = Snapshot(0);
    }

    /// <summary>Every session a catalogued tab names.</summary>
    public HashSet<string> Sessions()
    {
        lock (gate) return workspaces.SelectMany(entry => entry.Value.Select(tab => TerminalTab.SessionFor(entry.Key, tab.Key))).ToHashSet();
    }

    public TerminalCatalog OpenWorkspace(string workspaceRoot, IReadOnlyList<TerminalTab> tabs)
    {
        RequireRoot(workspaceRoot);
        foreach (var tab in tabs) Require(tab);
        lock (gate)
            return Change(() =>
            {
                var known = workspaces.TryGetValue(workspaceRoot, out var existing);
                if (!known) workspaces[workspaceRoot] = existing = [];
                var changed = !known;
                foreach (var tab in !known && tabs.Count == 0 ? [new(TerminalTab.InitialKey, TerminalTab.InitialTitle)] : tabs)
                {
                    if (known && tab.Key == TerminalTab.InitialKey || existing!.Count >= TerminalTab.MaxPerWorkspace || existing.Any(item => item.Key == tab.Key)) continue;
                    existing.Add(tab); changed = true;
                }
                return changed;
            });
    }

    public TerminalCatalog Reserve(string workspaceRoot, TerminalTab tab)
    {
        RequireRoot(workspaceRoot);
        Require(tab);
        lock (gate)
            return Change(() =>
            {
                var existing = workspaces.GetValueOrDefault(workspaceRoot);
                if (existing?.Any(item => item.Key == tab.Key) == true) return false;
                if (existing?.Count >= TerminalTab.MaxPerWorkspace)
                    throw new InvalidOperationException($"Terminal tabs are limited to {TerminalTab.MaxPerWorkspace} per workspace.");
                if (existing is null) workspaces[workspaceRoot] = existing = [];
                existing.Add(tab);
                return true;
            });
    }

    public TerminalCatalog Remove(string workspaceRoot, string key)
    {
        lock (gate) return Change(() => workspaces.GetValueOrDefault(workspaceRoot)?.RemoveAll(tab => tab.Key == key) > 0, strict: false);
    }

    /// <summary>Removes the tab a session belongs to, when it is a catalogued one.</summary>
    public TerminalCatalog RemoveSession(string sessionId)
    {
        lock (gate)
            return Change(() => workspaces.Any(entry => entry.Value.RemoveAll(tab => TerminalTab.SessionFor(entry.Key, tab.Key) == sessionId) > 0), strict: false);
    }

    /// <summary>Forgets a workspace; returns the tabs it had.</summary>
    public IReadOnlyList<TerminalTab> Forget(string workspaceRoot)
    {
        lock (gate)
        {
            IReadOnlyList<TerminalTab> removed = [];
            Change(() => { var known = workspaces.Remove(workspaceRoot, out var tabs); removed = tabs ?? []; return known; }, strict: false);
            return removed;
        }
    }

    public async IAsyncEnumerable<TerminalCatalog> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<TerminalCatalog>(new() { SingleReader = true });
        lock (gate) { watchers.Add(channel); channel.Writer.TryWrite(current); }
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                // Snapshots are complete; a slow watcher only needs the latest.
                var latest = default(TerminalCatalog);
                while (channel.Reader.TryRead(out var item)) latest = item;
                if (latest is not null) yield return latest;
            }
        }
        finally { lock (gate) watchers.Remove(channel); }
    }

    // A change is validated and applied, persisted, and only then published. A new reservation that cannot be
    // persisted is undone and reaches nobody; a removal that cannot be persisted still takes effect.
    private TerminalCatalog Change(Func<bool> mutation, bool strict = true)
    {
        var before = workspaces.ToDictionary(entry => entry.Key, entry => entry.Value.ToList());
        if (!mutation()) return current;
        var next = Snapshot(current.Revision + 1);
        try { Save(next); LastError = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LastError = error.Message;
            if (strict)
            {
                workspaces.Clear();
                foreach (var (root, tabs) in before) workspaces[root] = tabs;
                throw new IOException("The host could not save its terminal catalog: " + error.Message, error);
            }
        }
        current = next;
        foreach (var watcher in watchers) watcher.Writer.TryWrite(current);
        return current;
    }

    private TerminalCatalog Snapshot(long revision) =>
        new(revision, workspaces.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<TerminalTab>)entry.Value.ToArray()));

    private void Save(TerminalCatalog snapshot)
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored { Workspaces = snapshot.Workspaces.ToDictionary(entry => entry.Key, entry => entry.Value.ToList()) }, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool ValidKey(string? key) => key is { Length: > 0 and <= TerminalTab.MaxKeyLength };

    private static bool ValidTitle(string? title) => title is { Length: > 0 and <= TerminalTab.MaxTitleLength };

    private static void RequireRoot(string workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot) || workspaceRoot.Contains('\0')) throw new ArgumentException("Invalid workspace path.");
    }

    private static void Require(TerminalTab tab)
    {
        if (tab is null || !ValidKey(tab.Key)) throw new ArgumentException("Invalid terminal tab key.");
        if (!ValidTitle(tab.Title)) throw new ArgumentException("Invalid terminal title.");
    }
}

/// <summary>
/// A catalog over terminals this host does not run itself: membership is kept here and a closed tab's
/// session is ended through <paramref name="end"/>, when there is one.
/// </summary>
public sealed class MemoryTerminalCatalog(TerminalCatalogStore? store = null, Func<string, ValueTask>? end = null) : ITerminalCatalogService
{
    private readonly TerminalCatalogStore store = store ?? new();

    public TerminalCatalogStore Store => store;

    public ValueTask<TerminalCatalog> OpenWorkspaceAsync(string workspaceRoot, IReadOnlyList<TerminalTab> tabs, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(store.OpenWorkspace(workspaceRoot, tabs));

    public ValueTask<TerminalCatalog> ReserveAsync(string workspaceRoot, TerminalTab tab, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(store.Reserve(workspaceRoot, tab));

    public async ValueTask<TerminalCatalog> CloseTabAsync(string workspaceRoot, string key, CancellationToken cancellationToken = default)
    {
        var result = store.Remove(workspaceRoot, key);
        if (end is not null) await end(TerminalTab.SessionFor(workspaceRoot, key));
        return result;
    }

    public async ValueTask<TerminalCatalog> CloseWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        foreach (var tab in store.Forget(workspaceRoot))
            if (end is not null) await end(TerminalTab.SessionFor(workspaceRoot, tab.Key));
        return store.Current;
    }

    public IAsyncEnumerable<TerminalCatalog> WatchAsync(CancellationToken cancellationToken = default) => store.WatchAsync(cancellationToken);
}