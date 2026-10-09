using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed partial class LocalProjectAdapter
{
    public ValueTask<SpecGraph> GetSpecGraphAsync(CancellationToken cancellationToken = default) => host.GetSpecGraphAsync(cancellationToken);
    public ValueTask<bool> HasDurableSpecsAsync(CancellationToken cancellationToken = default) => host.HasDurableSpecsAsync(cancellationToken);
}

public sealed partial class RemoteProjectAdapter
{
    public async ValueTask<SpecGraph> GetSpecGraphAsync(CancellationToken cancellationToken = default)
    {
        var reply = await service.GetSpecGraphAsync(new(), Context(cancellationToken));
        return new(reply.Specs.Select(Map).ToArray(), reply.Edges.Select(Map).ToArray())
        {
            DanglingLinks = reply.DanglingLinks.Select(Map).ToArray(),
            DuplicateIds = reply.DuplicateIds.Select(duplicate => new SpecDuplicate(duplicate.Id, duplicate.Paths.ToArray())).ToArray(),
            ParentCycles = reply.ParentCycles.Select(cycle => (IReadOnlyList<string>)cycle.Ids.ToArray()).ToArray()
        };
    }

    public async ValueTask<bool> HasDurableSpecsAsync(CancellationToken cancellationToken = default)
        => (await service.HasDurableSpecsAsync(new(), Context(cancellationToken))).HasSpecs;

    private static SpecDocument Map(SpecReply spec) => new(spec.Id, spec.Title, spec.Path, spec.Parent, spec.Type) { Status = spec.Status };

    private static SpecEdge Map(SpecEdgeReply edge) => new(edge.From, edge.To, edge.Kind);
}