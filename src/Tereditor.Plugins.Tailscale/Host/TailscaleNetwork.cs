using System.Net;
using System.Net.Sockets;

namespace SharpRail.Plugins.Tailscale.Host;

internal sealed record TailscaleLocal(IPAddress Address, string Name);
internal sealed record TailscaleIdentity(string Id, string Name, string User);

internal class TailscaleNetwork
{
    internal virtual async Task<TailscaleLocal> SelfAsync(CancellationToken cancellationToken)
    {
        using var status = await TailscaleCli.RunAsync("status", null, cancellationToken);
        if (status.RootElement.GetProperty("BackendState").GetString() != "Running") throw new IOException("Connect this host to Tailscale first.");
        var self = status.RootElement.GetProperty("Self");
        var address = self.GetProperty("TailscaleIPs").EnumerateArray().Select(value => IPAddress.Parse(value.GetString()!))
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && TailscaleCli.IsTailnetAddress(ip))
            ?? throw new IOException("Connect this host to Tailscale first.");
        var name = self.GetProperty("DNSName").GetString()?.TrimEnd('.');
        return new(address, string.IsNullOrEmpty(name) ? self.GetProperty("HostName").GetString() ?? address.ToString() : name);
    }

    internal virtual async Task<TailscaleIdentity?> PeerAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (!TailscaleCli.IsTailnetAddress(address)) return null;
        using var identity = await TailscaleCli.RunAsync("whois", address.ToString(), cancellationToken);
        if (!identity.RootElement.TryGetProperty("Node", out var node) || node.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        var id = node.TryGetProperty("StableID", out var stable) ? stable.ToString() : node.GetProperty("ID").ToString();
        if (id.Length == 0) return null;
        var name = node.GetProperty("Name").GetString()?.TrimEnd('.') ?? address.ToString();
        var user = identity.RootElement.TryGetProperty("UserProfile", out var profile) && profile.TryGetProperty("LoginName", out var login) ? login.GetString() ?? "" : "";
        return new(id, name, user);
    }
}