using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Tailscale.UI;

public sealed class TailscaleUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Tailscale", "asset:tailscale.svg", () => new TailscaleSettingsView(context)));
        // One prompt per pending request; it comes down when the host no longer lists the request as pending.
        var prompts = new Dictionary<string, IDisposable>(StringComparer.Ordinal);
        var subscription = context.Subscribe(TailscaleContract.Changed, status =>
        {
            foreach (var settled in prompts.Keys.Except(status.Pending.Select(device => device.Id)).ToArray())
            {
                prompts[settled].Dispose();
                prompts.Remove(settled);
            }
            foreach (var device in status.Pending)
                if (!prompts.ContainsKey(device.Id)) prompts[device.Id] = context.Prompt(Prompt(context, device));
        }, new TailscaleStatusParams());
        return () =>
        {
            subscription.Dispose();
            foreach (var prompt in prompts.Values) prompt.Dispose();
            prompts.Clear();
        };
    }

    internal static PluginPrompt Prompt(IPluginUIContext context, TailscaleDevice device) => new(PluginNotificationKind.Info,
        $"Tailscale pairing request from {device.Name} ({device.User}). Compare this code with the requesting device:",
        [
            new("Approve", async () => await TailscaleSettingsView.DecideAsync(context, device.Id, true), PluginNotificationActionStyle.Primary),
            new("Decline", async () => await TailscaleSettingsView.DecideAsync(context, device.Id, false), PluginNotificationActionStyle.Plain),
            new("Open Tailscale settings", () => { context.OpenSettings(UiPages.PluginSection(context.Id)); return ValueTask.CompletedTask; }, Closes: false),
        ], Code: device.Code);
}