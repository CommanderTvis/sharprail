using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Tailscale;

/// <summary>The client-local bootstrap, usable before any host plugin context exists.</summary>
public sealed class TailscaleClient : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<string, CancellationToken, Task<Uri>> bootstrap;

    public TailscaleClient() : this(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, BootstrapAsync) { }

    internal TailscaleClient(HttpMessageHandler handler, Func<string, CancellationToken, Task<Uri>> bootstrap)
    {
        http = new(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 16 * 1024 };
        this.bootstrap = bootstrap;
    }

    internal static async Task<Uri> BootstrapAsync(string host, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate("http://" + host.Trim() + ":" + TailscaleContract.DiscoveryPort, UriKind.Absolute, out var uri) ||
            uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new IOException("Enter a Tailscale device name without a port or path.");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken); }
        catch (System.Net.Sockets.SocketException error)
        {
            throw new IOException("Could not resolve the Tailscale host name. Turn on Tailscale on this device, check that both devices use the same Tailscale network, and check the host name and MagicDNS.", error);
        }
        var ip = addresses.FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && TailscaleCli.IsTailnetAddress(address))
            ?? throw new IOException("This name does not resolve to a Tailscale address. Check Tailscale and MagicDNS.");
        return new UriBuilder("http", ip.ToString(), TailscaleContract.DiscoveryPort).Uri;
    }

    public async Task<IReadOnlyList<TailscalePeer>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var peers = await TailscaleCli.PeersAsync(cancellationToken);
        using var slots = new SemaphoreSlim(8);
        var results = await Task.WhenAll(peers.Select(async peer =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var uri = await bootstrap(peer.Address, timeout.Token);
                using var reply = await http.GetAsync(new Uri(uri, "sharprail"), timeout.Token);
                reply.EnsureSuccessStatusCode();
                var info = await reply.Content.ReadFromJsonAsync<TailscaleHostInfo>(PluginJson.Options, timeout.Token);
                return info is null ? null : new TailscalePeer(info.Name, peer.Name);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or JsonException) { return null; }
            finally { slots.Release(); }
        }));
        cancellationToken.ThrowIfCancellationRequested();
        return results.OfType<TailscalePeer>().ToArray();
    }

    public static TailscaleRemembered NewPairing(string host) => new(host.Trim(), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    public async Task<TailscaleConnection> PairAsync(TailscaleRemembered pairing, Action<string>? progress, CancellationToken cancellationToken)
    {
        var uri = await bootstrap(pairing.Host, cancellationToken);
        progress?.Invoke("Approve this device on the host in Settings › Tailscale. Compare code " + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pairing.Token)))[..8].ToUpperInvariant() + ".");
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "sharprail/pair"));
            request.Headers.Add("X-SharpRail-Pairing", pairing.Token);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var reply = await response.Content.ReadFromJsonAsync<TailscalePairReply>(PluginJson.Options, cancellationToken) ?? throw new IOException("The host sent an empty pairing reply.");
            switch (reply.State)
            {
                case "approved":
                    if (!Uri.TryCreate(reply.Address, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || endpoint.UserInfo.Length > 0 || endpoint.AbsolutePath != "/" ||
                        endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || !IPAddress.TryParse(endpoint.Host, out var ip) || !TailscaleCli.IsTailnetAddress(ip) || endpoint.Host != uri.Host)
                        throw new IOException("The host returned an invalid Tailscale endpoint.");
                    return new(endpoint.ToString(), pairing.Token);
                case "pending": await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); break;
                case "denied": throw new IOException("The host declined this pairing request. Start a new pairing to try again.");
                default: throw new IOException("The host cannot accept this pairing request (" + reply.State + ").");
            }
        }
    }

    public static TailscaleRemembered? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<TailscaleRemembered>(File.ReadAllText(path), PluginJson.Options) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Forget(string path, string token)
    {
        if (Load(path)?.Token == token) File.Delete(path);
    }

    public static void Save(string path, TailscaleRemembered pairing)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) JsonSerializer.Serialize(stream, pairing, PluginJson.Options);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public void Dispose() => http.Dispose();
}