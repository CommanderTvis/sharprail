using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed partial class ProjectRpc
{
    public ValueTask<ContextConsentReply> ContextConsentAsync(ContextConsentRequestDto request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).ContextConsentAsync(new(request.Operation, request.ArtifactId, request.TermsSha256), context.CancellationToken);
        return new ContextConsentReply { State = result.State, Detail = result.Detail, Version = result.Version, ArtifactId = result.ArtifactId, TermsSha256 = result.TermsSha256, Text = result.Text, AcceptedAtUtc = result.AcceptedAtUtc, Installation = result.Installation };
    });
}

public sealed partial class ProjectRpc
{
    public ValueTask<ContextReply> ContextAsync(ContextRequestDto request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).ContextAsync(new(request.Workspace, request.Operation, request.Query, request.Path, request.Limit), context.CancellationToken);
        return new ContextReply { State = result.State, Detail = result.Detail, Enabled = result.Enabled, Data = result.Data, Command = result.Command };
    });
}