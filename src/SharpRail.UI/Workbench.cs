using SharpRail.Host.Abstractions;
using SharpRail.UI.Plugins;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>
/// The app-owned composition every window shares: one host (its shared-state subscription and a
/// factory for per-window project sessions), the terminal factory and the profile. Each window
/// keeps its own layout in its <see cref="WindowProfile"/>.
/// </summary>
public sealed class Workbench : IDisposable
{
    private readonly Func<IProjectServices>? sessions;
    private readonly List<WorkbenchWindow> windows = [];
    private readonly Dictionary<string, int> workspaceRevisions = [];
    private readonly Dictionary<(string Workspace, string Path), int> fileRevisions = [];
    private WorkbenchWindow? activeWindow;

    public Workbench(ProfileStore profile, SharedState state, Terminal.TerminalFactory terminals, bool remote, Func<IProjectServices>? sessions, IPluginService? plugins = null)
    {
        Profile = profile; State = state; Terminals = terminals; Remote = remote; this.sessions = sessions; Plugins = plugins;
        PluginLoader = new(this);
        WorkspaceWatches = new(this, sessions);
        state.Changed += (_, _) => ProjectionChanged?.Invoke();
        state.Start();
        PluginLoader.Start();
    }

    public ProfileStore Profile { get; }
    public SharedState State { get; }
    public Terminal.TerminalFactory Terminals { get; }
    public bool Remote { get; }
    /// <summary>The host's plugin runtime; null for a workbench composed without one.</summary>
    public IPluginService? Plugins { get; }
    /// <summary>The app's plugin runtime and the registry its windows render contributions from.</summary>
    public PluginLoader PluginLoader { get; }
    public PluginRegistry PluginRegistry => PluginLoader.Registry;
    /// <summary>Workspaces plugins asked to keep watched, beside those windows have mounted.</summary>
    internal PluginWorkspaceWatches WorkspaceWatches { get; }
    /// <summary>A host this workbench composed for itself and stops when it is disposed.</summary>
    internal IAsyncDisposable? OwnedHost { get; init; }
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
            return Bytes(await window.Host.ReadFileAsync(path, cancellationToken));
        var session = sessions?.Invoke() ?? throw new InvalidOperationException("This workbench cannot open workspaces.");
        try
        {
            await session.OpenProjectAsync(workspace, cancellationToken);
            return Bytes(await session.ReadFileAsync(path, cancellationToken));
        }
        finally { (session as IDisposable)?.Dispose(); }

        static byte[] Bytes(FileDocument document) => document.ImageData ?? System.Text.Encoding.UTF8.GetBytes(document.Text);
    }

    public void Dispose()
    {
        PluginLoader.Stop();
        State.Dispose();
        if (OwnedHost is { } host) _ = Task.Run(async () => await host.DisposeAsync());
    }
}