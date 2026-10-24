using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

using SharpRail.Host.Client;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Tailscale;

namespace SharpRail.Checks;

/// <summary>
/// The browser client's Tailscale pieces that run headlessly: manifest and cache versioning, the lazy-download state
/// machine over a fake store, peer resolution, and pairing HTTP and WebSocket RPC carried over dialed streams (the
/// browser's tailnet route) against the real bootstrap and RPC listeners.
/// </summary>
internal static class BrowserTailscaleChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private const string ManifestJson = """{"version":"1.104.1","build":"1.104.1-abc","module":"tailscale/1.104.1-abc/sharprail-tailscale.wasm","exec":"tailscale/1.104.1-abc/wasm_exec.js","size":1000,"transferSize":4696811}""";

    internal static async Task Run()
    {
        Manifest();
        await Download();
        Peers();
        Console.WriteLine("PASS Browser Tailscale: manifest, cache versioning, lazy download, cancel, failure and retry, peer resolution");
    }

    private static void Manifest()
    {
        var manifest = TailscaleWasmManifest.Parse(ManifestJson);
        Require(manifest.CacheName == "sharprail-tailscale-1.104.1-abc", "Each build has its own Cache Storage name.");
        Require(manifest.TransferLabel == "~4.7 MB", "The download button names the compressed size: " + manifest.TransferLabel);
        var stale = manifest.Stale(["sharprail-tailscale-1.104.1-abc", "sharprail-tailscale-1.102.0-old", "other-app", "sharprail-tailscale-1.104.1-def"]);
        Require(stale.SequenceEqual(["sharprail-tailscale-1.102.0-old", "sharprail-tailscale-1.104.1-def"]), "Only other builds of this cache are stale; foreign caches are kept.");
        foreach (var bad in new[]
        {
            ManifestJson.Replace("tailscale/1.104.1-abc/sharprail", "https://evil.example/sharprail", StringComparison.Ordinal),
            ManifestJson.Replace("tailscale/1.104.1-abc/wasm_exec", "tailscale/../wasm_exec", StringComparison.Ordinal),
            ManifestJson.Replace("\"size\":1000", "\"size\":0", StringComparison.Ordinal),
            ManifestJson.Replace("1.104.1-abc\",\"module", "a/b\",\"module", StringComparison.Ordinal),
        })
        {
            var refused = false;
            try { TailscaleWasmManifest.Parse(bad); }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException) { refused = true; }
            Require(refused, "Manifests naming other origins, parent paths, empty modules or unsafe build names are refused.");
        }
    }

    private sealed class FakeStore : ITailscaleWasmStore
    {
        internal readonly HashSet<string> Caches = ["sharprail-tailscale-1.0.0-old", "unrelated"];
        internal string? FailManifest;
        internal string? FailDownload;
        internal TaskCompletionSource? Gate;
        internal int Downloads;

        public Task<string> ManifestAsync(CancellationToken cancellationToken) =>
            FailManifest is { } message ? Task.FromException<string>(new HttpRequestException(message)) : Task.FromResult(ManifestJson);
        public Task<IReadOnlyList<string>> CacheNamesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Caches.ToArray());
        public Task DeleteCacheAsync(string name, CancellationToken cancellationToken) { Caches.Remove(name); return Task.CompletedTask; }
        public Task<bool> IsCachedAsync(TailscaleWasmManifest manifest, CancellationToken cancellationToken) => Task.FromResult(Caches.Contains(manifest.CacheName));

        public async Task DownloadAsync(TailscaleWasmManifest manifest, IProgress<long> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            progress.Report(250);
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            progress.Report(500);
            if (FailDownload is { } message) throw new IOException(message);
            cancellationToken.ThrowIfCancellationRequested();
            Caches.Add(manifest.CacheName);
        }
    }

    private static async Task Download()
    {
        var store = new FakeStore();
        var module = new TailscaleWasmModule(store);
        var states = new List<TailscaleWasmState>();
        module.Changed += () => states.Add(module.State);
        await module.CheckAsync(CancellationToken.None);
        Require(module.State == TailscaleWasmState.NotDownloaded && store.Downloads == 0, "Opening the option checks the cache but never downloads.");
        Require(states.SequenceEqual([TailscaleWasmState.Checking, TailscaleWasmState.NotDownloaded]), "Checking is visible while the manifest loads.");

        store.Gate = new();
        var running = module.DownloadAsync();
        Require(module.State == TailscaleWasmState.Downloading && module.Progress == 0.25, "Progress reports decoded module bytes against the manifest size.");
        module.Cancel();
        await running;
        Require(module.State == TailscaleWasmState.NotDownloaded && module.Downloaded == 0 && !store.Caches.Contains("sharprail-tailscale-1.104.1-abc"),
            "Cancelling returns to the offer and caches nothing.");

        store.Gate = null;
        store.FailDownload = "connection reset";
        await module.DownloadAsync();
        Require(module.State == TailscaleWasmState.Failed && module.Error!.Contains("connection reset", StringComparison.Ordinal) && store.Caches.Contains("sharprail-tailscale-1.0.0-old"),
            "A failed download reports why and keeps older caches.");

        store.FailDownload = null;
        await module.DownloadAsync();
        Require(module.State == TailscaleWasmState.Cached && module.Progress == 1, "Retrying after a failure downloads and caches.");
        Require(!store.Caches.Contains("sharprail-tailscale-1.0.0-old") && store.Caches.Contains("unrelated"), "Caching a build deletes older builds' caches only.");

        var revisit = new TailscaleWasmModule(store);
        await revisit.CheckAsync(CancellationToken.None);
        Require(revisit.State == TailscaleWasmState.Cached && store.Downloads == 3, "A later visit finds the cached build and skips the download.");

        var broken = new TailscaleWasmModule(new FakeStore { FailManifest = "404" });
        await broken.CheckAsync(CancellationToken.None);
        Require(broken.State == TailscaleWasmState.Failed && broken.Manifest is null, "A server without the module reports an error with retry instead of an offer.");
        await broken.DownloadAsync();
        Require(broken.State == TailscaleWasmState.Failed, "Nothing downloads without a manifest.");
    }

    private static void Peers()
    {
        TailscalePeer[] peers = [new("studio.tail1234.ts.net", "100.70.1.2"), new("laptop.tail1234.ts.net", "100.70.1.3"), new("laptop.other.ts.net", "100.70.1.4"), new("odd.tail1234.ts.net", "192.168.1.9")];
        Require(TailscalePeers.Resolve(peers, "STUDIO").ToString() == "http://100.70.1.2:54124/", "Short names resolve case-insensitively to the numeric bootstrap address.");
        Require(TailscalePeers.Resolve(peers, "laptop.tail1234.ts.net.").Host == "100.70.1.3", "Full MagicDNS names resolve without system DNS.");
        static bool Refused(Action action) { try { action(); return false; } catch (IOException) { return true; } }
        Require(Refused(() => TailscalePeers.Resolve(peers, "laptop")), "An ambiguous short name is refused.");
        Require(Refused(() => TailscalePeers.Resolve(peers, "missing")), "Unknown devices are refused.");
        Require(Refused(() => TailscalePeers.Resolve(peers, "odd")), "A peer without a tailnet address is refused.");
        Require(TailscalePeers.LoginUrl("https://login.tailscale.com/a/123") is not null && TailscalePeers.LoginUrl("http://login.tailscale.com/a") is null &&
            TailscalePeers.LoginUrl("https://tailscale.com.evil.example/a") is null, "Only HTTPS Tailscale sign-in pages open.");
    }

    /// <summary>
    /// Pairing HTTP and the RPC WebSocket over dialed streams: the route a browser takes through its own node. Called
    /// from the Tailscale transport check with its real bootstrap listener and approved endpoint.
    /// </summary>
    internal static async Task StreamRoutes(int bootstrapPort, string pairingToken, Uri endpoint, CancellationToken token)
    {
        var dials = new List<int>();
        async ValueTask<Stream> Dial(Uri uri, CancellationToken cancellation)
        {
            lock (dials) dials.Add(uri.Port);
            var client = new TcpClient();
            try { await client.ConnectAsync(IPAddress.Loopback, uri.Port, cancellation); return new OwnedStream(client); }
            catch { client.Dispose(); throw; }
        }
        using (var http = new HttpClient(new StreamHttpHandler(Dial)) { BaseAddress = new Uri($"http://127.0.0.1:{bootstrapPort}/") })
        {
            var discovery = await http.GetStringAsync("sharprail", token);
            Require(discovery.Contains("fixture.tailnet.ts.net", StringComparison.Ordinal), "Discovery answers over a dialed stream.");
            using var request = new HttpRequestMessage(HttpMethod.Post, "sharprail/pair");
            request.Headers.Add("X-SharpRail-Pairing", pairingToken);
            using var response = await http.SendAsync(request, token);
            var reply = await response.Content.ReadFromJsonAsync<TailscalePairReply>(PluginJson.Options, token);
            Require(reply is { State: "approved" } && reply.Address == endpoint.ToString(), "Pairing over a dialed stream returns the approved endpoint.");
        }
        using (RpcTransports.RegisterStreamTransport(endpoint, cancellation => Dial(endpoint, cancellation)))
        using (var state = new RemoteStateAdapter(endpoint, pairingToken))
        {
            var handshake = await state.GetHandshakeAsync(token);
            Require(handshake.ProtocolVersion > 0, "WebSocket RPC authenticates over a dialed stream.");
        }
        Require(dials.Contains(bootstrapPort) && dials.Contains(endpoint.Port), "Both pairing and RPC used the registered stream dialer.");
        using (RpcTransports.RegisterStreamTransport(endpoint, (_ => throw new IOException("unreachable"))))
        using (var state = new RemoteStateAdapter(endpoint, pairingToken))
        {
            var failed = false;
            using var brief = CancellationTokenSource.CreateLinkedTokenSource(token);
            brief.CancelAfter(TimeSpan.FromSeconds(2));
            try { await state.GetHandshakeAsync(brief.Token); }
            catch (Exception error) when (RemoteErrors.IsTransport(error) || error is IOException or OperationCanceledException) { failed = !token.IsCancellationRequested; }
            Require(failed, "A registered stream route is used exclusively; a failed dial never falls back to ordinary sockets.");
        }
        Console.WriteLine("PASS Browser Tailscale streams: discovery, pairing and WebSocket RPC over dialed streams");
    }

    private sealed class OwnedStream(TcpClient client) : Stream
    {
        private readonly NetworkStream inner = client.GetStream();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
    }
}