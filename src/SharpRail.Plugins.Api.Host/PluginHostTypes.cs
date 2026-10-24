namespace SharpRail.Plugins.Api.Host;

/// <summary>The logger handed to a host half as <see cref="IPluginHostContext.Log"/>, one level per method.</summary>
public interface IPluginLogger
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
/// The cleanup a host half's activation may return. It runs after routing has stopped and in-flight work of the
/// activation has drained. On a toggle the runtime awaits it, bounded by a timeout; on host shutdown it is
/// started and not awaited.
/// </summary>
/// <returns>Completes when cleanup is done.</returns>
public delegate ValueTask PluginDisposer();

/// <summary>Identifies the client that called a method handler, so a publish can be addressed back to it.</summary>
/// <param name="ClientKey">Opaque key of the calling client: one app instance connected to this host.</param>
public sealed record PluginCall(string ClientKey);

/// <summary>A terminal lifecycle event, delivered to <see cref="IPluginHostContext.OnTerminal"/> observers.</summary>
/// <param name="Terminal">The terminal the event concerns.</param>
public abstract record TerminalEvent(TerminalRef Terminal);

/// <summary>A terminal's shell started.</summary>
/// <param name="Terminal">The terminal.</param>
/// <param name="Pid">The shell's process id.</param>
public sealed record TerminalSpawned(TerminalRef Terminal, int Pid) : TerminalEvent(Terminal);

/// <summary>A terminal's shell exited; the tab may still be open.</summary>
/// <param name="Terminal">The terminal.</param>
/// <param name="ExitCode">The exit code, or 128 plus the signal that ended it.</param>
public sealed record TerminalExited(TerminalRef Terminal, int ExitCode) : TerminalEvent(Terminal);

/// <summary>A terminal's tab was closed and its session forgotten, agent record included.</summary>
/// <param name="Terminal">The terminal.</param>
public sealed record TerminalClosed(TerminalRef Terminal) : TerminalEvent(Terminal);

/// <summary>A terminal's agent record was set, replaced or cleared.</summary>
/// <param name="Terminal">The terminal.</param>
/// <param name="Record">The new record, or <see langword="null"/> when cleared.</param>
public sealed record TerminalAgentChanged(TerminalRef Terminal, TerminalAgentRecord? Record) : TerminalEvent(Terminal);

/// <summary>
/// A terminal's shell-integration prompt state changed (OSC 133): it waits at a prompt (A or B, or after D) or runs a
/// command (C). <paramref name="Foreground"/> says the mark arrived while a program other than the shell held the
/// terminal, so that program (an agent) reported its own prompt; a shell's own marks around a program carry false.
/// Terminals whose output carries no marks raise none, so consumers keep their own signals then.
/// </summary>
/// <param name="Terminal">The terminal.</param>
/// <param name="AtPrompt">True at a prompt, false while a command runs.</param>
/// <param name="LastExitCode">The last finished command's exit code, when reported.</param>
/// <param name="Foreground">Whether a program other than the shell emitted the mark.</param>
public sealed record TerminalPromptChanged(TerminalRef Terminal, bool AtPrompt, int? LastExitCode, bool Foreground) : TerminalEvent(Terminal);

/// <summary>A known terminal and its shell's process id.</summary>
/// <param name="Terminal">The terminal.</param>
/// <param name="Pid">The shell's process id, or <see langword="null"/> when it has exited.</param>
public sealed record TerminalProcess(TerminalRef Terminal, int? Pid);

/// <summary>
/// The prefill a <see cref="IPluginHostContext.RevivePrefill"/> hook offers for a terminal whose shell starts
/// again for a tab that carries an agent record. The attaching client types it once the shell is up, so it
/// never lands before the prompt.
/// </summary>
/// <param name="Text">Text to type into the revived shell, or null to leave another plugin's text unchanged.</param>
/// <param name="Submit">Whether to press Return after it rather than leave it staged.</param>
public sealed record RevivePrefill(string? Text = null, bool Submit = false)
{
    /// <summary>
    /// Run <see cref="Text"/> as the revived terminal's program, through the user's login shell, instead of typing it:
    /// the terminal drops into an interactive shell in the same directory when it exits. A Windows host types it
    /// instead, as a non-submitted offer.
    /// </summary>
    public bool Program { get; init; }

    /// <summary>
    /// A command line that starts a new session of the same agent instead, typed and submitted when the user chooses
    /// Start fresh on the revived tab's resume offer; null leaves another plugin's command, or none.
    /// </summary>
    public string? Fresh { get; init; }
}

/// <summary>A workspace lifecycle event, delivered to <see cref="IPluginHostContext.OnWorkspace"/> observers.</summary>
public abstract record WorkspaceEvent;

/// <summary>The host created a workspace.</summary>
/// <param name="Workspace">The new workspace.</param>
public sealed record WorkspaceCreated(HostWorkspace Workspace) : WorkspaceEvent;

/// <summary>A workspace's label or branch changed.</summary>
/// <param name="Workspace">The workspace as it is now.</param>
public sealed record WorkspaceUpdated(HostWorkspace Workspace) : WorkspaceEvent;

/// <summary>The host removed a workspace.</summary>
/// <param name="ProjectId">The owning project's id.</param>
/// <param name="Id">The removed workspace's id.</param>
public sealed record WorkspaceRemoved(string ProjectId, string Id) : WorkspaceEvent;

/// <summary>One coalesced batch of filesystem changes in a watched workspace.</summary>
/// <param name="WorkspaceId">The workspace.</param>
/// <param name="Paths">Changed paths, relative to the workspace root.</param>
/// <param name="Truncated">Whether the batch overflowed and some paths were dropped.</param>
public sealed record WorkspaceFilesChanged(string WorkspaceId, IReadOnlyList<string> Paths, bool Truncated);

/// <summary>Options for <see cref="IPluginHostContext.GitAsync"/>.</summary>
/// <param name="Timeout">Kills Git after this long; the runner's default when <see langword="null"/>.</param>
/// <param name="Network">Allows Git network access, which is denied by default.</param>
public sealed record GitRunOptions(TimeSpan? Timeout = null, bool Network = false);

/// <summary>Why a Git run produced no exit status.</summary>
public enum GitRunFailure
{
    /// <summary>Killed when its timeout elapsed.</summary>
    Timeout,

    /// <summary>Git could not be started.</summary>
    Launch
}

/// <summary>The result of <see cref="IPluginHostContext.GitAsync"/>.</summary>
/// <param name="Ok">Whether Git exited with status 0.</param>
/// <param name="Out">Captured standard output.</param>
/// <param name="Err">Captured standard error.</param>
/// <param name="Failure">Set when <paramref name="Ok"/> is false because Git timed out or never launched.</param>
public sealed record GitRunResult(bool Ok, string Out, string Err, GitRunFailure? Failure = null);

/// <summary>An HTTP request reaching a plugin's route on the host's loopback server.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Subpath">The path under <c>/plugin/&lt;id&gt;/</c>, without a leading slash.</param>
/// <param name="Query">The query string, including its leading <c>?</c>, or empty.</param>
/// <param name="Headers">Request headers; repeated headers are joined with commas.</param>
/// <param name="Body">The request body.</param>
public sealed record PluginHttpRequest(string Method, string Subpath, string Query, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body);

/// <summary>The response a plugin route returns.</summary>
/// <param name="Status">The HTTP status code.</param>
public sealed record PluginHttpResponse(int Status)
{
    /// <summary>The response content type.</summary>
    public string ContentType { get; init; } = "text/plain; charset=utf-8";

    /// <summary>The response body.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Additional response headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>Where an agent plugin puts a <see cref="PromptContribution"/> in the instructions of a session (H20).</summary>
public enum PromptPlacement
{
    /// <summary>A list item in the guidance on SharpRail's MCP tools, ahead of the host's own text.</summary>
    ToolList,

    /// <summary>A block of text after the host's own text.</summary>
    Section,
}

/// <summary>
/// Text a plugin adds to the instructions agent plugins give sessions (H20). The text is data: it is bounded in size
/// and carries no control characters other than line feeds and tabs, and is placed as written.
/// </summary>
public sealed class PromptContribution
{
    /// <summary>The largest text, in characters.</summary>
    public const int MaxTextLength = 16_000;

    /// <summary>
    /// The contribution's stable identifier within its plugin: lower-case letters, digits, <c>.</c>, <c>-</c> and
    /// <c>_</c>, at most 64 characters. Registering an identifier twice in one activation is an error.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The text. A <see cref="PromptPlacement.ToolList"/> item is written without a list marker.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The short title the instructions view shows, such as <c>Visualize</c>; null shows one derived from <see cref="Id"/>.
    /// Projects turn the text off by its full identifier, <c>plugin.&lt;plugin id&gt;.&lt;Id&gt;</c>.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>One line on what the text tells the agent, shown under <see cref="Title"/>.</summary>
    public string? Description { get; init; }

    /// <summary>Where the text goes. The default is <see cref="PromptPlacement.ToolList"/>.</summary>
    public PromptPlacement Placement { get; init; } = PromptPlacement.ToolList;

    /// <summary>
    /// The agent plugin id the text is for, such as <c>claude-code</c> or <c>codex</c>, or <see langword="null"/>
    /// for every agent.
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>
    /// The order hint. Contributions are placed by ascending hint, those with the same hint in activation order. The
    /// host's own text always comes first.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// A condition evaluated each time instructions are composed, which is on every activation of an agent plugin and
    /// for every terminal it starts; the text is left out while it returns <see langword="false"/>. It must be quick and
    /// must not throw (a throw leaves the text out). <see langword="null"/> always includes the text.
    /// </summary>
    public Func<bool>? When { get; init; }
}