using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed partial class ProjectRpc
{
    public ValueTask<GitHubAuthenticationReply> GetGitHubAuthenticationAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var status = await Host(context).GetGitHubAuthenticationAsync(context.CancellationToken);
        return new GitHubAuthenticationReply { Connected = status.Connected, Detail = status.Detail };
    });
}