namespace SharpRail.Plugins.SpecDialect;

/// <summary>
/// The UI half's own state: the last graph read per workspace and whether the latest read failed. It lives outside
/// the Specs panel because the document-link slot needs the graph whether or not that tab was ever opened.
/// </summary>
public sealed class SpecStore
{
    private readonly Dictionary<string, IReadOnlyList<SpecGraphNode>> specs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> failed = new(StringComparer.Ordinal);

    /// <summary>Raised with the workspace whose graph or failure flag changed; an unchanged re-read raises nothing.</summary>
    public event Action<string>? Changed;

    public IReadOnlyList<SpecGraphNode>? Specs(string workspaceId) => specs.GetValueOrDefault(workspaceId);

    public bool Failed(string workspaceId) => failed.GetValueOrDefault(workspaceId);

    /// <summary>Keeps the previous list when a re-read found the same graph, so readers rebuild only on a real change.</summary>
    public void SetSpecs(string workspaceId, IReadOnlyList<SpecGraphNode> nodes)
    {
        if (SameGraph(specs.GetValueOrDefault(workspaceId), nodes)) return;
        specs[workspaceId] = nodes;
        Changed?.Invoke(workspaceId);
    }

    public void SetFailed(string workspaceId, bool value)
    {
        if (failed.GetValueOrDefault(workspaceId) == value) return;
        failed[workspaceId] = value;
        Changed?.Invoke(workspaceId);
    }

    /// <summary>Drops one workspace's graph and failure flag, leaving its siblings alone.</summary>
    public void Evict(string workspaceId)
    {
        if (specs.Remove(workspaceId) | failed.Remove(workspaceId)) Changed?.Invoke(workspaceId);
    }

    public static bool SameGraph(IReadOnlyList<SpecGraphNode>? previous, IReadOnlyList<SpecGraphNode> next) =>
        previous is not null && previous.Count == next.Count && previous.Zip(next).All(pair => SameNode(pair.First, pair.Second));

    private static bool SameNode(SpecGraphNode a, SpecGraphNode b) =>
        a.Id == b.Id && a.Type == b.Type && a.Title == b.Title && a.Status == b.Status && a.Path == b.Path && a.Parent == b.Parent &&
        a.DependsOn.SequenceEqual(b.DependsOn) && a.References.SequenceEqual(b.References) &&
        a.Implements.SequenceEqual(b.Implements) && a.Tags.SequenceEqual(b.Tags);
}