using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.GitHub.Host;

/// <summary>Provides the GitHub repo hosting provider (H24): every <c>gh</c> call of the product runs here.</summary>
public sealed class GitHubHost : PluginHostModule
{
    public override PluginContract Contract => GitHubContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.RepoHost(new GitHubProvider(context.RunToolAsync));
        return ValueTask.FromResult<PluginDisposer?>(null);
    }
}