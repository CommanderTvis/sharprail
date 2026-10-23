using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Tailscale;
using SharpRail.Plugins.Tailscale.Host;
using SharpRail.Plugins.Tailscale.UI;
using SharpRail.UI;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class TailscaleChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Run(string root)
    {
        Registry();
        Addresses();
        await BootstrapValidation();
        await ClientPolling();
        await Transport(Path.Combine(root, "tailscale"));
        await LateConnection(Path.Combine(root, "tailscale-late"));
        Console.WriteLine("PASS Tailscale pairing: identity, approval, expiry, bounds, persistence, revocation and local/remote authentication");
    }

    internal static void RunUi(string root)
    {
        var directory = Path.Combine(root, "tailscale-ui");
        Directory.CreateDirectory(directory);
        var profile = new ProfileStore(Path.Combine(directory, "profile"));
        var store = profile.OpenState();
        var roster = new PluginRosterEntry("tailscale", "Tailscale", "global-line", "0.1.0", 1, PluginOrigin.Builtin, PluginStatus.Disabled)
        {
            Channels = new Dictionary<string, PluginRosterChannel> { ["status"] = new(PluginChannelKind.State, "status", []) }
        };
        var fake = new FakePluginHost(new LocalStateAdapter(store), store.Current, roster);
        var device = new TailscaleDevice(new string('a', 64), "phone.tailnet.ts.net", "me@example.test", "A1B2C3D4");
        var pending = new TailscaleStatus("Ready to pair.", "host.tailnet.ts.net", [device], []);
        fake.Replies["status"] = pending;
        using var workbench = new Workbench(profile, new SharedState(fake, profile.Data.Preferences), E2eTerminals.Plain, false, () => new LocalProjectAdapter(new ProjectServices(directory, store)), fake);
        var window = workbench.Open(profile.Data.Windows[0], "");
        window.Width = 1200; window.Height = 800; window.Show();
        try
        {
            _ = workbench.State.ChangeAsync(HostStateChange.PluginEnabled("tailscale", true));
            E2eWorkspace.Until(() => workbench.PluginRegistry.Active.Contains("tailscale"));
            E2eWorkspace.Until(() => window.FindControl<TextBlock>("GestureToastMessage")!.Text?.Contains("Tailscale pairing approval requested", StringComparison.Ordinal) == true);
            Require(!window.OwnedWindows.OfType<SettingsWindow>().Any() &&
                window.FindControl<TextBlock>("GestureToastMessage")!.Text!.Contains(device.Code, StringComparison.Ordinal),
                "An incoming pairing shows its identity and comparison code even when Settings is closed.");
            window.ShowSettings("plugin:tailscale:tailscale");
            E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
            var settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
            T Find<T>(string name) where T : Control => settings.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
            E2eWorkspace.Until(() => Find<TextBlock>("PairingName").Text?.Contains("host.tailnet.ts.net", StringComparison.Ordinal) == true);
            E2eWorkspace.Until(() => fake.Subscriptions > 0 && settings.GetLogicalDescendants().OfType<Button>().Any(button => Equals(button.Content, "Approve")));
            Require(settings.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == device.Code), "The approval UI shows the comparison code.");
            var rows = Find<ItemsControl>("PairingPending").ItemsSource;
            window.Notify(false, "Unrelated notification");
            fake.Push("tailscale", "status", pending);
            E2eWorkspace.Settle(50);
            Require(ReferenceEquals(rows, Find<ItemsControl>("PairingPending").ItemsSource), "Unchanged polling retains approval controls and pointer targets.");
            Require(window.FindControl<TextBlock>("GestureToastMessage")!.Text == "Unrelated notification", "Repeated status polls do not repeat an approval popup.");
            var approved = pending with { Pending = [], Approved = [device] };
            fake.Replies["decide"] = approved;
            var approve = settings.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Approve"));
            approve.Command!.Execute(null);
            E2eWorkspace.Until(() => fake.Calls.Any(call => call.Method == "decide"));
            Require(fake.Calls.Last(call => call.Method == "decide").Params is TailscaleDecision { Id: var id, Allow: true } && id == device.Id,
                "Approval uses the matching pending request through plugin RPC.");
            E2eWorkspace.Until(() => settings.GetLogicalDescendants().OfType<Button>().Any(button => Equals(button.Content, "Revoke access")));
            fake.Replies["revoke"] = pending with { Pending = [], Approved = [] };
            settings.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Revoke access")).Command!.Execute(null);
            E2eWorkspace.Until(() => fake.Calls.Any(call => call.Method == "revoke"));
            settings.Close();
            E2eWorkspace.Until(() => fake.Subscriptions == 1);
            Require(fake.Subscriptions == 1, "The activation subscription continues to surface approvals with Settings closed.");
            var another = device with { Id = new string('b', 64), Code = "B2C3D4E5" };
            fake.Push("tailscale", "status", pending with { Pending = [another], Approved = [] });
            E2eWorkspace.Until(() => window.FindControl<TextBlock>("GestureToastMessage")!.Text?.Contains(another.Code, StringComparison.Ordinal) == true);
            var picker = new TailscaleConnectView(Path.Combine(directory, "client.json"));
            var pickerWindow = new Window { Content = picker, Width = 460, Height = 540 };
            try
            {
                pickerWindow.Show();
                Require(picker.FindControl<Button>("TailscalePair")!.MinHeight == 40 &&
                    picker.FindControl<Button>("TailscalePair")!.HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Center &&
                    picker.FindControl<Button>("TailscalePair")!.VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Center,
                    "Pairing buttons share the Android connect action size and centered text.");
                picker.FindControl<TextBox>("TailscaleHost")!.Text = "127.0.0.1";
                picker.FindControl<Button>("TailscalePair")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                E2eWorkspace.Until(() => picker.FindControl<TextBlock>("TailscaleProgress")!.Text?.StartsWith("Could not pair", StringComparison.Ordinal) == true);
                Require(picker.FindControl<Button>("TailscalePair")!.IsEnabled && !File.Exists(Path.Combine(directory, "client.json")),
                    "Invalid hosts leave the picker usable and never save credentials.");
            }
            finally { pickerWindow.Close(); }
            var rememberedPath = Path.Combine(directory, "remembered-picker.json");
            var rememberedPairing = TailscaleClient.NewPairing("saved.tailnet.ts.net");
            TailscaleClient.Save(rememberedPath, rememberedPairing);
            var privateNetwork = new PickerNetwork();
            var choose = new TailscaleConnectView(rememberedPath, network: privateNetwork, restorePairing: false);
            TailscaleConnection? chosen = null;
            choose.Connected += connection => chosen = connection;
            var chooseWindow = new Window { Content = choose, Width = 460, Height = 540 };
            try
            {
                chooseWindow.Show();
                E2eWorkspace.Until(() => choose.FindControl<TextBlock>("TailscaleProgress")!.Text?.StartsWith("No hosts found", StringComparison.Ordinal) == true);
                Require(privateNetwork.BootstrapCalls == 0 && chosen is null &&
                    choose.FindControl<TextBox>("TailscaleHost")!.Text == rememberedPairing.Host,
                    "Opening the host chooser must permit changing host or signing out instead of immediately reconnecting the remembered host.");
                choose.FindControl<Button>("TailscalePair")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                E2eWorkspace.Until(() => chosen is not null);
                Require(privateNetwork.BootstrapCalls == 1 && chosen!.Token == rememberedPairing.Token,
                    "Explicitly connecting the remembered host reuses its own approved pairing secret through the injected network.");
            }
            finally { chooseWindow.Close(); }
            foreach (var (peers, expected, text) in new (TailscalePeer[], string, string)[]
            {
                ([new("other", "100.70.1.5"), new("saved", "100.70.1.9")], "saved", rememberedPairing.Host),
                ([new("alpha", "100.70.1.5"), new("beta", "100.70.1.6")], "alpha", rememberedPairing.Host),
                ([new("solo", "100.70.1.7")], "solo", rememberedPairing.Host)
            })
            {
                var defaulted = new TailscaleConnectView(rememberedPath, network: new PickerNetwork { Peers = peers }, restorePairing: false);
                var defaultedWindow = new Window { Content = defaulted, Width = 460, Height = 540 };
                try
                {
                    defaultedWindow.Show();
                    E2eWorkspace.Until(() => defaulted.FindControl<TextBlock>("TailscaleProgress")!.Text?.StartsWith("Found", StringComparison.Ordinal) == true);
                    var selected = defaulted.FindControl<ComboBox>("TailscaleHosts")!.SelectedItem as TailscalePeer;
                    Require(selected?.Name == expected && !defaulted.FindControl<TextBox>("TailscaleHost")!.IsVisible && defaulted.FindControl<Button>("TailscaleManual")!.IsVisible &&
                        (expected == "saved" ? defaulted.FindControl<TextBox>("TailscaleHost")!.Text == text : defaulted.FindControl<TextBox>("TailscaleHost")!.Text == selected!.Address),
                        "Discovery selects the remembered peer (kept as is), else the first peer, and the embedded picker hides the host-name field behind manual entry.");
                }
                finally { defaultedWindow.Close(); }
            }
        }
        finally { window.Close(); }
        Console.WriteLine("PASS Tailscale UI: bootstrap picker, approval identity/code, RPC commands, retained rows and subscription teardown");
    }

    private static void Registry()
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<ApprovedDevice> persisted = [];
        var registry = new PairingRegistry([], next => persisted = next, () => now);
        var token = TailscaleClient.NewPairing("host").Token;
        Require(registry.Request("bad", "node", "phone", "me") == "invalid", "Malformed pairing tokens are refused.");
        Require(registry.Request(token, "node", "phone", "me") == "pending" && !registry.Authenticate(token), "New devices never authenticate before approval.");
        Require(registry.Request(token, "other-node", "impostor", "other") == "denied", "A pending token cannot move to another Tailscale node.");
        var pending = registry.Status("ready", "host").Pending.Single();
        Require(pending.Code == PairingRegistry.Hash(token)[..8].ToUpperInvariant(), "Host and client compare the token fingerprint.");
        registry.Decide(pending.Id, true);
        Require(registry.Authenticate(token), "Approval grants the client token.");
        Require(registry.Request(token, "other-node", "impostor", "other") == "denied", "Remembered pairing is bound to its original node.");
        var restarted = new PairingRegistry(persisted, next => persisted = next);
        Require(restarted.Authenticate(token), "The grant survives host restart.");
        var secondToken = TailscaleClient.NewPairing("host").Token;
        restarted.Request(secondToken, "node", "renamed-phone", "me");
        restarted.Decide(PairingRegistry.Hash(secondToken), true);
        Require(restarted.Status("ready", "host").Approved.Count == 1 && restarted.Authenticate(token) && restarted.Authenticate(secondToken),
            "A second approved client secret on the same verified node keeps existing sessions and displays one device.");
        restarted = new PairingRegistry(persisted, next => persisted = next);
        Require(restarted.Status("ready", "host").Approved.Count == 1, "Persisted duplicate device grants also display one device after restart.");
        Require(!JsonSerializer.Serialize(persisted).Contains(token, StringComparison.Ordinal), "Host state stores hashes, never client secrets.");
        restarted.Revoke(pending.Id);
        Require(!restarted.Authenticate(token) && !restarted.Authenticate(secondToken) && persisted.Count == 0, "Revoking the device removes every persisted grant for that node.");
        var expiring = TailscaleClient.NewPairing("host").Token;
        registry.Request(expiring, "node", "phone", "me");
        now = now.AddMinutes(3);
        Require(registry.Status("ready", "host").Pending.Count == 0, "Unapproved requests expire.");
        try { registry.Decide(PairingRegistry.Hash(expiring), true); throw new Exception("Expired approval succeeded."); }
        catch (InvalidOperationException) { }
        registry.Request(expiring, "node", "phone", "me");
        registry.Decide(PairingRegistry.Hash(expiring), false);
        Require(registry.Request(expiring, "node", "phone", "me") == "denied", "Declining does not recreate the request on the next poll.");
        var bounded = new PairingRegistry([], _ => { });
        for (var index = 0; index < 32; index++) bounded.Request(TailscaleClient.NewPairing("host").Token, "node", "phone", "me");
        Require(bounded.Request(TailscaleClient.NewPairing("host").Token, "node", "phone", "me") == "busy", "Pending requests have a hard bound.");
    }

    private static void Addresses()
    {
        foreach (var address in new[] { "100.64.0.1", "100.127.255.254", "fd7a:115c:a1e0::1" })
            Require(TailscaleCli.IsTailnetAddress(IPAddress.Parse(address)), "Tailnet addresses are recognized.");
        foreach (var address in new[] { "127.0.0.1", "192.168.1.2", "100.63.255.255", "100.128.0.1", "::1", "fd7a:115c:a1e1::1" })
            Require(!TailscaleCli.IsTailnetAddress(IPAddress.Parse(address)), "Non-tailnet addresses are excluded.");
        using var status = JsonDocument.Parse("""{"Peer":{"a":{"Online":true,"DNSName":"host.tailnet.ts.net.","TailscaleIPs":["100.70.1.2"]},"b":{"Online":false,"DNSName":"offline.","TailscaleIPs":["100.70.1.3"]},"c":{"Online":true,"DNSName":"lan.","TailscaleIPs":["192.168.1.2"]}}}""");
        Require(TailscaleCli.Peers(status.RootElement).SequenceEqual([new TailscalePeer("host.tailnet.ts.net", "100.70.1.2")]), "Discovery probes only online tailnet peers.");
    }

    private static async Task BootstrapValidation()
    {
        foreach (var host in new[] { "127.0.0.1", "192.168.1.2", "example.com/path", "host:123", "user@host", "host?query" })
        {
            try { await TailscaleClient.BootstrapAsync(host, CancellationToken.None); throw new Exception("Unsafe bootstrap address accepted: " + host); }
            catch (IOException) { }
        }
        try { await TailscaleClient.BootstrapAsync("sharprail-pairing-test.invalid", CancellationToken.None); throw new Exception("Unknown bootstrap hostname accepted."); }
        catch (IOException error)
        {
            Require(error.InnerException is SocketException && error.Message.Contains("Turn on Tailscale", StringComparison.Ordinal) &&
                error.Message.Contains("host name and MagicDNS", StringComparison.Ordinal), "DNS failures explain connection checks without asserting Tailscale is definitely off.");
        }
    }

    private sealed class PairingHandler(Func<int, TailscalePairReply> reply) : HttpMessageHandler
    {
        public readonly List<string> Tokens = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/sharprail/pair", "The client uses the bootstrap pairing route.");
            Tokens.Add(request.Headers.GetValues("X-SharpRail-Pairing").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply(Tokens.Count), options: PluginJson.Options) });
        }
    }

    private sealed class PickerHandler(PickerNetwork network) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker pairing = new(new PairingHandler(_ => new("approved", "http://100.70.1.2:54123/")));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/sharprail" &&
                network.Peers.FirstOrDefault(peer => peer.Address == request.RequestUri.Host) is { } found)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new TailscaleHostInfo(found.Name), options: PluginJson.Options) });
            return pairing.SendAsync(request, cancellationToken);
        }
    }

    private sealed class PickerNetwork : ITailscaleClientNetwork
    {
        public int BootstrapCalls;
        public IReadOnlyList<TailscalePeer> Peers = [];
        public HttpMessageHandler CreateHandler() => new PickerHandler(this);
        public Task<Uri> BootstrapAsync(string host, CancellationToken cancellationToken)
        {
            BootstrapCalls++;
            return Task.FromResult(new Uri(Peers.Any(peer => peer.Address == host) ? "http://" + host + ":54124/" : "http://100.70.1.2:54124/"));
        }
        public Task<IReadOnlyList<TailscalePeer>> PeersAsync(CancellationToken cancellationToken) => Task.FromResult(Peers);
    }

    private static async Task ClientPolling()
    {
        var pairing = TailscaleClient.NewPairing("host");
        var handler = new PairingHandler(attempt => attempt == 1 ? new("pending") : new("approved", "http://100.70.1.2:60000/"));
        using var client = new TailscaleClient(handler, (_, _) => Task.FromResult(new Uri("http://100.70.1.2:54124/")));
        string? progress = null;
        var connected = await client.PairAsync(pairing, text => progress = text, CancellationToken.None);
        Require(connected == new TailscaleConnection("http://100.70.1.2:60000/", pairing.Token) && handler.Tokens.SequenceEqual([pairing.Token, pairing.Token]),
            "The client polls with one secret and follows the approved listener's actual port.");
        Require(progress?.Contains(PairingRegistry.Hash(pairing.Token)[..8].ToUpperInvariant(), StringComparison.Ordinal) == true,
            "The client displays the host's comparison fingerprint.");
        foreach (var address in new[] { "http://192.168.1.2:1234", "http://100.70.1.3:1234", "https://100.70.1.2:1234", "http://100.70.1.2:1234/path" })
        {
            using var invalid = new TailscaleClient(new PairingHandler(_ => new("approved", address)), (_, _) => Task.FromResult(new Uri("http://100.70.1.2:54124/")));
            try { await invalid.PairAsync(pairing, null, CancellationToken.None); throw new Exception("Invalid approval endpoint accepted: " + address); }
            catch (IOException) { }
        }
        using var cancellation = new CancellationTokenSource();
        using var waiting = new TailscaleClient(new PairingHandler(_ => { cancellation.Cancel(); return new("pending"); }), (_, _) => Task.FromResult(new Uri("http://100.70.1.2:54124/")));
        try { await waiting.PairAsync(pairing, null, cancellation.Token); throw new Exception("Pairing ignored cancellation."); }
        catch (OperationCanceledException) { }
    }

    private sealed class Network : TailscaleNetwork
    {
        public bool Allow = true;
        public string NodeId = "phone-node";
        internal override Task<TailscaleLocal> SelfAsync(CancellationToken cancellationToken) => Task.FromResult(new TailscaleLocal(IPAddress.Loopback, "fixture.tailnet.ts.net"));
        internal override Task<TailscaleIdentity?> PeerAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(Allow ? new TailscaleIdentity(NodeId, "phone.tailnet.ts.net", "fixture@example.test") : null);
    }

    private sealed class FlippingNetwork : TailscaleNetwork
    {
        public volatile bool Connected;
        public int Detections;
        internal override Task<TailscaleLocal> SelfAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Detections);
            return Connected ? Task.FromResult(new TailscaleLocal(IPAddress.Loopback, "late.tailnet.ts.net")) : throw new IOException("Connect this host to Tailscale first.");
        }
    }

    private static async Task LateConnection(string directory)
    {
        Directory.CreateDirectory(directory);
        using var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        var port = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();
        var state = new HostStateStore(Path.Combine(directory, "state"));
        var bridge = new TailscaleServing();
        var network = new FlippingNetwork();
        await using var runtime = new PluginRuntime(new()
        {
            StateDirectory = Path.Combine(directory, "state"),
            State = state,
            Builtins = [(TailscaleContract.Manifest with { EnabledByDefault = true }, new TailscaleHost(bridge) { Network = network, PairingPort = port })]
        });
        await using var listener = new HostListener(directory, state, null, runtime);
        listener.ConfigureTailscale(bridge);
        await runtime.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task<TailscaleStatus> Call(PluginMethod<TailscaleStatusParams, TailscaleStatus> method) =>
            PluginJson.Convert<TailscaleStatus>(await runtime.CallAsync(new("tailscale", method.Name, new TailscaleStatusParams(), "fixture"), timeout.Token));
        async Task Until(Func<TailscaleStatus, bool> done, PluginMethod<TailscaleStatusParams, TailscaleStatus> method)
        {
            while (!done(await Call(method))) await Task.Delay(20, timeout.Token);
        }
        await Until(TailscaleContract.IsUnavailable, TailscaleContract.Status);
        var detections = network.Detections;
        await Task.Delay(200, timeout.Token);
        Require(network.Detections == detections, "An unavailable host does not poll Tailscale by itself.");
        await Until(TailscaleContract.IsUnavailable, TailscaleContract.Refresh);
        Require((await Call(TailscaleContract.Status)).HostName is null, "Refreshing while still disconnected stays unavailable.");
        network.Connected = true;
        await Until(status => status.Detail.StartsWith("Ready", StringComparison.Ordinal), TailscaleContract.Refresh);
        Require((await Call(TailscaleContract.Status)).HostName == "late.tailnet.ts.net", "Connecting after launch makes pairing available after a refresh.");
    }

    private static async Task Transport(string directory)
    {
        Directory.CreateDirectory(directory);
        using var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        var bootstrapPort = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();
        var state = new HostStateStore(Path.Combine(directory, "state"));
        var bridge = new TailscaleServing();
        var network = new Network();
        var module = new TailscaleHost(bridge) { Network = network, PairingPort = bootstrapPort };
        await using var runtime = new PluginRuntime(new()
        {
            StateDirectory = Path.Combine(directory, "state"),
            State = state,
            Builtins = [(TailscaleContract.Manifest with { EnabledByDefault = true }, module)]
        });
        await using var listener = new HostListener(directory, state, null, runtime);
        listener.ConfigureTailscale(bridge);
        await runtime.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task<TailscaleStatus> Status() => PluginJson.Convert<TailscaleStatus>(await runtime.CallAsync(new("tailscale", "status", new TailscaleStatusParams(), "fixture"), timeout.Token));
        while (!(await Status()).Detail.StartsWith("Ready", StringComparison.Ordinal)) await Task.Delay(20, timeout.Token);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{bootstrapPort}/") };
        var discovery = await http.GetStringAsync("sharprail", timeout.Token);
        Require(discovery.Contains("fixture.tailnet.ts.net", StringComparison.Ordinal) && !discovery.Contains("token", StringComparison.OrdinalIgnoreCase), "Discovery reveals only the host name.");
        network.Allow = false;
        using (var denied = await http.GetAsync("sharprail", timeout.Token)) Require(denied.StatusCode == HttpStatusCode.Forbidden, "Unverified identities cannot even discover the host.");
        network.Allow = true;
        var token = TailscaleClient.NewPairing("fixture").Token;
        async Task<TailscalePairReply> Pair()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "sharprail/pair");
            request.Headers.Add("X-SharpRail-Pairing", token);
            using var response = await http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<TailscalePairReply>(PluginJson.Options, timeout.Token))!;
        }
        var waiting = await Pair();
        Require(waiting is { State: "pending", Address: null } && listener.Endpoint is null, "Pending requests reveal no endpoint and do not start serving.");
        var device = (await Status()).Pending.Single();
        await runtime.CallAsync(new("tailscale", "decide", new TailscaleDecision(device.Id, true), "fixture"), timeout.Token);
        var ready = await Pair();
        Require(ready.State == "approved" && ready.Address == listener.Endpoint!.ToString(), "Approval starts the embedded listener and returns its actual port.");
        var firstAddress = ready.Address;
        await state.ChangeAsync([HostStateChange.PluginEnabled("tailscale", false)], timeout.Token);
        while ((await runtime.ListAsync(timeout.Token)).Single().Status != PluginStatus.Disabled) await Task.Delay(20, timeout.Token);
        Require(bridge.Authenticate is null && listener.Endpoint is null, "Stopping the plugin drops its authentication and listener.");
        using var previousPort = new TcpListener(IPAddress.Loopback, new Uri(firstAddress!).Port);
        previousPort.Start();
        await state.ChangeAsync([HostStateChange.PluginEnabled("tailscale", true)], timeout.Token);
        while ((await runtime.ListAsync(timeout.Token)).Single().Status != PluginStatus.Active || !(await Status()).Detail.StartsWith("Ready", StringComparison.Ordinal)) await Task.Delay(20, timeout.Token);
        ready = await Pair();
        Require(ready.State == "approved" && (await Status()).Pending.Count == 0 && ready.Address != firstAddress,
            "Reactivation restores the grant without approval and advertises the new ephemeral port.");
        previousPort.Stop();
        using var paired = new RemoteStateAdapter(new Uri(ready.Address!), token);
        await paired.GetHandshakeAsync(timeout.Token);
        using var ordinary = new RemoteStateAdapter(new Uri(ready.Address!), listener.Token);
        await ordinary.GetHandshakeAsync(timeout.Token);
        using var remotePlugins = new RemotePluginAdapter(new Uri(ready.Address!), listener.Token);
        Require(PluginJson.Convert<TailscaleStatus>(await remotePlugins.CallAsync(new("tailscale", "status", new TailscaleStatusParams(), "remote"), timeout.Token)).Approved.Single().Id == device.Id,
            "Approved devices have local/remote plugin parity.");
        Require((await paired.GetStateAsync(timeout.Token)).ConnectedThroughTailscale && !(await ordinary.GetStateAsync(timeout.Token)).ConnectedThroughTailscale,
            "The host tags a Tailscale-paired connection, and only that one, as depending on Tailscale.");
        try { await paired.ChangeAsync([HostStateChange.PluginEnabled("tailscale", false)], timeout.Token); throw new Exception("A Tailscale client turned Tailscale off."); }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition && error.Message == TailscaleDependence.Refusal) { }
        try { await paired.ChangeAsync([HostStateChange.PluginSettings("tailscale", null)], timeout.Token); throw new Exception("A Tailscale client reset Tailscale off."); }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition) { }
        Require((await runtime.ListAsync(timeout.Token)).Single().Status == PluginStatus.Active, "A refused disable leaves Tailscale running.");
        Require(TailscaleDependence.Protected([new PluginRosterEntry("tailscale", "Tailscale", "", "1", 1, PluginOrigin.Builtin, PluginStatus.Active) { DependsOn = ["net"] },
            new PluginRosterEntry("net", "Net", "", "1", 1, PluginOrigin.Builtin, PluginStatus.Active) { DependsOn = ["base"] }]).SetEquals(["tailscale", "net", "base"])
            && TailscaleDependence.TurnsOff(HostStateChange.PluginEnabled("base", false), [new PluginRosterEntry("tailscale", "Tailscale", "", "1", 1, PluginOrigin.Builtin, PluginStatus.Active) { DependsOn = ["base"] }])
            && !TailscaleDependence.TurnsOff(HostStateChange.PluginEnabled("other", false), [])
            && !TailscaleDependence.TurnsOff(HostStateChange.PluginEnabled("tailscale", true), []),
            "Tailscale's dependencies, transitively, get the same guard; enabling and unrelated plugins are not guarded.");
        network.NodeId = "different-node";
        Require((await Pair()).State == "denied", "Another verified node cannot reuse a remembered pairing secret.");
        network.NodeId = "phone-node";
        await runtime.CallAsync(new("tailscale", "revoke", new TailscaleRevoke(device.Id), "fixture"), timeout.Token);
        try { await paired.GetHandshakeAsync(timeout.Token); throw new Exception("Revoked token authenticated."); }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated) { }
        var freshToken = TailscaleClient.NewPairing("fixture").Token;
        using (var request = new HttpRequestMessage(HttpMethod.Post, "sharprail/pair"))
        {
            request.Headers.Add("X-SharpRail-Pairing", freshToken);
            using var reply = await http.SendAsync(request, timeout.Token);
            reply.EnsureSuccessStatusCode();
        }
        var freshId = PairingRegistry.Hash(freshToken);
        await runtime.CallAsync(new("tailscale", "decide", new TailscaleDecision(freshId, true), "fixture"), timeout.Token);
        using var streaming = new RemoteStateAdapter(new Uri(ready.Address!), freshToken);
        await using var frames = streaming.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Require(await frames.MoveNextAsync(), "Approved clients can stream state.");
        var nextFrame = frames.MoveNextAsync().AsTask();
        await runtime.CallAsync(new("tailscale", "revoke", new TailscaleRevoke(freshId), "fixture"), timeout.Token);
        try { await nextFrame.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Revoked stream remained open."); }
        catch (RpcException) { }
        // A client connected another way may still turn Tailscale off.
        await ordinary.ChangeAsync([HostStateChange.PluginEnabled("tailscale", false)], timeout.Token);
        ordinary.Dispose();
        // The listener drains the requesting client's own connection before it reports stopped.
        while ((await runtime.ListAsync(timeout.Token)).Single().Status != PluginStatus.Disabled || listener.Endpoint is not null) await Task.Delay(20, timeout.Token);
        Require(bridge.Authenticate is null && listener.Endpoint is null, "Disabling pairing drops authentication and its owned listener.");
        var clientPath = Path.Combine(directory, "client.json");
        var remembered = new TailscaleRemembered("fixture", token);
        TailscaleClient.Save(clientPath, remembered);
        Require(TailscaleClient.Load(clientPath) == remembered, "The client remembers host name and its own token.");
        if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(clientPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Client credentials are private to the OS user.");
        TailscaleClient.Forget(clientPath, "different-token");
        Require(TailscaleClient.Load(clientPath) == remembered, "Forgetting another connection preserves this pairing.");
        TailscaleClient.Forget(clientPath, token);
        Require(TailscaleClient.Load(clientPath) is null, "Disconnect forgets the matching pairing secret.");
    }
}