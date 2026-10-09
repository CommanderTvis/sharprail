using ProtoBuf;
using ProtoBuf.Grpc;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class SpecEdgeReply
{
    [ProtoMember(1)] public string From { get; set; } = "";
    [ProtoMember(2)] public string To { get; set; } = "";
    [ProtoMember(3)] public string Kind { get; set; } = "";
}

[ProtoContract]
public sealed class SpecDuplicateReply
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public List<string> Paths { get; set; } = [];
}

[ProtoContract]
public sealed class SpecCycleReply
{
    [ProtoMember(1)] public List<string> Ids { get; set; } = [];
}

[ProtoContract]
public sealed class SpecGraphReply
{
    [ProtoMember(1)] public List<SpecReply> Specs { get; set; } = [];
    [ProtoMember(2)] public List<SpecEdgeReply> Edges { get; set; } = [];
    [ProtoMember(3)] public List<SpecEdgeReply> DanglingLinks { get; set; } = [];
    [ProtoMember(4)] public List<SpecDuplicateReply> DuplicateIds { get; set; } = [];
    [ProtoMember(5)] public List<SpecCycleReply> ParentCycles { get; set; } = [];
}

[ProtoContract]
public sealed class HasSpecsReply
{
    [ProtoMember(1)] public bool HasSpecs { get; set; }
}

public partial interface IProjectRpc
{
    ValueTask<SpecGraphReply> GetSpecGraphAsync(ProjectRequest request, CallContext context = default);
    ValueTask<HasSpecsReply> HasDurableSpecsAsync(ProjectRequest request, CallContext context = default);
}