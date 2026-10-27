using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    /// <summary>The sign-in state of the provider owning the remote, else of the first active provider (a project may not be on GitHub); disconnected when none is active.</summary>
    public async ValueTask<GitHubAuthentication> GetGitHubAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        var route = RouteFor(await OriginUrlAsync(root, cancellationToken)) ?? repoHosts?.Invoke("");
        if (route is null) return new(false, "No repo hosting plugin is active.");
        var status = await route.Provider.SignInAsync(cancellationToken);
        return new(status.Connected, status.Detail);
    }
}