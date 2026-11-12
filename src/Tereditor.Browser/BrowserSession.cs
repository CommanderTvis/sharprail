using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Client;
using Tereditor.UI.State;
using Tereditor.UI.Terminal;

namespace Tereditor.UI.Browser;

/// <summary>Connects the browser workbench to a host and remembers its client-local view state.</summary>
public sealed class BrowserSession() : IDisposable
{
    private readonly string directory = "/tereditor";
    private readonly List<IDisposable> adapters = [];
    private IDisposable? route;
    private Workbench? workbench;
    internal Workbench? Workbench => workbench;
    private DispatcherTimer? watch;
    private int unreachable;
    private bool phone;
    private DispatcherTimer? deferred;

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
        phone = PhoneLayout.Read(false);
        var connect = ShowConnect(remembered);
        if (remembered is { Tab: true }) connect.OpenTab();
        else if (remembered is { Token.Length: > 0, Uri: not null }) connect.ConnectNow(remembered.Tailnet);
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
                if (endpoint.Tab) { _ = OpenTabAsync(endpoint, connect); return; }
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

    private TabHost? tab;

    private async Task OpenTabAsync(HostEndpoint endpoint, ConnectWindow connect)
    {
        try
        {
            await TabGit.StartAsync("/tereditor", "/workspace", "/home");
            await TabHost.EnsureProjectAsync();
            if (disposed) return;
            OpenTab(endpoint);
            _ = tab!.EnableDefaultsAsync();
            connect.Close();
        }
        catch (Exception fault)
        {
            Close();
            connect.Report("The tab host could not start: " + fault.Message);
            Console.Error.WriteLine(fault);
        }
    }

    private void OpenTab(HostEndpoint endpoint)
    {
        phone = PhoneLayout.Read(phone);
        var profile = new ProfileStore(directory, () => BrowserInterop.Load("profile"), json => BrowserInterop.Save("profile", json));
        tab ??= new TabHost(profile);
        var state = new SharedState(tab.States, profile.Data.Preferences, null, tab.Connection);
        var terminals = new TabTerminals();
        TerminalFactory factory = launch => new BrowserTerminal(launch, terminals);
        Endpoint = endpoint;
        // Its disk is not the browser's, so folders are typed as host paths, not picked with the operating system's chooser; its shell is a simulated bash.
        workbench = new Workbench(profile, state, factory, remote: true, tab.Session, tab.Plugins, null) { Endpoint = "this tab", CloneFolder = "/workspace", PlaygroundGuide = true, HostLabel = "Playground", ChangeHost = ChangeHost, Notifier = BrowserNotifier.Instance, Compact = phone, Touch = phone || BrowserInterop.IsCoarse() };
        workbench.WindowOpened += window => window.OpenLink = BrowserInterop.OpenLink;
        foreach (var entry in profile.Data.Windows.Take(1)) workbench.Open(entry, TabHost.ProjectRoot).Show();
    }

    private void Open(HostEndpoint endpoint)
    {
        if (disposed) return;
        phone = PhoneLayout.Read(phone);
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
                ReturnToConnect(forgotten, "Browser access expired or was revoked. Connect again to request approval in tereditor.");
            });
        };
        Func<IProjectServices> sessions = () => new RemoteProjectAdapter(address, token, connection);
        TerminalFactory factory = launch => new BrowserTerminal(launch, terminals);
        Endpoint = endpoint;
        workbench = new Workbench(profile, state, factory, remote: true, sessions, plugins, catalog) { Endpoint = address.ToString(), RedactError = message => string.IsNullOrEmpty(token) ? message : message.Replace(token, "[redacted]", StringComparison.Ordinal).Replace(Uri.EscapeDataString(token), "[redacted]", StringComparison.Ordinal), ChangeHost = ChangeHost, Notifier = BrowserNotifier.Instance, Compact = phone, Touch = phone || BrowserInterop.IsCoarse() };
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

    /// <summary>The page was resized or turned: crossing the phone breakpoint swaps the shell.</summary>
    internal void ViewportChanged()
    {
        if (disposed || workbench is not { } open || Endpoint is not { } endpoint) return;
        var next = PhoneLayout.Read(phone);
        if (next == phone) return;
        // Closing the windows asks about unsaved edits and running agents; a resize must not raise those questions, so try again later.
        if (open.AgentInProgress || open.State.Current.OwnedProcesses.Count > 0 || open.Windows.Any(HasUnsavedEdits))
        {
            deferred ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            deferred.Tick -= RetryViewport;
            deferred.Tick += RetryViewport;
            deferred.Start();
            return;
        }
        deferred?.Stop();
        phone = next;
        // The layout, tabs and open documents are the profile's and the host's, so the same windows come back in the other shell;
        // a Playground tab's own host is kept, not restarted.
        Close(keepTab: endpoint.Tab);
        if (endpoint.Tab) OpenTab(endpoint); else Open(endpoint);
    }

    private void RetryViewport(object? sender, EventArgs e)
    {
        deferred?.Stop();
        ViewportChanged();
    }

    private static bool HasUnsavedEdits(WorkbenchWindow window) =>
        window.GetVisualDescendants().OfType<Editor.CodeDocumentView>().Any(view => view.HasPendingChanges);

    private void Close(bool keepTab = false)
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
        if (keepTab) return;
        deferred?.Stop();
        tab?.Dispose();
        tab = null;
    }

    public void Dispose()
    {
        disposed = true;
        Close();
        if (Current == this) Current = null;
    }
}