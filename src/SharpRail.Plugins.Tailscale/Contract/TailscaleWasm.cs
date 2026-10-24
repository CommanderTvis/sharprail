using System.Text.Json;

namespace SharpRail.Plugins.Tailscale;

/// <summary>The lazily downloaded browser node as the host's web server describes it in <c>tailscale/manifest.json</c>.</summary>
public sealed record TailscaleWasmManifest(string Version, string Build, string Module, string Exec, long Size, long TransferSize)
{
    /// <summary>Cache Storage names start with this; anything else in the origin's caches is never touched.</summary>
    public const string CachePrefix = "sharprail-tailscale-";
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>One cache per build, so a new Tailscale pin or bridge change never reuses an older module.</summary>
    public string CacheName => CachePrefix + Build;

    /// <summary>The download size the connect screen offers, in megabytes rounded up to a tenth.</summary>
    public string TransferLabel => "~" + (Math.Ceiling(TransferSize / 100_000.0) / 10).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    /// <summary>Parses and validates the manifest: same-origin relative paths under <c>tailscale/</c> only.</summary>
    public static TailscaleWasmManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<TailscaleWasmManifest>(json, Options)
            ?? throw new FormatException("The Tailscale manifest is empty.");
        static bool Safe(string? path) => path is { Length: > 0 } && path.StartsWith("tailscale/", StringComparison.Ordinal) &&
            !path.Contains("..", StringComparison.Ordinal) && !path.Contains("//", StringComparison.Ordinal) && !path.Contains(':', StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Build) || manifest.Build.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) ||
            !Safe(manifest.Module) || !Safe(manifest.Exec) || manifest.Size <= 0 || manifest.TransferSize <= 0)
            throw new FormatException("The Tailscale manifest is invalid.");
        return manifest;
    }

    /// <summary>Caches of other builds, which are deleted once this build is cached.</summary>
    public IReadOnlyList<string> Stale(IEnumerable<string> cacheNames) =>
        cacheNames.Where(name => name.StartsWith(CachePrefix, StringComparison.Ordinal) && name != CacheName).ToArray();
}

/// <summary>What the browser supplies for the download: the manifest, Cache Storage and a cancellable fetch.</summary>
public interface ITailscaleWasmStore
{
    Task<string> ManifestAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> CacheNamesAsync(CancellationToken cancellationToken);
    Task DeleteCacheAsync(string name, CancellationToken cancellationToken);
    Task<bool> IsCachedAsync(TailscaleWasmManifest manifest, CancellationToken cancellationToken);
    /// <summary>Fetches both files into the build's cache, reporting decoded module bytes; cancellation leaves nothing cached.</summary>
    Task DownloadAsync(TailscaleWasmManifest manifest, IProgress<long> progress, CancellationToken cancellationToken);
}

public enum TailscaleWasmState { Unknown, Checking, NotDownloaded, Downloading, Cached, Failed }

/// <summary>
/// The lazy download's state machine: checking the cache, offering the download, downloading with progress and cancel,
/// then cached (stale builds pruned); failures keep the reason and allow a retry. Platform-neutral for headless checks.
/// Callers may invoke it from the UI thread; it never blocks, and <see cref="Changed"/> runs on the store's continuations.
/// </summary>
public sealed class TailscaleWasmModule(ITailscaleWasmStore store)
{
    private CancellationTokenSource? download;
    private readonly object gate = new();

    public TailscaleWasmState State { get; private set; }
    public TailscaleWasmManifest? Manifest { get; private set; }
    public long Downloaded { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    /// <summary>Fraction of the module downloaded, from 0 to 1.</summary>
    public double Progress => Manifest is { Size: > 0 } manifest ? Math.Clamp((double)Downloaded / manifest.Size, 0, 1) : 0;

    private void Set(TailscaleWasmState state, string? error = null)
    {
        State = state;
        Error = error;
        Changed?.Invoke();
    }

    /// <summary>Reads the manifest and whether its build is already cached, without downloading.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (State is TailscaleWasmState.Downloading or TailscaleWasmState.Checking) return;
        Set(TailscaleWasmState.Checking);
        try
        {
            var manifest = Manifest = TailscaleWasmManifest.Parse(await store.ManifestAsync(cancellationToken));
            var cached = await store.IsCachedAsync(manifest, cancellationToken);
            if (cached) await PruneAsync(manifest, cancellationToken);
            Set(cached ? TailscaleWasmState.Cached : TailscaleWasmState.NotDownloaded);
        }
        catch (OperationCanceledException) { Set(TailscaleWasmState.Unknown); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Set(TailscaleWasmState.Failed, "Could not check for Tailscale on this server: " + error.Message); }
    }

    /// <summary>Downloads the cached build after an explicit request; <see cref="Cancel"/> returns to the offer.</summary>
    public async Task DownloadAsync()
    {
        if (Manifest is not { } manifest || State is not (TailscaleWasmState.NotDownloaded or TailscaleWasmState.Failed)) return;
        var current = new CancellationTokenSource();
        lock (gate) download = current;
        Downloaded = 0;
        Set(TailscaleWasmState.Downloading);
        try
        {
            await store.DownloadAsync(manifest, new Reporter(this, current.Token), current.Token);
            current.Token.ThrowIfCancellationRequested();
            Downloaded = manifest.Size;
            await PruneAsync(manifest, CancellationToken.None);
            Set(TailscaleWasmState.Cached);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            Downloaded = 0;
            Set(TailscaleWasmState.NotDownloaded);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Downloaded = 0;
            Set(TailscaleWasmState.Failed, "The Tailscale download failed: " + error.Message);
        }
        finally
        {
            lock (gate) if (download == current) download = null;
            current.Dispose();
        }
    }

    public void Cancel()
    {
        lock (gate) download?.Cancel();
    }

    private async Task PruneAsync(TailscaleWasmManifest manifest, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var name in manifest.Stale(await store.CacheNamesAsync(cancellationToken))) await store.DeleteCacheAsync(name, cancellationToken);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException)) { }
    }

    private sealed class Reporter(TailscaleWasmModule module, CancellationToken token) : IProgress<long>
    {
        public void Report(long value)
        {
            if (token.IsCancellationRequested || module.State != TailscaleWasmState.Downloading) return;
            module.Downloaded = value;
            module.Changed?.Invoke();
        }
    }
}

/// <summary>Peer selection shared by userspace nodes that own their netmap.</summary>
public static class TailscalePeers
{
    /// <summary>
    /// Resolves a typed host (address, full MagicDNS name or its first label, case-insensitive) to one online peer's
    /// numeric bootstrap URI without system DNS.
    /// </summary>
    public static Uri Resolve(IReadOnlyList<TailscalePeer> peers, string host)
    {
        var input = host.Trim().TrimEnd('.');
        var matches = peers.Where(peer => peer.Address == input || peer.Name.Equals(input, StringComparison.OrdinalIgnoreCase) ||
            peer.Name.Split('.')[0].Equals(input, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new IOException("More than one Tailscale device has that short name. Choose a discovered host or use its full name.");
        var peer = matches.SingleOrDefault() ?? throw new IOException("This host is not an online device in this tailnet. Refresh hosts or check its Tailscale connection.");
        if (!System.Net.IPAddress.TryParse(peer.Address, out var ip) || !TailscaleCli.IsTailnetAddress(ip))
            throw new IOException("That device has no Tailscale IPv4 address.");
        return new UriBuilder("http", ip.ToString(), TailscaleContract.DiscoveryPort).Uri;
    }

    /// <summary>Only HTTPS Tailscale sign-in pages are opened.</summary>
    public static Uri? LoginUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && (uri.Host == "tailscale.com" || uri.Host.EndsWith(".tailscale.com", StringComparison.OrdinalIgnoreCase)) ? uri : null;
}