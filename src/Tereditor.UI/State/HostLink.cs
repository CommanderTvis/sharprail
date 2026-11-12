using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SharpRail.UI.State;

/// <summary>
/// What a serving app shows as a QR code and a client scans: the address to reach the host at and its session
/// token, as <c>sharprail://connect?host=…&amp;token=…</c>.
/// </summary>
public static class HostLink
{
    private const string Prefix = "sharprail://connect?";

    public static string Format(string address, string token) =>
        $"{Prefix}host={Uri.EscapeDataString(address)}&token={Uri.EscapeDataString(token)}";

    public static bool TryParse(string? text, out string address, out string token)
    {
        address = token = "";
        if (text is null || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var pair in text[Prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split < 0) continue;
            var value = Uri.UnescapeDataString(pair[(split + 1)..]);
            if (pair.AsSpan(0, split) is "host") address = value;
            else if (pair.AsSpan(0, split) is "token") token = value;
        }
        return address.Length > 0 && token.Length > 0;
    }

    /// <summary>
    /// The address another device reaches a listener at: a listener on every interface is named by this
    /// computer's first network address, since the wildcard it listens on is not one a client can dial.
    /// </summary>
    public static string Reachable(Uri endpoint, Func<IEnumerable<IPAddress>>? addresses = null)
    {
        var wildcard = IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var bound) && (bound.Equals(IPAddress.Any) || bound.Equals(IPAddress.IPv6Any));
        if (!wildcard) return endpoint.Authority;
        var local = (addresses ?? NetworkAddresses)().FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        return local is null ? endpoint.Authority : $"{local}:{endpoint.Port}";
    }

    private static IEnumerable<IPAddress> NetworkAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address));
}