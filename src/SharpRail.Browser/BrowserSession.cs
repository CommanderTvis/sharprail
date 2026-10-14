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
        if (remembered is { Token.Length: > 0, Uri: not null }) connect.ConnectNow();
    }

    /// <summary>Leaves the host and forgets its token, keeping the address for the connect screen.</summary>
    public void Disconnect()
    {
        if (Endpoint is not { } endpoint) return;
        var forgotten = endpoint with { Token = "" };
        forgotten.Save(directory);
        Close();
        ShowConnect(forgotten);
    }

    /// <summary>Returns to the connect screen with this host still filled in and remembered.</summary>
    public void ChangeHost()
    {
        if (Endpoint is not { } endpoint) return;
        Close();
        ShowConnect(endpoint);
    }

    private ConnectWindow ShowConnect(HostEndpoint? remembered)
    {
        var connect = new ConnectWindow(remembered);
        connect.Connected += endpoint =>
        {
            endpoint.Save(directory);
            connect.Close();
            Open(endpoint);
        };
        connect.Show();
        return connect;
    }

    private void Open(HostEndpoint endpoint)
    {
        if (disposed) return;
        var address = endpoint.Uri!;
        var token = endpoint.Token;
        var profile = new ProfileStore(directory, () => BrowserInterop.Load("profile"), json => BrowserInterop.Save("profile", json));
        // Every adapter is the same client of the host as the state subscription.
        var connection = new HostConnection();
        var states = new RemoteStateAdapter(address, token, connection);
        var terminals = new RemoteTerminalAdapter(address, token);
        var plugins = new RemotePluginAdapter(address, token);
        var catalog = new RemoteTerminalCatalogAdapter(address, token);
        adapters.AddRange([states, terminals, plugins, catalog]);
        var state = new SharedState(states, profile.Data.Preferences, null, connection);
        Func<IProjectServices> sessions = () => new RemoteProjectAdapter(address, token, connection);
        TerminalFactory factory = launch => new BrowserTerminal(launch, terminals);
        Endpoint = endpoint;
        workbench = new Workbench(profile, state, factory, remote: true, sessions, plugins, catalog) { Endpoint = address.ToString(), RedactError = message => string.IsNullOrEmpty(token) ? message : message.Replace(token, "[redacted]", StringComparison.Ordinal).Replace(Uri.EscapeDataString(token), "[redacted]", StringComparison.Ordinal), ChangeHost = ChangeHost, Notifier = BrowserNotifier.Instance, FollowHostTerminalPlacement = true };
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
            Close();
            ShowConnect(endpoint).Report("The host at " + address.Authority + " stopped answering.");
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
    }

    public void Dispose()
    {
        disposed = true;
        Close();
        if (Current == this) Current = null;
    }
}