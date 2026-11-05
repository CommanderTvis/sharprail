using SharpRail.Plugins.Acp;
using SharpRail.Plugins.Acp.UI;
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
using SharpRail.Plugins.Pi;
using SharpRail.Plugins.Pi.UI;
using SharpRail.Plugins.Processes;
using SharpRail.Plugins.Processes.UI;
using SharpRail.Plugins.Providers;
using SharpRail.Plugins.Providers.UI;
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
        SpecDialectContract.Contract, BlueprintContract.Contract, ClaudeCodeContract.Contract, DiscordContract.Contract, BranchGraphContract.Contract,
        ProcessesContract.Contract, VisualizeContract.Contract, CodexContract.Contract, OmpContract.Contract, PiContract.Contract, AcpContract.Contract,
        ProvidersContract.Contract, AirGatewayContract.Contract, AirContextContract.Contract, TailscaleContract.Contract, EpubContract.Contract,
        GitHubContract.Contract, EnvContract.Contract
    ];
}