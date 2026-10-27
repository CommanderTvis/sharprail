using System.Text.Json;

using SharpRail.Plugins.Api;

namespace SharpRail.Host.Abstractions;

/// <summary>Settings every client of one host shares. Empty or zero values mean the client's default.</summary>
public sealed record HostSettings
{
    /// <summary>Whether clients use the bundled file-type icons instead of generic file glyphs.</summary>
    public bool FileTypeIconsEnabled { get; init; } = true;
    /// <summary>The bundled icon set's look when <see cref="FileTypeIconsEnabled"/>: empty is plain (decoloured), <c>colored</c> tints it per file kind, <c>material</c> uses Material Icon Theme's own colours.</summary>
    public string FileTypeIconStyle { get; init; } = "";
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
    /// <summary>Whether a terminal whose shell exits keeps its tab with an exit notice; off closes the tab like Ghostty.</summary>
    public bool TerminalKeepAfterExit { get; init; }
    /// <summary>How long a terminal tab the user closed keeps its session for undo, in seconds; zero disables undo.</summary>
    public int TerminalUndoCloseSeconds { get; init; } = DefaultTerminalUndoCloseSeconds;
    /// <summary>Whether shells the host starts from now on load Ghostty's shell integration (prompt marks and working directory).</summary>
    public bool ShellIntegrationEnabled { get; init; } = true;
    /// <summary>Whether clients raise desktop notifications when a terminal's agent needs the user while they are away.</summary>
    public bool NotificationsEnabled { get; init; } = true;
    /// <summary>Whether programs in terminals may raise desktop notifications (OSC 9, OSC 777), like Ghostty's desktop-notifications.</summary>
    public bool TerminalNotificationsEnabled { get; init; } = true;
    /// <summary>
    /// What a terminal bell does, like Ghostty's bell-features: a comma-separated subset of <c>system</c> (the system alert
    /// sound), <c>attention</c> (request attention while the app is unfocused) and <c>title</c> (a bell on the tab until it is
    /// viewed), or <c>none</c>.
    /// </summary>
    public string BellFeatures { get; init; } = DefaultBellFeatures;
    /// <summary>Whether editor and agent language services are enabled on this host.</summary>
    public bool LspEnabled { get; init; } = true;
    /// <summary>Host-wide Java/Kotlin provider: standard or intellij; empty migrates legacy workspace choices.</summary>
    public string JavaKotlinProvider { get; init; } = "";
    /// <summary>Host-wide launcher overrides keyed by language-server id, stored as JSON.</summary>
    public string LanguageServers { get; init; } = "";
    /// <summary>Host-wide paths of the tools language servers need (go, node, java...), keyed by executable name, stored as JSON; absent tools are found on PATH.</summary>
    public string LanguageTools { get; init; } = "";
    /// <summary>Limits on running language servers (idle timeout, memory budget, per-server caps) as JSON; empty uses the defaults.</summary>
    public string LanguageLimits { get; init; } = "";
    /// <summary>Custom TextMate grammars and filename patterns, as a JSON array shared with clients.</summary>
    public string CustomHighlighting { get; init; } = "";
    /// <summary>Whether centre tabs use a vertical strip; null keeps an older client's preference.</summary>
    public bool? VerticalCenterTabs { get; init; }
    /// <summary>Whether the vertical strip lives in Projects; null keeps an older client's preference.</summary>
    public bool? VerticalTabsInProjects { get; init; }
    /// <summary>
    /// How Linux clients frame their window: empty is automatic, <c>client</c> merges the header into the title bar, <c>system</c> keeps the
    /// window manager's. Other platforms ignore it.
    /// </summary>
    public string WindowDecorations { get; init; } = "";
    /// <summary>Days without activity after which a workspace settles onto its project's shelf; null never settles.</summary>
    public int? SettleIdleDays { get; init; } = DefaultSettleIdleDays;

    public const int DefaultSettleIdleDays = 7;
    public const string DefaultBellFeatures = "attention,title";
    public static readonly IReadOnlyList<string> BellFeatureNames = ["system", "attention", "title"];

    /// <summary>A bell feature list in canonical order, or null when it names something else; empty and <c>none</c> enable nothing.</summary>
    public static string? NormalizeBellFeatures(string value)
    {
        var named = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (named is ["none"] || named.Length == 0) return "none";
        if (named.Any(name => !BellFeatureNames.Contains(name))) return null;
        return string.Join(",", BellFeatureNames.Where(named.Contains));
    }

    /// <summary>Whether this bell feature is on.</summary>
    public bool Bell(string feature) => BellFeatures.Split(',').Contains(feature);
    public const int MaxSettleIdleDays = 365;
    public const int DefaultTerminalReplayKb = 64;
    public const int MaxTerminalReplayKb = 1024;
    public const int DefaultTerminalUndoCloseSeconds = 5;
    public const int MaxTerminalUndoCloseSeconds = 600;
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
    /// <summary>Workspace path to JSON server configuration map; independent after creation.</summary>
    public IReadOnlyDictionary<string, string> WorkspaceLanguages { get; init; } = new Dictionary<string, string>();
    /// <summary>Project root to JSON server-id array started before managed agent launches.</summary>
    public IReadOnlyDictionary<string, string> ProjectLanguageAutostart { get; init; } = new Dictionary<string, string>();
    /// <summary>
    /// Project root to the JSON server-id array the user hid from the Code Intelligence pane: a personal choice, never written to the
    /// shared <c>language.toml</c>. Hidden servers are not suggested and not started before agent launches.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectLanguageHidden { get; init; } = new Dictionary<string, string>();
    /// <summary>Unexpected language-server deaths this host saw, keyed by workspace, NUL and server id, valued "time|reason". Never persisted; clients toast each new value.</summary>
    public IReadOnlyDictionary<string, string> LanguageDeaths { get; init; } = new Dictionary<string, string>();
    /// <summary>
    /// Project root to the JSON status of its <c>.sharprail/language.toml</c> as the host last read it: path, whether it
    /// exists, a stamp that changes with every edit, the server ids it configures and diagnostics for what was ignored.
    /// Derived by the host, never persisted; absent until the project's language configuration was first used.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectLanguageFiles { get; init; } = new Dictionary<string, string>();
    /// <summary>Project roots whose user chose to keep an untracked <c>.sharprail</c> directory in the repository; they are not asked again.</summary>
    public IReadOnlyList<string> SharprailExcludeDeclined { get; init; } = [];
    public IReadOnlyDictionary<string, string> WorkspaceLabels { get; init; } = new Dictionary<string, string>();
    /// <summary>
    /// Workspaces with a result the user has not seen yet (ThinkRail #770), by path: <c>blocked</c> (an agent waits for
    /// input), <c>failed</c>, <c>done</c>, or <c>manual</c> (marked unread by hand). Persisted and shared by every client.
    /// </summary>
    public IReadOnlyDictionary<string, string> UnreadWorkspaces { get; init; } = new Dictionary<string, string>();
    /// <summary>A project's display name by root, when renamed; persisted, kept across close and reopen (ThinkRail #764).</summary>
    public IReadOnlyDictionary<string, string> ProjectLabels { get; init; } = new Dictionary<string, string>();
    /// <summary>A project's display name: its renamed label, else its folder name.</summary>
    public string ProjectName(string projectRoot) => ProjectLabels.GetValueOrDefault(projectRoot) ??
        (Path.GetFileName(projectRoot.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : projectRoot);
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
    /// <summary>
    /// The title of each terminal's tab, at most one per terminal: the strongest of a user rename, an agent's <c>set_title</c>
    /// and the program's OSC 0/2 title (see <see cref="TerminalTitleSource"/>). User and agent titles persist; program
    /// titles are live only. All are dropped when the terminal closes.
    /// </summary>
    public IReadOnlyList<TerminalTitle> TerminalTitles { get; init; } = [];
    /// <summary>
    /// Agents asking to move their terminal tab into a workspace they created (<c>workspace_emigrate</c>), each waiting for
    /// the user's answer from any client. Never persisted: an unanswered request ends with its tool call.
    /// </summary>
    public IReadOnlyList<TerminalEmigrationRequest> PendingEmigrations { get; init; } = [];
    /// <summary>
    /// Approved emigrations whose relaunch failed, newest last, with the error as <see cref="TerminalEmigrationRequest.Reason"/>;
    /// the tab went back to its workspace. Never persisted; clients notify once per id.
    /// </summary>
    public IReadOnlyList<TerminalEmigrationRequest> FailedEmigrations { get; init; } = [];
    /// <summary>
    /// Revived agent tabs whose session SharpRail did not resume on its own: each waits for the user to resume it or start
    /// fresh, from any client, through <see cref="HostStateChange.AnswerResume"/>. Never persisted: the next revival offers again.
    /// </summary>
    public IReadOnlyList<TerminalResumeOffer> ResumeOffers { get; init; } = [];
    /// <summary>The host's operating system; null from a host that does not report it.</summary>
    public HostPlatform? Platform { get; init; }
    /// <summary>Whether this client's own connection to the host runs over Tailscale, as the host saw it; false for embedded and other transports.</summary>
    public bool ConnectedThroughTailscale { get; init; }
}

public sealed record TerminalAgent(TerminalRef Terminal, TerminalAgentRecord Record);

/// <summary>An agent's request to move its tab from <paramref name="Terminal"/>'s workspace into <paramref name="Target"/>, awaiting the user.</summary>
/// <param name="Reason">The agent's own explanation, shown in the prompt; may be empty.</param>
public sealed record TerminalEmigrationRequest(string Id, TerminalRef Terminal, string Target, string Reason = "");

/// <summary>A revived agent tab's session, offered for resuming instead of resumed automatically.</summary>
/// <param name="Agent">The agent record's kind (<c>claude</c>, <c>codex</c>, <c>omp</c>, ...).</param>
/// <param name="SessionId">The recorded session id; empty for a legacy record that has none.</param>
/// <param name="OpenElsewhere">The session is already open in another process (another tab, window or SharpRail instance), so only a fresh start is offered.</param>
/// <param name="OwnerText">Who has the session open (pid, age, tty, whether a live SharpRail owns it), shown instead of a generic refusal; empty when unknown.</param>
/// <param name="QuitConfirmation">Set only when the owner is a stray process left by a SharpRail instance that is gone: the text a client confirms before asking the host to end it. Empty offers no quit.</param>
/// <param name="Progress">The host's inline status while it ends the stray owner, or why that failed; empty otherwise.</param>
public sealed record TerminalResumeOffer(TerminalRef Terminal, string Agent, string SessionId, bool OpenElsewhere = false,
    string OwnerText = "", string QuitConfirmation = "", string Progress = "");

/// <param name="Source">Who chose the title; a stronger source's title is never replaced by a weaker one's.</param>
public sealed record TerminalTitle(TerminalRef Terminal, string Title, TerminalTitleSource Source = TerminalTitleSource.User);

/// <summary>Who titled a terminal tab. A user rename outranks an agent's title, which outranks the program's.</summary>
public enum TerminalTitleSource
{
    /// <summary>The user, renaming the tab. The default, so titles stored before sources existed stay as they were.</summary>
    User = 0,
    /// <summary>An agent, through the <c>set_title</c> tool.</summary>
    Agent = 1,
    /// <summary>The program in the terminal, through OSC 0 or OSC 2.</summary>
    Program = 2
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
    /// <summary>Re-points a workspace's review target; an empty ref, or its creation base, restores that base.</summary>
    public static HostStateChange DiffBase(string path, string reference) => new("workspace-diff-base", path, reference);
    /// <summary>Remembers that a project keeps its <c>.sharprail</c> directory in the repository, so it is not offered a local exclusion again.</summary>
    public static HostStateChange DeclineSharprailExclude(string projectRoot) => new("sharprail-exclude-declined", projectRoot);
    /// <summary>Persists the title of a terminal tab for every client.</summary>
    public static HostStateChange TitleTerminal(TerminalRef terminal, string title) => new("terminal-title", JsonSerializer.Serialize(terminal), title);
    /// <summary>Marks a workspace unread for a reason (blocked, failed, done or manual); a stronger reason already recorded stays.</summary>
    public static HostStateChange MarkUnread(string path, string reason) => new("workspace-unread", path, reason);
    /// <summary>Marks a workspace read. By hand it keeps a blocked agent's mark; <paramref name="seen"/> clears every mark.</summary>
    public static HostStateChange MarkRead(string path, bool seen = false) => new("workspace-read", path, seen ? "seen" : "");
    /// <summary>Renames a project for display only; its path and identity stay. A name equal to the folder name clears the label.</summary>
    public static HostStateChange RenameProject(string projectRoot, string name) => new("project-label", projectRoot, name);
    public static HostStateChange OpenProject(string path) => new("project-open", path);
    /// <summary>Moves an open project to the front of the recents.</summary>
    public static HostStateChange CloseProject(string path) => new("project-close", path);
    public static HostStateChange ForgetProject(string path) => new("project-forget", path);
    /// <summary>Merges a JSON object into one plugin's settings namespace; null resets the namespace.</summary>
    public static HostStateChange PluginSettings(string id, string? json) => new("plugin-settings", id, json ?? "");
    public static HostStateChange PluginEnabled(string id, bool enabled) => PluginSettings(id, enabled ? """{"enabled":true}""" : """{"enabled":false}""");
    /// <summary>Answers a pending <see cref="HostState.PendingEmigrations"/> request; an unknown or answered id changes nothing.</summary>
    public static HostStateChange AnswerEmigration(string id, bool approved) => new("terminal-emigration-answer", id, approved ? "approve" : "deny");
    /// <summary>Answers a revived tab's <see cref="HostState.ResumeOffers"/> entry: resume its session, or start a fresh one.</summary>
    public static HostStateChange AnswerResume(TerminalRef terminal, bool resume) => new("terminal-resume-answer", JsonSerializer.Serialize(terminal), resume ? "resume" : "fresh");
    /// <summary>Answers a resume offer by ending its stray owner process, then resuming; the host re-checks that the process is still a stray owner.</summary>
    public static HostStateChange QuitOwnerAndResume(TerminalRef terminal) => new("terminal-resume-answer", JsonSerializer.Serialize(terminal), "quit");
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