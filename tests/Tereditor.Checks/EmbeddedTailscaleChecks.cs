using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using SharpRail.Plugins.Tailscale;
using SharpRail.UI.Android;

namespace SharpRail.Checks;

internal static class EmbeddedTailscaleChecks
{
    internal static async Task Run(string root)
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var operations = new List<string>();
        var running = false;
        var online = true;
        string Native(string operation, string input)
        {
            operations.Add(operation);
            if (operation == "start")
            {
                using var request = JsonDocument.Parse(input);
                Require(request.RootElement.GetProperty("stateDirectory").GetString() == Path.Combine(root, "tailscale-node"), "The native node must use private persistent state.");
                running = true;
            }
            if (operation == "logout") online = false;
            if (operation == "stop") running = false;
            return JsonSerializer.Serialize(new
            {
                state = online ? "Running" : "NeedsLogin",
                online = running && online,
                proxyAddress = $"socks5://127.0.0.1:{proxyPort}",
                proxyUsername = "fixture-user",
                proxyPassword = "fixture-secret",
                authUrl = "https://login.tailscale.com/a/fixture",
                peers = new[] { new { name = "fixture.tailnet.ts.net", address = "100.70.1.2" }, new { name = "bad", address = "192.168.0.1" } }
            });
        }
        var network = new EmbeddedTailscaleNetwork(Path.Combine(root, "tailscale-node"), Native,
            () => "{\"interfaces\":[],\"defaultInterface\":\"\",\"gateway\":\"\"}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await network.StartAsync(timeout.Token);
        await network.StartAsync(timeout.Token);
        Require(operations.Take(2).SequenceEqual(["network", "start"]), "Android connectivity must be supplied before starting tsnet; it cannot enumerate restricted netlink interfaces.");
        await network.UpdateNetworkAsync(timeout.Token);
        Require(operations.Count(operation => operation == "network") == 2, "Connectivity changes must update the native monitor without restarting identity.");
        Require(operations.Count(operation => operation == "start") == 1, "Repeated discovery must reuse one native node.");
        Require((await network.BootstrapAsync("fixture", timeout.Token)).Host == "100.70.1.2", "Embedded name lookup must use its own peer map.");
        try { await network.BootstrapAsync("192.168.0.1", timeout.Token); throw new InvalidOperationException("A LAN address escaped netmap validation."); }
        catch (IOException) { }
        var probe = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = socket.GetStream();
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, timeout.Token);
            Require(greeting[0] == 5, "Embedded HTTP must use SOCKS5.");
            var methods = new byte[greeting[1]];
            await stream.ReadExactlyAsync(methods, timeout.Token);
            Require(methods.Contains((byte)2), "The local proxy must support credential authentication.");
            await stream.WriteAsync(new byte[] { 5, 2 }, timeout.Token);
            var auth = new byte[2];
            await stream.ReadExactlyAsync(auth, timeout.Token);
            var username = new byte[auth[1]];
            await stream.ReadExactlyAsync(username, timeout.Token);
            var length = new byte[1];
            await stream.ReadExactlyAsync(length, timeout.Token);
            var password = new byte[length[0]];
            await stream.ReadExactlyAsync(password, timeout.Token);
            Require(Encoding.UTF8.GetString(username) == "fixture-user" && Encoding.UTF8.GetString(password) == "fixture-secret", "The private proxy credentials must reach SOCKS authentication.");
            await stream.WriteAsync(new byte[] { 1, 0 }, timeout.Token);
            var command = new byte[4];
            await stream.ReadExactlyAsync(command, timeout.Token);
            Require(command[0] == 5 && command[1] == 1 && command[3] == 1, "The bootstrap destination must be a numeric IPv4 SOCKS connection.");
            var destination = new byte[6];
            await stream.ReadExactlyAsync(destination, timeout.Token);
            Require(new IPAddress(destination[..4]).ToString() == "100.70.1.2" && destination[4] * 256 + destination[5] == TailscaleContract.DiscoveryPort,
                "Bootstrap HTTP must target the peer through the proxy, never an OS socket or local DNS.");
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, timeout.Token);
            var request = new StringBuilder();
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var one = new byte[1];
                await stream.ReadExactlyAsync(one, timeout.Token);
                request.Append((char)one[0]);
                Require(request.Length < 8192, "The discovery request must be bounded.");
            }
            Require(request.ToString().StartsWith("GET /sharprail ", StringComparison.Ordinal) && !request.ToString().Contains("fixture-secret", StringComparison.Ordinal),
                "Proxy credentials must not leak into the host HTTP request.");
            const string body = "{\"name\":\"SharpRail fixture\"}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), timeout.Token);
        }, timeout.Token);
        using (var client = new TailscaleClient(network))
        {
            var discovered = await client.DiscoverAsync(timeout.Token);
            Require(discovered.Count == 1 && discovered[0] == new TailscalePeer("SharpRail fixture", "100.70.1.2"), "Discovery must probe only eligible peers and keep their numeric embedded route.");
        }
        await probe;
        await network.LoginAsync(timeout.Token);
        await network.LogoutAsync(timeout.Token);
        Require(!(await network.RefreshAsync(timeout.Token)).Online, "Logout must clear connected state.");
        await network.DisposeAsync();
        await network.DisposeAsync();
        Require(operations.Count(operation => operation == "stop") == 1, "Disposal must stop the node once.");
        online = true;
        await using var firstActivity = new EmbeddedTailscaleNetwork(Path.Combine(root, "tailscale-node"), Native);
        await firstActivity.StartAsync(timeout.Token);
        await using var replacementActivity = new EmbeddedTailscaleNetwork(Path.Combine(root, "tailscale-node"), Native);
        await replacementActivity.StartAsync(timeout.Token);
        await firstActivity.DisposeAsync();
        Require((await replacementActivity.RefreshAsync(timeout.Token)).Online,
            "Disposing a previous activity must not stop the replacement activity's native singleton.");
        Require(EmbeddedTailscaleNetwork.LoginUrl("https://login.tailscale.com/a/test") is not null &&
            EmbeddedTailscaleNetwork.LoginUrl("http://login.tailscale.com/a/test") is null &&
            EmbeddedTailscaleNetwork.LoginUrl("https://tailscale.com.attacker.test/") is null,
            "Only HTTPS Tailscale authentication pages may launch in the browser.");
        Console.WriteLine("PASS embedded Tailscale: persistent node lifecycle, private netmap discovery and authenticated SOCKS HTTP routing without system VPN/DNS");
    }
}