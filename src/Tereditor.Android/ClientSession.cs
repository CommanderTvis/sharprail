using Avalonia.Threading;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Client;
using Tereditor.Plugins.Tailscale;
using Tereditor.UI.State;
using Tereditor.UI.Terminal;

namespace Tereditor.UI.Android;

/// <summary>
/// What the activity shows: the connect screen until a host answers, then the workbench as that host's client.
/// The address and token are remembered in the app's private files, so the next launch connects by itself.
/// The workbench wears the phone pages or the desktop frame by the window's size, and swaps them as it changes.
/// </summary>
public sealed class ClientSession(string filesDirectory, ShellSwitch shell) : IDisposable
{
    private readonly string directory = Path.Combine(filesDirectory, ".tereditor");
    private readonly List<IDisposable> adapters = [];
    private readonly EmbeddedTailscaleNetwork tailscale = new(Path.Combine(filesDirectory, ".tereditor", "tailscale-node"), networkSnapshot: AndroidTailscalePlatform.Snapshot);
    private AndroidTailscalePlatform? networkMonitor;
    private IDisposable? tailscaleRoute;
    private Workbench? workbench;
    private ConnectWindow? connecting;
    private DispatcherTimer? watch;
    private int unreachable;
    private DispatcherTimer? deferred;
    private (double Width, double Height) size;

    /// <summary>
    /// Whether the app is on screen. Android cuts a background app's connections, so the host is given its
    /// full time to answer again from the moment the app returns.
    /// </summary>
    public bool Foreground
    {
        get;
        set { field = value; unreachable = 0; }
    } = true;
    // How long the host may stay unreachable, counted while the app runs, before the workbench gives way.
    private const int LostAfterSeconds = 45;
    private bool disposed;

    /// <summary>The running session, for the workbench's Host settings.</summary>
    public static ClientSession? Current { get; private set; }

    /// <summary>The connected host, or null while the connect screen shows.</summary>
    public HostEndpoint? Endpoint { get; private set; }

    public void Start()
    {
        Current = this;
        networkMonitor = new(tailscale);
        var remembered = HostEndpoint.Load(directory);
        var connect = ShowConnect(remembered);
        if (remembered is { Token.Length: > 0, Uri: not null }) connect.ConnectNow();
    }

    /// <summary>Leaves the host and forgets its token, keeping the address for the connect screen.</summary>
    public void Disconnect() => _ = DisconnectAsync();

    private async Task DisconnectAsync()
    {
        if (Endpoint is not { } endpoint) return;
        var forgotten = endpoint with { Token = "" };
        await Task.Run(() =>
        {
            forgotten.Save(directory);
            TailscaleClient.Forget(Path.Combine(directory, "tailscale-client.json"), endpoint.Token);
        });
        if (disposed || Endpoint != endpoint) return;
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
        var connect = new ConnectWindow(remembered, Path.Combine(directory, "tailscale-client.json"), tailscale);
        connecting = connect;
        connect.Connected += endpoint =>
        {
            if (disposed || connecting != connect || !connect.IsVisible) return;
            connecting = null;
            if (connect.UsesEmbeddedTailscale)
                tailscaleRoute = RpcTransports.RegisterTransport(endpoint.Uri!, tailscale.CreateHandler);
            endpoint.Save(directory);
            // One window fills the activity at a time: the connect screen leaves before the workbench is created.
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
        var fresh = !File.Exists(Path.Combine(directory, "profile.json"));
        var profile = new ProfileStore(directory);
        // A phone has room for the centre only: its first frame starts with the side and bottom panels hidden.
        if (fresh && shell.Compact)
        {
            profile.Data.Windows[0].Layout = Docking.DockState.Preset("focus");
            profile.Data.Windows[0].DefaultPreset = "focus";
        }
        // Every adapter is the same client of the host as the state subscription.
        var connection = new HostConnection();
        var states = new RemoteStateAdapter(address, token, connection);
        var terminals = new RemoteTerminalAdapter(address, token);
        var plugins = new RemotePluginAdapter(address, token);
        var catalog = new RemoteTerminalCatalogAdapter(address, token);
        adapters.AddRange([states, terminals, plugins, catalog]);
        var state = new SharedState(states, profile.Data.Preferences, null, connection);
        Func<IProjectServices> sessions = () => new RemoteProjectAdapter(address, token, connection);
        // Android draws terminals with Skia only; the host runs their shells.
        var factory = TerminalBackends.Ghostty(new RemoteTerminalConnection(address, token, terminals), () => TerminalRenderers.Skia);
        Endpoint = endpoint;
        // Sideways scrolling is a chore under a finger: lines wrap at the screen's edge whatever the line width says.
        Tereditor.Scintilla.ScintillaEditor.WrapAlways = true;
        workbench = new Workbench(profile, state, factory, remote: true, sessions, plugins, catalog) { Endpoint = address.ToString(), Compact = shell.Compact, Touch = true, ChangeHost = ChangeHost };
        // The activity holds one workbench window; a profile never restores more.
        var slot = profile.Data.Windows[0];
        profile.Data.Windows.RemoveRange(1, profile.Data.Windows.Count - 1);
        shell.Prepare(slot);
        workbench.Open(slot, slot.LastProject).Show();
        // The workbench retries a dropped host by itself; one that stays away sends the user back to the
        // connect screen, where the address can be corrected. Seconds are counted, not read from a clock,
        // so time the app spent suspended or in the background does not count against the host.
        unreachable = 0;
        watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watch.Tick += (_, _) =>
        {
            unreachable = state.Connected || !Foreground ? 0 : unreachable + 1;
            if (unreachable < LostAfterSeconds) return;
            Close();
            ShowConnect(endpoint).Report("The host at " + address.Authority + " stopped answering.");
        };
        watch.Start();
    }

    /// <summary>
    /// The activity's window changed size (a rotation, a fold, a desktop-mode window resized or moved to another
    /// display), in dp. Crossing the threshold reopens the workbench in the other shell; the layout, tabs and
    /// documents are the profile's and the host's, so the same workspace and tab come back.
    /// </summary>
    public void Resized(double width, double height)
    {
        size = (width, height);
        // Behind the connect screen there is nothing to rebuild; the workbench it opens takes the shell that fits.
        if (workbench is null) { shell.Fit(width, height); return; }
        if (disposed || workbench is not { } open || Endpoint is not { } endpoint || !shell.Wants(width, height)) return;
        // Closing the window would ask about unsaved edits and running agents; a resize must not, so try again later.
        if (ShellSwitch.Busy(open))
        {
            deferred ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            deferred.Tick -= RetryResize;
            deferred.Tick += RetryResize;
            deferred.Start();
            return;
        }
        deferred?.Stop();
        shell.Swap(open.Profile.Data.Windows[0]);
        // The route the connect screen registered for an embedded tailnet stays: the same host is reached again.
        Close(keepRoute: true);
        Open(endpoint);
    }

    private void RetryResize(object? sender, EventArgs e)
    {
        deferred?.Stop();
        Resized(size.Width, size.Height);
    }

    private void Close(bool keepRoute = false)
    {
        deferred?.Stop();
        connecting?.Close();
        connecting = null;
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
        if (keepRoute) return;
        tailscaleRoute?.Dispose();
        tailscaleRoute = null;
    }

    public void Dispose()
    {
        disposed = true;
        Close();
        networkMonitor?.Dispose();
        networkMonitor = null;
        _ = tailscale.DisposeAsync();
        if (Current == this) Current = null;
    }
}