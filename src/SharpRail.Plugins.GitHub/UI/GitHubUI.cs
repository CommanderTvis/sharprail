using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.GitHub.UI;

/// <summary>The UI half: the Pull request item of the location strip and the Review panel section (W28), and the Settings page (W4).</summary>
public sealed class GitHubUI : PluginUIModule
{
    public const string Provider = GitHubContract.Id;

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.RepoHostUI(new("Pull request", surface => new GitHubLocationItem(surface), surface => new GitHubReviewSection(surface)));
        context.SettingsSection(new("GitHub", "gitBranch", () => new GitHubSettingsPage(context)));
        return null;
    }
}