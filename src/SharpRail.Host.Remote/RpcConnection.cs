using System.Net;
using System.Net.Sockets;

namespace SharpRail.Host.Remote;

/// <summary>How the RPC call running on this async flow reached the host; unset for direct (embedded) calls.</summary>
public static class RpcConnection
{
    private static readonly AsyncLocal<bool> throughTailscale = new();

    /// <summary>Whether the current call arrived over Tailscale: authenticated by a Tailscale pairing, or from a tailnet address.</summary>
    public static bool ThroughTailscale { get => throughTailscale.Value; internal set => throughTailscale.Value = value; }

    /// <summary>Tailscale's address space: 100.64.0.0/10 and fd7a:115c:a1e0::/48.</summary>
    public static bool IsTailnetAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] == 100 && (bytes[1] & 0xC0) == 64,
            AddressFamily.InterNetworkV6 => bytes[0] == 0xfd && bytes[1] == 0x7a && bytes[2] == 0x11 && bytes[3] == 0x5c && bytes[4] == 0xa1 && bytes[5] == 0xe0,
            _ => false
        };
    }
}
