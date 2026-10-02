using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.SpecDialect;
using SharpRail.Plugins.Blueprint;
using SharpRail.Plugins.Blueprint.Host;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.Host;
using SharpRail.Plugins.Discord;
using SharpRail.Plugins.Discord.Host;


namespace SharpRail.Host.Core.Plugins;

/// <summary>The plugins that ship with the host, each a manifest and its host half (null for a manifest-only plugin).</summary>
public static class BuiltinPlugins
{
    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> All =>
    [
        (SpecDialectManifest.Manifest, new SpecDialectHost()),
        (BlueprintContract.Manifest, new BlueprintHost()),
        (ClaudeCodeManifest.Manifest, new ClaudeCodeHost()),
        (DiscordPlugin.Manifest, new DiscordHost()),
    ];
}
