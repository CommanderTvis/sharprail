using System.Net;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia.Threading;

using SharpRail.Host.Client;
using SharpRail.Plugins.Tailscale;

namespace SharpRail.UI.Browser;

/// <summary>tailscale.js, imported on first use so the connect screen pays nothing until Tailscale is chosen.</summary>
internal static partial class TailscaleInterop
{
    private static Task? imported;

    // Every call runs on the dispatcher, where the app's other JavaScript interop runs; callers only await.
    internal static async Task<T> Call<T>(Func<Task<T>> call) => await Dispatcher.UIThread.InvokeAsync(async () =>
    {
        await (imported ??= JSHost.ImportAsync("tailscale", "../tailscale.js"));
        return await call();
    });
    internal static Task Call(Func<Task> call) => Call(async () => { await call(); return true; });
    internal static Task<T> CallSync<T>(Func<T> call) => Call(() => Task.FromResult(call()));

    [JSImport("manifest", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.String>>] internal static partial Task<string> Manifest();
    [JSImport("cacheNames", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.String>>] internal static partial Task<string> CacheNames();
    [JSImport("deleteCache", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.Void>>] internal static partial Task DeleteCache(string name);
    [JSImport("isCached", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.Boolean>>] internal static partial Task<bool> IsCached(string manifest);
    [JSImport("startDownload", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.Void>>] internal static partial Task StartDownload(string manifest);
    [JSImport("downloaded", "tailscale")] internal static partial double Downloaded();
    [JSImport("cancelDownload", "tailscale")] internal static partial void CancelDownload();
    [JSImport("start", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.Void>>] internal static partial Task Start(string manifest, string hostname);
    [JSImport("status", "tailscale")] internal static partial string Status();
    [JSImport("login", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.Void>>] internal static partial Task Login();
    [JSImport("logout", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.Void>>] internal static partial Task Logout();
    [JSImport("dial", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.Number>>] internal static partial Task<double> Dial(string address, int timeout);
    [JSImport("read", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.String>>] internal static partial Task<string?> Read(int id);
    [JSImport("write", "tailscale")][return: JSMarshalAs<JSType.Promise<JSType.Void>>] internal static partial Task Write(int id, string base64);
    [JSImport("close", "tailscale")] internal static partial void Close(int id);
}

/// <summary>Cache Storage and same-origin fetch for the lazily downloaded module.</summary>
internal sealed class BrowserTailscaleStore : ITailscaleWasmStore
{
    private static string Json(TailscaleWasmManifest manifest) => JsonSerializer.Serialize(new
    {
        version = manifest.Version, build = manifest.Build, module = manifest.Module, exec = manifest.Exec, size = manifest.Size, transferSize = manifest.TransferSize
    });

    public Task<string> ManifestAsync(CancellationToken cancellationToken) => TailscaleInterop.Call(TailscaleInterop.Manifest).WaitAsync(cancellationToken);
    public async Task<IReadOnlyList<string>> CacheNamesAsync(CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<string[]>(await TailscaleInterop.Call(TailscaleInterop.CacheNames).WaitAsync(cancellationToken)) ?? [];
    public Task DeleteCacheAsync(string name, CancellationToken cancellationToken) => TailscaleInterop.Call(() => TailscaleInterop.DeleteCache(name)).WaitAsync(cancellationToken);
    public Task<bool> IsCachedAsync(TailscaleWasmManifest manifest, CancellationToken cancellationToken) =>
        TailscaleInterop.Call(() => TailscaleInterop.IsCached(Json(manifest))).WaitAsync(cancellationToken);

    public async Task DownloadAsync(TailscaleWasmManifest manifest, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var download = TailscaleInterop.Call(() => TailscaleInterop.StartDownload(Json(manifest)));
        using var cancel = cancellationToken.Register(() => _ = TailscaleInterop.CallSync(() => { TailscaleInterop.CancelDownload(); return true; }));
        while (!download.IsCompleted)
        {
            await Task.WhenAny(download, Task.Delay(200, CancellationToken.None));
            progress.Report((long)await TailscaleInterop.CallSync(TailscaleInterop.Downloaded));
        }
        try { await download; }
        catch (JSException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }
}

internal sealed record BrowserTailscaleStatus(string State, string AuthUrl, bool Online, TailscalePeer[] Peers, string? Error);

/// <summary>The tab's own userspace node: discovery, pairing HTTP and WebSocket RPC all dial through it.</summary>
internal sealed class BrowserTailscaleNetwork : ITailscaleClientNetwork
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    internal static BrowserTailscaleNetwork Instance { get; } = new();
    internal static BrowserTailscaleStore Store { get; } = new();
    internal static TailscaleWasmModule Module { get; } = new(Store);

    internal Task StartAsync(CancellationToken cancellationToken) => Module.Manifest is { } manifest
        ? TailscaleInterop.Call(() => TailscaleInterop.Start(JsonSerializer.Serialize(new
        {
            version = manifest.Version, build = manifest.Build, module = manifest.Module, exec = manifest.Exec, size = manifest.Size, transferSize = manifest.TransferSize
        }), "sharprail-browser")).WaitAsync(cancellationToken)
        : throw new IOException("Download Tailscale first.");

    internal async Task<BrowserTailscaleStatus> StatusAsync(CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<BrowserTailscaleStatus>(await TailscaleInterop.CallSync(TailscaleInterop.Status).WaitAsync(cancellationToken), Options)
        ?? throw new IOException("Tailscale reported no status.");

    internal Task LoginAsync(CancellationToken cancellationToken) => TailscaleInterop.Call(TailscaleInterop.Login).WaitAsync(cancellationToken);
    internal Task LogoutAsync(CancellationToken cancellationToken) => TailscaleInterop.Call(TailscaleInterop.Logout).WaitAsync(cancellationToken);

    public HttpMessageHandler CreateHandler() => new StreamHttpHandler((uri, token) => DialAsync(uri, token));

    public async Task<Uri> BootstrapAsync(string host, CancellationToken cancellationToken) =>
        TailscalePeers.Resolve(await PeersAsync(cancellationToken), host);

    public async Task<IReadOnlyList<TailscalePeer>> PeersAsync(CancellationToken cancellationToken)
    {
        var status = await StatusAsync(cancellationToken);
        if (!status.Online) throw new IOException("Tailscale is not connected.");
        return status.Peers;
    }

    /// <summary>A TCP stream to a numeric tailnet address over the node; names never reach system DNS.</summary>
    internal static async ValueTask<Stream> DialAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(uri.Host, out var ip) || !TailscaleCli.IsTailnetAddress(ip))
            throw new IOException("Only Tailscale addresses are reached through the browser's tailnet.");
        var dialing = TailscaleInterop.Call(() => TailscaleInterop.Dial(ip + ":" + uri.Port, 10_000));
        try { return new TailnetStream((int)await dialing.WaitAsync(cancellationToken)); }
        catch (OperationCanceledException)
        {
            _ = dialing.ContinueWith(late => { if (late.IsCompletedSuccessfully) _ = TailscaleInterop.CallSync(() => { TailscaleInterop.Close((int)late.Result); return true; }); }, TaskScheduler.Default);
            throw;
        }
        catch (JSException error) { throw new IOException(error.Message, error); }
    }

    private sealed class TailnetStream(int id) : Stream
    {
        private byte[] pending = [];
        private int offset;
        private int closed;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Tailnet streams are asynchronous.");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Tailnet streams are asynchronous.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (offset == pending.Length)
            {
                string? chunk;
                try { chunk = await TailscaleInterop.Call(() => TailscaleInterop.Read(id)).WaitAsync(cancellationToken); }
                catch (JSException error) { throw new IOException(error.Message, error); }
                if (chunk is null) return 0;
                pending = Convert.FromBase64String(chunk);
                offset = 0;
            }
            var count = Math.Min(buffer.Length, pending.Length - offset);
            pending.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return;
            var base64 = Convert.ToBase64String(buffer.Span);
            try { await TailscaleInterop.Call(() => TailscaleInterop.Write(id, base64)).WaitAsync(cancellationToken); }
            catch (JSException error) { throw new IOException(error.Message, error); }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref closed, 1) == 0) _ = TailscaleInterop.CallSync(() => { TailscaleInterop.Close(id); return true; });
            base.Dispose(disposing);
        }
    }
}