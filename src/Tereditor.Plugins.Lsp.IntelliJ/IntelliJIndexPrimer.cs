namespace SharpRail.Plugins.Lsp.IntelliJ;

/// <summary>
/// Seeds a new workspace's IntelliJ index from a running session of the checkout it was created from. Each launch keeps its
/// index (RocksDB plus the workspace-model cache) in <c>index</c> inside its session directory, named through the
/// <c>indexDir</c> initialization option; the server stores project paths relative to its workspace folders, so a copy
/// opened from another checkout is reused as is and only changed files are indexed again. A RocksDB copy missing a file is
/// not recovered by the server, so the copy is taken only from an index no file of which changed for a while, and kept
/// only when the source is unchanged afterwards. The copy waits in the target's state directory until its first launch
/// claims it, so it is removed with that session like any other index.
/// </summary>
internal static class IntelliJIndexPrimer
{
    internal const string IndexDirectory = "index";
    internal const string PrimedDirectory = "primed-index";
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    /// <summary>Copies the newest session index under <paramref name="sourceState"/> to <paramref name="targetState"/>; returns whether it did.</summary>
    internal static bool Prime(string sourceState, string targetState, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(targetState, PrimedDirectory);
        if (sourceState == targetState || !Directory.Exists(sourceState) || Directory.Exists(destination)) return false;
        var source = Directory.EnumerateDirectories(sourceState).Select(session => Path.Combine(session, IndexDirectory))
            .Where(Directory.Exists).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
        if (source is null) return false;
        var staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            var before = Snapshot(source);
            // Mid-write (indexing, a flush or a compaction): leave the workspace to index itself.
            if (before.Count == 0 || before.Values.Max(file => file.Modified) > DateTime.UtcNow - Quiet) return false;
            foreach (var relative in before.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(Path.Combine(source, relative), output); // A clone on APFS.
            }
            var after = Snapshot(source);
            if (after.Count != before.Count || after.Any(file => !before.TryGetValue(file.Key, out var earlier) || earlier != file.Value)) return false;
            Directory.CreateDirectory(targetState);
            Directory.Move(staging, destination);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The index directory for a launch, after moving a primed index there when the launch has none yet.</summary>
    internal static string Claim(string workspaceState, string sessionDirectory)
    {
        var index = Path.Combine(sessionDirectory, IndexDirectory);
        var primed = Path.Combine(workspaceState, PrimedDirectory);
        if (!Directory.Exists(index) && Directory.Exists(primed))
            try
            {
                Directory.CreateDirectory(sessionDirectory);
                Directory.Move(primed, index);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return index;
    }

    private static Dictionary<string, (long Length, DateTime Modified)> Snapshot(string root) =>
        new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(root, file.FullName), file => (file.Length, file.LastWriteTimeUtc), StringComparer.Ordinal);
}