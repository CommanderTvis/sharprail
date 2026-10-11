using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SharpRail.Plugins.Tailscale;

public static class TailscaleCli
{
    public static bool IsTailnetAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
            address.AddressFamily == AddressFamily.InterNetworkV6 && bytes.AsSpan(0, 6).SequenceEqual(new byte[] { 0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0 });
    }

    public static async Task<JsonDocument> RunAsync(string command, string? argument, CancellationToken cancellationToken)
    {
        var executable = OperatingSystem.IsMacOS() && File.Exists("/Applications/Tailscale.app/Contents/MacOS/Tailscale")
            ? "/Applications/Tailscale.app/Contents/MacOS/Tailscale" : "tailscale";
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add("--json");
        if (argument is not null) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Tailscale could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var json = await output;
            await error;
            if (process.ExitCode != 0) throw new IOException("Tailscale is unavailable. Check that it is installed and connected.");
            return JsonDocument.Parse(json);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    internal static IReadOnlyList<TailscalePeer> Peers(JsonElement status)
    {
        if (!status.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object) return [];
        return peers.EnumerateObject().Select(peer => peer.Value)
            .Where(peer => peer.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True)
            .Select(peer => new TailscalePeer(peer.GetProperty("DNSName").GetString()?.TrimEnd('.') ?? peer.GetProperty("HostName").GetString() ?? "",
                peer.GetProperty("TailscaleIPs").EnumerateArray().Select(ip => ip.GetString()!).FirstOrDefault(ip => IPAddress.TryParse(ip, out var address) && address.AddressFamily == AddressFamily.InterNetwork && IsTailnetAddress(address)) ?? ""))
            .Where(peer => peer.Name.Length > 0 && peer.Address.Length > 0).OrderBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase).Take(256).ToArray();
    }

    public static async Task<IReadOnlyList<TailscalePeer>> PeersAsync(CancellationToken cancellationToken)
    {
        using var status = await RunAsync("status", null, cancellationToken);
        return Peers(status.RootElement);
    }
}