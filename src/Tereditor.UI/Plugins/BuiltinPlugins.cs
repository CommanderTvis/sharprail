using Tereditor.Plugins.Acp;
using Tereditor.Plugins.Acp.UI;
using Tereditor.Plugins.AirContext;
using Tereditor.Plugins.AirContext.UI;
using Tereditor.Plugins.AirGateway;
using Tereditor.Plugins.AirGateway.UI;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Blueprint;
using Tereditor.Plugins.Blueprint.UI;
using Tereditor.Plugins.BranchGraph;
using Tereditor.Plugins.BranchGraph.UI;
using Tereditor.Plugins.ClaudeCode;
using Tereditor.Plugins.ClaudeCode.UI;
using Tereditor.Plugins.Codex;
using Tereditor.Plugins.Codex.UI;
using Tereditor.Plugins.Discord;
using Tereditor.Plugins.Discord.UI;
using Tereditor.Plugins.Env;
using Tereditor.Plugins.Env.UI;
using Tereditor.Plugins.Epub;
using Tereditor.Plugins.Epub.UI;
using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.UI;
using Tereditor.Plugins.Omp;
using Tereditor.Plugins.Omp.UI;
using Tereditor.Plugins.Pi;
using Tereditor.Plugins.Pi.UI;
using Tereditor.Plugins.Processes;
using Tereditor.Plugins.Processes.UI;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.Providers.UI;
using Tereditor.Plugins.Review;
using Tereditor.Plugins.Review.UI;
using Tereditor.Plugins.SpecDialect;
using Tereditor.Plugins.Tailscale;
using Tereditor.Plugins.Tailscale.UI;
using Tereditor.Plugins.Visualize;
using Tereditor.Plugins.Visualize.UI;

namespace Tereditor.UI.Plugins;

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
        (ReviewPlugin.Manifest, () => new ReviewUI()),
        (ProcessesPlugin.Manifest, () => new ProcessesUI()),
        (VisualizeContract.Manifest, () => new VisualizeUI()),
        (CodexManifest.Manifest, () => new CodexUI()),
        (OmpManifest.Manifest, () => new OmpUI()),
        (PiManifest.Manifest, () => new PiUI()),
        (AcpManifest.Manifest, () => new AcpUI()),
        (ProvidersManifest.Manifest, () => new ProvidersUI()),
        (AirGatewayContract.Manifest, () => new AirGatewayUI()),
        (AirContextContract.Manifest, () => new AirContextUI()),
        (TailscaleContract.Manifest, () => new TailscaleUI()),
        (EpubContract.Manifest, () => new EpubUI()),
        (GitHubContract.Manifest, () => new GitHubUI()),
        (EnvContract.Manifest, () => new EnvUI())
    ];

    /// <summary>
    /// The contracts of the plugins above, whose JSON contexts the loader registers before any UI half runs: a UI
    /// half may read its settings before it touches a descriptor, and an app connected only to a remote host
    /// creates no host half that would register them.
    /// </summary>
    public static IReadOnlyList<PluginContract> Contracts =>
    [
        SpecDialectContract.Contract, BlueprintContract.Contract, ClaudeCodeContract.Contract, DiscordContract.Contract, BranchGraphContract.Contract, ReviewContract.Contract,
        ProcessesContract.Contract, VisualizeContract.Contract, CodexContract.Contract, OmpContract.Contract, PiContract.Contract, AcpContract.Contract,
        ProvidersContract.Contract, AirGatewayContract.Contract, AirContextContract.Contract, TailscaleContract.Contract, EpubContract.Contract,
        GitHubContract.Contract, EnvContract.Contract
    ];
}