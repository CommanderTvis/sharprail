using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.BranchGraph.UI;

/// <summary>The UI half: the Git Graph side tool, which the manifest declares as needing Git.</summary>
public sealed class BranchGraphUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SideTool(new(BranchGraphPlugin.Tool, workspace => new GraphPanel(context, workspace)));
        return null;
    }
}