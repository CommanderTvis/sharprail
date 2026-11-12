using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.SpecDialect;

internal static partial class SpecTools
{
    private sealed record SpecEdge(string From, string To, string Kind);
    private static IReadOnlyList<SpecEdge> Edges(IEnumerable<SpecFile> files) => files.SelectMany(file =>
        SpecFrontmatter.LinkKinds.SelectMany(kind => file.Frontmatter.Targets(kind).Select(target => new SpecEdge(file.Id, target, kind)))).ToArray();
    private static object GraphNode(SpecFile file) => new { file.Id, file.Type, file.Title, file.Path, Frontmatter = file.Frontmatter.Values };

    private static async ValueTask<PluginToolResult> GraphAsync(SpecGraphToolParams parameters, string cwd, CancellationToken ct)
    {
        var nodes = (await Index(cwd).FilesAsync(ct)).DistinctBy(file => file.Id).ToDictionary(file => file.Id);
        if (!nodes.ContainsKey(parameters.Root)) return Error($"No spec with id \"{parameters.Root}\".");
        var allEdges = Edges(nodes.Values);
        var included = new HashSet<string> { parameters.Root };
        var order = new List<string> { parameters.Root };
        var missing = new HashSet<string>();
        var missingOrder = new List<string>();
        var edges = new List<SpecEdge>();
        var seenEdges = new HashSet<SpecEdge>();
        var frontier = new List<string> { parameters.Root };
        var depth = parameters.Depth ?? 1;
        var edgeKind = EnumName(parameters.Edge ?? SpecEdgeKind.DependsOn);
        for (var d = 0; d < depth; d++)
        {
            ct.ThrowIfCancellationRequested();
            var next = new List<string>();
            foreach (var id in frontier)
            {
                var candidates = parameters.Direction switch
                {
                    SpecSliceDirection.Subtree => allEdges.Where(edge => edge.Kind == "parent" && edge.To == id),
                    SpecSliceDirection.Ancestors => allEdges.Where(edge => edge.Kind == "parent" && edge.From == id),
                    _ => allEdges.Where(edge => edge.Kind == edgeKind && edge.From == id).Concat(allEdges.Where(edge => edge.Kind == edgeKind && edge.To == id))
                };
                foreach (var edge in candidates)
                {
                    var to = parameters.Direction == SpecSliceDirection.Subtree ? edge.From : edge.From == id ? edge.To : edge.From;
                    if (seenEdges.Add(edge)) edges.Add(edge);
                    if (!nodes.ContainsKey(to) && missing.Add(to)) missingOrder.Add(to);
                    if (included.Add(to)) { order.Add(to); next.Add(to); }
                }
            }
            if (next.Count == 0) break;
            frontier = next;
        }
        var slice = order.Where(nodes.ContainsKey).Select(id => nodes[id]).ToArray();
        var direction = EnumName(parameters.Direction);
        var lines = new List<string> { $"Slice of \"{parameters.Root}\" ({direction}, depth {depth}):", $"nodes ({slice.Length}):" };
        lines.AddRange(slice.Select(node => $"  {node.Id} [{node.Type}]" + (node.Title is { } title ? " — " + title : "") + $" ({node.Path})"));
        lines.Add($"edges ({edges.Count}):");
        lines.AddRange(edges.Select(edge => $"  {edge.From} --{edge.Kind}--> {edge.To}"));
        if (missing.Count > 0) lines.Add("missing targets: " + string.Join(", ", missingOrder));
        return Result(string.Join('\n', lines), new { parameters.Root, Direction = direction, Nodes = slice.Select(GraphNode).ToArray(), Edges = edges, Missing = missingOrder });
    }

    private sealed record DanglingLink(string From, string FromPath, string Kind, string Target);
    private sealed record DuplicateId(string Id, IReadOnlyList<string> Paths);
    private sealed record ParentCycle(IReadOnlyList<string> Ids);

    private static async ValueTask<PluginToolResult> ValidateAsync(string cwd, CancellationToken ct)
    {
        var files = await Index(cwd).FilesAsync(ct);
        var nodes = files.DistinctBy(file => file.Id).ToDictionary(file => file.Id);
        var duplicates = files.GroupBy(file => file.Id).Where(group => group.Count() > 1).Select(group => new DuplicateId(group.Key, group.Select(file => file.Path).ToArray())).ToArray();
        var dangling = Edges(nodes.Values).Where(edge => !nodes.ContainsKey(edge.To)).Select(edge => new DanglingLink(edge.From, nodes[edge.From].Path, edge.Kind, edge.To)).ToArray();
        var cycles = new List<ParentCycle>();
        var seen = new HashSet<string>();
        foreach (var start in nodes.Keys)
        {
            if (seen.Contains(start)) continue;
            var path = new List<string>();
            var onPath = new Dictionary<string, int>();
            var current = start;
            while (current is not null && nodes.TryGetValue(current, out var node))
            {
                if (onPath.TryGetValue(current, out var offset)) { cycles.Add(new(path.Skip(offset).ToArray())); break; }
                if (seen.Contains(current)) break;
                onPath[current] = path.Count;
                path.Add(current);
                current = node.Frontmatter.Scalar("parent");
            }
            foreach (var id in path) seen.Add(id);
        }
        var report = new { DanglingLinks = dangling, DuplicateIds = duplicates, ParentCycles = cycles };
        var sections = new List<string>();
        if (duplicates.Length > 0) sections.Add($"Duplicate ids ({duplicates.Length}):\n" + string.Join('\n', duplicates.Select(duplicate => $"  {duplicate.Id}: {string.Join(", ", duplicate.Paths)}")));
        if (dangling.Length > 0) sections.Add($"Dangling links ({dangling.Length}):\n" + string.Join('\n', dangling.Select(link => $"  {link.From} ({link.FromPath}) --{link.Kind}--> {link.Target} [missing]")));
        if (cycles.Count > 0) sections.Add($"Parent cycles ({cycles.Count}):\n" + string.Join('\n', cycles.Select(cycle => $"  {string.Join(" -> ", cycle.Ids)} -> {cycle.Ids[0]}")));
        return Result(sections.Count == 0 ? "Spec-graph is valid: no issues found." : string.Join("\n\n", sections), report);
    }
}