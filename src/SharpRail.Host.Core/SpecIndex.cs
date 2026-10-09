using System.Text;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// One workspace's derived spec index. Every read walks the tree and revalidates each Markdown file by
/// modification time and size, so only changed files are parsed again and the graph is rebuilt only when the
/// set of parsed files changed. The filesystem stays the source of truth.
/// </summary>
internal sealed class SpecIndex(string root)
{
    private const int MaxBytes = 512 * 1024;

    internal static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    { ".git", ".tools", ".bench", ".sharprail", "node_modules", "bin", "obj", "dist", "build", "artifacts", "vendor", "target", ".next" };

    private sealed record SpecFile(SpecDocument Document, Dictionary<string, string[]> Frontmatter);
    private sealed record Entry(DateTime Modified, long Size, SpecFile? Spec);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private SpecGraph? graph;

    /// <summary>How many files were parsed and how many graphs were built, for verifying that reads are incremental.</summary>
    internal int Parses { get; private set; }
    internal int Builds { get; private set; }

    internal SpecGraph Graph(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var specs = Scan(cancellationToken);
            if (graph is null) { graph = Build(specs); Builds++; }
            return graph;
        }
    }

    /// <summary>The file that currently defines <paramref name="id"/>, relative to the root.</summary>
    internal string? PathOf(string id, CancellationToken cancellationToken) => Graph(cancellationToken).Specs.FirstOrDefault(spec => spec.Id == id)?.Path;

    private List<SpecFile> Scan(CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var specs = new List<SpecFile>();
        var changed = false;
        foreach (var path in Walk(root, top: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) continue;
            seen.Add(path);
            if (!cache.TryGetValue(path, out var entry) || entry.Modified != info.LastWriteTimeUtc || entry.Size != info.Length)
            {
                string text;
                try { text = File.ReadAllText(path, Encoding.UTF8); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { seen.Remove(path); continue; }
                cache[path] = entry = new(info.LastWriteTimeUtc, info.Length, Parse(path, text));
                Parses++; changed = true;
            }
            if (entry.Spec is not null) specs.Add(entry.Spec);
        }
        foreach (var path in cache.Keys.Where(path => !seen.Contains(path)).ToArray()) { cache.Remove(path); changed = true; }
        if (changed) graph = null;
        return specs;
    }

    /// <summary>
    /// Candidates are filtered first and then sorted by NFC-normalized name with a code-unit tie-break, directories
    /// and files in one list, so the first file to claim an id is the same on every filesystem.
    /// </summary>
    private static IEnumerable<string> Walk(string directory, bool top = false)
    {
        var candidates = new List<(string Key, string Name, bool Directory)>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var info = new FileInfo(entry);
                if (info.LinkTarget is not null) continue;
                var name = Path.GetFileName(entry);
                if (info.Attributes.HasFlag(FileAttributes.Directory)) { if (!Excluded.Contains(name)) candidates.Add((name.Normalize(NormalizationForm.FormC), name, true)); }
                else if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) candidates.Add((name.Normalize(NormalizationForm.FormC), name, false));
            }
        }
        // An unreadable folder below the root hides its own specs only; an unreadable root is a failed read.
        catch (Exception error) when (!top && error is IOException or UnauthorizedAccessException) { yield break; }
        candidates.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key) is not 0 and var order ? order : string.CompareOrdinal(a.Name, b.Name));
        foreach (var (_, name, isDirectory) in candidates)
        {
            var path = Path.Combine(directory, name);
            if (!isDirectory) yield return path;
            else foreach (var nested in Walk(path)) yield return nested;
        }
    }

    private SpecFile? Parse(string path, string text)
    {
        var frontmatter = SpecFrontmatter.Parse(text) ?? [];
        string? Scalar(string key) => frontmatter.TryGetValue(key, out var values) && values.Length > 0 ? values[0] : null;
        var relative = Path.GetRelativePath(root, path);
        if (Scalar("id") is null && Path.GetFileName(path) != "SPEC.md") return null;
        var first = text.AsSpan().TrimStart('﻿');
        var lineEnd = first.IndexOfAny('\r', '\n');
        var heading = (lineEnd < 0 ? first : first[..lineEnd]).ToString();
        return new(new(Scalar("id") ?? relative, Scalar("title") ?? (heading.StartsWith("# ", StringComparison.Ordinal) ? heading[2..] : relative),
            relative, Scalar(SpecLinks.Parent) ?? "", Scalar("type") ?? "spec")
        { Status = Scalar("status") ?? "" }, frontmatter);
    }

    private static SpecGraph Build(List<SpecFile> files)
    {
        var winners = new Dictionary<string, SpecFile>(StringComparer.Ordinal);
        var paths = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            winners.TryAdd(file.Document.Id, file);
            if (!paths.TryGetValue(file.Document.Id, out var claimed)) paths[file.Document.Id] = claimed = [];
            claimed.Add(file.Document.Path);
        }
        var edges = new List<SpecEdge>();
        foreach (var file in winners.Values)
            foreach (var kind in SpecLinks.Kinds)
            {
                if (!file.Frontmatter.TryGetValue(kind, out var targets)) continue;
                // A parent is one link even when written as a list.
                foreach (var target in kind == SpecLinks.Parent ? targets.Take(1) : targets) edges.Add(new(file.Document.Id, target, kind));
            }
        return new(winners.Values.Select(file => file.Document).OrderBy(spec => spec.Title, StringComparer.Ordinal).ToArray(), edges)
        {
            DanglingLinks = edges.Where(edge => !winners.ContainsKey(edge.To)).ToArray(),
            DuplicateIds = paths.Where(entry => entry.Value.Count > 1).Select(entry => new SpecDuplicate(entry.Key, entry.Value)).ToArray(),
            ParentCycles = ParentCycles(winners)
        };
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParentCycles(Dictionary<string, SpecFile> specs)
    {
        var cycles = new List<IReadOnlyList<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in specs.Keys)
        {
            if (seen.Contains(start)) continue;
            var walk = new List<string>();
            var current = start;
            while (specs.TryGetValue(current, out var spec) && !seen.Contains(current))
            {
                var index = walk.IndexOf(current);
                if (index >= 0) { cycles.Add(walk[index..]); break; }
                walk.Add(current);
                current = spec.Document.Parent;
            }
            seen.UnionWith(walk);
        }
        return cycles;
    }
}