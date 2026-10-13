using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed partial class LocalProjectAdapter
{
    public ValueTask<GitHubAuthentication> GetGitHubAuthenticationAsync(CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.GetGitHubAuthenticationAsync(cancellationToken));
}

public sealed partial class RemoteProjectAdapter
{
    public async ValueTask<GitHubAuthentication> GetGitHubAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await service.GetGitHubAuthenticationAsync(new(), Context(cancellationToken));
            return new(status.Connected, status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented)
        {
            return new(false, "This host does not report GitHub authentication.");
        }
    }
}