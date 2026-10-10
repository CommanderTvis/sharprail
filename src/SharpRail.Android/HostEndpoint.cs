using System.Text.Json;

namespace SharpRail.UI.Android;

/// <summary>The host this client connects to: what the connect screen asks for and the app remembers.</summary>
public sealed record HostEndpoint(string Address, string Token)
{
    public const int DefaultPort = 54123;

    /// <summary>The host's gRPC address; an address without a port uses the host's default.</summary>
    public Uri? Uri => Parse(Address);

    public static Uri? Parse(string address)
    {
        var text = address.Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!System.Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        // Uri reports the scheme's port when none was written, and hides a written one that equals it.
        var authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        var written = authority.LastIndexOf(':') > authority.LastIndexOf(']');
        return new UriBuilder(uri.Scheme, uri.Host, written ? uri.Port : DefaultPort).Uri;
    }

    /// <summary>The remembered host of a profile directory, or null when none was saved.</summary>
    public static HostEndpoint? Load(string directory)
    {
        try
        {
            var path = File(directory);
            return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<HostEndpoint>(System.IO.File.ReadAllText(path)) : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(File(directory), JsonSerializer.Serialize(this));
    }

    public static void Forget(string directory) => System.IO.File.Delete(File(directory));

    private static string File(string directory) => Path.Combine(directory, "host.json");
}