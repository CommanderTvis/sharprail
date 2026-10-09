using ProtoBuf;
using ProtoBuf.Grpc;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class InspectProjectReply
{
    /// <summary>0 repository, 1 initable, 2 missing, 3 not a directory.</summary>
    [ProtoMember(1)] public int Kind { get; set; }
}

public partial interface IProjectRpc
{
    ValueTask<InspectProjectReply> InspectProjectPathAsync(ProjectRequest request, CallContext context = default);
    ValueTask<SaveFileReply> PrewarmWorkspaceAsync(ProjectRequest request, CallContext context = default);
}