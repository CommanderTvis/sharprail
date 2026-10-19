using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Tailscale.Host;

public sealed class TailscaleHost(TailscaleServing? serving = null) : PluginHostModule
{
    public override PluginContract Contract => TailscaleContract.Contract;
    internal TailscaleNetwork Network { get; init; } = new();
    internal int PairingPort { get; init; } = TailscaleContract.DiscoveryPort;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var registry = new PairingRegistry(context.ReadState("devices", Array.Empty<ApprovedDevice>()), devices => context.WriteState("devices", devices));
        var lifetime = new CancellationTokenSource();
        var hostName = (string?)null;
        var detail = "Checking Tailscale…";
        TailscaleStatus Status() => registry.Status(Volatile.Read(ref detail), Volatile.Read(ref hostName));
        void Publish() => context.Publish(TailscaleContract.Changed, Status());
        context.Method(TailscaleContract.Status, (_, _, _) => ValueTask.FromResult(Status()));
        context.Method(TailscaleContract.Decide, (request, _, _) => { registry.Decide(request.Id, request.Allow); Publish(); return ValueTask.FromResult(Status()); });
        context.Method(TailscaleContract.Revoke, (request, _, _) => { registry.Revoke(request.Id); Publish(); return ValueTask.FromResult(Status()); });
        var refresh = System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });
        context.Method(TailscaleContract.Refresh, (_, _, _) => { refresh.Writer.TryWrite(true); return ValueTask.FromResult(Status()); });
        if (serving is not null) serving.Authenticate = registry.Authenticate;
        var run = Task.Run(async () =>
        {
            if (serving?.EnsureListening is null) { detail = "This host does not expose Tailscale pairing."; Publish(); return; }
            // Detection runs again on every refresh request while unavailable, so connecting Tailscale later is seen without a restart.
            while (!lifetime.IsCancellationRequested)
            {
                refresh.Reader.TryRead(out _);
                if (await ServeAsync()) return;
                try { await refresh.Reader.WaitToReadAsync(lifetime.Token); }
                catch (OperationCanceledException) { return; }
                Volatile.Write(ref detail, "Checking Tailscale…");
                Publish();
            }
        });
        async Task<bool> ServeAsync()
        {
            try
            {
                var self = await Network.SelfAsync(lifetime.Token);
                var ip = self.Address;
                Volatile.Write(ref hostName, self.Name);
                using var listener = new HttpListener();
                listener.Prefixes.Add($"http://{ip}:{PairingPort}/");
                listener.Start();
                using var stop = lifetime.Token.Register(listener.Close);
                Volatile.Write(ref detail, "Ready to pair. Approve new devices below.");
                Publish();
                using var slots = new SemaphoreSlim(8);
                var requests = new List<Task>();
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var http = await listener.GetContextAsync().WaitAsync(lifetime.Token);
                        if (!slots.Wait(0)) { http.Response.StatusCode = 503; http.Response.Close(); continue; }
                        requests.RemoveAll(task => task.IsCompleted);
                        requests.Add(Task.Run(async () =>
                        {
                            try { await HandleAsync(http, ip, hostName ?? ip.ToString(), registry, serving, Network, lifetime.Token); Publish(); }
                            catch (Exception error) when (error is IOException or HttpListenerException or OperationCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or KeyNotFoundException or FormatException or ObjectDisposedException)
                            {
                                try { http.Response.StatusCode = 503; http.Response.Close(); } catch (Exception closeError) when (closeError is ObjectDisposedException or HttpListenerException or InvalidOperationException) { }
                                if (!lifetime.IsCancellationRequested) context.Log.Debug("Tailscale request failed: " + error.Message);
                            }
                            finally { slots.Release(); }
                        }, CancellationToken.None));
                    }
                }
                finally { listener.Close(); await Task.WhenAll(requests); }
                return true;
            }
            catch (Exception error) when (error is IOException or HttpListenerException or OperationCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or KeyNotFoundException or FormatException or ObjectDisposedException)
            {
                if (!lifetime.IsCancellationRequested) { Volatile.Write(ref detail, "Pairing unavailable: " + error.Message); Publish(); }
                return lifetime.IsCancellationRequested;
            }
        }
        return ValueTask.FromResult<PluginDisposer?>(async () =>
        {
            if (serving is not null) serving.Authenticate = null;
            await lifetime.CancelAsync();
            await run;
            if (serving?.StopListening is { } stop) await stop();
            lifetime.Dispose();
        });
    }

    private static async Task HandleAsync(HttpListenerContext http, IPAddress hostAddress, string hostName, PairingRegistry registry, TailscaleServing serving, TailscaleNetwork network, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var remote = http.Request.RemoteEndPoint?.Address;
        var identity = remote is null ? null : await network.PeerAsync(remote, timeout.Token);
        if (identity is null) { http.Response.StatusCode = 403; http.Response.Close(); return; }
        object response;
        if (http.Request.HttpMethod == "GET" && http.Request.Url?.AbsolutePath == "/sharprail") response = new TailscaleHostInfo(hostName);
        else if (http.Request.HttpMethod == "POST" && http.Request.Url?.AbsolutePath == "/sharprail/pair")
        {
            var token = http.Request.Headers["X-SharpRail-Pairing"] ?? "";
            var state = registry.Request(token, identity.Id, identity.Name, identity.User);
            var address = state == "approved" ? await serving.EnsureListening!(hostAddress.ToString(), timeout.Token) : null;
            response = new TailscalePairReply(state, address);
        }
        else { http.Response.StatusCode = 404; http.Response.Close(); return; }
        http.Response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, response.GetType(), PluginJson.Options);
        http.Response.ContentLength64 = bytes.Length;
        await http.Response.OutputStream.WriteAsync(bytes, timeout.Token);
        http.Response.Close();
    }
}