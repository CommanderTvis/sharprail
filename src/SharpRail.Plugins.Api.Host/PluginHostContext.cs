namespace SharpRail.Plugins.Api.Host;

/// <summary>
/// The context passed to <see cref="PluginHostModule.ActivateAsync"/>: the closed set of host capabilities
/// (H1–H10, H12–H15, H17 and external files in the module spec). Every registration made through it belongs to
/// one activation, is recorded by the runtime and is torn down on dispose, so a plugin keeps no cleanup
/// bookkeeping of its own. A context outlives its activation only as a dead handle: once the activation is
/// disposed, registrations and publishes made through it are dropped.
/// </summary>
public interface IPluginHostContext
{
    /// <summary>The plugin's id.</summary>
    string Id { get; }

    /// <summary>This activation's logger.</summary>
    IPluginLogger Log { get; }

    /// <summary>The absolute path of the manifest's assets directory, or <see langword="null"/> when none is declared (H15).</summary>
    string? AssetsDirectory { get; }

    /// <summary>
    /// Registers the handler for one of the contract's request methods (H1). Params are validated against the
    /// method's params type before the handler runs. While the plugin is disabled the method answers
    /// <see cref="PluginCallError.Disabled"/> instead.
    /// </summary>
    /// <typeparam name="TParams">The method's params.</typeparam>
    /// <typeparam name="TResult">The method's result.</typeparam>
    /// <param name="method">The method, from this plugin's contract.</param>
    /// <param name="handler">Called with the validated params and the calling client.</param>
    void Method<TParams, TResult>(PluginMethod<TParams, TResult> method, Func<TParams, PluginCall, CancellationToken, ValueTask<TResult>> handler);

    /// <summary>
    /// Publishes a payload on one of the contract's channels (H2), to every subscriber or, with
    /// <paramref name="target"/>, to that client only. A no-op while the plugin is disabled and for a disposed
    /// activation.
    /// </summary>
    /// <typeparam name="TPayload">The channel's payload.</typeparam>
    /// <param name="channel">The channel, from this plugin's contract.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="target">Addresses the publish to the client that made this call.</param>
    void Publish<TPayload>(PluginChannel<TPayload> channel, TPayload payload, PluginCall? target = null);

    /// <summary>
    /// Mounts the plugin's HTTP route under <c>/plugin/&lt;id&gt;/</c> on the host's loopback server (H3), the
    /// server terminals reach. While the plugin is disabled its route answers 404.
    /// </summary>
    /// <param name="handler">Called for each request under the plugin's route.</param>
    void Route(Func<PluginHttpRequest, CancellationToken, ValueTask<PluginHttpResponse>> handler);

    /// <summary>The base URL the plugin's route is reachable at from processes on the host machine (H3).</summary>
    /// <returns>The URL of <c>/plugin/&lt;id&gt;</c> on the loopback server, without a trailing slash.</returns>
    string PublicBaseUrl();

    /// <summary>
    /// Exposes exact absolute paths outside the worktree for core text editing. The host consults active
    /// providers on every absolute file read or save; this is a file allowlist, not directory access.
    /// </summary>
    /// <param name="provider">Returns, synchronously, the absolute paths exposed for a workspace id.</param>
    void ExternalFiles(Func<string, IReadOnlyList<string>> provider);

    /// <summary>
    /// Registers a tool on the per-terminal MCP surface (H4). While the plugin is disabled the tool is absent
    /// from listings and calls; a call already running finishes.
    /// </summary>
    /// <param name="definition">The tool, usually a <see cref="PluginTool{TParams}"/>.</param>
    void Tool(PluginToolDefinition definition);

    /// <summary>
    /// Registers a contributor of environment variables for shells started after it (H5). A running shell keeps
    /// the environment it was given.
    /// </summary>
    /// <param name="contributor">Called with the spawning terminal; returns variables to add.</param>
    void TerminalEnvironment(Func<TerminalRef, IReadOnlyDictionary<string, string>> contributor);

    /// <summary>Mints, or returns the existing, opaque identity token for a terminal (H6).</summary>
    /// <param name="terminal">The terminal.</param>
    /// <returns>The token, valid until the terminal closes.</returns>
    string TerminalToken(TerminalRef terminal);

    /// <summary>Resolves a token minted by <see cref="TerminalToken"/> (H6).</summary>
    /// <param name="token">The token.</param>
    /// <returns>Its terminal, or <see langword="null"/> when unknown.</returns>
    TerminalRef? TerminalForToken(string token);

    /// <summary>Reads a terminal's persisted, broadcast agent record (H7).</summary>
    /// <param name="terminal">The terminal.</param>
    /// <returns>The record, or <see langword="null"/> when none is set.</returns>
    TerminalAgentRecord? AgentRecord(TerminalRef terminal);

    /// <summary>Sets or clears a terminal's agent record (H7). It is persisted, broadcast, and dropped when the terminal closes.</summary>
    /// <param name="terminal">The terminal.</param>
    /// <param name="record">The record, or <see langword="null"/> to clear it.</param>
    void SetAgentRecord(TerminalRef terminal, TerminalAgentRecord? record);

    /// <summary>Observes terminal lifecycle events: spawned, exited, closed, agent changed (H8).</summary>
    /// <param name="handler">Called with each event.</param>
    void OnTerminal(Action<TerminalEvent> handler);

    /// <summary>Lists the terminals the host currently holds, with their shells' process ids (H8).</summary>
    /// <returns>The terminals.</returns>
    IReadOnlyList<TerminalProcess> Terminals();

    /// <summary>Resolves the workspace a process belongs to, by its pid or any ancestor's being a terminal shell (H8).</summary>
    /// <param name="pid">The process id.</param>
    /// <returns>The workspace id, or <see langword="null"/> when unknown.</returns>
    string? WorkspaceForProcess(int pid);

    /// <summary>Registers a hook offering a prefill when a shell starts again for a tab that carries an agent record (H9).</summary>
    /// <param name="hook">Called with the terminal and its record; returns a prefill or <see langword="null"/>.</param>
    void RevivePrefill(Func<TerminalRef, TerminalAgentRecord, RevivePrefill?> hook);

    /// <summary>Writes into a terminal host-side, bypassing client attachment (H10).</summary>
    /// <param name="terminal">The terminal.</param>
    /// <param name="data">The text, sent as UTF-8.</param>
    void WriteTerminal(TerminalRef terminal, string data);

    /// <summary>Lists the host's open projects (H12).</summary>
    /// <returns>The projects.</returns>
    IReadOnlyList<HostProject> Projects();

    /// <summary>Lists workspaces, from each project's worktrees (H12).</summary>
    /// <param name="projectId">Limits the list to one project.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workspaces.</returns>
    ValueTask<IReadOnlyList<HostWorkspace>> WorkspacesAsync(string? projectId = null, CancellationToken cancellationToken = default);

    /// <summary>Reads one workspace (H12).</summary>
    /// <param name="id">The workspace id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workspace, or <see langword="null"/> when it does not exist.</returns>
    ValueTask<HostWorkspace?> WorkspaceAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Starts watching a workspace's files for this activation, feeding <see cref="OnFilesChanged"/> (H12).</summary>
    /// <param name="id">The workspace id.</param>
    /// <param name="cancellationToken">Cancels starting the watch.</param>
    /// <returns>Completes when the watch is running.</returns>
    ValueTask WatchWorkspaceAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Observes workspace lifecycle events (H12).</summary>
    /// <param name="handler">Called with each event.</param>
    void OnWorkspace(Action<WorkspaceEvent> handler);

    /// <summary>Observes coalesced filesystem changes in watched workspaces (H12).</summary>
    /// <param name="handler">Called with each batch.</param>
    void OnFilesChanged(Action<WorkspaceFilesChanged> handler);

    /// <summary>Reads the plugin's settings namespace, validated and with defaults filled (H13).</summary>
    /// <typeparam name="T">The contract's settings type.</typeparam>
    /// <returns>The current settings.</returns>
    T Settings<T>() where T : class;

    /// <summary>Observes the plugin's settings namespace (H13); not called for the value current at registration.</summary>
    /// <typeparam name="T">The contract's settings type.</typeparam>
    /// <param name="handler">Called with the settings after each change.</param>
    void OnSettings<T>(Action<T> handler) where T : class;

    /// <summary>Reads JSON state at <see cref="PluginIdentity.StateFile"/> under the host state directory (H14).</summary>
    /// <typeparam name="T">The state's type.</typeparam>
    /// <param name="name">The state file name.</param>
    /// <param name="fallback">Returned when the file is missing or does not parse.</param>
    /// <returns>The state, or <paramref name="fallback"/>.</returns>
    T ReadState<T>(string name, T fallback);

    /// <summary>
    /// Writes JSON state at <see cref="PluginIdentity.StateFile"/> under the host state directory, atomically
    /// (H14). It survives the plugin being disabled or replaced.
    /// </summary>
    /// <typeparam name="T">The state's type.</typeparam>
    /// <param name="name">The state file name.</param>
    /// <param name="value">The value, serialized with <see cref="PluginJson.Options"/>.</param>
    void WriteState<T>(string name, T value);

    /// <summary>
    /// Runs Git through the host's bounded runner (H17), which disables terminal prompts, runs without a
    /// console window and sets <c>GIT_OPTIONAL_LOCKS=0</c> so a read never looks like a repository change.
    /// </summary>
    /// <param name="cwd">The directory to run Git in.</param>
    /// <param name="args">The Git arguments.</param>
    /// <param name="options">Timeout and network access.</param>
    /// <param name="cancellationToken">Kills Git when cancelled.</param>
    /// <returns>The result; a non-zero exit is a result, not an exception.</returns>
    ValueTask<GitRunResult> GitAsync(string cwd, IReadOnlyList<string> args, GitRunOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Resolves a handle on a dependency declared in this plugin's manifest.</summary>
    /// <param name="contract">The dependency's contract.</param>
    /// <returns>The handle, dispatching in process.</returns>
    /// <exception cref="InvalidOperationException">The contract's plugin is not a declared dependency.</exception>
    IPluginDependencyHandle Dependency(PluginContract contract);
}

/// <summary>
/// A plugin's host half. An external plugin's host assembly holds exactly one public concrete subclass with a
/// public parameterless constructor; a builtin plugin's instance is listed in the host's builtin array. Each
/// activation calls <see cref="ActivateAsync"/> with a fresh context and a new activation id; anything arriving
/// from a stale activation (a late publish, a forgotten timer) is dropped.
/// </summary>
/// <example>
/// <code>
/// public sealed class TodoHost : PluginHostModule
/// {
///     public override PluginContract Contract =&gt; TodoContract.Contract;
///     public override ValueTask&lt;PluginDisposer?&gt; ActivateAsync(IPluginHostContext context)
///     {
///         context.Method(TodoContract.List, (_, _, _) =&gt; ValueTask.FromResult(context.ReadState("todos", Array.Empty&lt;string&gt;())));
///         return ValueTask.FromResult&lt;PluginDisposer?&gt;(null);
///     }
/// }
/// </code>
/// </example>
public abstract class PluginHostModule
{
    /// <summary>The plugin's wire contract; its id must equal the manifest's.</summary>
    public abstract PluginContract Contract { get; }

    /// <summary>Activates the plugin. A throw marks the plugin failed with the exception's message, and its dependents refused.</summary>
    /// <param name="context">This activation's context.</param>
    /// <returns>A disposer to run when the activation ends, or <see langword="null"/>.</returns>
    public abstract ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context);
}