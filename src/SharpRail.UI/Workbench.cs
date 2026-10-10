using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Notifications;
using SharpRail.UI.Plugins;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>
/// The app-owned composition every window shares: one host (its shared-state subscription and a
/// factory for per-window project sessions), the terminal factory, the host's terminal catalog and the
/// profile. Each window keeps its own layout in its <see cref="WindowProfile"/>.
/// </summary>
public sealed class Workbench : IDisposable
{
    private readonly Func<IProjectServices>? sessions;
    private readonly List<WorkbenchWindow> windows = [];
    private readonly Dictionary<string, int> workspaceRevisions = [];
    private readonly Dictionary<(string Workspace, string Path), int> fileRevisions = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<bool> terminalCatalogStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkbenchWindow? activeWindow;
    private AttentionNotifications? attention;

    /// <summary>Without a <paramref name="terminalTabs"/> catalog, terminal tabs are shared by this workbench's windows only.</summary>
    public Workbench(ProfileStore profile, SharedState state, Terminal.TerminalFactory terminals, bool remote, Func<IProjectServices>? sessions, IPluginService? plugins = null,
        ITerminalCatalogService? terminalTabs = null)
    {
        Commands = new AppCommands(this);
        Profile = profile; State = state; Terminals = terminals; Remote = remote; this.sessions = sessions; Plugins = plugins;
        TerminalTabs = terminalTabs ?? new Host.Core.MemoryTerminalCatalog();
        _ = Task.Run(WatchTerminalsAsync);
        PluginLoader = new(this);
        WorkspaceWatches = new(this, sessions);
        state.Changed += (_, _) => ProjectionChanged?.Invoke();
        state.Start();
        PluginLoader.Start();
    }

    public ProfileStore Profile { get; }
    public SharedState State { get; }
    /// <summary>The one owner of quit and close commands for every window.</summary>
    public AppCommands Commands { get; }
    public Terminal.TerminalFactory Terminals { get; }
    /// <summary>The host's terminal catalog: which terminal tabs every window of every client shows.</summary>
    public ITerminalCatalogService TerminalTabs { get; }
    /// <summary>The latest catalog snapshot; null until the host has answered.</summary>
    public TerminalCatalog? TerminalCatalog { get; private set; }
    /// <summary>Completes with the first snapshot (true), or once the host turns out to keep no catalog (false).</summary>
    internal Task<bool> TerminalCatalogStarted => terminalCatalogStarted.Task;
    /// <summary>Raised on the UI thread when <see cref="TerminalCatalog"/> advances.</summary>
    public event Action? TerminalCatalogChanged;
    public bool Remote { get; }
    /// <summary>A phone-sized screen: windows show their side panels as drawers over the centre.</summary>
    public bool Compact { get; init; }
    /// <summary>Centre tabs always live in Projects, under their workspace, whatever the tab layout preferences say.</summary>
    public bool TabsInProjects { get; init; }
#if !ANDROID
    /// <summary>Optional serving for this app's embedded host, shared by every window.</summary>
    public Host.Remote.HostListener? Listener { get; init; }
#endif
    /// <summary>The host's plugin runtime; null for a workbench composed without one.</summary>
    public IPluginService? Plugins { get; }
    /// <summary>The app's plugin runtime and the registry its windows render contributions from.</summary>
    public PluginLoader PluginLoader { get; }
    public PluginRegistry PluginRegistry => PluginLoader.Registry;
    /// <summary>Workspaces plugins asked to keep watched, beside those windows have mounted.</summary>
    internal PluginWorkspaceWatches WorkspaceWatches { get; }
    /// <summary>A host this workbench composed for itself and stops when it is disposed.</summary>
    internal IAsyncDisposable? OwnedHost { get; init; }
    /// <summary>The system's notification channel; a workbench without one raises no desktop notifications.</summary>
    public IDesktopNotifier? Notifier { get; init; }
    /// <summary>How long attention requests collect before they are shown as one notification.</summary>
    public TimeSpan NotificationWindow { get; init; } = AttentionNotifications.DefaultWindow;
    /// <summary>Whether the user is in the app: one of its windows, or a dialog of one, is the active window.</summary>
    public bool Focused => windows.Any(window => window.IsActive || window.OwnedWindows.Any(owned => owned.IsActive));
    /// <summary>Qualifies client-local plugin preferences, so two hosts' plugins never share one.</summary>
    public string Endpoint { get; init; } = "local";
    /// <summary>The window plugins act on: the last one activated, else the first open.</summary>
    public WorkbenchWindow? ActiveWindow => activeWindow is { } window && windows.Contains(window) ? window : windows.FirstOrDefault();
    /// <summary>Raised on the UI thread when anything a plugin's host projection reads may have changed.</summary>
    public event Action? ProjectionChanged;
    /// <summary>Raised when a watched file's revision advances.</summary>
    public event Action? RevisionsChanged;
    public IReadOnlyDictionary<string, int> WorkspaceRevisions => new Dictionary<string, int>(workspaceRevisions);
    public IReadOnlyList<WorkbenchWindow> Windows => windows;
    public bool CanOpenWindows => sessions is not null;
    /// <summary>A further project session of the same host, for looking at a project a window has not opened; the caller disposes it.</summary>
    public IProjectServices? NewSession() => sessions?.Invoke();
    /// <summary>Set while the app quits, so closing windows keep their profile entries for the next launch.</summary>
    public bool ShuttingDown { get; set; }
    public event Action<WorkbenchWindow>? WindowOpened;

    /// <summary>Opens a window for a profile entry, starting at <paramref name="root"/> when given.</summary>
    public WorkbenchWindow Open(WindowProfile slot, string root) =>
        Attach(new WorkbenchWindow(this, sessions?.Invoke() ?? throw new InvalidOperationException("This workbench cannot open windows."), slot, root));

    internal WorkbenchWindow Attach(WorkbenchWindow window)
    {
        windows.Add(window);
        activeWindow ??= window;
        window.Activated += (_, _) => { activeWindow = window; ProjectionChanged?.Invoke(); };
        window.Closed += (_, _) =>
        {
            windows.Remove(window);
            ProjectionChanged?.Invoke();
            if (!ShuttingDown && windows.Count > 0) Profile.Data.Windows.Remove(window.Slot);
            Profile.Save();
            if (window.Host is IDisposable session && sessions is not null) session.Dispose();
            if (windows.Count == 0) Dispose();
        };
        WindowOpened?.Invoke(window);
        return window;
    }

    /// <summary>Opens another window at the Project Home of <paramref name="source"/>'s project, with a fresh frame.</summary>
    public WorkbenchWindow? NewWindow(WorkbenchWindow source)
    {
        if (!CanOpenWindows) return null;
        var project = source.ProjectRoot;
        var slot = new WindowProfile { LastProject = project, LastProjectRoot = project, LastAtHome = project.Length > 0 };
        Profile.Data.Windows.Add(slot);
        Profile.Save();
        var window = Open(slot, project);
        window.Width = source.Width; window.Height = source.Height;
        window.Show();
        return window;
    }

    internal void RaiseProjectionChanged() => ProjectionChanged?.Invoke();

    /// <summary>Queues a desktop notification for a terminal that needs the user; shown only while the app is away.</summary>
    internal void RequestAttention(AttentionNotification notification)
    {
        if (Notifier is null) return;
        (attention ??= new(this, Notifier, NotificationWindow)).Request(notification);
    }

    internal WorkbenchWindow? WindowHolding(string workspace, string tabKey) => windows.FirstOrDefault(window =>
        window.TerminalTabs().Any(open => open.Workspace == workspace && open.Tabs.Any(tab => tab.Id == tabKey)));

    /// <summary>Brings forward the window holding a terminal tab, on that tab's workspace with the tab selected.</summary>
    internal async Task RevealTerminalAsync(string workspace, string tabKey)
    {
        if (WindowHolding(workspace, tabKey) is not { } window) return;
        if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
        window.Activate();
        await window.RevealTerminalAsync(workspace, tabKey);
    }

    /// <summary>
    /// Takes a snapshot from the subscription or from a change's reply, whichever arrives first, on the UI thread.
    /// The first of a subscription always applies, since a restarted host counts its revisions again.
    /// </summary>
    internal void OfferTerminalCatalog(TerminalCatalog snapshot, bool first = false)
    {
        if (!first && TerminalCatalog is { } current && snapshot.Revision <= current.Revision) return;
        TerminalCatalog = snapshot;
        TerminalCatalogChanged?.Invoke();
    }

    private async Task WatchTerminalsAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                var first = true;
                await foreach (var snapshot in TerminalTabs.WatchAsync(lifetime.Token))
                {
                    var opening = first; first = false;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (lifetime.IsCancellationRequested) return;
                        OfferTerminalCatalog(snapshot, opening);
                        terminalCatalogStarted.TrySetResult(true);
                    });
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            // A host that predates the catalog: every window keeps its own terminal tabs.
            catch (NotSupportedException) { terminalCatalogStarted.TrySetResult(false); return; }
            catch (Exception) { }
            try { await Task.Delay(State.RetryDelay, lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public int FileRevision(string workspace, string path) => fileRevisions.GetValueOrDefault((workspace, path));

    /// <summary>Advances the revisions of changed files in a watched workspace.</summary>
    internal void BumpRevisions(string workspace, IEnumerable<string> paths)
    {
        workspaceRevisions[workspace] = workspaceRevisions.GetValueOrDefault(workspace) + 1;
        foreach (var path in paths) fileRevisions[(workspace, path)] = fileRevisions.GetValueOrDefault((workspace, path)) + 1;
        RevisionsChanged?.Invoke();
        ProjectionChanged?.Invoke();
    }

    /// <summary>Records an observed path, so a broad invalidation of its workspace reaches it before it first changes.</summary>
    internal void TrackRevision(string workspace, string path) => fileRevisions.TryAdd((workspace, path), 0);

    /// <summary>Advances a workspace's revision and every file revision recorded for it, for changes that may have gone unseen.</summary>
    internal void InvalidateRevisions(string workspace) =>
        BumpRevisions(workspace, [.. fileRevisions.Keys.Where(key => key.Workspace == workspace).Select(key => key.Path)]);

    /// <summary>Reads a workspace file through a window already on it, else through a session opened for the read.</summary>
    internal async Task<byte[]> ReadWorkspaceFileAsync(string workspace, string path, CancellationToken cancellationToken)
    {
        if (windows.FirstOrDefault(window => window.WorkspaceMounted && window.WorkspaceRoot == workspace) is { } window)
            return (await window.Host.ReadContentBytesAsync(path, null, cancellationToken)).Data;
        var session = sessions?.Invoke() ?? throw new InvalidOperationException("This workbench cannot open workspaces.");
        try
        {
            await session.OpenProjectAsync(workspace, cancellationToken);
            return (await session.ReadContentBytesAsync(path, null, cancellationToken)).Data;
        }
        finally { (session as IDisposable)?.Dispose(); }
    }

    public void Dispose()
    {
        lifetime.Cancel(); terminalCatalogStarted.TrySetCanceled();
        PluginLoader.Stop();
        attention?.Stop();
        State.Dispose();
        if (OwnedHost is { } host) _ = Task.Run(async () => await host.DisposeAsync());
    }
}