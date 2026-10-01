namespace SharpRail.Plugins.Api;

/// <summary>Whether a plugin ships with SharpRail or was installed by the user.</summary>
public enum PluginOrigin
{
    /// <summary>Referenced by the host and the app and listed in their builtin arrays.</summary>
    Builtin,

    /// <summary>A directory under a plugin root, loaded from disk.</summary>
    External
}

/// <summary>A plugin's runtime state in the roster.</summary>
public enum PluginStatus
{
    /// <summary>Enabled, and its host half (if any) activated.</summary>
    Active,

    /// <summary>Known and turned off.</summary>
    Disabled,

    /// <summary>Enabled, but loading or activation threw; <see cref="PluginRosterEntry.Reason"/> says why. Retried only on request.</summary>
    Failed,

    /// <summary>Cannot load: an unreadable manifest, a generation mismatch, a missing dependency or a cycle; <see cref="PluginRosterEntry.Reason"/> says which.</summary>
    Refused
}

/// <summary>The wire-facing projection of one contract channel, so a UI half never needs the host half's assembly.</summary>
/// <param name="Kind">State or event.</param>
/// <param name="Snapshot">For a state channel, its snapshot method's name.</param>
/// <param name="Key">For a state channel, the JSON names of its key fields.</param>
public sealed record PluginRosterChannel(PluginChannelKind Kind, string? Snapshot, IReadOnlyList<string> Key);

/// <summary>
/// One row of the plugin roster: the host's grant and the UI runtime's desired state. The host publishes the
/// roster on every shared-state snapshot and answers it from the plugin list call. Status alone encodes
/// enablement.
/// </summary>
/// <param name="Id">The plugin's id. A refused external plugin with no readable id is keyed <c>__refused:&lt;directory&gt;</c>.</param>
/// <param name="Label">The manifest label.</param>
/// <param name="Icon">The manifest icon.</param>
/// <param name="Version">The manifest version.</param>
/// <param name="WireVersion">The wire version of the contract the host loaded, or the manifest's before the host half loads.</param>
/// <param name="Origin">Builtin or external.</param>
/// <param name="Status">The runtime state.</param>
public sealed record PluginRosterEntry(string Id, string Label, string Icon, string Version, int WireVersion, PluginOrigin Origin, PluginStatus Status)
{
    /// <summary>The manifest description.</summary>
    public string? Description { get; init; }

    /// <summary>Why the plugin is failed or refused; absent otherwise.</summary>
    public string? Reason { get; init; }

    /// <summary>The ids of the plugins this one depends on.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>The manifest's static contributions.</summary>
    public PluginContributions Contributes { get; init; } = new();

    /// <summary>The loaded contract's channels by name; empty until the host half is loaded, and for a plugin without one.</summary>
    public IReadOnlyDictionary<string, PluginRosterChannel> Channels { get; init; } = new Dictionary<string, PluginRosterChannel>();

    /// <summary>For an external plugin with a UI half, the manifest's UI assembly path, readable through the plugin file read.</summary>
    public string? Ui { get; init; }

    /// <summary>The manifest's assets directory, when declared, for either origin.</summary>
    public string? Assets { get; init; }
}

/// <summary>
/// The agent running in a terminal, as the host persists and broadcasts it. Typed rather than an opaque
/// per-plugin bag, so one plugin can read a session id another plugin's agent recorded.
/// </summary>
/// <param name="Kind">Names the agent, conventionally the owning plugin's launcher id.</param>
/// <param name="Command">The command that started it.</param>
public sealed record TerminalAgentRecord(string Kind, string Command)
{
    /// <summary>The agent's own session id, for resuming it.</summary>
    public string? SessionId { get; init; }

    /// <summary>The agent's working directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>The model the agent runs.</summary>
    public string? Model { get; init; }
}

/// <summary>A known project, as a plugin sees it.</summary>
/// <param name="Id">The project's id, its main working tree's absolute path.</param>
/// <param name="Name">The project's display name.</param>
/// <param name="Path">The project's main working tree.</param>
public sealed record HostProject(string Id, string Name, string Path);

/// <summary>A workspace (a Git worktree of a project, or the project folder itself), as a plugin sees it.</summary>
/// <param name="Id">The workspace's id, its worktree root's absolute path.</param>
/// <param name="ProjectId">The owning project's id.</param>
/// <param name="Name">The host label, or the directory name when none is set.</param>
/// <param name="Branch">The checked-out branch; empty when HEAD is detached or the folder has no Git.</param>
/// <param name="Path">The worktree root, equal to <paramref name="Id"/>.</param>
/// <param name="IsDefault">Whether this is the project's Default workspace, its main working tree.</param>
public sealed record HostWorkspace(string Id, string ProjectId, string Name, string Branch, string Path, bool IsDefault);

/// <summary>The operating system a host runs on.</summary>
public enum HostPlatform
{
    /// <summary>macOS.</summary>
    MacOS,

    /// <summary>Linux.</summary>
    Linux,

    /// <summary>Windows.</summary>
    Windows
}