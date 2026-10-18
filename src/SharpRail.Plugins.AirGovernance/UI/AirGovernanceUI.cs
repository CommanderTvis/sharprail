using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.AirGovernance.UI;

/// <summary>The UI half: Settings › Air Governance, and the `central run` contribution to Claude Code and Codex launches.</summary>
public sealed class AirGovernanceUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Air Governance", "asset:air-governance.svg", () => new AirSettingsView(context)));
        // Contributions are read synchronously on the UI thread, so the path Central was found at comes from the host's
        // status channel rather than the file system; until it arrives the bare name is used and the shell resolves it.
        string? centralPath = null;
        context.AgentLaunch(launcherId => AirLaunch.Contribution(context.Settings<AirSettings>(), launcherId, Volatile.Read(ref centralPath)));
        var subscription = context.Subscribe(AirGovernanceContract.Changed, status => Volatile.Write(ref centralPath, status.Central.Path), new AirStatusParams());
        return subscription.Dispose;
    }
}
