namespace SharpRail.Host.Abstractions;

/// <summary>Settings every client of one host shares. Empty or zero values mean the client's default.</summary>
public sealed record HostSettings
{
    public string Theme { get; init; } = "";
    public string ThemeMode { get; init; } = "fixed";
    public string SystemLight { get; init; } = "";
    public string SystemDark { get; init; } = "";
    public int FileLineWidth { get; init; }
    public bool FileLineWidthBounded { get; init; } = true;
    public int MarkdownLineWidth { get; init; }
    public bool MarkdownLineWidthBounded { get; init; } = true;
}

/// <summary>A custom layout preset; the layout is opaque to the host.</summary>
public sealed record LayoutPreset(string Name, string Layout);

/// <summary>
/// A project's identity, kept while it is open or recent so closing and reopening it changes nothing but
/// <paramref name="LastOpened"/> (Unix milliseconds). The slug is a readable name unique among the host's projects.
/// </summary>
public sealed record ProjectRecord(string Id, string Path, string Slug, long LastOpened);

/// <summary>
/// One host's shared state. Every snapshot is complete, so a client that missed
/// events rehydrates from the next one it receives.
/// </summary>
public sealed record HostState
{
    public long Revision { get; init; }
    public HostSettings Settings { get; init; } = new();
    public IReadOnlyList<LayoutPreset> Presets { get; init; } = [];
    public IReadOnlyList<string> Projects { get; init; } = [];
    public IReadOnlyList<string> RecentProjects { get; init; } = [];
    /// <summary>One record per open or recent project, in no particular order.</summary>
    public IReadOnlyList<ProjectRecord> ProjectRecords { get; init; } = [];
    public IReadOnlyDictionary<string, string> WorkspaceLabels { get; init; } = new Dictionary<string, string>();
    /// <summary>Workspace paths per project root, published after the host creates or removes a workspace. Not persisted.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Workspaces { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

public sealed record HostStateChange(string Kind, string Key = "", string Value = "")
{
    public const int RecentLimit = 10;

    public static HostStateChange Setting(string name, string value) => new("setting", name, value);
    public static HostStateChange SavePreset(string name, string layout) => new("preset-save", name, layout);
    public static HostStateChange RenamePreset(string name, string next) => new("preset-rename", name, next);
    public static HostStateChange DeletePreset(string name) => new("preset-delete", name);
    /// <summary>An empty label restores the directory name.</summary>
    public static HostStateChange Label(string path, string label) => new("workspace-label", path, label);
    public static HostStateChange OpenProject(string path) => new("project-open", path);
    /// <summary>Moves an open project to the front of the recents.</summary>
    public static HostStateChange CloseProject(string path) => new("project-close", path);
    public static HostStateChange ForgetProject(string path) => new("project-forget", path);
}

public interface IHostStateService
{
    /// <summary>The host's protocol version, so a client shipped separately can detect drift.</summary>
    ValueTask<HostHandshake> GetHandshakeAsync(CancellationToken cancellationToken = default);
    ValueTask<HostState> GetStateAsync(CancellationToken cancellationToken = default);
    /// <summary>Applies the changes atomically and publishes one snapshot to every watcher.</summary>
    ValueTask<HostState> ChangeAsync(IReadOnlyList<HostStateChange> changes, CancellationToken cancellationToken = default);
    /// <summary>Yields the current snapshot, then every later one, until cancelled or disconnected.</summary>
    IAsyncEnumerable<HostState> WatchAsync(CancellationToken cancellationToken = default);
}