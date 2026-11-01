using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Processes.UI;

/// <summary>The UI half: the Processes side tool. Its panel polls the host only while it is on screen.</summary>
public sealed class ProcessesUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var view = new ProcessesView();
        context.SideTool(new(ProcessesPlugin.Tool, workspace => new ProcessesPanel(context, workspace, view)));
        return null;
    }
}

/// <summary>The pane's view choice, shared by every Processes pane of this window set for the session: false shows only the pane's own project.</summary>
internal sealed class ProcessesView
{
    internal bool AllProjects { get; set; }
}