namespace SharpRail.Plugins.ClaudeCode.Host.Chat;

/// <summary>
/// The <c>@</c> mention search, shaped like Claude Code's own: files and the directories above them, matched fuzzily (the query's characters
/// in order, so <c>@msrc</c> finds <c>src/main.c</c>), best first: the name equals the query, starts with it, contains it, the path contains
/// it, then the query as a subsequence of the name, then of the whole path. An empty query lists the top level. Directories end in a slash.
/// </summary>
internal static class ChatFileSearch
{
    /// <summary>The ranked matches for <paramref name="query"/> among <paramref name="files"/> (repository-relative, forward slashes).</summary>
    internal static IReadOnlyList<string> Rank(IEnumerable<string> files, string query, int take)
    {
        query = query.Trim().Replace('\\', '/');
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Length == 0) continue;
            candidates.Add(file);
            for (var slash = file.IndexOf('/'); slash >= 0; slash = file.IndexOf('/', slash + 1)) candidates.Add(file[..(slash + 1)]);
        }
        if (query.Length == 0)
            return [.. candidates.Where(path => path.TrimEnd('/').IndexOf('/') < 0).OrderByDescending(path => path.EndsWith('/')).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).Take(take)];
        return [.. candidates.Select(path => (Path: path, Score: Score(path, query))).Where(item => item.Score is not null)
            .OrderBy(item => item.Score!.Value.Tier).ThenBy(item => item.Score!.Value.Span).ThenBy(item => item.Path.EndsWith('/') ? 0 : 1).ThenBy(item => item.Path.Length)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Take(take).Select(item => item.Path)];
    }

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", ".hg", ".svn", "node_modules", "bin", "obj", "target", "dist", "build", ".venv", "__pycache__", ".idea", ".DS_Store" };

    /// <summary>The files below <paramref name="root"/> (relative, forward slashes) for a folder that is not a Git repository, skipping dependency and build directories and capped at <paramref name="limit"/>.</summary>
    internal static IReadOnlyList<string> Walk(string root, int limit, CancellationToken cancellationToken)
    {
        var found = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue("");
        while (pending.Count > 0 && found.Count < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = pending.Dequeue();
            try
            {
                foreach (var entry in new DirectoryInfo(Path.Combine(root, relative)).EnumerateFileSystemInfos())
                {
                    if (Skipped.Contains(entry.Name)) continue;
                    var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                    if (entry is DirectoryInfo && entry.LinkTarget is null) pending.Enqueue(path);
                    else found.Add(path);
                    if (found.Count >= limit) break;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    private static (int Tier, int Span)? Score(string path, string query)
    {
        var trimmed = path.TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        const StringComparison Fold = StringComparison.OrdinalIgnoreCase;
        if (name.Equals(query, Fold) || Path.GetFileNameWithoutExtension(name).Equals(query, Fold)) return (0, 0);
        if (name.StartsWith(query, Fold)) return (1, 0);
        if (name.Contains(query, Fold)) return (2, 0);
        if (trimmed.Contains(query, Fold)) return (3, 0);
        if (Span(name, query) is { } inName) return (4, inName);
        if (Span(trimmed, query) is { } inPath) return (5, inPath);
        return null;
    }

    // The length of the shortest window of the text that holds the query's characters in order, or null when it does not; greedy from each start.
    private static int? Span(string text, string query)
    {
        int? best = null;
        for (var start = 0; start < text.Length; start++)
        {
            if (char.ToUpperInvariant(text[start]) != char.ToUpperInvariant(query[0])) continue;
            var at = start;
            var matched = 1;
            while (matched < query.Length && ++at < text.Length)
                if (char.ToUpperInvariant(text[at]) == char.ToUpperInvariant(query[matched])) matched++;
            if (matched < query.Length) break;
            var span = at - start + 1;
            if (best is null || span < best) best = span;
        }
        return best;
    }
}