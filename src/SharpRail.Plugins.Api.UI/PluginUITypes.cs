namespace SharpRail.Plugins.Api.UI;

/// <summary>The logger handed to a UI half as <see cref="IPluginUIContext.Log"/>, one level per method.</summary>
public interface IPluginUILogger
{
    /// <summary>Logs a debug message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="fields">Structured fields, serialized with <see cref="PluginJson.Options"/>.</param>
    void Debug(string message, object? fields = null);

    /// <summary>Logs an informational message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="fields">Structured fields, serialized with <see cref="PluginJson.Options"/>.</param>
    void Info(string message, object? fields = null);

    /// <summary>Logs a warning.</summary>
    /// <param name="message">The message.</param>
    /// <param name="fields">Structured fields, serialized with <see cref="PluginJson.Options"/>.</param>
    void Warn(string message, object? fields = null);

    /// <summary>Logs an error.</summary>
    /// <param name="message">The message.</param>
    /// <param name="fields">Structured fields, serialized with <see cref="PluginJson.Options"/>.</param>
    void Error(string message, object? fields = null);
}

/// <summary>
/// The cleanup a UI half's activation may return, run when the plugin is disabled or leaves the roster, before
/// its contributions unmount. The plugin's assembly stays loaded, so enabling it again re-activates without
/// loading anything.
/// </summary>
public delegate void PluginDisposer();

/// <summary>The app settings a UI half may read, as the window applies them.</summary>
/// <param name="ThemeId">The applied theme's id.</param>
/// <param name="IsLightTheme">Whether the applied theme is light.</param>
/// <param name="FileLineWidth">The file line width in symbols; 0 for the default.</param>
/// <param name="FileLineWidthBounded">Whether file lines wrap at <paramref name="FileLineWidth"/>.</param>
/// <param name="MarkdownLineWidth">The Markdown reading measure in symbols; 0 for the default.</param>
/// <param name="MarkdownLineWidthBounded">Whether the Markdown column is capped.</param>
/// <param name="PluginPaths">Extra roots scanned for external plugins.</param>
public sealed record PluginAppSettings(string ThemeId, bool IsLightTheme, int FileLineWidth, bool FileLineWidthBounded,
    int MarkdownLineWidth, bool MarkdownLineWidthBounded, IReadOnlyList<string> PluginPaths);

/// <summary>One terminal tab a workspace has open in the app.</summary>
/// <param name="TabKey">The tab's layout id.</param>
/// <param name="Title">The tab's title.</param>
/// <param name="Agent">The terminal's agent record, when one is set.</param>
public sealed record TerminalTabInfo(string TabKey, string Title, TerminalAgentRecord? Agent);

/// <summary>
/// The read-only slice of app state a UI half may read (W3). It is the whole read surface onto core state; there
/// is no store or actions bag behind it. Window-scoped members follow the app's active window. Read it with
/// <see cref="IPluginUIContext.Host"/> or observe a selection with <see cref="IPluginUIContext.WatchHost"/>.
/// Collections are replaced, never mutated, when they change.
/// </summary>
/// <param name="Projects">Known projects.</param>
/// <param name="Workspaces">Known workspaces by project id.</param>
/// <param name="ActiveWorkspaceId">The active window's workspace, or <see langword="null"/> at Project Home.</param>
/// <param name="ContextProjectId">The project the active window's view is about, or <see langword="null"/>.</param>
/// <param name="ActiveEditor">The active window's active editor, or <see langword="null"/>.</param>
/// <param name="AppSettings">The applied app settings.</param>
/// <param name="Terminals">Terminal tabs by workspace id, across windows.</param>
/// <param name="ShownTerminalTabKeys">Per workspace, the terminal tabs a window shows: the one in its last-focused centre group first, then its bottom group, then the rest.</param>
/// <param name="WorkspaceRevisions">Filesystem revision counters by workspace id, bumped on a watched change.</param>
/// <param name="Roster">The plugin roster.</param>
/// <param name="HostPlatform">The host's platform, or <see langword="null"/> before it is known.</param>
public sealed record PluginHostProjection(
    IReadOnlyList<HostProject> Projects,
    IReadOnlyDictionary<string, IReadOnlyList<HostWorkspace>> Workspaces,
    string? ActiveWorkspaceId,
    string? ContextProjectId,
    EditorRef? ActiveEditor,
    PluginAppSettings AppSettings,
    IReadOnlyDictionary<string, IReadOnlyList<TerminalTabInfo>> Terminals,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ShownTerminalTabKeys,
    IReadOnlyDictionary<string, int> WorkspaceRevisions,
    IReadOnlyList<PluginRosterEntry> Roster,
    HostPlatform? HostPlatform);

/// <summary>What an editor shows.</summary>
public enum EditorKind
{
    /// <summary>A file in the workspace.</summary>
    File,

    /// <summary>A file outside the worktree that a plugin exposes through its external-files provider.</summary>
    ExternalFile,

    /// <summary>A diff.</summary>
    Diff
}

/// <summary>One open editor and its dirty state.</summary>
/// <param name="Id">The editor's id, stable while it stays open and unique across windows.</param>
/// <param name="WorkspaceId">The workspace the editor belongs to.</param>
/// <param name="Path">The workspace-relative path, or an absolute path for an external file.</param>
/// <param name="Kind">What the editor shows.</param>
/// <param name="Dirty">Whether it has unsaved changes.</param>
public sealed record EditorRef(string Id, string WorkspaceId, string Path, EditorKind Kind, bool Dirty);

/// <summary>A text selection, in one-based lines and columns.</summary>
/// <param name="StartLine">The first line.</param>
/// <param name="StartColumn">The first column.</param>
/// <param name="EndLine">The last line.</param>
/// <param name="EndColumn">The column after the last selected character.</param>
/// <param name="Text">The selected text.</param>
public sealed record EditorSelection(int StartLine, int StartColumn, int EndLine, int EndColumn, string Text);

/// <summary>What happened to an editor.</summary>
public enum EditorLifecycle
{
    /// <summary>The editor opened.</summary>
    Opened,

    /// <summary>The editor closed.</summary>
    Closed,

    /// <summary>The editor became the active window's active editor.</summary>
    Activated,

    /// <summary>The editor's contents were saved.</summary>
    Saved
}

/// <summary>An editor event delivered to <see cref="IPluginEditors.OnEvent"/> observers (W12).</summary>
/// <param name="Editor">The editor the event concerns.</param>
public abstract record EditorEvent(EditorRef Editor);

/// <summary>An editor opened, closed, became active or was saved.</summary>
/// <param name="Editor">The editor.</param>
/// <param name="Lifecycle">What happened.</param>
public sealed record EditorLifecycleEvent(EditorRef Editor, EditorLifecycle Lifecycle) : EditorEvent(Editor);

/// <summary>
/// The selection in an editor changed, reported by the code editor, the Markdown preview or a plugin's own
/// rendered document through <see cref="IPluginEditors.ReportSelection"/>.
/// </summary>
/// <param name="Editor">The editor.</param>
/// <param name="Selection">The selection, or <see langword="null"/> when cleared.</param>
public sealed record EditorSelectionEvent(EditorRef Editor, EditorSelection? Selection) : EditorEvent(Editor);

/// <summary>Options for <see cref="IPluginEditors.OpenAsync"/>.</summary>
public sealed record EditorOpenOptions
{
    /// <summary>A one-based line to reveal.</summary>
    public int? Line { get; init; }

    /// <summary>A JSON key path to reveal in the editor's current contents; it takes precedence over <see cref="Line"/>, and an unresolved path opens at the top.</summary>
    public IReadOnlyList<string>? KeyPath { get; init; }

    /// <summary>Opens as a preview tab that the next preview replaces.</summary>
    public bool Preview { get; init; }

    /// <summary>Bypasses every registered file viewer and read strategy and opens the file's text.</summary>
    public bool Raw { get; init; }
}

/// <summary>The terminal a companion pane is attached to (W6).</summary>
/// <param name="WorkspaceId">The terminal's workspace.</param>
/// <param name="TabKey">The terminal's tab key.</param>
public sealed record CompanionHost(string WorkspaceId, string TabKey);

/// <summary>How a terminal encodes typed keys before they reach the shell.</summary>
public enum TerminalKeyEncoding
{
    /// <summary>The terminal's own encoding.</summary>
    Default,

    /// <summary>Shift+Return sends a newline the agent reads as a line break rather than a submit.</summary>
    AgentNewline
}

/// <summary>The handle a terminal accessory receives, scoped to one terminal tab (W11).</summary>
public interface ITerminalAccessoryApi
{
    /// <summary>The terminal's workspace.</summary>
    string WorkspaceId { get; }

    /// <summary>The terminal's tab key.</summary>
    string TabKey { get; }

    /// <summary>Writes text into the terminal as if typed.</summary>
    /// <param name="data">The text.</param>
    void Write(string data);

    /// <summary>Reads the last lines of the terminal's screen and scrollback.</summary>
    /// <param name="lines">How many lines to read.</param>
    /// <param name="omitFaint">Blanks faint cells: the placeholders and suggestions a TUI draws in its input line.</param>
    /// <returns>The lines, oldest first.</returns>
    IReadOnlyList<string> BufferTail(int lines, bool omitFaint = false);

    /// <summary>Sets how typed keys are encoded.</summary>
    /// <param name="encoding">The encoding.</param>
    void SetKeyEncoding(TerminalKeyEncoding encoding);
}

/// <summary>A model an agent launcher offers.</summary>
/// <param name="Id">The model id passed back in <see cref="LauncherCommandOptions.Model"/>.</param>
/// <param name="Label">Shown in the picker.</param>
/// <param name="Icon">The maker's mark, a Remix Icon name or <c>asset:&lt;path&gt;</c>.</param>
public sealed record LauncherModel(string Id, string Label, string? Icon = null);

/// <summary>What an agent launcher builds its command from.</summary>
public sealed record LauncherCommandOptions
{
    /// <summary>The chosen model.</summary>
    public string? Model { get; init; }

    /// <summary>A system prompt for the agent.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The agent's opening prompt.</summary>
    public string? InitialPrompt { get; init; }

    /// <summary>An agent session to resume.</summary>
    public string? ResumeSessionId { get; init; }
}

/// <summary>Whether a launcher can be used now.</summary>
/// <param name="Available">Whether it can.</param>
/// <param name="Reason">Why not, when it cannot.</param>
public sealed record LauncherAvailability(bool Available, string? Reason = null);

/// <summary>Why a file icon is being resolved.</summary>
public enum FileIconKind
{
    /// <summary>A file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory
}

/// <summary>What the Changes panel compares (W18).</summary>
public abstract record GitDiffScope;

/// <summary>The workspace against its comparison target.</summary>
public sealed record BranchDiffScope : GitDiffScope;

/// <summary>Uncommitted changes: HEAD against the working tree, staged content included.</summary>
public sealed record UncommittedDiffScope : GitDiffScope;

/// <summary>One commit against its first parent.</summary>
/// <param name="Sha">The commit.</param>
public sealed record CommitDiffScope(string Sha) : GitDiffScope;

/// <summary>The workspace against a pinned comparison target.</summary>
/// <param name="BaseRef">The branch or ref to compare against.</param>
public sealed record PinnedDiffScope(string BaseRef) : GitDiffScope;

/// <summary>Options for <see cref="IPluginUIContext.OpenTerminalAsync"/>.</summary>
public sealed record TerminalOpenOptions
{
    /// <summary>A command typed into the new shell once it starts.</summary>
    public string? Command { get; init; }

    /// <summary>The tab key to open or, when that tab already exists, to select.</summary>
    public string? TabKey { get; init; }

    /// <summary>The centre group to place a new tab in; the last-focused centre group otherwise.</summary>
    public string? GroupId { get; init; }
}

/// <summary>Options for <see cref="IPluginUIContext.PickFileAsync"/>.</summary>
/// <param name="WorkspaceId">The workspace the picker starts in.</param>
/// <param name="Directory">Picks a folder instead of a file.</param>
public sealed record FilePickOptions(string? WorkspaceId = null, bool Directory = false);

/// <summary>The tone of a notification.</summary>
public enum PluginNotificationKind
{
    /// <summary>Information.</summary>
    Info,

    /// <summary>A failure.</summary>
    Error
}

/// <summary>One client-local preference, namespaced by <see cref="PluginIdentity.PreferenceKey"/> and qualified by host endpoint.</summary>
public interface IPluginPreference
{
    /// <summary>Reads the value.</summary>
    /// <returns>The value, or <see langword="null"/> when unset.</returns>
    string? Get();

    /// <summary>Writes the value.</summary>
    /// <param name="value">The value.</param>
    void Set(string value);

    /// <summary>Removes the value.</summary>
    void Remove();
}