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
        workbench = new Workbench(profile, state, factory, remote: true, sessions, plugins, catalog) { Endpoint = address.ToString(), Compact = compact };
        // The activity holds one workbench window; a profile never restores more.
        var slot = profile.Data.Windows[0];
        profile.Data.Windows.RemoveRange(1, profile.Data.Windows.Count - 1);
        workbench.Open(slot, slot.LastProject).Show();
    }

    private void Close()
    {
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