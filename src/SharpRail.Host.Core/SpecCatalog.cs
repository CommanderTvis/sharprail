using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

internal static class SpecCatalog
{
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    { ".git", ".tools", ".bench", ".sharprail", "node_modules", "bin", "obj", "dist", "build", "artifacts", "vendor", "target", ".next" };

    public static async Task<IReadOnlyList<SpecDocument>> ReadAsync(string root, CancellationToken ct)
    {
        var result = new List<SpecDocument>();
        var directories = new Stack<string>(); directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (new FileInfo(entry).LinkTarget is not null) continue;
                if (Directory.Exists(entry))
                {
                    if (!Excluded.Contains(Path.GetFileName(entry))) directories.Push(entry);
                    continue;
                }
                if (!entry.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || new FileInfo(entry).Length > 512 * 1024) continue;
                using var reader = new StreamReader(entry);
                var first = await reader.ReadLineAsync(ct);
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                if (first == "---")
                {
                    for (var lineNumber = 0; lineNumber < 80; lineNumber++)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line is null || line == "---") break;
                        var colon = line.IndexOf(':');
                        if (colon > 0) metadata[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"', '\'');
                    }
                }
                var path = Path.GetRelativePath(root, entry);
                if (!metadata.ContainsKey("id") && Path.GetFileName(entry) != "SPEC.md") continue;
                result.Add(new(metadata.GetValueOrDefault("id") ?? path,
                    metadata.GetValueOrDefault("title") ?? (first?.StartsWith("# ", StringComparison.Ordinal) == true ? first[2..] : path),
                    path, (metadata.GetValueOrDefault("parent") ?? "").Trim('[', ']'),
                    metadata.GetValueOrDefault("type") ?? "spec"));
            }
        }
        return result.GroupBy(spec => spec.Id).Select(group => group.First()).OrderBy(spec => spec.Title, StringComparer.Ordinal).ToArray();
    }
}
