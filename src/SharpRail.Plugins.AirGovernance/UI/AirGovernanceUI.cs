using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.AirGovernance.UI;

/// <summary>The UI half: Settings › Air Governance, the `central run` contribution to Claude Code and Codex launches, Central's Pi wire for omp, and the remaining credits in the header while Central governs an agent.</summary>
public sealed class AirGovernanceUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SettingsSection(new("Air Governance", "asset:air-governance.svg", () => new AirSettingsView(context)));
        // Contributions are read synchronously on the UI thread, so the path Central was found at comes from the host's
        // status channel rather than the file system; until it arrives the bare name is used and the shell resolves it.
        // Central's Pi wire is likewise known only from the channel; until then a governed omp launch fails closed.
        string? centralPath = null, piWire = null;
        context.AgentLaunch(launcherId => AirLaunch.Contribution(context.Settings<AirSettings>(), launcherId, Volatile.Read(ref centralPath), Volatile.Read(ref piWire), context.Host().HostPlatform == HostPlatform.Windows));
        var feed = new AirCreditsFeed(context);
        context.HeaderItem(new(() => new AirCreditsHeader(context, feed)));
        var subscription = context.Subscribe(AirGovernanceContract.Changed, status => { Volatile.Write(ref centralPath, status.Central.Path); Volatile.Write(ref piWire, status.PiWire); feed.Apply(status); }, new AirStatusParams());
        return () => { subscription.Dispose(); feed.Dispose(); };
    }
}