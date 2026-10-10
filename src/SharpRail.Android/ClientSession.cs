using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.UI.State;
using SharpRail.UI.Terminal;

namespace SharpRail.UI.Android;

/// <summary>
/// What the activity shows: the connect screen until a host answers, then the workbench as that host's client.
/// The address and token are remembered in the app's private files, so the next launch connects by itself.
/// </summary>
public sealed class ClientSession(string filesDirectory, bool compact) : IDisposable
{
    private readonly string directory = Path.Combine(filesDirectory, ".sharprail");
    private readonly List<IDisposable> adapters = [];
    private Workbench? workbench;
    private DispatcherTimer? watch;
    private int unreachable;

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
        var remembered = HostEndpoint.Load(directory);
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
        if (fresh && compact)
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
        SharpRail.Scintilla.ScintillaEditor.WrapAlways = true;
        workbench = new Workbench(profile, state, factory, remote: true, sessions, plugins, catalog) { Endpoint = address.ToString(), Compact = compact, TabsInProjects = true, Touch = true, ChangeHost = ChangeHost };
        // The activity holds one workbench window; a profile never restores more.
        var slot = profile.Data.Windows[0];
        profile.Data.Windows.RemoveRange(1, profile.Data.Windows.Count - 1);
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