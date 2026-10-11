using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Tailscale;

public sealed record TailscaleSettings;
public sealed record TailscaleStatusParams;
public sealed record TailscaleDecision(string Id, bool Allow);
public sealed record TailscaleRevoke(string Id);
public sealed record TailscaleDevice(string Id, string Name, string User, string Code);
public sealed record TailscaleStatus(string Detail, string? HostName, IReadOnlyList<TailscaleDevice> Pending, IReadOnlyList<TailscaleDevice> Approved);
public sealed record TailscaleHostInfo(string Name);
public sealed record TailscalePairReply(string State, string? Address = null);
public sealed record TailscaleConnection(string Address, string Token);
public sealed record TailscaleRemembered(string Host, string Token);
public sealed record TailscalePeer(string Name, string Address);

public static class TailscaleContract
{
    public const string Id = "tailscale";
    public const int DiscoveryPort = 54124;
    public static readonly PluginMethod<TailscaleStatusParams, TailscaleStatus> Status = new("status");
    public static readonly PluginMethod<TailscaleDecision, TailscaleStatus> Decide = new("decide");
    public static readonly PluginMethod<TailscaleRevoke, TailscaleStatus> Revoke = new("revoke");
    public static readonly PluginChannel<TailscaleStatus> Changed = PluginChannel<TailscaleStatus>.State("status", Status);
    public static readonly PluginContract Contract = PluginContract.Create<TailscaleSettings>(Id, 1, [Status, Decide, Revoke], [Changed]);
    public static readonly PluginManifest Manifest = new(Id, "Tailscale", "global-line", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Pairs clients on your Tailscale network without copying IP addresses, ports or session tokens.",
        EnabledByDefault = false,
        Host = "SharpRail.Plugins.Tailscale.Host.dll",
        Ui = "SharpRail.Plugins.Tailscale.UI.dll"
    };
}