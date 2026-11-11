using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.GitHub.UI;

/// <summary>The UI half: the Review panel section (W28), and the Settings page (W4).</summary>
public sealed class GitHubUI : PluginUIModule
{
    public const string Provider = GitHubContract.Id;

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.RepoHostUI(new(surface => new GitHubReviewSection(surface)));
        context.SettingsSection(new("GitHub", "asset:github.svg", () => new GitHubSettingsPage(context)));
        context.KeymapAction(new("OpenSettings", "Open GitHub settings", "GitHub", () => context.OpenSettings(UiPages.PluginSection(context.Id))));
        return null;
    }
}