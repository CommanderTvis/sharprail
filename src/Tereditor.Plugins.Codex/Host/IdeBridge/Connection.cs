using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.Codex.Host.IdeBridge;

/// <summary>
/// Joins the Codex router at a socket path, or becomes it when none is listening, and keeps reconnecting (one timer)
/// until disposed. A live socket is never replaced; an owned stale socket is removed only after a refused connection,
/// once its owner and inode are checked again. The parent directory must be the user's own and not writable by others.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class Connection : IDisposable
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly string path;
    private readonly Action<Peer> connected;
    private readonly Action<Peer, JsonObject> receive;
    private readonly Action<Exception> warn;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private Peer? peer;
    private Socket? listener;
    private UnixStat? listening;
    private Router? router;
    private string lastError = "";

    public Connection(string path, Action<Peer> connected, Action<Peer, JsonObject> receive, Action<Exception> warn)
    {
        this.path = path; this.connected = connected; this.receive = receive; this.warn = warn;
        _ = Task.Run(RunAsync);
    }

    private void Report(Exception error)
    {
        var message = error.Message;
        if (message != lastError) warn(error);
        lastError = message;
    }

    private UnixStat? Prepare()
    {
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var directory = Unix.Lstat(parent);
        if (directory is not { IsDirectory: true } owned || owned.Uid != Unix.Uid || (owned.Mode & 0b010_010) != 0)
            throw new IOException("Codex IPC directory must be owned by the current user and not writable by others");
        var socket = Unix.Lstat(path);
        if (socket is { } existing && (!existing.IsSocket || existing.Uid != Unix.Uid))
            throw new IOException("Codex IPC path is not a socket owned by the current user");
        return socket;
    }

    private async Task RunAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            if (!await AttemptAsync()) continue;
            try { await Task.Delay(Retry, lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    // One connection attempt; false asks for an immediate retry (this bridge has just become the router).
    private async Task<bool> AttemptAsync()
    {
        UnixStat? before;
        try { before = Prepare(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Report(error); return true; }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                timeout.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
            }
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ArgumentException)
        {
            socket.Dispose();
            if (lifetime.IsCancellationRequested) return true;
            var missing = Unix.Lstat(path) is null;
            var refused = error is SocketException { SocketErrorCode: SocketError.ConnectionRefused };
            if (!missing && !refused)
            {
                Report(error is OperationCanceledException ? new TimeoutException("Codex IPC connection timed out") : error);
                return true;
            }
            try
            {
                if (refused && before is { } stale && Prepare() is { } now && now.Inode == stale.Inode && now.Device == stale.Device) File.Delete(path);
                return !Listen();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SocketException)
            {
                Report(failure);
                return true;
            }
        }
        lastError = "";
        var current = new Peer(socket);
        lock (gate)
        {
            if (lifetime.IsCancellationRequested) { current.Dispose(); return true; }
            peer = current;
        }
        connected(current);
        await current.ReadAsync(message => receive(current, message));
        return true;
    }

    // Becomes the router; false when another process bound the path first.
    private bool Listen()
    {
        lock (gate)
        {
            if (lifetime.IsCancellationRequested || listener is not null) return false;
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(path));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                socket.Listen(64);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                socket.Dispose();
                return false;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            listener = socket;
            listening = Unix.Lstat(path);
            router = new Router(socket);
            var owned = router;
            _ = Task.Run(async () =>
            {
                await owned.AcceptAsync();
                lock (gate) if (router == owned) { router = null; listener = null; }
            });
            return true;
        }
    }

    public void Dispose()
    {
        Peer? current;
        Socket? owned;
        Router? routing;
        UnixStat? created;
        lock (gate)
        {
            lifetime.Cancel();
            current = peer;
            owned = listener;
            routing = router;
            created = listening;
            peer = null; listener = null; router = null;
        }
        current?.Dispose();
        routing?.Dispose();
        if (owned is null) return;
        owned.Dispose();
        // Closing a listening Unix socket leaves its file; remove it only while it is still the one this bridge bound.
        try
        {
            if (created is { } mine && Unix.Lstat(path) is { } now && now.Inode == mine.Inode && now.Device == mine.Device) File.Delete(path);
        }
        catch (IOException) { }
    }
}