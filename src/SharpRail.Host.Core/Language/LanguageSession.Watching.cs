using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpRail.Host.Core.Language;

/// <summary>
/// Tells the server about files created, changed and deleted on disk (by agents, Git or the editor) through
/// <c>workspace/didChangeWatchedFiles</c>, fed by the host's shared workspace watcher and honouring the watchers the server
/// registered; and knows when the project graph is still loading, so answers that could be incomplete wait instead.
/// </summary>
internal sealed partial class LanguageSession
{
    /// <summary>How long a batch of changes settles before it is sent; a steady stream is still sent at least this often times four.</summary>
    internal static TimeSpan WatchDebounce { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>The longest a reported project-input change holds requests back when the server never says it finished reloading.</summary>
    internal static TimeSpan ReloadWait { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>After loading or a restore ends, how long to wait for the watched outputs it wrote to arrive and start a reload.</summary>
    internal static TimeSpan SettleGrace { get; set; } = TimeSpan.FromSeconds(2);
    private const int PendingLimit = 4096;

    private sealed record Watcher(string Base, Regex Pattern, int Kind);
    private readonly ConcurrentDictionary<string, Watcher[]> watchers = new();
    private readonly Lock pendingGate = new();
    private readonly HashSet<string> pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> announced = new(StringComparer.Ordinal);
    private bool pendingRescan;
    private long pendingSince;
    private IDisposable? watch;
    private Timer? flushTimer;
    private DateTime watchStarted;
    private volatile bool serving;
    private long reloadRequested;
    private long settledAfter;

    /// <summary>A reported project-input change the server has not yet said it finished reloading for, bounded by <see cref="ReloadWait"/>.</summary>
    private bool Reloading => Interlocked.Read(ref reloadRequested) is var requested and > 0 && DateTime.UtcNow.Ticks - requested < ReloadWait.Ticks;
    private bool Settling => DateTime.UtcNow.Ticks < Interlocked.Read(ref settledAfter);

    /// <summary>Starts listening before <c>initialize</c>, so a file created while the graph loads is still reported once the server serves.</summary>
    private void StartWatching()
    {
        watchStarted = DateTime.UtcNow;
        // Created once, before any event: watcher callbacks run concurrently and must share one timer.
        flushTimer = new Timer(_ => _ = FlushWatchedAsync(), null, Timeout.Infinite, Timeout.Infinite);
        try { watch = WorkspaceWatches.Subscribe(workspace, Observed, owner: "language:" + server.Id); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { detail = "File watcher unavailable: " + error.Message; }
    }

    /// <summary>Lets changes flow once the server serves, first sending what changed while it loaded.</summary>
    private void ServeWatched()
    {
        serving = true;
        lock (pendingGate) if (pending.Count == 0 && !pendingRescan) return;
        _ = FlushWatchedAsync();
    }

    private void Observed(string? relative)
    {
        lock (pendingGate)
        {
            if (relative is null || pending.Count >= PendingLimit) pendingRescan = true;
            else pending.Add(Path.GetFullPath(Path.Combine(workspace, relative)));
            if (pendingSince == 0) pendingSince = Environment.TickCount64;
        }
        if (!serving) return;
        // Debounced, but a steady stream of changes is still sent at least every few debounce periods.
        var waited = Environment.TickCount64 - Interlocked.Read(ref pendingSince);
        var due = waited >= WatchDebounce.TotalMilliseconds * 4 ? TimeSpan.Zero : WatchDebounce;
        try { flushTimer?.Change(due, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private async Task FlushWatchedAsync()
    {
        try
        {
            await gate.WaitAsync(lifetime.Token);
            try { await SendWatchedAsync(lifetime.Token); }
            finally { gate.Release(); }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException) { }
    }

    /// <summary>Sends the pending changes the server watches; callers hold <see cref="gate"/> so documents and notifications stay ordered.</summary>
    private async Task SendWatchedAsync(CancellationToken token)
    {
        if (!serving || connection.Exited) return;
        string[] paths; bool rescan;
        lock (pendingGate)
        {
            paths = pending.ToArray(); rescan = pendingRescan;
            pending.Clear(); pendingRescan = false; pendingSince = 0;
        }
        if (paths.Length == 0 && !rescan) return;
        var changes = new Dictionary<string, (string Path, int Type)>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) continue;
            var type = !File.Exists(path) ? 3 : announced.Contains(path) ? 2 : Born(path) >= watchStarted ? 1 : 2;
            if (type == 3) announced.Remove(path);
            else if (type == 1) announced.Add(path);
            if (Wants(path, type)) changes[UriOf(path)] = (path, type);
            else if (type != 2 && server.Spec.ReloadingExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && Manifest(path) is { } manifest)
                changes.TryAdd(UriOf(manifest), (manifest, 2));
        }
        // Lost events (an overflow or a restarted watcher) cannot name files; a project-file change makes the server re-read them.
        if (rescan)
            foreach (var manifest in projectModel.Manifests.Select(item => Path.GetFullPath(Path.Combine(workspace, item))).Where(File.Exists))
                changes.TryAdd(UriOf(manifest), (manifest, 2));
        if (changes.Count == 0) return;
        if (server.Spec.ProjectReloadedMessage.Length > 0 && changes.Values.Any(change => change.Type != 2 || server.Spec.IsProjectInput(change.Path)))
            Interlocked.Exchange(ref reloadRequested, DateTime.UtcNow.Ticks);
        await connection.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = changes.Select(change => new { uri = change.Key, type = change.Value.Type }).ToArray() }, token);
        // An open document keeps the server's view of the file, so one edited on disk is resynchronized here: otherwise
        // files that depend on it report phantom errors (a member "does not exist") until it is diagnosed itself.
        foreach (var (uri, change) in changes)
            if (change.Type != 3 && documents.TryGetValue(uri, out var open) && !open.Dirty)
                await RefreshOpenAsync(uri, open, token);
    }

    private static DateTime Born(string path)
    {
        try { return File.GetCreationTimeUtc(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>Whether the server registered a watcher for this change; without registrations, its own files and project inputs.</summary>
    private bool Wants(string path, int type)
    {
        var registered = watchers.Values.SelectMany(list => list).ToArray();
        if (registered.Length == 0)
        {
            var relative = Path.GetRelativePath(workspace, path);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is ".git" or ".tools" or "node_modules" or "bin" or "obj" or ".bench" or "target")) return false;
            return server.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || server.Spec.IsProjectManifest(path) || server.Spec.IsProjectInput(path);
        }
        var bit = type switch { 1 => 1, 2 => 2, _ => 4 };
        foreach (var watcher in registered)
        {
            if ((watcher.Kind & bit) == 0) continue;
            string candidate;
            if (watcher.Base.Length == 0) candidate = path;
            else
            {
                var relative = Path.GetRelativePath(watcher.Base, path);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
                candidate = relative;
            }
            if (watcher.Pattern.IsMatch(candidate.Replace('\\', '/'))) return true;
        }
        return false;
    }

    /// <summary>The nearest project manifest at or above the file's directory, inside the workspace.</summary>
    private string? Manifest(string path)
    {
        for (var directory = Path.GetDirectoryName(path); directory is not null && LanguageProjectModel.Contains(workspace, directory); directory = Path.GetDirectoryName(directory))
        {
            try
            {
                if (Directory.EnumerateFiles(directory).Where(file => server.Spec.IsProjectManifest(file) && Path.GetExtension(file) is not (".sln" or ".slnx")).Order(StringComparer.Ordinal).FirstOrDefault() is { } manifest)
                    return manifest;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            if (string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(workspace), StringComparison.Ordinal)) break;
        }
        return null;
    }

    private void Register(JsonNode? parameters)
    {
        foreach (var registration in parameters?["registrations"] as JsonArray ?? [])
        {
            if (registration?["id"]?.ToString() is not { } id || registration["method"]?.ToString() != "workspace/didChangeWatchedFiles") continue;
            var list = new List<Watcher>();
            foreach (var item in registration["registerOptions"]?["watchers"] as JsonArray ?? [])
            {
                var glob = item?["globPattern"];
                var kind = item?["kind"]?.GetValue<int>() ?? 7;
                string? pattern, root;
                if (glob is JsonValue) { pattern = glob.ToString(); root = Path.IsPathRooted(pattern) ? "" : workspace; }
                else
                {
                    pattern = glob?["pattern"]?.ToString();
                    var baseUri = glob?["baseUri"] is JsonValue value ? value.ToString() : glob?["baseUri"]?["uri"]?.ToString();
                    root = baseUri is not null && Uri.TryCreate(baseUri, UriKind.Absolute, out var parsed) && parsed.IsFile ? Path.TrimEndingDirectorySeparator(parsed.LocalPath) : null;
                }
                if (pattern is null || root is null) continue;
                try { list.Add(new(root, Glob(pattern), kind)); }
                catch (ArgumentException) { }
            }
            watchers[id] = list.ToArray();
        }
    }

    private void Unregister(JsonNode? parameters)
    {
        // The protocol misspells the array as "unregisterations".
        foreach (var registration in parameters?["unregisterations"] as JsonArray ?? parameters?["unregistrations"] as JsonArray ?? [])
            if (registration?["id"]?.ToString() is { } id) watchers.TryRemove(id, out _);
    }

    /// <summary>An LSP glob (<c>*</c>, <c>**</c>, <c>?</c>, <c>{a,b}</c>, <c>[...]</c>) as an anchored regular expression over '/'-separated paths.</summary>
    internal static Regex Glob(string pattern)
    {
        var text = new StringBuilder("^");
        var braces = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var character = pattern[i];
            switch (character)
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; text.Append("(?:.*/)?"); }
                    else text.Append(".*");
                    break;
                case '*': text.Append("[^/]*"); break;
                case '?': text.Append("[^/]"); break;
                case '{': braces++; text.Append("(?:"); break;
                case '}' when braces > 0: braces--; text.Append(')'); break;
                case ',' when braces > 0: text.Append('|'); break;
                case '[':
                    var end = pattern.IndexOf(']', i + 1);
                    if (end < 0) { text.Append(@"\["); break; }
                    var set = pattern[(i + 1)..end];
                    text.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]) : Regex.Escape(set)).Append(']');
                    i = end;
                    break;
                default: text.Append(Regex.Escape(character.ToString())); break;
            }
        }
        return new Regex(text.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary>Waits until no project loading, restore or reload is in progress; false when <paramref name="timeout"/> passed first.</summary>
    internal async Task<bool> WaitSettledAsync(TimeSpan timeout, CancellationToken token)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            // Changes the watcher already saw count as pending reloads even before their debounce ends.
            bool queued;
            lock (pendingGate) queued = pending.Count > 0 || pendingRescan;
            if (queued) await FlushWatchedAsync();
            if (!IsIndexing) return true;
            if (DateTime.UtcNow >= until || connection.Exited) return false;
            await Task.Delay(100, token);
        }
    }

    private void ObserveLoading(string method, JsonNode? parameters)
    {
        if (server.Spec.ProjectReloadedMessage.Length == 0) return;
        if (method == "window/logMessage" && parameters?["message"]?.ToString() is { } message && message.Contains(server.Spec.ProjectReloadedMessage, StringComparison.Ordinal))
            Interlocked.Exchange(ref reloadRequested, 0);
        // Loading or a restore that ended writes outputs the server watches; the reload they start is reported a moment later.
        if (method == "$/progress" && parameters?["value"]?["kind"]?.ToString() == "end" || method == server.Spec.ProjectLoadedNotification)
            Interlocked.Exchange(ref settledAfter, (DateTime.UtcNow + SettleGrace).Ticks);
    }
}