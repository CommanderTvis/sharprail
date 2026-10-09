using System.Text;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

internal sealed record SpecDraft(string Path, string Id, string Type, string Title)
{
    public string Status { get; init; } = "";
    public string Parent { get; init; } = "";
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<string> Implements { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Body { get; init; } = "";
}

/// <summary>Scalar fields to set or remove, and values to add to or take from the list fields. Nothing else in the file changes.</summary>
internal sealed record FrontmatterEdit
{
    public IReadOnlyDictionary<string, string> Set { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Remove { get; init; } = [];
    public IReadOnlyDictionary<string, string[]> AddList { get; init; } = new Dictionary<string, string[]>();
    public IReadOnlyDictionary<string, string[]> RemoveList { get; init; } = new Dictionary<string, string[]>();
}

/// <summary>
/// Creating, updating and deleting spec files. One path rule decides where a spec may live: only somewhere the
/// catalog's traversal can see, so a spec never becomes invisible by being written.
/// </summary>
internal static class SpecAuthoring
{
    private static readonly string[] ListFields = [SpecLinks.DependsOn, SpecLinks.References, SpecLinks.Implements, "covers", "tags"];

    /// <summary>The root-relative path with forward slashes, or an <see cref="ArgumentException"/> naming the rule it breaks.</summary>
    internal static string ResolvePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) throw new ArgumentException("Path must not be empty.");
        if (Path.IsPathRooted(path)) throw new ArgumentException($"Path must be root-relative, not absolute: {path}");
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Spec files must end in .md: {path}");
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != ".").ToArray();
        if (segments.Length == 0 || segments.Contains("..")) throw new ArgumentException($"Path must stay inside the project root: {path}");
        if (!Directory.Exists(root)) throw new ArgumentException($"Project root does not exist: {root}");
        var walked = root;
        for (var index = 0; index < segments.Length; index++)
        {
            if (index < segments.Length - 1 && SpecIndex.Excluded.Contains(segments[index]))
                throw new ArgumentException($"Path is inside an ignored directory (\"{segments[index]}\") and would not be indexed: {path}");
            walked = Path.Combine(walked, segments[index]);
            if (new FileInfo(walked).LinkTarget is not null) throw new ArgumentException($"Path passes through a symlink, which the index never follows: {path}");
        }
        if (Directory.Exists(walked)) throw new ArgumentException($"Path is a directory: {path}");
        return string.Join('/', segments);
    }

    internal static string Create(string root, SpecDraft draft, CancellationToken cancellationToken = default)
    {
        var relative = ResolvePath(root, draft.Path);
        foreach (var (name, value) in new[] { ("id", draft.Id), ("type", draft.Type), ("title", draft.Title) })
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\n')) throw new ArgumentException($"A spec needs a single-line {name}.");
        if (SpecCatalog.For(root).PathOf(draft.Id, cancellationToken) is { } taken) throw new InvalidOperationException($"Spec id \"{draft.Id}\" already exists at {taken}.");
        var text = new StringBuilder(SpecFrontmatter.Fence).Append('\n');
        foreach (var (key, value) in new[] { ("id", draft.Id), ("type", draft.Type), ("status", draft.Status), ("title", draft.Title), (SpecLinks.Parent, draft.Parent) })
            if (value.Length > 0) text.Append(key).Append(": ").Append(SpecFrontmatter.Write(value)).Append('\n');
        foreach (var (key, values) in new[] { (SpecLinks.DependsOn, draft.DependsOn), (SpecLinks.References, draft.References), (SpecLinks.Implements, draft.Implements), ("tags", draft.Tags) })
            if (values.Count > 0) text.Append(ListLine(key, values)).Append('\n');
        text.Append(SpecFrontmatter.Fence).Append('\n').Append(draft.Body);
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
            file.Write(Encoding.UTF8.GetBytes(text.ToString()));
        }
        catch (IOException) when (File.Exists(full)) { throw new InvalidOperationException($"A file already exists at {relative}."); }
        return relative;
    }

    internal static string Update(string root, string id, FrontmatterEdit edit, CancellationToken cancellationToken = default)
    {
        var relative = Existing(root, id, cancellationToken);
        var full = Path.Combine(root, relative);
        // Bytes in, bytes out: the body and a byte order mark are never decoded into something else.
        var bytes = File.ReadAllBytes(full);
        var updated = Encoding.UTF8.GetBytes(UpdateText(ContentInfo.Decode(bytes), edit));
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, ".sharprail-spec-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(temporary, updated);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(full));
            File.Move(temporary, full, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return relative;
    }

    internal static string Delete(string root, string id, CancellationToken cancellationToken = default)
    {
        var relative = Existing(root, id, cancellationToken);
        File.Delete(Path.Combine(root, relative));
        return relative;
    }

    private static string Existing(string root, string id, CancellationToken cancellationToken) =>
        ResolvePath(root, SpecCatalog.For(root).PathOf(id, cancellationToken) ?? throw new InvalidOperationException($"Unknown spec id: {id}"));

    /// <summary>Rewrites only the frontmatter block; untouched keys, comments, the body, the line ending and a byte order mark survive.</summary>
    internal static string UpdateText(string text, FrontmatterEdit edit)
    {
        if (SpecFrontmatter.Block(text, out var bodyStart) is not { } lines) throw new InvalidOperationException("File has no frontmatter to update.");
        var segments = new List<(string? Key, List<string> Lines)>();
        foreach (var line in lines)
        {
            if (SpecFrontmatter.KeyOf(line) is { } key) segments.Add((key, [line]));
            else if (segments.Count > 0 && segments[^1].Key is not null && line.Length > 0 && (char.IsWhiteSpace(line[0]) || line[0] == '-')) segments[^1].Lines.Add(line);
            else segments.Add((null, [line]));
        }
        void Put(string key, string? line)
        {
            var index = segments.FindIndex(segment => segment.Key == key);
            if (line is null) { if (index >= 0) segments.RemoveAt(index); }
            else if (index >= 0) segments[index] = (key, [line]);
            else segments.Add((key, [line]));
        }
        string[] Values(string key) => segments.FirstOrDefault(segment => segment.Key == key).Lines is { } owned
            ? SpecFrontmatter.Parse($"{SpecFrontmatter.Fence}\n{string.Join('\n', owned)}\n{SpecFrontmatter.Fence}\n")!.GetValueOrDefault(key) ?? [] : [];

        foreach (var (key, value) in edit.Set)
        {
            if (key == "id") throw new ArgumentException("Cannot rename a spec's id via set.");
            if (ListFields.Contains(key)) throw new ArgumentException($"Use addList/removeList to edit the list field \"{key}\".");
            if (value.Contains('\n')) throw new ArgumentException($"A frontmatter value must be a single line: {key}");
            Put(key, value.Length == 0 ? null : $"{key}: {SpecFrontmatter.Write(value)}");
        }
        foreach (var key in edit.Remove)
        {
            if (key is "id" or "type") throw new ArgumentException($"Cannot remove protected field \"{key}\".");
            Put(key, null);
        }
        foreach (var field in ListFields)
        {
            if (!edit.AddList.TryGetValue(field, out var add)) add = [];
            if (!edit.RemoveList.TryGetValue(field, out var remove)) remove = [];
            if (add.Length == 0 && remove.Length == 0) continue;
            var next = Values(field).Concat(add).Distinct().Where(value => !remove.Contains(value)).ToArray();
            Put(field, next.Length == 0 ? null : ListLine(field, next));
        }
        if (Values("id").Length == 0 || Values("type").Length == 0) throw new InvalidOperationException("Update would leave the file without a valid id and type.");

        var firstBreak = text.IndexOf('\n');
        var ending = firstBreak > 0 && text[firstBreak - 1] == '\r' ? "\r\n" : "\n";
        var result = new StringBuilder(text.StartsWith('﻿') ? "﻿" : "").Append(SpecFrontmatter.Fence).Append(ending);
        foreach (var line in segments.SelectMany(segment => segment.Lines)) result.Append(line).Append(ending);
        return result.Append(SpecFrontmatter.Fence).Append(ending).Append(text, bodyStart, text.Length - bodyStart).ToString();
    }

    private static string ListLine(string key, IEnumerable<string> values) => $"{key}: [{string.Join(", ", values.Select(SpecFrontmatter.Write))}]";
}