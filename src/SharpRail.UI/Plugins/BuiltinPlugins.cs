using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.SpecDialect;
using SharpRail.Plugins.Blueprint;
using SharpRail.Plugins.Blueprint.UI;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.UI;
using SharpRail.Plugins.Discord;
using SharpRail.Plugins.Discord.UI;
using SharpRail.Plugins.PdfPreview;
using SharpRail.Plugins.PdfPreview.UI;
using SharpRail.Plugins.BranchGraph;
using SharpRail.Plugins.BranchGraph.UI;
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
        (PdfPreviewPlugin.Manifest, () => new PdfPreviewUI()),
        (BranchGraphPlugin.Manifest, () => new BranchGraphUI()),
        (VisualizeContract.Manifest, () => new VisualizeUI()),
    ];
}
