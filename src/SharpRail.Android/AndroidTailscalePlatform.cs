using System.Text.Json;

using Android.Content;
using Android.Net;

namespace SharpRail.UI.Android;

/// <summary>Supplies the supported Android connectivity snapshot instead of Go's restricted netlink query.</summary>
internal sealed class AndroidTailscalePlatform : IDisposable
{
    private readonly ConnectivityManager manager;
    private readonly Callback callback;
    private bool disposed;

    internal AndroidTailscalePlatform(EmbeddedTailscaleNetwork network)
    {
        manager = (ConnectivityManager)global::Android.App.Application.Context.GetSystemService(Context.ConnectivityService)!;
        callback = new(network);
        manager.RegisterDefaultNetworkCallback(callback);
    }

    internal static string Snapshot()
    {
        var manager = (ConnectivityManager)global::Android.App.Application.Context.GetSystemService(Context.ConnectivityService)!;
        using var active = manager.ActiveNetwork;
        using var link = active is null ? null : manager.GetLinkProperties(active);
        var name = link?.InterfaceName;
        if (string.IsNullOrEmpty(name)) return "{\"interfaces\":[],\"defaultInterface\":\"\",\"gateway\":\"\"}";
        var index = 1;
        try
        {
            using var device = Java.Net.NetworkInterface.GetByName(name);
            index = device?.Index ?? 1;
        }
        catch (Java.Net.SocketException) { }
        var addresses = (link!.LinkAddresses ?? []).Select(address => address.Address?.HostAddress is { } ip
            ? ip.Split('%')[0] + "/" + address.PrefixLength : "").Where(address => address.Length > 0).ToArray();
        var routes = link.Routes ?? [];
        var defaultRoute = routes.FirstOrDefault(route => route.IsDefaultRoute && route.Gateway is Java.Net.Inet4Address)
            ?? routes.FirstOrDefault(route => route.IsDefaultRoute);
        var gateway = defaultRoute?.Gateway?.HostAddress?.Split('%')[0] ?? "";
        return JsonSerializer.Serialize(new
        {
            interfaces = new[] { new { name, index, mtu = link.Mtu > 0 ? link.Mtu : 1500, addresses } },
            defaultInterface = name,
            gateway
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        callback.Stop();
        manager.UnregisterNetworkCallback(callback);
        callback.Dispose();
    }

    private sealed class Callback(EmbeddedTailscaleNetwork network) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => Changed();
        public override void OnLost(Network network) => Changed();
        public override void OnLinkPropertiesChanged(Network network, LinkProperties properties) => Changed();

        private readonly object updates = new();
        private bool pending;
        private bool running;
        private bool stopped;

        internal void Stop() { lock (updates) stopped = true; }

        private void Changed()
        {
            lock (updates)
            {
                if (stopped) return;
                pending = true;
                if (running) return;
                running = true;
            }
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    lock (updates)
                    {
                        if (stopped || !pending) { running = false; return; }
                        pending = false;
                    }
                    try { await network.UpdateNetworkAsync(CancellationToken.None); }
                    catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
                }
            });
        }
    }
}