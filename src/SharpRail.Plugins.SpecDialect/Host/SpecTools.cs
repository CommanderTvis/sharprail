using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.SpecDialect;

/// <summary>The fork's seven spec tools, available to terminal agents through MCP.</summary>
internal static partial class SpecTools
{
    private static readonly ConcurrentDictionary<string, SpecIndex> Indexes = new(StringComparer.Ordinal);
    private static SpecIndex Index(string cwd) => Indexes.GetOrAdd(cwd, root => new(root));
    private static PluginToolResult Error(string message) => new("Error: " + message) { IsError = true, Details = new { Error = message } };
    private static PluginToolResult Result(string text, object details) => new(text) { Details = details };
    private static string EnumName<T>(T value) => JsonSerializer.SerializeToElement(value, PluginJson.Options).GetString()!;

    public static IReadOnlyList<PluginToolDefinition> All { get; } =
    [
        new PluginTool<SpecGrepParams>("spec_grep", "Spec Grep",
            "Search the project's spec-graph: regex or substring match within spec files (files whose frontmatter carries id + type), optionally narrowed by metadata (type / tag / parent / depends-on). Returns path:line matches with a snippet. Read a matched file's body with the normal read tool.",
            (parameters, context, ct) => GrepAsync(parameters, context.Cwd, ct)),
        new PluginTool<SpecGetParams>("spec_get", "Spec Get",
            "Get one spec node by id: its frontmatter, path, and resolved links (forward + reverse edges across parent/depends-on/references/implements). Returns no prose body — read the file at the returned path with the read tool.",
            (parameters, context, ct) => GetAsync(parameters, context.Cwd, ct)),
        new PluginTool<SpecGraphToolParams>("spec_graph", "Spec Graph",
            "Return a bounded slice of the spec-graph rooted at a node: subtree (down the parent tree), ancestors (up to the tree root), or neighbors (across a chosen edge and its reverse). Bounded by depth (default 1).",
            (parameters, context, ct) => GraphAsync(parameters, context.Cwd, ct)),
        new PluginTool<SpecCreateParams>("spec_create", "Spec Create",
            "Create a new spec file with scaffolded frontmatter (id, type, title, an optional status, and any links) and a heading-only body stub chosen by type. Fails if the file already exists, the id is already in use, or the path is not an indexable root-relative .md path. Edit prose afterward with the write/edit tools.",
            (parameters, context, ct) => CreateAsync(parameters, context.Cwd, ct)),
        new PluginTool<SpecUpdateParams>("spec_update", "Spec Update",
            "Edit a spec's frontmatter only (never its prose): set/overwrite scalar fields, remove fields, and add/remove entries in the list fields (depends-on/references/implements/covers/tags). Comments and any non-dialect fields are preserved. Prose is edited with the write/edit tools.",
            (parameters, context, ct) => UpdateAsync(parameters, context.Cwd, ct)),
        new PluginTool<SpecDeleteParams>("spec_delete", "Spec Delete",
            "Delete a spec file by id. Other specs may still reference it afterward — run spec_validate to find dangling links.",
            (parameters, context, ct) => DeleteAsync(parameters, context.Cwd, ct)),
        new PluginTool<SpecValidateParams>("spec_validate", "Spec Validate",
            "Validate the spec-graph: report dangling parent/depends-on/references/implements links, duplicate ids, and parent cycles.",
            (_, context, ct) => ValidateAsync(context.Cwd, ct))
    ];

    private sealed record GrepMatch(string Path, int Line, string Snippet);
    private sealed record ResolvedLink(string Kind, string Target, string? Path);

    private static async ValueTask<PluginToolResult> GrepAsync(SpecGrepParams parameters, string cwd, CancellationToken ct)
    {
        var ignoreCase = parameters.IgnoreCase ?? true;
        Func<string, bool> matches;
        if (parameters.Regex == true)
        {
            try
            {
                var expression = new Regex(parameters.Pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(1));
                matches = expression.IsMatch;
            }
            catch (ArgumentException error) { return Error("Invalid search pattern: " + error.Message); }
        }
        else matches = line => line.Contains(parameters.Pattern, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var requested = Math.Truncate(parameters.Limit ?? 200);
        var limit = requested > 0 ? requested : 200;
        var found = new List<GrepMatch>();
        var truncated = false;
        foreach (var file in await Index(cwd).FilesAsync(ct))
        {
            var fm = file.Frontmatter;
            if ((parameters.Type is { } type && file.Type != type) || (parameters.Parent is { } parent && fm.Scalar("parent") != parent) ||
                (parameters.Tag is { } tag && !fm.List("tags").Contains(tag)) || (parameters.DependsOn is { } dependency && !fm.List("depends-on").Contains(dependency))) continue;
            var lines = file.Content.TrimStart('\uFEFF').Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var line = lines[index].TrimEnd('\r');
                if (!matches(line)) continue;
                if (found.Count >= limit) { truncated = true; break; }
                found.Add(new(file.Path, index + 1, line.Trim()));
            }
            if (truncated) break;
        }
        var header = found.Count == 0 ? "No matches." : $"{found.Count} match(es){(truncated ? " (truncated)" : "")}:";
        return Result((header + "\n" + string.Join('\n', found.Select(match => $"{match.Path}:{match.Line}: {match.Snippet}"))).TrimEnd(), new { Matches = found, Truncated = truncated });
    }

    private static async ValueTask<PluginToolResult> GetAsync(SpecGetParams parameters, string cwd, CancellationToken ct)
    {
        var nodes = (await Index(cwd).FilesAsync(ct)).DistinctBy(file => file.Id).ToDictionary(file => file.Id);
        if (!nodes.TryGetValue(parameters.Id, out var node)) return Error($"No spec with id \"{parameters.Id}\".");
        var links = SpecFrontmatter.LinkKinds.SelectMany(kind => node.Frontmatter.Targets(kind).Select(target => new ResolvedLink(kind, target, nodes.GetValueOrDefault(target)?.Path))).ToArray();
        var reverse = SpecFrontmatter.LinkKinds.SelectMany(kind => nodes.Values.SelectMany(file => file.Frontmatter.Targets(kind)
            .Where(target => target == node.Id).Select(_ => new ResolvedLink(kind, file.Id, file.Path)))).ToArray();
        static string Format(ResolvedLink link) => $"  {link.Kind} -> {link.Target}" + (link.Path is { } path ? $" ({path})" : " (missing)");
        return Result(string.Join('\n', $"{node.Id} [{node.Type}]" + (node.Title is { } title ? " — " + title : ""), $"path: {node.Path}",
            links.Length > 0 ? "links:\n" + string.Join('\n', links.Select(Format)) : "links: (none)",
            reverse.Length > 0 ? "referenced by:\n" + string.Join('\n', reverse.Select(Format)) : "referenced by: (none)"),
            new { node.Id, node.Type, node.Title, node.Path, Frontmatter = node.Frontmatter.Values, Links = links, ReverseLinks = reverse });
    }
}