using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.UI.Plugins;

/// <summary>
/// One plugin's <see cref="IPluginUIContext"/>, bound to one activation. Registrations pass the activation guard;
/// observers are recorded against the activation and stopped when it ends; window-scoped members act on the app's
/// active window.
/// </summary>
internal sealed class PluginUIContext : IPluginUIContext
{
    private readonly PluginLoader loader;
    private readonly PluginRosterEntry entry;
    private readonly PluginActivation activation;
    private readonly Dictionary<string, Task> watched = [];

    public PluginUIContext(PluginLoader loader, PluginRosterEntry entry, PluginActivation activation)
    {
        this.loader = loader; this.entry = entry; this.activation = activation;
        Log = new Logger(entry.Id);
        Editors = new EditorsApi(this);
    }

    public string Id => entry.Id;
    public IPluginUILogger Log { get; }
    public IPluginEditors Editors { get; }
    private Workbench Workbench => loader.Workbench;
    private PluginRegistry Registry => loader.Registry;

    private IPluginService Service => loader.Service ?? throw new PluginCallException(PluginCallError.Disabled, "This app has no plugin host.");

    private void Guard()
    {
        if (!activation.Open) throw new InvalidOperationException($"Plugin {Id} registered a contribution outside Activate.");
    }

    private IDisposable Track(IDisposable observer) => activation.Track(observer);

    private static IDisposable Disposable(Action dispose) => new EditorEvents.Subscription(dispose);

    public async ValueTask<TResult> RequestAsync<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken cancellationToken = default) =>
        PluginJson.Convert<TResult>(await Service.CallAsync(new(Id, method.Name, parameters, loader.ClientKey), cancellationToken));

    public IDisposable Subscribe<TPayload>(PluginChannel<TPayload> channel, Action<TPayload> handler, object? scope = null) =>
        Track(Channel(Id, channel.Name, handler, scope));

    // Snapshot-then-stream for a state channel: the snapshot is read on every (re)subscription, and a push whose
    // key fields disagree with the scope is dropped. A dropped connection resubscribes when the host returns.
    internal IDisposable Channel<TPayload>(string pluginId, string name, Action<TPayload> handler, object? scope)
    {
        var lifetime = new CancellationTokenSource();
        var spec = (pluginId == Id ? entry : Registry.Entry(pluginId))?.Channels.GetValueOrDefault(name);
        var scopeJson = scope is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(scope, PluginJson.Options);
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            while (!lifetime.IsCancellationRequested && !activation.Closed)
            {
                if (loader.Service is not { } service) return;
                var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Signal() => reconnected.TrySetResult();
                loader.Connected += Signal;
                try
                {
                    var stream = service.SubscribeAsync(new(pluginId, name, scope, loader.ClientKey), lifetime.Token).GetAsyncEnumerator(lifetime.Token);
                    if (spec is { Kind: PluginChannelKind.State, Snapshot: { } snapshot } && (scope is not null || spec.Key.Count == 0))
                        _ = ReadSnapshotAsync(service, snapshot);
                    while (await stream.MoveNextAsync())
                        Deliver(stream.Current);
                    await stream.DisposeAsync();
                    return;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    if (!activation.Unmounting) Console.Error.WriteLine($"Plugin {pluginId} channel {name} dropped: {error.Message}");
                }
                finally { loader.Connected -= Signal; }
                try
                {
                    if (Workbench.State.Connected) await Task.Delay(250, lifetime.Token);
                    else await reconnected.Task.WaitAsync(lifetime.Token);
                }
                catch (OperationCanceledException) { return; }
            }
        });
        return Disposable(lifetime.Cancel);

        async Task ReadSnapshotAsync(IPluginService service, string snapshot)
        {
            try
            {
                var result = await service.CallAsync(new(pluginId, snapshot, scope ?? new { }, loader.ClientKey), lifetime.Token);
                if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(typeof(TPayload)) && result is JsonElement { ValueKind: JsonValueKind.Array } rows)
                    foreach (var row in rows.EnumerateArray()) Deliver(row);
                else if (result is System.Collections.IEnumerable list && result is not TPayload && result is not string)
                    foreach (var row in list) Deliver(row);
                else Deliver(result);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (!activation.Unmounting) Console.Error.WriteLine($"Plugin {pluginId} snapshot {snapshot} failed: {error.Message}");
            }
        }

        void Deliver(object? payload)
        {
            if (lifetime.IsCancellationRequested || activation.Closed) return;
            if (spec is { Kind: PluginChannelKind.State } && scopeJson is { } wanted && !MatchesScope(payload, spec.Key, wanted)) return;
            TPayload typed;
            try { typed = PluginJson.Convert<TPayload>(payload); }
            catch (JsonException error) { Console.Error.WriteLine($"Plugin {pluginId} channel {name} sent an unreadable payload: {error.Message}"); return; }
            Dispatcher.UIThread.Post(() =>
            {
                if (lifetime.IsCancellationRequested || activation.Closed) return;
                try { handler(typed); }
                catch (Exception error) { Console.Error.WriteLine($"Plugin {pluginId} channel handler failed: {error.Message}"); }
            });
        }
    }

    internal static bool MatchesScope(object? payload, IReadOnlyList<string> keys, JsonElement scope)
    {
        if (payload is null || scope.ValueKind != JsonValueKind.Object) return true;
        var element = payload as JsonElement? ?? JsonSerializer.SerializeToElement(payload, PluginJson.Options);
        if (element.ValueKind != JsonValueKind.Object) return true;
        return keys.All(key => !scope.TryGetProperty(key, out var wanted) ||
            (element.TryGetProperty(key, out var actual) && actual.GetRawText() == wanted.GetRawText()));
    }

    public T Settings<T>() where T : class => ReadSettings<T>(Workbench.State.Current);

    private T ReadSettings<T>(HostState state) where T : class
    {
        var namespaceObject = new JsonObject();
        if (state.PluginSettings.GetValueOrDefault(Id) is { ValueKind: JsonValueKind.Object } stored)
            foreach (var property in stored.EnumerateObject().Where(property => property.Name != "enabled"))
                namespaceObject[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        try { return PluginJson.Convert<T>(JsonSerializer.SerializeToElement(namespaceObject)); }
        catch (JsonException error)
        {
            Log.Warn("Settings do not match the contract; using defaults.", new { error = error.Message });
            return PluginJson.Convert<T>(JsonSerializer.SerializeToElement(new JsonObject()));
        }
    }

    public IDisposable OnSettings<T>(Action<T> handler) where T : class
    {
        void Changed(HostState previous, HostState next)
        {
            if (Raw(previous) == Raw(next)) return;
            handler(ReadSettings<T>(next));
        }
        Workbench.State.Changed += Changed;
        return Track(Disposable(() => Workbench.State.Changed -= Changed));
    }

    private string Raw(HostState state) => state.PluginSettings.GetValueOrDefault(Id) is { ValueKind: JsonValueKind.Object } value ? value.GetRawText() : "";

    public async ValueTask UpdateSettingsAsync<T>(T settings) where T : class
    {
        var written = JsonSerializer.SerializeToElement(settings, PluginJson.Options);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Carries(HostState state) => state.PluginSettings.GetValueOrDefault(Id) is { ValueKind: JsonValueKind.Object } stored &&
            written.EnumerateObject().All(member => stored.TryGetProperty(member.Name, out var value) && value.GetRawText() == member.Value.GetRawText());
        void Changed(HostState previous, HostState next) { if (Carries(next)) arrived.TrySetResult(); }
        Workbench.State.Changed += Changed;
        try
        {
            await Workbench.State.ChangeAsync(HostStateChange.PluginSettings(Id, written.GetRawText()));
            if (Carries(Workbench.State.Current)) return;
            await arrived.Task;
        }
        finally { Workbench.State.Changed -= Changed; }
    }

    public PluginHostProjection Host() => PluginProjection.Build(loader);

    public IDisposable WatchHost<T>(Func<PluginHostProjection, T> selector, Action<T, T> listener)
    {
        var previous = selector(Host());
        void Recompute()
        {
            if (activation.Closed) return;
            var next = selector(Host());
            if (EqualityComparer<T>.Default.Equals(next, previous)) return;
            var prior = previous; previous = next;
            listener(next, prior);
        }
        void RegistryChanged(PluginTables tables) { if ((tables & (PluginTables.Roster | PluginTables.Active)) != 0) Recompute(); }
        Workbench.ProjectionChanged += Recompute;
        Registry.Changed += RegistryChanged;
        return Track(Disposable(() => { Workbench.ProjectionChanged -= Recompute; Registry.Changed -= RegistryChanged; }));
    }

    public void SettingsSection(SettingsSectionRegistration section) { Guard(); Registry.AddSettingsSection(Id, section); }
    public void SideTool(SideToolRegistration registration) { Guard(); Registry.AddSideTool(Id, registration); }
    public void Companion(CompanionRegistration registration) { Guard(); Registry.AddCompanion(Id, registration); }
    public void TabDecoration(Func<TabRef, TabDecoration?> decorate) { Guard(); Registry.AddTabDecorator(Id, decorate); }
    public void Launcher(AgentLauncher launcher) { Guard(); Registry.AddLauncher(Id, launcher); }
    public void TerminalAccessory(TerminalAccessoryRegistration registration) { Guard(); Registry.AddTerminalAccessory(Id, registration); }
    public void FileIconSlot(Func<string, FileIconKind, string?> resolver) { Guard(); Registry.AddFileIconSlot(Id, resolver); }
    public void DocumentLinkSlot(Func<string, string, string?> resolver) { Guard(); Registry.AddDocumentLinkSlot(Id, resolver); }

    public void FileViewer(FileViewerRegistration registration)
    {
        Guard();
        Registry.AddFileViewer(Id, registration, entry.Contributes.FileViewers.FirstOrDefault()?.Read ?? PluginFileRead.Text);
    }

    public void WorkspaceAction(WorkspaceActionRegistration action)
    {
        Guard();
        switch (action)
        {
            case ProjectScopedActionRegistration project: Registry.AddProjectAction(Id, project); break;
            case WorkspaceScopedActionRegistration workspace: Registry.AddWorkspaceAction(Id, workspace); break;
            default: throw new ArgumentException($"Unknown workspace action kind {action.GetType().Name}.", nameof(action));
        }
    }

    public void FocusCompanion(CompanionHost host, string kind)
    {
        foreach (var window in Workbench.Windows.ToArray())
            if (window.TerminalTabs().Any(workspace => workspace.Workspace == host.WorkspaceId && workspace.Tabs.Any(tab => tab.Id == host.TabKey)))
                window.FocusCompanion(host, Id, kind);
    }

    public IReadOnlyList<AgentLauncher> Launchers() => Registry.LauncherList;

    public IDisposable OnLaunchersChanged(Action<IReadOnlyList<AgentLauncher>> handler)
    {
        void Changed(PluginTables tables) { if ((tables & (PluginTables.Launchers | PluginTables.Predicates)) != 0) handler(Registry.LauncherList); }
        Registry.Changed += Changed;
        return Track(Disposable(() => Registry.Changed -= Changed));
    }

    public async ValueTask<string> OpenTerminalAsync(string workspaceId, TerminalOpenOptions? options = null) =>
        Workbench.ActiveWindow is { } window ? await window.OpenPluginTerminalAsync(workspaceId, options ?? new())
            : throw new InvalidOperationException("No window is open.");

    public async ValueTask<HostWorkspace?> EnterDefaultWorkspaceAsync(string projectId) =>
        Workbench.ActiveWindow is { } window ? await window.EnterDefaultWorkspaceAsync(projectId) : null;

    public async ValueTask<string?> PickFileAsync(FilePickOptions? options = null) =>
        Workbench.ActiveWindow is { } window ? await window.PickHostPathAsync(options ?? new()) : null;

    public async ValueTask<byte[]> ReadFileAsync(string workspaceId, string path, CancellationToken cancellationToken = default) =>
        await Workbench.ReadWorkspaceFileAsync(workspaceId, path, cancellationToken);

    public int FileRevision(string workspaceId, string path) => Workbench.FileRevision(workspaceId, path);

    public IDisposable ObserveFileRevision(string workspaceId, string path, Action<int> handler)
    {
        Workbench.TrackRevision(workspaceId, path);
        var last = FileRevision(workspaceId, path);
        void Changed()
        {
            var revision = FileRevision(workspaceId, path);
            if (revision == last) return;
            last = revision; handler(revision);
        }
        Workbench.RevisionsChanged += Changed;
        return Track(Disposable(() => Workbench.RevisionsChanged -= Changed));
    }

    public async ValueTask WatchWorkspaceAsync(string workspaceId)
    {
        if (activation.Closed) return;
        if (!watched.TryGetValue(workspaceId, out var ready))
        {
            var (lease, started) = Workbench.WorkspaceWatches.Acquire(workspaceId);
            watched[workspaceId] = ready = started;
            void Release()
            {
                if (watched.GetValueOrDefault(workspaceId) == started) watched.Remove(workspaceId);
                lease.Dispose();
            }
            Track(Disposable(Release));
            // A watch that failed or stopped before readiness is not kept: a later call starts another.
            _ = started.ContinueWith(_ => Dispatcher.UIThread.Post(Release), CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        }
        await ready;
    }

    public IDisposable OnReconnect(Action handler)
    {
        void Reconnected() { if (!activation.Closed) handler(); }
        loader.Reconnected += Reconnected;
        return Track(Disposable(() => loader.Reconnected -= Reconnected));
    }

    public IDisposable OnWorkspaceRemoved(Action<string> handler)
    {
        void Changed(HostState previous, HostState next)
        {
            var remaining = next.Workspaces.Select(workspace => workspace.Path).ToHashSet();
            foreach (var removed in previous.Workspaces.Select(workspace => workspace.Path).Where(path => !remaining.Contains(path)))
                handler(removed);
        }
        Workbench.State.Changed += Changed;
        return Track(Disposable(() => Workbench.State.Changed -= Changed));
    }

    public void RevealTool(string workspaceId, string tool) => Workbench.ActiveWindow?.RevealToolFor(workspaceId, tool);

    public void SetDiffScope(string workspaceId, GitDiffScope scope) => Workbench.ActiveWindow?.SetDiffScope(workspaceId, scope);

    public async ValueTask<byte[]> ReadAssetAsync(string path, CancellationToken cancellationToken = default)
    {
        if (entry.Assets is not { } assets) throw new InvalidOperationException($"Plugin {Id} declares no assets.");
        return await Service.ReadFileAsync(Id, assets.TrimEnd('/') + "/" + path.TrimStart('/'), cancellationToken)
            ?? throw new FileNotFoundException($"Plugin {Id} has no asset {path}.");
    }

    public IPluginPreference Preference(string key) => new PreferenceStore(Workbench, PluginIdentity.PreferenceKey(Id, key));

    public IPluginDependencyHandle Dependency(PluginContract contract)
    {
        var declared = Workbench.PluginLoader.Registry.Manifests.GetValueOrDefault(Id)?.DependsOn.Select(dependency => dependency.Id)
            ?? entry.DependsOn;
        if (!declared.Contains(contract.Id)) throw new InvalidOperationException($"Plugin {Id} does not declare a dependency on {contract.Id}.");
        return new DependencyHandle(this, contract.Id);
    }

    public void Notify(PluginNotificationKind kind, string title, string? description = null) =>
        Workbench.ActiveWindow?.Notify(kind == PluginNotificationKind.Error, description is null ? title : title + " — " + description);

    public void NotifyAttention(AttentionNotification notification) => Workbench.RequestAttention(notification);

    public void Invalidate() => Registry.Invalidate(Id);

    private sealed class DependencyHandle(PluginUIContext context, string pluginId) : IPluginDependencyHandle
    {
        public async ValueTask<TResult> RequestAsync<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken cancellationToken = default) =>
            PluginJson.Convert<TResult>(await context.Service.CallAsync(new(pluginId, method.Name, parameters, context.loader.ClientKey), cancellationToken));

        public IDisposable Subscribe<TPayload>(PluginChannel<TPayload> channel, Action<TPayload> handler) =>
            context.Track(context.Channel(pluginId, channel.Name, handler, null));
    }

    private sealed class PreferenceStore(Workbench workbench, string key) : IPluginPreference
    {
        private string Key => workbench.Endpoint + "|" + key;
        public string? Get() => workbench.Profile.Data.PluginPreferences.GetValueOrDefault(Key);
        public void Set(string value) { workbench.Profile.Data.PluginPreferences[Key] = value; workbench.Profile.Save(); }
        public void Remove() { if (workbench.Profile.Data.PluginPreferences.Remove(Key)) workbench.Profile.Save(); }
    }

    private sealed class Logger(string id) : IPluginUILogger
    {
        public void Debug(string message, object? fields = null) => Write("debug", message, fields);
        public void Info(string message, object? fields = null) => Write("info", message, fields);
        public void Warn(string message, object? fields = null) => Write("warn", message, fields);
        public void Error(string message, object? fields = null) => Write("error", message, fields);

        private void Write(string level, string message, object? fields) =>
            Console.Error.WriteLine($"[plugin:{id}] {level}: {message}{(fields is null ? "" : " " + JsonSerializer.Serialize(fields, PluginJson.Options))}");
    }

    private sealed class EditorsApi(PluginUIContext context) : IPluginEditors
    {
        private Workbench Workbench => context.Workbench;

        public EditorRef? Active => Workbench.ActiveWindow?.ActiveEditor();

        public IDisposable OnEvent(Action<EditorEvent> handler) => context.Track(context.loader.Editors.On(handler));

        public async ValueTask<EditorRef?> OpenAsync(string workspaceId, string path, EditorOpenOptions? options = null) =>
            Workbench.ActiveWindow is { } window ? await window.OpenEditorAsync(workspaceId, path, options ?? new()) : null;

        public void Close(string id) { foreach (var window in Workbench.Windows) window.CloseEditor(id); }

        public IReadOnlyList<EditorRef> List(string? workspaceId = null) =>
            [.. Workbench.Windows.SelectMany(window => window.EditorRefs()).Where(editor => workspaceId is null || editor.WorkspaceId == workspaceId)
                .DistinctBy(editor => editor.Id)];

        public bool IsDirty(string id) => List().FirstOrDefault(editor => editor.Id == id)?.Dirty == true;

        public async ValueTask SaveAsync(string id)
        {
            foreach (var window in Workbench.Windows) await window.SaveEditorAsync(id);
        }

        public void ReportSelection(EditorRef editor, EditorSelection? selection) =>
            context.loader.Editors.Emit(new EditorSelectionEvent(editor, selection));
    }
}