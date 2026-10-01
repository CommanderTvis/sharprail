using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    internal const int SearchHitLimit = 200;
    private const long SearchFileBytes = 512 * 1024;
    private const int SearchLineChars = 400;

    /// <summary>
    /// A plain case-insensitive substring sweep of the worktree: Git-ignored files, <c>.git</c>, files over 512 KB
    /// and anything carrying a NUL byte are skipped, and the sweep stops at <see cref="SearchHitLimit"/> hits.
    /// </summary>
    public async ValueTask<SearchHits> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var hits = new List<SearchHit>();
        if (query.Length == 0) return new(hits, false);
        foreach (var path in await SearchablePathsAsync(currentRoot, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(Path.Combine(currentRoot, path));
            string text;
            try
            {
                if (!file.Exists || file.LinkTarget is not null || file.Length > SearchFileBytes) continue;
                text = await File.ReadAllTextAsync(file.FullName, cancellationToken);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (text.Contains('\0')) continue;
            var lines = text.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var line = lines[index].TrimEnd('\r');
                hits.Add(new(path.Replace('\\', '/'), index + 1, line.Length > SearchLineChars ? line[..SearchLineChars] : line));
                if (hits.Count >= SearchHitLimit) return new(hits, true);
            }
        }
        return new(hits, false);
    }

    /// <summary>Tracked and untracked-but-not-ignored files; a folder without Git has nothing ignored.</summary>
    private static async Task<IEnumerable<string>> SearchablePathsAsync(string currentRoot, CancellationToken cancellationToken)
    {
        try
        {
            var listed = await GitRepository.RunAsync(currentRoot, cancellationToken, "ls-files", "--cached", "--others", "--exclude-standard", "-z");
            return listed.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().Order(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var found = new List<string>();
            var pending = new Stack<string>([currentRoot]);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", options))
                {
                    if (Hidden(Path.GetFileName(entry))) continue;
                    if (Directory.Exists(entry)) pending.Push(entry);
                    else found.Add(Path.GetRelativePath(currentRoot, entry));
                }
            }
            return found.Order(StringComparer.Ordinal);
        }
    }
}