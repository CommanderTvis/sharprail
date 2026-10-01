using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// One activation's view of the host. Every mutating member compares the registry's current activation with
/// the one this context was built for before it writes, so a context kept past its dispose is a dead handle:
/// a late publish reaches nobody and a late registration lands in no table.
/// </summary>
internal sealed class PluginHostContext(PluginRuntime runtime, PluginEntry entry, PluginContract contract, ActivationTables tables) : IPluginHostContext
{
    private sealed class Logger(PluginRuntime runtime, string id) : IPluginLogger
    {
        public void Debug(string message, object? fields = null) => runtime.Log(id, "debug", message, fields);
        public void Info(string message, object? fields = null) => runtime.Log(id, "info", message, fields);
        public void Warn(string message, object? fields = null) => runtime.Log(id, "warn", message, fields);
        public void Error(string message, object? fields = null) => runtime.Log(id, "error", message, fields);
    }

    private sealed class DependencyHandle(PluginRuntime runtime, PluginHostContext owner, string id) : IPluginDependencyHandle
    {
        public async ValueTask<TResult> RequestAsync<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken cancellationToken = default) =>
            PluginJson.Convert<TResult>(await runtime.DispatchAsync(new(id, method.Name, parameters, "plugin:" + owner.Id), cancellationToken));

        public IDisposable Subscribe<TPayload>(PluginChannel<TPayload> channel, Action<TPayload> handler)
        {
            var subscription = runtime.SubscribeLocal(id, channel.Name, payload => handler(PluginJson.Convert<TPayload>(payload)));
            owner.Register(table => table.Resources = table.Resources.Add(subscription));
            return subscription;
        }
    }

    public string Id => entry.Id;

    public IPluginLogger Log { get; } = new Logger(runtime, entry.Id);

    public string? AssetsDirectory => runtime.AssetsDirectory(entry);

    private bool Live => runtime.IsLive(entry, tables);

    // Registration happens under the activation's own lock, after the liveness check, so a table the
    // runtime has already torn down never gains an entry.
    private bool Register(Action<ActivationTables> change)
    {
        lock (tables.Registration)
        {
            if (!Live) return false;
            change(tables);
            return true;
        }
    }

    public void Method<TParams, TResult>(PluginMethod<TParams, TResult> method, Func<TParams, PluginCall, CancellationToken, ValueTask<TResult>> handler)
    {
        if (!Live) return;
        if (contract.Methods.All(declared => declared.Name != method.Name))
            throw new InvalidOperationException($"plugin {Id} registers method \"{method.Name}\", which its contract does not declare");
        Register(table => table.Methods = table.Methods.SetItem(method.Name,
            async (parameters, call, cancellationToken) => await handler(PluginJson.Convert<TParams>(parameters), call, cancellationToken)));
    }

    public void Publish<TPayload>(PluginChannel<TPayload> channel, TPayload payload, PluginCall? target = null)
    {
        if (!Live) return;
        runtime.Publish(Id, channel.Name, payload, target);
    }

    public void Route(Func<PluginHttpRequest, CancellationToken, ValueTask<PluginHttpResponse>> handler) => Register(table => table.Route = handler);

    public string PublicBaseUrl() => runtime.Seams.PublicBaseUrl().TrimEnd('/') + PluginIdentity.Route(Id);

    public void ExternalFiles(Func<string, IReadOnlyList<string>> provider) => Register(table => table.ExternalFiles = table.ExternalFiles.Add(provider));

    public void Tool(PluginToolDefinition definition)
    {
        if (!Live) return;
        if (runtime.ToolClash(definition.Name, tables) is { } owner)
        {
            runtime.Log(Id, "warn", $"tool {definition.Name} was refused: the name is already registered by {owner}", null);
            return;
        }
        Register(table => table.Tools = table.Tools.Add(definition));
    }

    public void TerminalEnvironment(Func<TerminalRef, IReadOnlyDictionary<string, string>> contributor) =>
        Register(table => table.Environment = table.Environment.Add(contributor));

    public string TerminalToken(TerminalRef terminal) => TerminalSeams.Token(terminal);

    public TerminalRef? TerminalForToken(string token) => TerminalSeams.ForToken(token);

    public TerminalAgentRecord? AgentRecord(TerminalRef terminal) => runtime.AgentRecord(terminal);

    public void SetAgentRecord(TerminalRef terminal, TerminalAgentRecord? record) => runtime.SetAgentRecord(terminal, record);

    public void OnTerminal(Action<TerminalEvent> handler) => Register(table => table.TerminalObservers = table.TerminalObservers.Add(handler));

    public IReadOnlyList<TerminalProcess> Terminals() => runtime.Seams.Terminals?.List() ?? [];

    public string? WorkspaceForProcess(int pid) => runtime.Seams.Terminals?.WorkspaceForProcess(pid);

    public void RevivePrefill(Func<TerminalRef, TerminalAgentRecord, RevivePrefill?> hook) => Register(table => table.ReviveHooks = table.ReviveHooks.Add(hook));

    public void WriteTerminal(TerminalRef terminal, string data) => runtime.Seams.Terminals?.Write(terminal, data);

    public IReadOnlyList<HostProject> Projects() => runtime.Projects();

    public ValueTask<IReadOnlyList<HostWorkspace>> WorkspacesAsync(string? projectId = null, CancellationToken cancellationToken = default) =>
        runtime.WorkspacesAsync(projectId, cancellationToken);

    public ValueTask<HostWorkspace?> WorkspaceAsync(string id, CancellationToken cancellationToken = default) => runtime.WorkspaceAsync(id, cancellationToken);

    public async ValueTask WatchWorkspaceAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Live) return;
        if (await runtime.WorkspaceAsync(id, cancellationToken) is null) throw new ArgumentException($"There is no workspace {id}.", nameof(id));
        var watch = new WorkspaceFileWatcher(id, batch =>
        {
            if (!Live) return;
            foreach (var observer in tables.FileObservers) runtime.Guard(Id, () => observer(batch));
        });
        if (!Register(table => table.Resources = table.Resources.Add(watch))) watch.Dispose();
    }

    public void OnWorkspace(Action<WorkspaceEvent> handler) => Register(table => table.WorkspaceObservers = table.WorkspaceObservers.Add(handler));

    public void OnFilesChanged(Action<WorkspaceFilesChanged> handler) => Register(table => table.FileObservers = table.FileObservers.Add(handler));

    public T Settings<T>() where T : class => runtime.Settings<T>(Id);

    public void OnSettings<T>(Action<T> handler) where T : class =>
        Register(table => table.SettingsObservers = table.SettingsObservers.Add(value => handler(PluginRuntime.SettingsOf<T>(value))));

    public T ReadState<T>(string name, T fallback)
    {
        if (StatePath(name) is not { } path) return fallback;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), PluginJson.Options) ?? fallback; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return fallback; }
    }

    public void WriteState<T>(string name, T value)
    {
        if (!Live) return;
        var path = StatePath(name) ?? throw new InvalidOperationException("This host keeps no state directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, PluginJson.Options));
        File.Move(temporary, path, overwrite: true);
    }

    public async ValueTask<GitRunResult> GitAsync(string cwd, IReadOnlyList<string> args, GitRunOptions? options = null, CancellationToken cancellationToken = default) =>
        await runtime.Seams.Git(cwd, args, options, cancellationToken);

    public IPluginDependencyHandle Dependency(PluginContract dependency)
    {
        if (entry.Manifest.DependsOn.All(declared => declared.Id != dependency.Id))
            throw new InvalidOperationException($"plugin {Id} does not declare a dependency on {dependency.Id}");
        return new DependencyHandle(runtime, this, dependency.Id);
    }

    private IPluginTerminalSeams TerminalSeams => runtime.Seams.Terminals ?? throw new InvalidOperationException("This host runs no terminals.");

    private string? StatePath(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException($"Invalid state name \"{name}\".", nameof(name));
        return runtime.Seams.StateDirectory is { } directory ? Path.Combine(directory, PluginIdentity.StateFile(Id, name)) : null;
    }
}