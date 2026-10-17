using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Blueprint;
using SharpRail.Plugins.Blueprint.Host;
using SharpRail.Plugins.BranchGraph;
using SharpRail.Plugins.BranchGraph.Host;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.Host;
using SharpRail.Plugins.Codex;
using SharpRail.Plugins.Codex.Host;
using SharpRail.Plugins.Discord;
using SharpRail.Plugins.Discord.Host;
using SharpRail.Plugins.SpecDialect;
using SharpRail.Plugins.Tailscale;
using SharpRail.Plugins.Tailscale.Host;
using SharpRail.Plugins.Visualize;
using SharpRail.Plugins.Visualize.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>The plugins that ship with the host, each a manifest and its host half (null for a manifest-only plugin).</summary>
public static class BuiltinPlugins
{
    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> All => Create(null);

    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> Create(TailscaleServing? tailscale) =>
    [
        (SpecDialectManifest.Manifest, new SpecDialectHost()),
        (BlueprintContract.Manifest, new BlueprintHost()),
        (ClaudeCodeManifest.Manifest, new ClaudeCodeHost()),
        (DiscordPlugin.Manifest, new DiscordHost()),
        (BranchGraphPlugin.Manifest, new BranchGraphHost()),
        (VisualizeContract.Manifest, new VisualizeHost()),
        (CodexManifest.Manifest, new CodexHost()),
        (TailscaleContract.Manifest, new TailscaleHost(tailscale))
    ];
}