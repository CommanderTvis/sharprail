using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.AirContext.UI;

/// <summary>The UI half: the Air Context side tool, the only place consent, enablement and sign-in are offered.</summary>
public sealed class AirContextUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SideTool(new(AirContextContract.Tool, workspace => new AirContextPane(context, workspace))
        {
            Retarget = (control, workspace) => ((AirContextPane)control).Retarget(workspace)
        });
        return null;
    }
}