using SharpRail.Plugins.AirContext;
using SharpRail.Plugins.AirContext.UI;
using SharpRail.Plugins.AirGateway;
using SharpRail.Plugins.AirGateway.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Blueprint;
using SharpRail.Plugins.Blueprint.UI;
using SharpRail.Plugins.BranchGraph;
using SharpRail.Plugins.BranchGraph.UI;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.UI;
using SharpRail.Plugins.Codex;
using SharpRail.Plugins.Codex.UI;
using SharpRail.Plugins.Discord;
using SharpRail.Plugins.Discord.UI;
using SharpRail.Plugins.Env;
using SharpRail.Plugins.Env.UI;
using SharpRail.Plugins.Epub;
using SharpRail.Plugins.Epub.UI;
using SharpRail.Plugins.GitHub;
using SharpRail.Plugins.GitHub.UI;
using SharpRail.Plugins.Omp;
using SharpRail.Plugins.Omp.UI;
using SharpRail.Plugins.SpecDialect;
using SharpRail.Plugins.Tailscale;
using SharpRail.Plugins.Tailscale.UI;
using SharpRail.Plugins.Visualize;
using SharpRail.Plugins.Visualize.UI;

namespace SharpRail.UI.Plugins;

/// <summary>The builtin plugins' UI halves, each with the manifest the host lists it under.</summary>
public static class BuiltinPlugins
{
    public static IReadOnlyList<(PluginManifest Manifest, Func<PluginUIModule> Load)> All =
    [
        (SpecDialectManifest.Manifest, () => new SpecDialectUI()),
        (BlueprintContract.Manifest, () => new BlueprintUI()),
        (ClaudeCodeManifest.Manifest, () => new ClaudeCodeUI()),
        (DiscordPlugin.Manifest, () => new DiscordUI()),
        (BranchGraphPlugin.Manifest, () => new BranchGraphUI()),
        (VisualizeContract.Manifest, () => new VisualizeUI()),
        (CodexManifest.Manifest, () => new CodexUI()),
        (OmpManifest.Manifest, () => new OmpUI()),
        (AirGatewayContract.Manifest, () => new AirGatewayUI()),
        (AirContextContract.Manifest, () => new AirContextUI()),
        (TailscaleContract.Manifest, () => new TailscaleUI()),
        (EpubContract.Manifest, () => new EpubUI()),
        (GitHubContract.Manifest, () => new GitHubUI()),
        (EnvContract.Manifest, () => new EnvUI())
    ];
}