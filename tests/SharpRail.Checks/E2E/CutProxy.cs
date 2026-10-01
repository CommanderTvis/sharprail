using System.Net;
using System.Net.Sockets;

namespace SharpRail.Checks.E2E;

/// <summary>
/// A loopback TCP relay in front of a gRPC host that can drop one client's connections and refuse
/// reconnects until allowed, standing in for the reference's routed WebSocket.
/// </summary>
internal sealed class CutProxy : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly int target;
    private readonly List<TcpClient> open = [];
    private readonly CancellationTokenSource lifetime = new();
    private volatile bool refusing;

    internal CutProxy(int target, bool refusing = false)
    {
        this.target = target; this.refusing = refusing;
        listener.Start();
        _ = AcceptAsync();
    }

    internal Uri Endpoint => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
    internal int Accepted { get; private set; }

    /// <summary>Drops every relayed connection and refuses new ones until <see cref="Allow"/>.</summary>
    internal void Cut()
    {
        refusing = true;
        lock (open) { foreach (var client in open) client.Dispose(); open.Clear(); }
    }

    internal void Allow() => refusing = false;

    private async Task AcceptAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(lifetime.Token); }
            catch (Exception) { return; }
            if (refusing) { client.Dispose(); continue; }
            Accepted++;
            _ = RelayAsync(client);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        var server = new TcpClient();
        try
        {
            await server.ConnectAsync(IPAddress.Loopback, target, lifetime.Token);
            lock (open) { open.Add(client); open.Add(server); }
            var up = client.GetStream().CopyToAsync(server.GetStream(), lifetime.Token);
            var down = server.GetStream().CopyToAsync(client.GetStream(), lifetime.Token);
            await Task.WhenAny(up, down);
        }
        catch (Exception) { }
        finally
        {
            lock (open) { open.Remove(client); open.Remove(server); }
            client.Dispose(); server.Dispose();
        }
    }

    public void Dispose()
    {
        lifetime.Cancel(); listener.Stop(); Cut();
    }
}