using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Review.UI;

/// <summary>The UI half: the Review tool window, one pane that follows the workspace it is shown for.</summary>
public sealed class ReviewUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SideTool(new(ReviewPlugin.Tool, workspace => new ReviewPanel(context, workspace))
        {
            Retarget = (control, workspace) => ((ReviewPanel)control).Retarget(workspace)
        });
        return null;
    }
}