using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Builder;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Remote;

namespace SharpRail.UI.Terminal;

// Serves the app's own terminal sessions to the relays that its Ghostty tabs run, over a private Unix
// socket with a per-run token. It starts with the first terminal, off the UI thread, and stops with the app;
// the sessions themselves belong to the app's host, so they outlive any window. The host's loopback server
// starts with it, so shells get the MCP route from the first one.
public sealed class LocalTerminalRelay(ITerminalService terminals, LoopbackServer? loopback = null) : IAsyncDisposable
{
    // macOS limits a socket path to 104 bytes.
    private const int SocketPathLimit = 103;
    private readonly Lock gate = new();
    private Task<RemoteTerminalConnection>? started;
    private WebApplication? server;
    private string? socket;

    public Task<RemoteTerminalConnection> ConnectAsync()
    {
        lock (gate) return started ??= Task.Run(StartAsync);
    }

    private async Task<RemoteTerminalConnection> StartAsync()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The terminal relay requires Unix domain socket permissions.");
        _ = loopback?.BaseUrl;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)) + ".sock";
        socket = Path.Combine(TerminalRelay.PrivateDirectory(), name);
        if (Encoding.UTF8.GetByteCount(socket) > SocketPathLimit) socket = Path.Combine("/tmp", "sharprail-" + name);
        server = RemoteServer.CreateTerminalRelay(terminals, socket, token);
        await server.StartAsync();
        File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new(new Uri("unix:" + socket), token, new LocalTerminalAdapter(terminals));
    }

    public async ValueTask DisposeAsync()
    {
        Task<RemoteTerminalConnection>? pending;
        lock (gate) pending = started;
        if (pending is null) return;
        try { await pending; } catch (Exception) { }
        if (server is not null) await server.DisposeAsync();
        if (socket is not null) File.Delete(socket);
    }
}