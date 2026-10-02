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
public sealed record RevivePrefill(string? Text = null, bool Submit = false);

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