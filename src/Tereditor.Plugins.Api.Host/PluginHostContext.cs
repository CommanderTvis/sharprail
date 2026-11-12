using System.Text.Json.Serialization.Metadata;

namespace SharpRail.Plugins.Api.Host;

/// <summary>
/// The context passed to <see cref="PluginHostModule.ActivateAsync"/>: the closed set of host capabilities
/// (H1–H10, H12–H15, H17–H20 and external files in the module spec). Every registration made through it belongs to
/// one activation, is recorded by the runtime and is torn down on dispose, so a plugin keeps no cleanup
/// bookkeeping of its own. A context outlives its activation only as a dead handle: once the activation is
/// disposed, registrations and publishes made through it are dropped.
/// </summary>
public interface IPluginHostContext
{
    /// <summary>The plugin's id.</summary>
    [PluginAmbient, PluginCrossing(PluginCrossing.LocalInRunner)]
    string Id { get; }

    /// <summary>This activation's logger.</summary>
    [PluginAmbient, PluginCrossing(PluginCrossing.LocalInRunner)]
    IPluginLogger Log { get; }

    /// <summary>The absolute path of the manifest's assets directory, or <see langword="null"/> when none is declared (H15).</summary>
    [PluginAmbient, PluginCrossing(PluginCrossing.LocalInRunner)]
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
    [PluginAmbient]
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
    [PluginAmbient]
    void Publish<TPayload>(PluginChannel<TPayload> channel, TPayload payload, PluginCall? target = null);

    /// <summary>
    /// Mounts the plugin's HTTP route under <c>/plugin/&lt;id&gt;/</c> on the host's loopback server (H3), the
    /// server terminals reach. While the plugin is disabled its route answers 404.
    /// </summary>
    /// <param name="handler">Called for each request under the plugin's route.</param>
    [PluginPermission("net.loopback.route")]
    void Route(Func<PluginHttpRequest, CancellationToken, ValueTask<PluginHttpResponse>> handler);

    /// <summary>The base URL the plugin's route is reachable at from processes on the host machine (H3).</summary>
    /// <returns>The URL of <c>/plugin/&lt;id&gt;</c> on the loopback server, without a trailing slash.</returns>
    [PluginPermission("net.loopback.route"), PluginCrossing(PluginCrossing.Blocking)]
    string PublicBaseUrl();

    /// <summary>
    /// Exposes exact absolute paths outside the worktree for core text editing. The host consults active
    /// providers on every absolute file read or save; this is a file allowlist, not directory access.
    /// </summary>
    /// <param name="provider">Returns, synchronously, the absolute paths exposed for a workspace id.</param>
    [PluginPermission("files.external")]
    void ExternalFiles([PluginCrossing(PluginCrossing.Live)] Func<string, IReadOnlyList<string>> provider);

    /// <summary>
    /// Registers a tool on the per-terminal MCP surface (H4). While the plugin is disabled the tool is absent
    /// from listings and calls; a call already running finishes. Its parameters type must be named by a
    /// registered context (<see cref="PluginHostModule.Json"/>); otherwise registering it throws.
    /// </summary>
    /// <param name="definition">The tool, usually a <see cref="PluginTool{TParams}"/>.</param>
    [PluginPermission("agent.instructions")]
    void Tool(PluginToolDefinition definition);

    /// <summary>
    /// Registers a contributor of environment variables for shells started after it (H5). A running shell keeps
    /// the environment it was given.
    /// </summary>
    /// <param name="contributor">Called with the spawning terminal; returns variables to add.</param>
    [PluginPermission("shell.environment")]
    void TerminalEnvironment([PluginCrossing(PluginCrossing.UnderLock)] Func<TerminalRef, IReadOnlyDictionary<string, string>> contributor);


    /// <summary>
    /// Contributes to the launch of another plugin's agent, such as a wrapper command, environment variables or extra
    /// arguments, applied wherever that plugin builds a command line on the host, such as a revive offer (H18).
    /// Contributions are active only while this plugin is.
    /// </summary>
    /// <param name="contributor">Called with the agent's id (for example <c>claude</c> or <c>codex</c>); returns its contribution or <see langword="null"/>.</param>
    [PluginPermission("shell.environment")]
    void AgentLaunch([PluginCrossing(PluginCrossing.Live)] Func<string, AgentLaunchContribution?> contributor);

    /// <summary>
    /// The contributions of every active plugin for an agent, in plugin activation order, for the agent's own plugin to
    /// apply with <see cref="AgentLaunchComposition.Apply"/> (H18).
    /// </summary>
    /// <param name="agentId">The agent's id.</param>
    /// <returns>The contributions, outermost first.</returns>
    [PluginPermission("shell.environment"), PluginCrossing(PluginCrossing.Mirrored)]
    IReadOnlyList<AgentLaunchContribution> AgentLaunchContributions(string agentId);

    /// <summary>Mints, or returns the existing, opaque identity token for a terminal (H6).</summary>
    /// <param name="terminal">The terminal.</param>
    /// <returns>The token, valid until the terminal closes.</returns>
    [PluginAmbient, PluginCrossing(PluginCrossing.Blocking)]
    string TerminalToken(TerminalRef terminal);

    /// <summary>
    /// The environment a shell started for this terminal would get (H5, H22): the host's own, sanitised the way terminals are, with the
    /// workspace environment steps and every plugin's contribution applied. A host without terminals returns the process environment.
    /// Resolves the workspace environment, so it runs off the UI thread.
    /// </summary>
    /// <param name="terminal">The terminal the environment is for.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The variables, in full.</returns>
    [PluginPermission("terminal.read")]
    ValueTask<IReadOnlyDictionary<string, string>> ShellEnvironmentAsync(TerminalRef terminal, CancellationToken cancellationToken = default)
    {
        var plain = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) plain[(string)entry.Key] = (string?)entry.Value ?? "";
        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(plain);
    }

    /// <summary>Resolves a token minted by <see cref="TerminalToken"/> (H6).</summary>
    /// <param name="token">The token.</param>
    /// <returns>Its terminal, or <see langword="null"/> when unknown.</returns>
    [PluginAmbient, PluginCrossing(PluginCrossing.Blocking)]
    TerminalRef? TerminalForToken(string token);

    /// <summary>Reads a terminal's persisted, broadcast agent record (H7).</summary>
    /// <param name="terminal">The terminal.</param>
    /// <returns>The record, or <see langword="null"/> when none is set.</returns>
    [PluginPermission("agent.records"), PluginCrossing(PluginCrossing.Mirrored)]
    TerminalAgentRecord? AgentRecord(TerminalRef terminal);

    /// <summary>Sets or clears a terminal's agent record (H7). It is persisted, broadcast, and dropped when the terminal closes.</summary>
    /// <param name="terminal">The terminal.</param>
    /// <param name="record">The record, or <see langword="null"/> to clear it.</param>
    [PluginPermission("agent.records")]
    void SetAgentRecord(TerminalRef terminal, TerminalAgentRecord? record);

    /// <summary>Reads the title a terminal's tab shows: a user rename, an agent's title or the program's.</summary>
    /// <param name="terminal">The terminal.</param>
    /// <returns>The title, or <see langword="null"/> when the tab has none beyond its default.</returns>
    [PluginAmbient, PluginCrossing(PluginCrossing.Mirrored)]
    string? TerminalTitle(TerminalRef terminal) => null;

    /// <summary>Registers a source of running agent sessions that are not terminals, such as chats (the Processes pane lists them).</summary>
    /// <param name="source">Called for each snapshot; returns the sessions running now.</param>
    [PluginPermission("agent.records")]
    void AgentSessions([PluginCrossing(PluginCrossing.Live)] Func<IReadOnlyList<AgentSession>> source)
    {
    }

    /// <summary>Lists the running agent sessions every plugin's source reports.</summary>
    /// <returns>The sessions.</returns>
    [PluginPermission("agent.records"), PluginCrossing(PluginCrossing.Mirrored)]
    IReadOnlyList<AgentSession> ListAgentSessions() => [];

    /// <summary>Observes terminal lifecycle events: spawned, exited, closed, agent changed (H8).</summary>
    /// <param name="handler">Called with each event.</param>
    [PluginPermission("terminal.read")]
    void OnTerminal(Action<TerminalEvent> handler);

    /// <summary>Lists the terminals the host currently holds, with their shells' process ids (H8).</summary>
    /// <returns>The terminals.</returns>
    [PluginPermission("terminal.read", Condition = "only the pids; the tabs are ambient"), PluginCrossing(PluginCrossing.Mirrored)]
    IReadOnlyList<TerminalProcess> Terminals();

    /// <summary>Resolves the workspace a process belongs to, by its pid or any ancestor's being a terminal shell (H8).</summary>
    /// <param name="pid">The process id.</param>
    /// <returns>The workspace id, or <see langword="null"/> when unknown.</returns>
    [PluginPermission("terminal.read"), PluginCrossing(PluginCrossing.Blocking)]
    string? WorkspaceForProcess(int pid);

    /// <summary>Registers a hook offering a prefill when a shell starts again for a tab that carries an agent record (H9).</summary>
    /// <param name="hook">Called with the terminal and its record; returns a prefill or <see langword="null"/>.</param>
    [PluginPermission("terminal.input")]
    void RevivePrefill([PluginCrossing(PluginCrossing.UnderLock)] Func<TerminalRef, TerminalAgentRecord, RevivePrefill?> hook);

    /// <summary>Writes into a terminal host-side, bypassing client attachment (H10).</summary>
    /// <param name="terminal">The terminal.</param>
    /// <param name="data">The text, sent as UTF-8.</param>
    [PluginPermission("terminal.input")]
    void WriteTerminal(TerminalRef terminal, string data);

    /// <summary>Lists the host's open projects (H12).</summary>
    /// <returns>The projects.</returns>
    [PluginAmbient, PluginCrossing(PluginCrossing.Mirrored)]
    IReadOnlyList<HostProject> Projects();

    /// <summary>Lists workspaces, from each project's worktrees (H12).</summary>
    /// <param name="projectId">Limits the list to one project.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workspaces.</returns>
    [PluginAmbient]
    ValueTask<IReadOnlyList<HostWorkspace>> WorkspacesAsync(string? projectId = null, CancellationToken cancellationToken = default);

    /// <summary>Reads one workspace (H12).</summary>
    /// <param name="id">The workspace id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workspace, or <see langword="null"/> when it does not exist.</returns>
    [PluginAmbient]
    ValueTask<HostWorkspace?> WorkspaceAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Starts watching a workspace's files for this activation, feeding <see cref="OnFilesChanged"/> (H12).
    /// Repeated/concurrent requests share one lease; workspace removal, project close or activation teardown releases it.</summary>
    /// <param name="id">The workspace id.</param>
    /// <param name="cancellationToken">Cancels starting the watch.</param>
    /// <returns>Completes when the watch is running.</returns>
    [PluginAmbient]
    ValueTask WatchWorkspaceAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Observes workspace lifecycle events (H12).</summary>
    /// <param name="handler">Called with each event.</param>
    [PluginAmbient]
    void OnWorkspace(Action<WorkspaceEvent> handler);

    /// <summary>Observes coalesced filesystem changes in watched workspaces (H12).</summary>
    /// <param name="handler">Called with each batch.</param>
    [PluginPermission("files.read")]
    void OnFilesChanged(Action<WorkspaceFilesChanged> handler);

    /// <summary>Reads the plugin's settings namespace, validated and with defaults filled (H13).</summary>
    /// <typeparam name="T">The contract's settings type.</typeparam>
    /// <returns>The current settings.</returns>
    [PluginAmbient, PluginCrossing(PluginCrossing.Mirrored)]
    T Settings<T>() where T : class;

    /// <summary>Observes the plugin's settings namespace (H13); not called for the value current at registration.</summary>
    /// <typeparam name="T">The contract's settings type.</typeparam>
    /// <param name="handler">Called with the settings after each change.</param>
    [PluginAmbient, PluginCrossing(PluginCrossing.LocalInRunner)]
    void OnSettings<T>(Action<T> handler) where T : class;

    /// <summary>Reads JSON state at <see cref="PluginIdentity.StateFile"/> under the host state directory (H14).</summary>
    /// <typeparam name="T">The state's type, named by the contract's context or <see cref="PluginHostModule.Json"/>.</typeparam>
    /// <param name="name">The state file name.</param>
    /// <param name="fallback">Returned when the file is missing or does not parse.</param>
    /// <returns>The state, or <paramref name="fallback"/>.</returns>
    [PluginAmbient, PluginCrossing(PluginCrossing.LocalInRunner)]
    T ReadState<T>(string name, T fallback);

    /// <summary>
    /// Writes JSON state at <see cref="PluginIdentity.StateFile"/> under the host state directory, atomically
    /// (H14). It survives the plugin being disabled or replaced.
    /// </summary>
    /// <typeparam name="T">The state's type, named by the contract's context or <see cref="PluginHostModule.Json"/>.</typeparam>
    /// <param name="name">The state file name.</param>
    /// <param name="value">The value, serialized with <see cref="PluginJson.Options"/>.</param>
    [PluginAmbient, PluginCrossing(PluginCrossing.LocalInRunner)]
    void WriteState<T>(string name, T value);

    /// <summary>
    /// The absolute path of this plugin's own directory for files under the host state directory (H19),
    /// <c>plugin-state/&lt;id&gt;/files</c>, or <see langword="null"/> when the host keeps no state directory. It
    /// holds what JSON state cannot, such as a downloaded executable, and survives the plugin being disabled or
    /// replaced. The directory is not created until the plugin does so.
    /// </summary>
    [PluginAmbient, PluginCrossing(PluginCrossing.LocalInRunner)]
    string? StateDirectory { get; }

    /// <summary>
    /// Contributes one list item to the guidance on SharpRail's MCP tools that agent plugins put in the
    /// instructions of sessions they launch (H20), for every agent. It is a <see cref="PromptContribution"/> with
    /// <see cref="PromptPlacement.ToolList"/> and a generated identifier. It is offered only while the plugin is
    /// enabled, so a disabled plugin leaves no trace in the instructions.
    /// </summary>
    /// <param name="text">The item's text, without a leading list marker. It describes the plugin's tools and when to use them.</param>
    [PluginPermission("agent.instructions")]
    void ToolGuidance(string text);

    /// <summary>
    /// Contributes text to the instructions agent plugins give sessions (H20). It is offered only while the plugin
    /// is enabled, and only while its <see cref="PromptContribution.When"/> holds.
    /// </summary>
    /// <param name="contribution">The contribution.</param>
    /// <exception cref="ArgumentException">The identifier is malformed or already registered by this plugin, or the text is empty, too long or has control characters.</exception>
    [PluginPermission("agent.instructions")]
    void Prompt(PromptContribution contribution);

    /// <summary>
    /// Provides a language server (H21). The server is in the host's catalog only while the plugin is enabled:
    /// disabling the plugin stops its running sessions and removes it from listings, while the user's settings for it
    /// are kept for when it returns.
    /// </summary>
    /// <param name="spec">The server.</param>
    /// <exception cref="ArgumentException">The id is malformed or already provided by an active plugin.</exception>
    [PluginPermission("language.server")]
    void LanguageServer(LanguageServerSpec spec);

    /// <summary>
    /// Owns the workspace environment policy (H22): the order the registered steps run in. Before a shell starts for a
    /// tab with none running the host runs the enabled plugins' steps (<see cref="WorkspaceEnvironmentStep"/>) on a pool
    /// thread in the policy's order (ties by plugin id), each seeing what the earlier ones produced, and gives the shell
    /// the variables that differ from the host's own. The host bounds the wait, shares a resolution already running for
    /// the workspace and keeps the last good result for a shell that cannot wait. A step that throws is logged and skipped.
    /// Without an active policy no step runs and shells get the plain environment. One owner is expected (<c>env</c>);
    /// with several, the first by plugin id wins.
    /// </summary>
    /// <param name="policy">Orders the steps.</param>
    [PluginPermission("shell.environment")]
    void WorkspaceEnvironment(IWorkspaceEnvironmentPolicy policy);

    /// <summary>
    /// Provides this plugin's one step of every workspace's shell environment (H23). The policy (H22), not the step,
    /// decides where it runs; a second registration by the same plugin is ignored.
    /// </summary>
    /// <param name="id">The step id the policy orders by, such as <c>mise</c>.</param>
    /// <param name="step">Edits the workspace's environment.</param>
    [PluginPermission("shell.environment")]
    void WorkspaceEnvironmentStep(string id, Func<IWorkspaceEnvironmentEdit, CancellationToken, ValueTask> step);

    /// <summary>
    /// Observes every workspace environment resolution (H22): the workspace's root directory and what the enabled steps
    /// reported through <see cref="IWorkspaceEnvironmentEdit.Report"/>, in step order. The list is empty when nothing applies.
    /// </summary>
    /// <param name="handler">Called on a pool thread after each resolution.</param>
    [PluginPermission("shell.environment")]
    void OnWorkspaceEnvironment(Action<string, IReadOnlyList<WorkspaceEnvironmentFact>> handler);

    /// <summary>
    /// Lists the texts the host and every enabled plugin contribute for one agent and placement (H20): the host's own
    /// first, then the plugins' by order hint and activation order, leaving out those whose condition does not hold. An
    /// agent plugin reads it when it composes the instructions of a session.
    /// </summary>
    /// <param name="agent">The agent plugin id.</param>
    /// <param name="placement">Which texts to list.</param>
    /// <returns>The texts, without list markers.</returns>
    [PluginAmbient, PluginCrossing(PluginCrossing.Mirrored)]
    IReadOnlyList<string> PromptTexts(string agent, PromptPlacement placement);

    /// <summary>
    /// <see cref="PromptTexts(string, PromptPlacement)"/> for a session in one workspace: the host's Code Intelligence
    /// text then names that workspace's languages, with examples on its own symbols. It never waits for the workspace's
    /// files to be read: before the first read it is the workspace-independent text, and a later call gets the result.
    /// </summary>
    /// <param name="agent">The agent plugin id.</param>
    /// <param name="placement">Which texts to list.</param>
    /// <param name="workspace">The workspace's absolute path.</param>
    /// <returns>The texts, without list markers.</returns>
    [PluginAmbient, PluginCrossing(PluginCrossing.Mirrored)]
    IReadOnlyList<string> PromptTexts(string agent, PromptPlacement placement, string workspace) => PromptTexts(agent, placement);

    /// <summary>
    /// Lists every section <see cref="PromptTexts(string, PromptPlacement, string)"/> composes for one agent in one
    /// workspace, both placements, with its stable identifier, title and current text, and whether the workspace's project
    /// turns it off in <c>.sharprail/instructions.toml</c>. Disabled sections are still listed; sections whose plugin is
    /// off or whose condition does not hold are not. It reads the project file and must not run on a UI thread.
    /// </summary>
    /// <param name="agent">The agent plugin id.</param>
    /// <param name="workspace">The workspace's absolute path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sections and the project file's state.</returns>
    [PluginPermission("agent.records")]
    ValueTask<InstructionSectionList> InstructionSectionsAsync(string agent, string workspace, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new InstructionSectionList());

    /// <summary>
    /// Turns one section on or off for the workspace's project by rewriting the disable-list in
    /// <c>.sharprail/instructions.toml</c>; identifiers it does not know are kept. New sessions get the change; running
    /// ones keep their instructions. Every <see cref="OnInstructionSectionsChanged"/> handler hears of it.
    /// </summary>
    /// <param name="agent">The agent plugin id, for the returned listing.</param>
    /// <param name="workspace">The workspace's absolute path.</param>
    /// <param name="id">The section's identifier.</param>
    /// <param name="enabled">Whether it is on.</param>
    /// <param name="cancellationToken">Cancels before the write.</param>
    /// <returns>The listing after the change.</returns>
    /// <exception cref="ArgumentException">The identifier is malformed, or the file is broken and must be fixed by hand first.</exception>
    [PluginPermission("agent.instructions")]
    ValueTask<InstructionSectionList> SetInstructionSectionAsync(string agent, string workspace, string id, bool enabled, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host keeps no instruction settings.");

    /// <summary>Observes changes to any project's <c>.sharprail/instructions.toml</c> made through the host or found on disk; the argument is the project root.</summary>
    /// <param name="handler">Called on a background thread.</param>
    [PluginPermission("agent.records")]
    void OnInstructionSectionsChanged(Action<string> handler) { }

    /// <summary>
    /// The host's process facts, language-server processes and owned-process registry (H27), or <see langword="null"/> on a host that has none.
    /// </summary>
    [PluginPermission("processes.read"), PluginCrossing(PluginCrossing.LocalInRunner)]
    IProcessHost? Processes => null;

    /// <summary>
    /// Lists the guidance every enabled plugin contributed with <see cref="ToolGuidance"/> (H20), in activation
    /// order. It is <see cref="PromptTexts(string, PromptPlacement)"/> for every agent with <see cref="PromptPlacement.ToolList"/>; an agent
    /// plugin should call that with its own id instead.
    /// </summary>
    /// <returns>The items, without list markers.</returns>
    [PluginAmbient, PluginCrossing(PluginCrossing.Mirrored)]
    IReadOnlyList<string> ToolGuidanceSections();

    /// <summary>
    /// Runs Git through the host's bounded runner (H17), which disables terminal prompts, runs without a
    /// console window and sets <c>GIT_OPTIONAL_LOCKS=0</c> so a read never looks like a repository change.
    /// </summary>
    /// <param name="cwd">The directory to run Git in.</param>
    /// <param name="args">The Git arguments.</param>
    /// <param name="options">Timeout and network access.</param>
    /// <param name="cancellationToken">Kills Git when cancelled.</param>
    /// <returns>The result; a non-zero exit is a result, not an exception.</returns>
    [PluginPermission("process")]
    ValueTask<GitRunResult> GitAsync(string cwd, IReadOnlyList<string> args, GitRunOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Provides this plugin's repo hosting provider (H24) for the remotes it recognizes. The host routes a workspace's
    /// branch to the first active provider, by plugin id, that recognizes <c>origin</c>; none means a plain remote. A
    /// second registration by the same plugin is ignored.
    /// </summary>
    /// <param name="provider">The provider.</param>
    [PluginPermission("repo.host")]
    void RepoHost(RepoHostProvider provider);

    /// <summary>
    /// Observes each finished branch status (H24) the host computed for a workspace, with the owner's pull request.
    /// Handlers run on a pool thread and publish on the plugin's own channels for its UI half.
    /// </summary>
    /// <param name="handler">Receives the status.</param>
    [PluginPermission("repo.host")]
    void OnBranchStatus(Action<BranchStatusEvent> handler);

    /// <summary>
    /// Runs a provider's command-line tool with the host's bounded runner (H24): no console, prompts off, the process
    /// group killed on expiry. A non-zero exit is a result.
    /// </summary>
    /// <param name="tool">The program, resolved on the host's PATH.</param>
    /// <param name="directory">The working directory.</param>
    /// <param name="args">The arguments.</param>
    /// <param name="timeout">The deadline.</param>
    /// <param name="environment">Overrides over the host's environment; a null value removes the variable.</param>
    /// <param name="cancellationToken">Kills the tool when cancelled.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">The tool is not installed.</exception>
    /// <exception cref="TimeoutException">The deadline passed.</exception>
    [PluginPermission("process")]
    ValueTask<RepoHostToolResult> RunToolAsync(string tool, string directory, IReadOnlyList<string> args, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds Bun by the host's toolchain rules (the user's own path, the pinned Bun in the host's cache, or PATH) and, when the pinned one is the
    /// answer, downloads it first, checked against its SHA-256. It never returns a program inside the workspace.
    /// </summary>
    /// <param name="workspace">The directory the program would run in; a Bun inside it is refused.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The path of Bun, or <see langword="null"/> when none is available (the caller falls back to its own runner).</returns>
    [PluginPermission("process")]
    ValueTask<string?> BunPathAsync(string workspace, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);

    /// <summary>Resolves a handle on a dependency declared in this plugin's manifest.</summary>
    /// <param name="contract">The dependency's contract.</param>
    /// <returns>The handle, dispatching in process.</returns>
    /// <exception cref="InvalidOperationException">The contract's plugin is not a declared dependency.</exception>
    [PluginAmbient, PluginCrossing(PluginCrossing.LocalInRunner)]
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

    /// <summary>
    /// The source-generated metadata of the types only the host half exchanges through <see cref="PluginJson"/>:
    /// its state files and its tools' parameters. <see langword="null"/> when the contract's context covers them.
    /// The runtime registers it when it loads the plugin.
    /// </summary>
    public virtual IJsonTypeInfoResolver? Json => null;

    /// <summary>Activates the plugin. A throw marks the plugin failed with the exception's message, and its dependents refused.</summary>
    /// <param name="context">This activation's context.</param>
    /// <returns>A disposer to run when the activation ends, or <see langword="null"/>.</returns>
    public abstract ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context);
}