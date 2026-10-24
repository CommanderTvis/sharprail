using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.UI.State;
using SharpRail.UI.Terminal;

namespace SharpRail.UI.Browser;

/// <summary>Connects the browser workbench to a host and remembers its client-local view state.</summary>
public sealed class BrowserSession() : IDisposable
{
    private readonly string directory = "/sharprail";
    private readonly List<IDisposable> adapters = [];
    private IDisposable? route;
    private Workbench? workbench;
    internal Workbench? Workbench => workbench;
    private DispatcherTimer? watch;
    private int unreachable;

    private const int LostAfterSeconds = 45;
    private bool disposed;

    /// <summary>The running session, for the workbench's Host settings.</summary>
    public static BrowserSession? Current { get; private set; }

    /// <summary>The connected host, or null while the connect screen shows.</summary>
    public HostEndpoint? Endpoint { get; private set; }

    public void Start()
    {
        Current = this;
        var remembered = HostEndpoint.Load(directory) ?? new HostEndpoint(BrowserInterop.Origin(), "");
        var connect = ShowConnect(remembered);
        if (remembered is { Token.Length: > 0, Uri: not null }) connect.ConnectNow(remembered.Tailnet);
    }

    /// <summary>Leaves the host and forgets its token, keeping the address for the connect screen.</summary>
    public void Disconnect()
    {
        if (Endpoint is not { } endpoint) return;
        var forgotten = endpoint with { Token = "" };
        forgotten.Save(directory);
        // As on Android, disconnecting from a tailnet host also forgets that pairing secret.
        if (endpoint.Tailnet) _ = BrowserTailscaleView.ForgetPairingAsync();
        ReturnToConnect(forgotten);
    }

    /// <summary>Returns to the connect screen with this host still filled in and remembered.</summary>
    public void ChangeHost()
    {
        if (Endpoint is not { } endpoint) return;
        ReturnToConnect(endpoint);
    }

    private ConnectWindow ShowConnect(HostEndpoint? remembered)
    {
        var connect = new ConnectWindow(remembered);
        connect.Connected += endpoint =>
        {
            try
            {
                endpoint.Save(directory);
                Open(endpoint);
                connect.Close();
            }
            catch (Exception fault)
            {
                Close();
                connect.Report("The workbench could not open: " + fault.Message);
                Console.Error.WriteLine(fault);
            }
        };
        connect.Show();
        return connect;
    }

    private void ReturnToConnect(HostEndpoint endpoint, string? message = null)
    {
        var connect = ShowConnect(endpoint);
        Close();
        if (message is not null) connect.Report(message);
    }

    private void Open(HostEndpoint endpoint)
    {
        if (disposed) return;
        var address = endpoint.Uri!;
        var token = endpoint.Token;
        // Registered before any adapter creates a channel and removed after they close.
        if (endpoint.Tailnet) route = RpcTransports.RegisterStreamTransport(address, cancellation => BrowserTailscaleNetwork.DialAsync(address, cancellation));
        var profile = new ProfileStore(directory, () => BrowserInterop.Load("profile"), json => BrowserInterop.Save("profile", json));
        // Every adapter is the same client of the host as the state subscription.
        var connection = new HostConnection();
        var states = new RemoteStateAdapter(address, token, connection);
        var terminals = new RemoteTerminalAdapter(address, token);
        var plugins = new RemotePluginAdapter(address, token);
        var catalog = new RemoteTerminalCatalogAdapter(address, token);
        adapters.AddRange([states, terminals, plugins, catalog]);
        var state = new SharedState(states, profile.Data.Preferences, null, connection);
        state.ConnectionFailed += fault =>
        {
            if (!endpoint.LocalApproval || !RemoteErrors.IsUnauthenticated(fault)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (disposed || !ReferenceEquals(workbench?.State, state)) return;
                var forgotten = endpoint with { Token = "", LocalApproval = false };
                forgotten.Save(directory);
                ReturnToConnect(forgotten, "Browser access expired or was revoked. Connect again to request approval in SharpRail.");
            });
        };
        Func<IProjectServices> sessions = () => new RemoteProjectAdapter(address, token, connection);
        TerminalFactory factory = launch => new BrowserTerminal(launch, terminals);
        Endpoint = endpoint;
        workbench = new Workbench(profile, state, factory, remote: true, sessions, plugins, catalog) { Endpoint = address.ToString(), RedactError = message => string.IsNullOrEmpty(token) ? message : message.Replace(token, "[redacted]", StringComparison.Ordinal).Replace(Uri.EscapeDataString(token), "[redacted]", StringComparison.Ordinal), ChangeHost = ChangeHost, Notifier = BrowserNotifier.Instance };
        workbench.WindowOpened += window => window.OpenLink = BrowserInterop.OpenLink;
        foreach (var entry in profile.Data.Windows.Take(1)) workbench.Open(entry, entry.LastProject).Show();
        // The workbench retries a dropped host by itself; one that stays away sends the user back to the
        // connect screen, where the address can be corrected. Seconds are counted, not read from a clock,
        // so time the app spent suspended or in the background does not count against the host.
        unreachable = 0;
        watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watch.Tick += (_, _) =>
        {
            unreachable = state.Connected || !BrowserInterop.IsVisible() ? 0 : unreachable + 1;
            if (unreachable < LostAfterSeconds) return;
            ReturnToConnect(endpoint, "The host at " + address.Authority + " stopped answering.");
        };
        watch.Start();
    }

    private void Close()
    {
        watch?.Stop();
        watch = null;
        Endpoint = null;
        if (workbench is { } open)
        {
            open.ShuttingDown = true;
            foreach (var window in open.Windows.ToArray()) window.Close();
        }
        workbench = null;
        foreach (var adapter in adapters) adapter.Dispose();
        adapters.Clear();
        route?.Dispose();
        route = null;
    }

    public void Dispose()
    {
        disposed = true;
        Close();
        if (Current == this) Current = null;
    }
}