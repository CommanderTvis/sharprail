namespace SharpRail.Plugins.Api.UI;

/// <summary>Editor operations and events (W12).</summary>
public interface IPluginEditors
{
    /// <summary>The active window's active editor, or <see langword="null"/>.</summary>
    EditorRef? Active { get; }

    /// <summary>Observes editor lifecycle and selection events.</summary>
    /// <param name="handler">Called on the UI thread with each event.</param>
    /// <returns>Disposing it stops the observation.</returns>
    IDisposable OnEvent(Action<EditorEvent> handler);

    /// <summary>Opens a file in the active window. An absolute path opens an external-file editor when an active plugin exposes it.</summary>
    /// <param name="workspaceId">The file's workspace.</param>
    /// <param name="path">The workspace-relative path, or an exposed absolute path.</param>
    /// <param name="options">Where to reveal, whether to preview, and whether to bypass viewers.</param>
    /// <returns>The opened editor, or <see langword="null"/> when it could not be opened.</returns>
    ValueTask<EditorRef?> OpenAsync(string workspaceId, string path, EditorOpenOptions? options = null);

    /// <summary>Closes an editor.</summary>
    /// <param name="id">The editor's id.</param>
    void Close(string id);

    /// <summary>Lists open editors across windows.</summary>
    /// <param name="workspaceId">Limits the list to one workspace.</param>
    /// <returns>The editors.</returns>
    IReadOnlyList<EditorRef> List(string? workspaceId = null);

    /// <summary>Reports whether an editor has unsaved changes.</summary>
    /// <param name="id">The editor's id.</param>
    /// <returns>Whether it is dirty; <see langword="false"/> for an unknown id.</returns>
    bool IsDirty(string id);

    /// <summary>Saves an editor's contents through the host's compare-and-swap save.</summary>
    /// <param name="id">The editor's id.</param>
    /// <returns>Completes when saved.</returns>
    ValueTask SaveAsync(string id);

    /// <summary>
    /// Reports a selection made in the plugin's own rendered document into the editor-event stream, the same
    /// surface the code editor and the Markdown preview report through.
    /// </summary>
    /// <param name="editor">The editor the selection belongs to.</param>
    /// <param name="selection">The selection, or <see langword="null"/> to clear it.</param>
    void ReportSelection(EditorRef editor, EditorSelection? selection);
}

/// <summary>
/// The context passed to <see cref="PluginUIModule.Activate"/>: the closed set of UI capabilities (W1–W20 in the
/// module spec, without chat). Registrations are accepted only while <c>Activate</c> runs, so a plugin's whole
/// surface exists when it returns and the runtime can remove it by plugin id; every registration is unmounted
/// when the plugin is disabled. Calls arrive and callbacks run on the UI thread.
/// </summary>
public interface IPluginUIContext
{
    /// <summary>The plugin's id.</summary>
    string Id { get; }

    /// <summary>This activation's logger.</summary>
    IPluginUILogger Log { get; }

    /// <summary>Calls one of the plugin's methods on its host half (W1).</summary>
    /// <typeparam name="TParams">The method's params.</typeparam>
    /// <typeparam name="TResult">The method's result.</typeparam>
    /// <param name="method">The method, from the plugin's contract.</param>
    /// <param name="parameters">The params.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    /// <exception cref="PluginCallException">The plugin is disabled on the host, the method is unknown, the params are invalid or the handler failed.</exception>
    ValueTask<TResult> RequestAsync<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to one of the plugin's channels (W1). For a state channel the snapshot method named on the
    /// roster is called with <paramref name="scope"/> as its params on subscribe and after every reconnect, and
    /// its result is delivered before pushes; a push whose key fields differ from the scope is dropped.
    /// </summary>
    /// <typeparam name="TPayload">The channel's payload.</typeparam>
    /// <param name="channel">The channel, from the plugin's contract.</param>
    /// <param name="handler">Called on the UI thread with each payload.</param>
    /// <param name="scope">For a keyed state channel, an object whose JSON properties are the key fields' values.</param>
    /// <returns>Disposing it cancels the subscription.</returns>
    IDisposable Subscribe<TPayload>(PluginChannel<TPayload> channel, Action<TPayload> handler, object? scope = null);

    /// <summary>Reads the plugin's settings namespace (W2).</summary>
    /// <typeparam name="T">The contract's settings type.</typeparam>
    /// <returns>The current settings, with defaults filled.</returns>
    T Settings<T>() where T : class;

    /// <summary>Observes the plugin's settings namespace (W2); not called for the value current at registration.</summary>
    /// <typeparam name="T">The contract's settings type.</typeparam>
    /// <param name="handler">Called on the UI thread with each new value.</param>
    /// <returns>Disposing it stops the observation.</returns>
    IDisposable OnSettings<T>(Action<T> handler) where T : class;

    /// <summary>
    /// Writes the plugin's settings namespace through the host (W2). The UI never writes settings locally:
    /// the task completes when the host's broadcast carrying the new value has arrived, so every client
    /// converges the same way.
    /// </summary>
    /// <typeparam name="T">The contract's settings type.</typeparam>
    /// <param name="settings">The new settings; merged field by field into the namespace.</param>
    /// <returns>Completes after the broadcast.</returns>
    ValueTask UpdateSettingsAsync<T>(T settings) where T : class;

    /// <summary>Reads the host projection now (W3).</summary>
    /// <returns>The current projection.</returns>
    PluginHostProjection Host();

    /// <summary>
    /// Observes a selection of the host projection (W3), calling <paramref name="listener"/> only when the
    /// selected value changes by <see cref="EqualityComparer{T}.Default"/>; select a record or tuple to compare
    /// by value. Not called for the value current at registration. Stopped when the activation ends.
    /// </summary>
    /// <typeparam name="T">The selected value.</typeparam>
    /// <param name="selector">Projects the value to watch.</param>
    /// <param name="listener">Called with the new and previous values.</param>
    /// <returns>Disposing it stops the observation early.</returns>
    IDisposable WatchHost<T>(Func<PluginHostProjection, T> selector, Action<T, T> listener);

    /// <summary>Contributes a Settings section (W4).</summary>
    /// <param name="section">The section.</param>
    void SettingsSection(SettingsSectionRegistration section);

    /// <summary>Registers the control for a manifest-declared side tool (W5).</summary>
    /// <param name="registration">The tool's registration.</param>
    void SideTool(SideToolRegistration registration);

    /// <summary>Contributes an embedded companion pane for terminals (W6).</summary>
    /// <param name="registration">The companion.</param>
    void Companion(CompanionRegistration registration);

    /// <summary>Opens and focuses a companion of this plugin beside a terminal (W6).</summary>
    /// <param name="host">The terminal.</param>
    /// <param name="kind">The companion's kind.</param>
    void FocusCompanion(CompanionHost host, string kind);

    /// <summary>Registers a viewer for the manifest's declared file viewers (W7).</summary>
    /// <param name="registration">The viewer.</param>
    void FileViewer(FileViewerRegistration registration);

    /// <summary>Registers a tab decorator (W8); decorators run in registration order and the first non-null result wins.</summary>
    /// <param name="decorate">Returns a decoration for a tab, or <see langword="null"/>.</param>
    void TabDecoration(Func<TabRef, TabDecoration?> decorate);

    /// <summary>Registers an agent launcher (W9).</summary>
    /// <param name="launcher">The launcher.</param>
    void Launcher(AgentLauncher launcher);

    /// <summary>Lists every registered launcher, across plugins (W9).</summary>
    /// <returns>The launchers, in registration order.</returns>
    IReadOnlyList<AgentLauncher> Launchers();

    /// <summary>Observes the launcher list and invalidated availability/model reads (W9).</summary>
    /// <param name="handler">Called on the UI thread with the list after each change.</param>
    /// <returns>Disposing it stops the observation.</returns>
    IDisposable OnLaunchersChanged(Action<IReadOnlyList<AgentLauncher>> handler);

    /// <summary>Contributes a workspace- or project-scoped start action (W10).</summary>
    /// <param name="action">The action.</param>
    void WorkspaceAction(WorkspaceActionRegistration action);

    /// <summary>Contributes a terminal accessory row (W11).</summary>
    /// <param name="registration">The accessory.</param>
    void TerminalAccessory(TerminalAccessoryRegistration registration);

    /// <summary>Editor operations and events (W12).</summary>
    IPluginEditors Editors { get; }

    /// <summary>
    /// Opens a terminal tab in a workspace of the active window and selects it (W13). A tab key already open is
    /// selected rather than created again.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="options">The command to type, the tab key and the target centre group.</param>
    /// <returns>The opened or selected tab's key.</returns>
    ValueTask<string> OpenTerminalAsync(string workspaceId, TerminalOpenOptions? options = null);

    /// <summary>Enters a project's Default workspace in the active window (W13).</summary>
    /// <param name="projectId">The project.</param>
    /// <returns>The workspace, or <see langword="null"/> when the project cannot be opened.</returns>
    ValueTask<HostWorkspace?> EnterDefaultWorkspaceAsync(string projectId);

    /// <summary>Asks for a file or folder on the host (W13): the system picker for a local host, a path dialog for a remote one.</summary>
    /// <param name="options">The starting workspace and whether to pick a folder.</param>
    /// <returns>The chosen absolute path, or <see langword="null"/> when cancelled.</returns>
    ValueTask<string?> PickFileAsync(FilePickOptions? options = null);

    /// <summary>Reads a worktree file's bytes through the host (W14).</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="path">The workspace-relative path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file's bytes.</returns>
    ValueTask<byte[]> ReadFileAsync(string workspaceId, string path, CancellationToken cancellationToken = default);

    /// <summary>Reads a file's filesystem revision, bumped on each watched change to it (W14).</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="path">The workspace-relative path.</param>
    /// <returns>The revision.</returns>
    int FileRevision(string workspaceId, string path);

    /// <summary>Observes a file's filesystem revision (W14).</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="path">The workspace-relative path.</param>
    /// <param name="handler">Called on the UI thread with each new revision.</param>
    /// <returns>Disposing it stops the observation.</returns>
    IDisposable ObserveFileRevision(string workspaceId, string path, Action<int> handler);

    /// <summary>Keeps a workspace watched so its revisions stay current (W14).</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns>Completes when the watch is running.</returns>
    ValueTask WatchWorkspaceAsync(string workspaceId);

    /// <summary>Observes the host connection coming back after a drop (W15).</summary>
    /// <param name="handler">Called on the UI thread after each reconnect.</param>
    /// <returns>Disposing it stops the observation.</returns>
    IDisposable OnReconnect(Action handler);

    /// <summary>Observes workspace removal (W15).</summary>
    /// <param name="handler">Called on the UI thread with each removed workspace's id.</param>
    /// <returns>Disposing it stops the observation.</returns>
    IDisposable OnWorkspaceRemoved(Action<string> handler);

    /// <summary>Reveals a side tool by layout id in the active window, placing and selecting it (W16).</summary>
    /// <param name="workspaceId">The workspace the reveal is for.</param>
    /// <param name="tool">A core tool id or a <see cref="PluginIdentity.ToolId"/>.</param>
    void RevealTool(string workspaceId, string tool);

    /// <summary>
    /// Fills the file-icon slot (W17), consulted wherever core renders a file. Resolvers run in registration
    /// order; the first non-null name wins, and core's generic glyph is the fallback.
    /// </summary>
    /// <param name="resolver">Returns a Remix Icon name or <c>asset:&lt;path&gt;</c> (from this plugin's assets) for a path, or <see langword="null"/>.</param>
    void FileIconSlot(Func<string, FileIconKind, string?> resolver);

    /// <summary>
    /// Fills the document-link slot, consulted when a rendered document's link is followed. Resolvers run in
    /// registration order; the first non-null path wins, and core's own resolution is the fallback.
    /// </summary>
    /// <param name="resolver">Maps a workspace id and a link target to a workspace-relative path core can open, or <see langword="null"/>.</param>
    void DocumentLinkSlot(Func<string, string, string?> resolver);

    /// <summary>Scopes the Changes panel of the active window (W18).</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="scope">The scope.</param>
    void SetDiffScope(string workspaceId, GitDiffScope scope);

    /// <summary>Reads a file under the manifest's assets directory through the host (W19).</summary>
    /// <param name="path">The path relative to the assets directory.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="InvalidOperationException">The manifest declares no assets.</exception>
    ValueTask<byte[]> ReadAssetAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Opens a client-local preference of this plugin.</summary>
    /// <param name="key">The preference name.</param>
    /// <returns>The preference.</returns>
    IPluginPreference Preference(string key);

    /// <summary>Resolves a handle on a dependency declared in this plugin's manifest, calling over the ordinary plugin call path.</summary>
    /// <param name="contract">The dependency's contract.</param>
    /// <returns>The handle.</returns>
    /// <exception cref="InvalidOperationException">The contract's plugin is not a declared dependency.</exception>
    IPluginDependencyHandle Dependency(PluginContract contract);

    /// <summary>Shows a transient notification in the active window.</summary>
    /// <param name="kind">Information or failure.</param>
    /// <param name="title">The headline.</param>
    /// <param name="description">More detail.</param>
    void Notify(PluginNotificationKind kind, string title, string? description = null);

    /// <summary>
    /// Asks for a desktop notification about a terminal that needs the user (W20). Core decides whether it is shown:
    /// only while no window of the app is focused and the host's notification setting is on, with requests arriving
    /// together folded into one. Activating it brings the app forward on the terminal's workspace and tab. Nothing
    /// is shown where the platform has no desktop notifications.
    /// </summary>
    /// <param name="notification">The terminal and what it wants.</param>
    void NotifyAttention(AttentionNotification notification);

    /// <summary>
    /// Re-evaluates every predicate this plugin registered (companion availability and titles, launcher
    /// availability and models, tab decorations, slots) and refreshes what they render. The UI's substitute for
    /// a reactive hook: call it when state those predicates read has changed.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// A plugin's UI half. An external plugin's UI assembly holds exactly one public concrete subclass with a public
/// parameterless constructor; a builtin plugin's instance is listed in the app's builtin array. Activation is
/// synchronous and runs on the UI thread; registrations made after it returns are rejected.
/// </summary>
/// <example>
/// <code>
/// public sealed class TodoUI : PluginUIModule
/// {
///     public override PluginDisposer? Activate(IPluginUIContext context)
///     {
///         context.SideTool(new("board", workspace =&gt; new TodoBoard(context, workspace)));
///         return null;
///     }
/// }
/// </code>
/// </example>
public abstract class PluginUIModule
{
    /// <summary>Activates the plugin; a throw leaves it dormant with the exception logged.</summary>
    /// <param name="context">This activation's context.</param>
    /// <returns>A disposer to run when the plugin is disabled, or <see langword="null"/>.</returns>
    public abstract PluginDisposer? Activate(IPluginUIContext context);
}