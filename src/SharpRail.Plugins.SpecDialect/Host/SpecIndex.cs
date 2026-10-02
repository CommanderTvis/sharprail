namespace SharpRail.Plugins.SpecDialect;

/// <summary>A worktree's spec files. Every read checks file stamps; unchanged files reuse their parsed frontmatter.</summary>
internal sealed class SpecIndex(string root)
{
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { ".git", "node_modules", "dist", "build" };
    private readonly Dictionary<string, (long Ticks, long Size, SpecFile? File)> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);
    private IReadOnlyList<SpecFile> snapshot = [];
    private IReadOnlyList<SpecFile>? graphFiles;
    private IReadOnlyList<SpecGraphNode> graph = [];

    public async Task<IReadOnlyList<SpecGraphNode>> ReadAsync(CancellationToken ct)
    {
        var files = await FilesAsync(ct);
        lock (cache)
        {
            if (!ReferenceEquals(files, graphFiles))
            {
                graphFiles = files;
                graph = files.DistinctBy(file => file.Id).Select(file => file.Node).ToArray();
            }
            return graph;
        }
    }

    public async Task<IReadOnlyList<SpecFile>> FilesAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var files = new List<SpecFile>();
            var seen = new HashSet<string>();
            var changed = false;
            foreach (var path in Walk(root, ct))
            {
                seen.Add(path);
                try
                {
                    var info = new FileInfo(path);
                    if (!cache.TryGetValue(path, out var entry) || entry.Ticks != info.LastWriteTimeUtc.Ticks || entry.Size != info.Length)
                    {
                        var content = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, ct));
                        var frontmatter = SpecFrontmatter.Parse(content);
                        entry = (info.LastWriteTimeUtc.Ticks, info.Length, frontmatter?.IsSpec == true
                            ? new SpecFile(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), content, frontmatter) : null);
                        cache[path] = entry;
                        changed = true;
                    }
                    if (entry.File is { } file) files.Add(file);
                }
                catch (IOException) { changed |= cache.Remove(path); }
                catch (UnauthorizedAccessException) { changed |= cache.Remove(path); }
            }
            foreach (var path in cache.Keys.Where(path => !seen.Contains(path)).ToArray()) { cache.Remove(path); changed = true; }
            if (changed) snapshot = files;
            return snapshot;
        }
        finally { gate.Release(); }
    }

    private static IEnumerable<string> Walk(string directory, CancellationToken ct)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(directory); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var entry in entries.OrderBy(path => Path.GetFileName(path).Normalize(), StringComparer.Ordinal).ThenBy(Path.GetFileName, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(entry).LinkTarget is not null) continue;
            if (Directory.Exists(entry))
            {
                if (!Excluded.Contains(Path.GetFileName(entry)))
                    foreach (var path in Walk(entry, ct)) yield return path;
            }
            else if (entry.EndsWith(".md", StringComparison.Ordinal)) yield return entry;
        }
    }

    public static (string? Absolute, string? Relative, string? Error) ResolvePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, null, "Path must not be empty.");
        if (Path.IsPathRooted(path)) return (null, null, $"Path must be root-relative, not absolute: {path}");
        if (OperatingSystem.IsWindows() && path.Contains(':')) return (null, null, $"Path must not use Windows drive or stream syntax: {path}");
        if (!path.EndsWith(".md", StringComparison.Ordinal)) return (null, null, $"Spec files must end in .md: {path}");
        var absolute = Path.GetFullPath(path, root);
        var relative = Path.GetRelativePath(root, absolute);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return (null, null, $"Path must stay inside the project root: {path}");
        if (!Directory.Exists(root)) return (null, null, $"Project root does not exist: {root}");
        var walked = root;
        var canonical = new List<string>();
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            var name = segment;
            if (Directory.Exists(walked))
            {
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(walked).Select(Path.GetFileName).OfType<string>().ToArray(); }
                catch (IOException) { return (null, null, $"Path passes through a directory the index cannot list: {path}"); }
                catch (UnauthorizedAccessException) { return (null, null, $"Path passes through a directory the index cannot list: {path}"); }
                if (!entries.Contains(segment) && (File.Exists(Path.Combine(walked, segment)) || Directory.Exists(Path.Combine(walked, segment))))
                {
                    var matches = entries.Where(entry => entry.Normalize().Equals(segment.Normalize(), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) return (null, null, $"Path component \"{segment}\" resolves ambiguously on this filesystem: {path}");
                    name = matches[0];
                }
            }
            if (Excluded.Any(entry => entry.Equals(name.Normalize(), StringComparison.OrdinalIgnoreCase)))
                return (null, null, $"Path is inside an ignored directory (\"{name}\") and would not be indexed: {path}");
            walked = Path.Combine(walked, name);
            if (new FileInfo(walked).LinkTarget is not null) return (null, null, $"Path passes through a symlink, which the index never follows: {path}");
            canonical.Add(name);
        }
        var rel = string.Join('/', canonical);
        return rel.EndsWith(".md", StringComparison.Ordinal) ? (walked, rel, null) : (null, null, $"Spec files must end in .md: {path}");
    }
}