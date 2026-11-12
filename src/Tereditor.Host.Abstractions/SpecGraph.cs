namespace SharpRail.Host.Abstractions;

public static class SpecLinks
{
    public const string Parent = "parent";
    public const string DependsOn = "depends-on";
    public const string References = "references";
    public const string Implements = "implements";
    /// <summary>Every link kind, in the order edges are emitted for one spec.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [Parent, DependsOn, References, Implements];
    /// <summary>An ephemeral scratch spec, which never counts as a project having specs.</summary>
    public const string TaskType = "task-spec";
}

/// <summary>One link as written in <paramref name="From"/>'s frontmatter; <paramref name="To"/> may name no spec.</summary>
public record SpecEdge(string From, string To, string Kind);

/// <summary>An id claimed by several files, first in traversal order first; that first file is the spec.</summary>
public record SpecDuplicate(string Id, IReadOnlyList<string> Paths);

/// <summary>
/// The whole spec graph of one workspace. Edges run from the spec that declares a link; reverse edges are the
/// same list read by target. The three validation lists are empty for a well-formed graph.
/// </summary>
public record SpecGraph(IReadOnlyList<SpecDocument> Specs, IReadOnlyList<SpecEdge> Edges)
{
    public IReadOnlyList<SpecEdge> DanglingLinks { get; init; } = [];
    public IReadOnlyList<SpecDuplicate> DuplicateIds { get; init; } = [];
    /// <summary>Each ring of specs that reach themselves through <c>parent</c>, in walk order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ParentCycles { get; init; } = [];

    public bool IsValid => DanglingLinks.Count == 0 && DuplicateIds.Count == 0 && ParentCycles.Count == 0;

    public IEnumerable<string> Targets(string id, string kind) => Edges.Where(edge => edge.From == id && edge.Kind == kind).Select(edge => edge.To);

    public IEnumerable<string> Sources(string id, string kind) => Edges.Where(edge => edge.To == id && edge.Kind == kind).Select(edge => edge.From);
}

public partial interface IProjectServices
{
    /// <summary>The workspace's specs with every link and the graph's validation.</summary>
    ValueTask<SpecGraph> GetSpecGraphAsync(CancellationToken cancellationToken = default);
    /// <summary>Whether the workspace holds any spec that is not an ephemeral task spec; false when it cannot be read.</summary>
    ValueTask<bool> HasDurableSpecsAsync(CancellationToken cancellationToken = default);
}