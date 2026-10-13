using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Tailscale.UI;

public sealed class TailscaleUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Tailscale", "asset:tailscale.svg", () => new TailscaleSettingsView(context)));
        var notified = new HashSet<string>(StringComparer.Ordinal);
        var subscription = context.Subscribe(TailscaleContract.Changed, status =>
        {
            notified.IntersectWith(status.Pending.Select(device => device.Id));
            foreach (var device in status.Pending)
                if (notified.Add(device.Id)) context.Notify(PluginNotificationKind.Info, "Tailscale pairing approval requested",
                    device.Name + " · " + device.User + ". Compare code " + device.Code + ", then open Settings › Tailscale to approve or decline.");
        }, new TailscaleStatusParams());
        return subscription.Dispose;
    }
}