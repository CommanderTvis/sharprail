using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Tailscale.UI;

public sealed class TailscaleUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Tailscale", "asset:tailscale.svg", () => new TailscaleSettingsView(context)));
        return null;
    }
}