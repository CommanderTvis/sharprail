using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.SpecDialect;


namespace SharpRail.Host.Core.Plugins;

/// <summary>The plugins that ship with the host, each a manifest and its host half (null for a manifest-only plugin).</summary>
public static class BuiltinPlugins
{
    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> All =>
    [
        (SpecDialectManifest.Manifest, new SpecDialectHost()),
    ];
}
