using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.GitHub.UI;

/// <summary>The UI half; the pull request item, Review section and Settings page move here from the workbench.</summary>
public sealed class GitHubUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context) => null;
}