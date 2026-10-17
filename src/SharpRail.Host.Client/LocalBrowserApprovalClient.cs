using System.Net;
using System.Net.Http.Json;

using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

/// <summary>Bootstraps browser access through consent in a local embedded app.</summary>
public static class LocalBrowserApprovalClient
{
    /// <summary>Accepts literal loopback HTTP(S) addresses without resolving arbitrary hostnames.</summary>
    public static bool IsLoopback(Uri? address) => address is { Scheme: "http" or "https" } &&
        (address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>Requests a temporary credential using the browser's Origin header; HTTP rejection preserves its status code.</summary>
    public static async Task<string> RequestAsync(Uri address, CancellationToken cancellationToken = default)
    {
        if (!IsLoopback(address)) throw new ArgumentException("App approval requires a loopback host.", nameof(address));
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.PostAsync(new Uri(address, "/browser/connect"), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<LocalBrowserApprovalReply>(cancellationToken);
        if (string.IsNullOrEmpty(reply?.Token)) throw new IOException("The host did not return a browser credential.");
        return reply.Token;
    }
}