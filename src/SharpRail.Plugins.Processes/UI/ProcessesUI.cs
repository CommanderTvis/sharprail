using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Processes.UI;

/// <summary>The UI half: the Processes side tool. Its panel polls the host only while it is on screen.</summary>
public sealed class ProcessesUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SideTool(new(ProcessesPlugin.Tool, _ => new ProcessesPanel(context)));
        return null;
    }
}