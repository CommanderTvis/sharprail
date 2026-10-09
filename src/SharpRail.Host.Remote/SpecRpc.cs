using ProtoBuf.Grpc;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed partial class ProjectRpc
{
    public ValueTask<SpecGraphReply> GetSpecGraphAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var graph = await Host(context).GetSpecGraphAsync(context.CancellationToken);
        return new SpecGraphReply
        {
            Specs = graph.Specs.Select(Map).ToList(),
            Edges = graph.Edges.Select(Map).ToList(),
            DanglingLinks = graph.DanglingLinks.Select(Map).ToList(),
            DuplicateIds = graph.DuplicateIds.Select(duplicate => new SpecDuplicateReply { Id = duplicate.Id, Paths = duplicate.Paths.ToList() }).ToList(),
            ParentCycles = graph.ParentCycles.Select(cycle => new SpecCycleReply { Ids = cycle.ToList() }).ToList()
        };
    });

    public ValueTask<HasSpecsReply> HasDurableSpecsAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
        new HasSpecsReply { HasSpecs = await Host(context).HasDurableSpecsAsync(context.CancellationToken) });

    private static SpecReply Map(SpecDocument spec) =>
        new() { Id = spec.Id, Title = spec.Title, Path = spec.Path, Parent = spec.Parent, Type = spec.Type, Status = spec.Status };

    private static SpecEdgeReply Map(SpecEdge edge) => new() { From = edge.From, To = edge.To, Kind = edge.Kind };
}