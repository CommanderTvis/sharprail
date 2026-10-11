namespace SharpRail.Plugins.Tailscale;

/// <summary>A credential-free QR link that starts the normal host-approved pairing flow.</summary>
public static class TailscaleLink
{
    private const string Prefix = "sharprail://tailscale?host=";

    public static string Format(string host) => Prefix + Uri.EscapeDataString(host);

    public static bool TryParse(string? text, out string host)
    {
        host = "";
        if (text is null || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var encoded = text[Prefix.Length..];
        if (encoded.Length > 1024 || encoded.Contains('&') || encoded.Contains('#')) return false;
        try { host = Uri.UnescapeDataString(encoded); }
        catch (UriFormatException) { return false; }
        if (host.Length <= 253 && Uri.CheckHostName(host) == UriHostNameType.Dns) return true;
        host = "";
        return false;
    }
}