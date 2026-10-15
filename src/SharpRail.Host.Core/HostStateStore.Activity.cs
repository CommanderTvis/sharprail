using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// The settled lifecycle's host-owned facts: each workspace's newest activity and the user's Settled/Keep active
/// override. Which rows sit on the shelf is a client derivation over them; the host never stores that verdict.
/// </summary>
public sealed partial class HostStateStore
{
    /// <summary>A workspace's activity stamp is rewritten at most this often, so a busy terminal does not rewrite state.json per keystroke.</summary>
    public static readonly TimeSpan ActivityCoalescing = TimeSpan.FromMinutes(1);

    // The HEAD each workspace was last seen at during this host's lifetime, kept across watcher restarts.
    private readonly Dictionary<string, string> heads = new(StringComparer.Ordinal);

    /// <summary>The host's clock in Unix milliseconds; checks replace it to cross the idle window.</summary>
    internal Func<long> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Stamps real activity in the workspace at <paramref name="path"/> and clears any override: real work supersedes
    /// an explicit park or pin. A stamp within <see cref="ActivityCoalescing"/> of the stored one is skipped unless it
    /// clears an override, since only crossing the idle window, measured in days, can change the partition.
    /// </summary>
    public void RecordWorkspaceActivity(string path, long? at = null)
    {
        lock (gate)
        {
            var now = at ?? Clock();
            var workspace = state.Workspaces.FirstOrDefault(known => known.Path == path);
            if (workspace is null) return;
            if (workspace.SettledOverride is null && workspace.LastActiveAt is { } last && now - last < (long)ActivityCoalescing.TotalMilliseconds) return;
            var stamped = workspace with { LastActiveAt = Math.Max(now, workspace.LastActiveAt ?? 0), SettledOverride = null, SettledAt = null };
            Publish(state with { Workspaces = state.Workspaces.Select(known => known.Id == workspace.Id ? stamped : known).ToArray() }, persist: true);
        }
    }

    /// <summary>Parks a workspace on the Settled shelf, or keeps it live, until the user says otherwise or works in it.</summary>
    internal WorkspaceRecord SetSettledOverride(string id, string value)
    {
        lock (gate)
        {
            var workspace = state.Workspaces.FirstOrDefault(known => known.Id == id) ?? throw new InvalidOperationException($"Unknown workspace: {id}");
            if (workspace.Kind == WorkspaceKinds.Default) throw new InvalidOperationException("The Default workspace never settles.");
            if (workspace.SettledOverride == value) return workspace;
            var changed = workspace with { SettledOverride = value, SettledAt = Clock() };
            Publish(state with { Workspaces = state.Workspaces.Select(known => known.Id == id ? changed : known).ToArray() }, persist: true);
            return changed;
        }
    }

    /// <summary>
    /// Observes the HEAD of a workspace whose watcher started or whose Git directory changed. The first observation in
    /// this host's lifetime only seeds the baseline; a later one that differs is activity, including a move made while
    /// no watcher ran. HEAD rather than the watcher event is the signal: index refreshes fire it too.
    /// </summary>
    public void ObserveWorkspaceHead(string path)
    {
        var head = ReadHead(path);
        if (head is null) return;
        bool moved;
        lock (gate)
        {
            moved = heads.TryGetValue(path, out var known) && known != head;
            heads[path] = head;
        }
        if (moved) RecordWorkspaceActivity(path);
    }

    /// <summary>The commit a checkout's HEAD names, read from its Git directory without starting Git; null when unreadable.</summary>
    internal static string? ReadHead(string root)
    {
        var (gitDirectory, commonDirectory) = ProjectServices.ResolveGitDirectories(root);
        if (gitDirectory is null || commonDirectory is null) return null;
        try
        {
            var head = File.ReadAllText(Path.Combine(gitDirectory, "HEAD")).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal)) return head;
            var reference = head["ref:".Length..].Trim();
            foreach (var directory in new[] { gitDirectory, commonDirectory })
                if (File.Exists(Path.Combine(directory, reference))) return File.ReadAllText(Path.Combine(directory, reference)).Trim();
            var packed = Path.Combine(commonDirectory, "packed-refs");
            if (File.Exists(packed))
                foreach (var line in File.ReadLines(packed))
                    if (line.EndsWith(" " + reference, StringComparison.Ordinal)) return line[..line.IndexOf(' ')];
            // An unborn branch has no commit yet; its name still tells a later checkout apart.
            return head;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Gives each record that predates activity stamps one: a linked worktree's <c>.git</c> file time (about when it was
    /// added), else now. A record that cannot be read gets now rather than failing the registry.
    /// </summary>
    private static HostState BackfillActivity(HostState value, long now)
    {
        if (value.Workspaces.All(workspace => workspace.LastActiveAt is not null)) return value;
        return value with
        {
            Workspaces = value.Workspaces.Select(workspace => workspace.LastActiveAt is not null ? workspace
                : workspace with { LastActiveAt = CreatedAt(workspace) ?? now }).ToArray()
        };

        static long? CreatedAt(WorkspaceRecord workspace)
        {
            if (workspace.Kind == WorkspaceKinds.Default) return null;
            try
            {
                var marker = new FileInfo(Path.Combine(workspace.Path, ".git"));
                return marker.Exists ? new DateTimeOffset(marker.LastWriteTimeUtc).ToUnixTimeMilliseconds() : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
    }
}