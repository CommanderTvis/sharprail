namespace SharpRail.UI.Panels;

public sealed record GitHubStatus(bool Connected, string Detail);

public static class GitHubProbe
{
    public static async Task<GitHubStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var status = await Host.Core.GitHubAuthenticationProbe.CheckAsync(cancellationToken);
        return new(status.Connected, status.Detail);
    }
}