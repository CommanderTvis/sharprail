using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.AirContext.UI;

/// <summary>The UI half: two sections of the Code Intelligence pages (account in Settings, workspace in the language servers pane); no side tool.</summary>
public sealed class AirContextUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        // The global account section sits in Settings, the workspace section in the language servers pane.
        context.SlotSection(new(UiSlots.CodeIntelligenceSettings, workspace => new AirContextPane(context, workspace ?? "", account: true)));
        context.SlotSection(new(UiSlots.WorkspaceLanguages, workspace => new AirContextPane(context, workspace ?? "", account: false)));
        return null;
    }
}