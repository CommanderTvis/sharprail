using System.Text.Json;

using SharpRail.Plugins.Api;

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
    /// <summary>How much recent output a terminal opened from now on keeps for replay, in KiB; zero keeps none.</summary>
    public int TerminalReplayKb { get; init; } = DefaultTerminalReplayKb;
    /// <summary>Whether clients raise desktop notifications when a terminal's agent needs the user while they are away.</summary>
    public bool NotificationsEnabled { get; init; } = true;

    public const int DefaultTerminalReplayKb = 64;
    public const int MaxTerminalReplayKb = 1024;
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
    /// <summary>The ref each workspace was created from, recorded by the host and never changed by a client.</summary>
    public IReadOnlyDictionary<string, string> WorkspaceBases { get; init; } = new Dictionary<string, string>();
    /// <summary>A review target re-pointed away from the creation base; absent while they agree.</summary>
    public IReadOnlyDictionary<string, string> WorkspaceDiffBases { get; init; } = new Dictionary<string, string>();
    /// <summary>The ref a workspace's changes are measured against: its re-pointed target, else its creation base, else none.</summary>
    public string DiffBase(string workspace) =>
        WorkspaceDiffBases.GetValueOrDefault(workspace) ?? WorkspaceBases.GetValueOrDefault(workspace) ?? "";

    /// <summary>The persisted workspace registry of every project, each project's Default workspace first.</summary>
    public IReadOnlyList<WorkspaceRecord> Workspaces { get; init; } = [];

    public IEnumerable<WorkspaceRecord> WorkspacesOf(string projectRoot) => Workspaces.Where(workspace => workspace.ProjectRoot == projectRoot);
    /// <summary>Plugin settings namespaces by plugin id, each a JSON object; its <c>enabled</c> member belongs to core.</summary>
    public IReadOnlyDictionary<string, JsonElement> PluginSettings { get; init; } = new Dictionary<string, JsonElement>();
    /// <summary>Extra directories scanned for external plugins, besides the state directory's <c>plugins</c>.</summary>
    public IReadOnlyList<string> PluginPaths { get; init; } = [];
    /// <summary>The plugin roster as the host's runtime last reconciled it. Not persisted.</summary>
    public IReadOnlyList<PluginRosterEntry> Plugins { get; init; } = [];
    /// <summary>The agent record of each terminal that has one; persisted, and dropped when its terminal closes.</summary>
    public IReadOnlyList<TerminalAgent> TerminalAgents { get; init; } = [];
    /// <summary>The title an agent gave its terminal's tab; persisted, and dropped when the terminal closes.</summary>
    public IReadOnlyList<TerminalTitle> TerminalTitles { get; init; } = [];
    /// <summary>The host's operating system; null from a host that does not report it.</summary>
    public HostPlatform? Platform { get; init; }
}

public sealed record TerminalAgent(TerminalRef Terminal, TerminalAgentRecord Record);

public sealed record TerminalTitle(TerminalRef Terminal, string Title);

public sealed record HostStateChange(string Kind, string Key = "", string Value = "")
{
    public const int RecentLimit = 10;

    public static HostStateChange Setting(string name, string value) => new("setting", name, value);
    public static HostStateChange SavePreset(string name, string layout) => new("preset-save", name, layout);
    public static HostStateChange RenamePreset(string name, string next) => new("preset-rename", name, next);
    public static HostStateChange DeletePreset(string name) => new("preset-delete", name);
    /// <summary>An empty label restores the directory name.</summary>
    public static HostStateChange Label(string path, string label) => new("workspace-label", path, label);
    /// <summary>Re-points a workspace's review target; an empty ref, or its creation base, restores that base.</summary>
    public static HostStateChange DiffBase(string path, string reference) => new("workspace-diff-base", path, reference);
    public static HostStateChange OpenProject(string path) => new("project-open", path);
    /// <summary>Moves an open project to the front of the recents.</summary>
    public static HostStateChange CloseProject(string path) => new("project-close", path);
    public static HostStateChange ForgetProject(string path) => new("project-forget", path);
    /// <summary>Merges a JSON object into one plugin's settings namespace; null resets the namespace.</summary>
    public static HostStateChange PluginSettings(string id, string? json) => new("plugin-settings", id, json ?? "");
    public static HostStateChange PluginEnabled(string id, bool enabled) => PluginSettings(id, enabled ? """{"enabled":true}""" : """{"enabled":false}""");
    public static HostStateChange PluginPaths(IReadOnlyList<string> paths) => new("plugin-paths", Value: JsonSerializer.Serialize(paths));
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
    /// <summary>
    /// Yields each later project and workspace lifecycle change, after the snapshot that carries it was saved and
    /// published. Nothing is replayed: a client that reconnects rehydrates from <see cref="WatchAsync"/>.
    /// </summary>
    IAsyncEnumerable<LifecycleEvent> WatchLifecycleAsync(CancellationToken cancellationToken = default);
}