using System.Text.Json;

using Tereditor.Host.Client;

namespace Tereditor.UI.Browser;

/// <summary>
/// The host this client connects to: what the connect screen asks for and the app remembers. A tailnet endpoint is
/// reached only through the browser's own Tailscale node.
/// </summary>
public sealed record HostEndpoint(string Address, string Token, bool LocalApproval = false, bool Tailnet = false, bool Tab = false)
{
    /// <summary>The host's browser endpoint.</summary>
    public Uri? Uri => Tab ? null : Parse(Address);

    /// <summary>The host that runs inside this tab, which needs no address or token.</summary>
    public static HostEndpoint InTab { get; } = new("this tab", "", Tab: true);

    /// <summary>Only literal loopback addresses and localhost can request app approval.</summary>
    public static bool IsLocal(Uri? uri) => LocalBrowserApprovalClient.IsLoopback(uri);

    public static Uri? Parse(string address)
    {
        var text = address.Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!System.Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        return uri;
    }

    public static HostEndpoint? Load(string directory)
    {
        try { return BrowserInterop.Load("endpoint") is { } json ? JsonSerializer.Deserialize(json, BrowserJson.Default.HostEndpoint) : null; }
        catch (JsonException) { return null; }
    }
    public void Save(string directory) => BrowserInterop.Save("endpoint", JsonSerializer.Serialize(this, BrowserJson.Default.HostEndpoint));
}