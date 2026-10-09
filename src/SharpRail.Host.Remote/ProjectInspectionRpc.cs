using ProtoBuf.Grpc;

using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed partial class ProjectRpc
{
    public ValueTask<InspectProjectReply> InspectProjectPathAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
        new InspectProjectReply { Kind = (int)await sessions.Detached().InspectProjectPathAsync(request.Path, context.CancellationToken) });

    public ValueTask<SaveFileReply> PrewarmWorkspaceAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        await sessions.Detached().PrewarmWorkspaceAsync(request.Path, context.CancellationToken);
        return new SaveFileReply();
    });
}