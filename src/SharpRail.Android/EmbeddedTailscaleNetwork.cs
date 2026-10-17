using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

using SharpRail.Plugins.Tailscale;

namespace SharpRail.UI.Android;

/// <summary>The app-private userspace node. Neither the system VPN nor another application's state is used.</summary>
internal sealed class EmbeddedTailscaleNetwork : ITailscaleClientNetwork, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private static EmbeddedTailscaleNetwork? nativeOwner;
    private readonly Func<string, string, string> native;
    private readonly Func<string>? networkSnapshot;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string stateDirectory;
    private volatile EmbeddedTailscaleStatus? status;
    private long statusUpdated;
    private bool started;
    private bool disposed;

    internal EmbeddedTailscaleNetwork(string stateDirectory, Func<string, string, string>? native = null, Func<string>? networkSnapshot = null)
    {
        this.stateDirectory = stateDirectory;
        this.native = native ?? NativeCall;
        this.networkSnapshot = networkSnapshot;
    }

    internal async Task<EmbeddedTailscaleStatus> StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started)
            {
                Directory.CreateDirectory(stateDirectory);
                status = await CallAsync("start", JsonSerializer.Serialize(new { stateDirectory, hostname = "sharprail-android" }), cancellationToken);
                started = true;
            }
            return status!;
        }
        finally { gate.Release(); }
    }

    internal async Task<EmbeddedTailscaleStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);
        return await ExecuteAsync("status", cancellationToken);
    }

    internal async Task<EmbeddedTailscaleStatus> LoginAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);
        return await ExecuteAsync("login", cancellationToken);
    }

    internal async Task LogoutAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync("logout", cancellationToken);
    }

    internal async Task UpdateNetworkAsync(CancellationToken cancellationToken)
    {
        if (networkSnapshot is null) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (disposed || !started) return;
            status = await CallAsync("network", await Task.Run(networkSnapshot, cancellationToken), cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<EmbeddedTailscaleStatus> ExecuteAsync(string operation, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return status = await CallAsync(operation, "{}", cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<EmbeddedTailscaleStatus> CallAsync(string operation, string input, CancellationToken cancellationToken)
    {
        // Native operations have their own finite timeout. Do not abandon an in-flight call: the next
        // operation, especially stop/logout, must run after it rather than racing the native singleton.
        cancellationToken.ThrowIfCancellationRequested();
        await NativeGate.WaitAsync(cancellationToken);
        try
        {
            if (operation == "start")
            {
                // Activity recreation can overlap asynchronous disposal. A replacement owns the singleton
                // before an old disposer runs; the old disposer then cannot stop the replacement node.
                if (nativeOwner is { } previous && previous != this)
                    await Task.Run(() => previous.native("stop", "{}"));
                nativeOwner = this;
                if (networkSnapshot is not null)
                {
                    var network = await Task.Run(() => native("network", networkSnapshot()));
                    var result = JsonSerializer.Deserialize<EmbeddedTailscaleStatus>(network, Json);
                    if (!string.IsNullOrEmpty(result?.Error)) throw new IOException(result.Error);
                }
            }
            else if (nativeOwner != this) throw new IOException("This Tailscale session has been replaced. Reopen the connection screen.");
            var json = await Task.Run(() => native(operation, input));
            var reply = JsonSerializer.Deserialize<EmbeddedTailscaleStatus>(json, Json) ?? throw new IOException("Tailscale sent no status.");
            if (!string.IsNullOrEmpty(reply.Error)) throw new IOException(reply.Error);
            statusUpdated = Environment.TickCount64;
            return reply;
        }
        finally { NativeGate.Release(); }
    }

    public HttpMessageHandler CreateHandler()
    {
        var current = status ?? throw new IOException("Sign in to Tailscale before connecting.");
        if (!Uri.TryCreate(current.ProxyAddress, UriKind.Absolute, out var proxy) || proxy.Scheme != "socks5" ||
            proxy.Host != "127.0.0.1" || proxy.UserInfo.Length != 0 || proxy.AbsolutePath != "/" ||
            proxy.Query.Length != 0 || proxy.Fragment.Length != 0 || current.ProxyUsername.Length == 0 || current.ProxyPassword.Length == 0)
            throw new IOException("The embedded Tailscale proxy is unavailable. Reopen Tailscale and retry.");
        return new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy) { Credentials = new NetworkCredential(current.ProxyUsername, current.ProxyPassword) },
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<IReadOnlyList<TailscalePeer>> PeersAsync(CancellationToken cancellationToken)
    {
        var current = await StartAsync(cancellationToken);
        if (Environment.TickCount64 - Interlocked.Read(ref statusUpdated) >= 1000) current = await RefreshAsync(cancellationToken);
        if (!current.Online) throw new IOException("Tailscale is not connected. Finish signing in or ask your tailnet administrator to approve this device.");
        return current.Peers.Where(peer => peer.Name.Length > 0 && IPAddress.TryParse(peer.Address, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && TailscaleCli.IsTailnetAddress(ip)).Take(256).ToArray();
    }

    public async Task<Uri> BootstrapAsync(string host, CancellationToken cancellationToken)
    {
        var peers = await PeersAsync(cancellationToken);
        var input = host.Trim().TrimEnd('.');
        var matches = peers.Where(peer => peer.Address == input || peer.Name.Equals(input, StringComparison.OrdinalIgnoreCase) ||
            peer.Name.Split('.')[0].Equals(input, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new IOException("More than one Tailscale device has that short name. Choose a discovered host or use its full name.");
        var peer = matches.SingleOrDefault();
        if (peer is null) throw new IOException("This host is not an online device in SharpRail's Tailscale network. Refresh hosts or check its Tailscale connection.");
        // Use an address from the embedded netmap, never system DNS or an arbitrary user-supplied socket.
        return new UriBuilder("http", peer.Address, TailscaleContract.DiscoveryPort).Uri;
    }

    internal static Uri? LoginUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && (uri.Host == "tailscale.com" || uri.Host.EndsWith(".tailscale.com", StringComparison.OrdinalIgnoreCase)) ? uri : null;

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            await NativeGate.WaitAsync();
            try
            {
                if (nativeOwner == this)
                {
                    try { await Task.Run(() => native("stop", "{}")); }
                    catch (Exception error) when (error is IOException or DllNotFoundException or EntryPointNotFoundException) { }
                    finally { nativeOwner = null; }
                }
            }
            finally { NativeGate.Release(); }
            status = null;
        }
        finally { gate.Release(); }
    }

    private static string NativeCall(string operation, string input)
    {
        IntPtr reply;
        try { reply = Native.Call(operation, input); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { throw new IOException("Embedded Tailscale is missing from this package. Install a complete SharpRail Android build.", error); }
        if (reply == IntPtr.Zero) throw new IOException("Embedded Tailscale could not return its status.");
        try { return Marshal.PtrToStringUTF8(reply) ?? throw new IOException("Embedded Tailscale sent no status."); }
        finally { Native.Free(reply); }
    }

    private static class Native
    {
        [DllImport("SharpRailTailscale", EntryPoint = "sr_ts_call", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Call([MarshalAs(UnmanagedType.LPUTF8Str)] string operation, [MarshalAs(UnmanagedType.LPUTF8Str)] string input);
        [DllImport("SharpRailTailscale", EntryPoint = "sr_ts_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(IntPtr value);
    }
}

// Keep credentials out of synthesized record ToString() output and UI messages.
internal sealed class EmbeddedTailscaleStatus
{
    public string State { get; set; } = "Starting";
    public string AuthUrl { get; set; } = "";
    public bool Online { get; set; }
    public TailscalePeer[] Peers { get; set; } = [];
    public string ProxyAddress { get; set; } = "";
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public string? Error { get; set; }
}