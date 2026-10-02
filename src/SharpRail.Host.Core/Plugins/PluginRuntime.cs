using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// The host's plugin runtime: builtin and discovered plugins in one registry, converged on their settings by a
/// serialized reconciler, and served to clients (methods, channels, the file read), to terminals (environment,
/// prefill, MCP tools, routes) and to each other (dependencies). It runs where the host runs.
/// </summary>
public sealed partial class PluginRuntime : IPluginService, IAsyncDisposable
{
    private sealed record Subscriber(string PluginId, string Channel, JsonElement? Key, string ClientKey, Channel<object?> Queue);

    private readonly Lock gate = new();
    private readonly PluginRegistry registry = new();
    private readonly List<Subscriber> subscribers = [];
    private readonly Dictionary<(string Id, string Channel), List<Action<object?>>> local = [];
    private readonly HashSet<string> validatedNamespaces = [];
    private readonly SemaphoreSlim passes = new(1, 1);
    private readonly Lock scheduling = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? running;
    private bool pending;
    private Task started = Task.CompletedTask;
    private Task watching = Task.CompletedTask;
    private int disposed;

    public PluginRuntime(PluginHostSeams seams)
    {
        Seams = seams;
        foreach (var (manifest, host) in seams.Builtins)
            registry.Add(new PluginEntry(manifest, PluginOrigin.Builtin) { Module = host });
    }

    internal PluginHostSeams Seams { get; }

    /// <summary>
    /// Wires the runtime into the host's seams, discovers external plugins and runs the first reconcile in the
    /// background. Calls made before it finishes wait for it.
    /// </summary>
    public Task Start()
    {
        Seams.State.PluginNamespaceValidator = ValidateNamespace;
        Seams.State.PluginDependents = id => { lock (gate) return registry.Dependents(id); };
        if (Seams.Terminals is { } terminals)
        {
            terminals.EnvironmentContributor = Environment;
            terminals.Lifecycle = OnTerminal;
            terminals.RevivePrefill = Prefill;
            terminals.SessionClosed = OnSessionClosed;
        }
        started = Task.Run(async () =>
        {
            await passes.WaitAsync();
            try { Discover(); }
            finally { passes.Release(); }
            await Schedule();
        });
        watching = Task.Run(WatchStateAsync);
        return started;
    }

    public Task Started => started;

    private void Publish() => Seams.State.PublishPlugins(Roster());

    private IReadOnlyList<PluginRosterEntry> Roster()
    {
        lock (gate) return registry.All.Select(entry => entry.Roster()).ToArray();
    }

    internal void Log(string scope, string level, string message, object? fields)
    {
        if (fields is not null)
        {
            try { message += " " + JsonSerializer.Serialize(fields, fields.GetType(), PluginJson.Options); }
            catch (Exception error) when (error is JsonException or NotSupportedException) { }
        }
        Seams.Log(scope, level, message);
    }

    // A plugin's callback never takes the host down with it; a throw is logged against the plugin.
    internal void Guard(string id, Action callback)
    {
        try { callback(); }
        catch (Exception error) { Log(id, "error", "a callback threw: " + error.Message, null); }
    }

    internal bool IsLive(PluginEntry entry, ActivationTables tables)
    {
        lock (gate) return ReferenceEquals(entry.Activation, tables);
    }

    private IReadOnlyList<(PluginEntry Entry, ActivationTables Tables)> Active()
    {
        lock (gate)
            return registry.All.Where(entry => entry is { State: PluginStatus.Active, Activation: not null })
                .Select(entry => (entry, entry.Activation!)).ToArray();
    }

    // Discovery

    private IEnumerable<string> Roots()
    {
        if (Seams.StateDirectory is { } directory) yield return Path.Combine(directory, "plugins");
        foreach (var root in Seams.State.Current.PluginPaths) yield return root;
    }

    // Startup discovery: every directory found becomes an entry, refused or not.
    private void Discover()
    {
        foreach (var found in PluginDiscovery.Discover(Roots()))
            lock (gate)
            {
                if (found.Manifest is { } manifest)
                {
                    if (registry.Get(manifest.Id) is { Origin: PluginOrigin.Builtin })
                        registry.Add(new PluginEntry(PluginRegistry.Placeholder(PluginRegistry.RefusedKey(found.Directory)), PluginOrigin.External)
                        { Directory = found.Directory, State = PluginStatus.Refused, Reason = $"plugin {manifest.Id} is already a builtin plugin" });
                    else registry.Add(new PluginEntry(manifest, PluginOrigin.External) { Directory = found.Directory });
                }
                else registry.Add(new PluginEntry(PluginRegistry.Placeholder(PluginRegistry.RefusedKey(found.Directory)), PluginOrigin.External)
                { Directory = found.Directory, State = PluginStatus.Refused, Reason = found.Refused });
            }
    }

    public async ValueTask<IReadOnlyList<PluginRosterEntry>> RescanAsync(CancellationToken cancellationToken = default)
    {
        await started.WaitAsync(cancellationToken);
        await passes.WaitAsync(cancellationToken);
        try
        {
            var discovered = PluginDiscovery.Discover(Roots());
            var directories = discovered.Select(found => found.Directory).ToHashSet();
            List<PluginEntry> gone;
            lock (gate) gone = registry.All.Where(entry => entry.Origin == PluginOrigin.External && entry.Directory is { } directory && !directories.Contains(directory)).ToList();
            foreach (var entry in gone)
            {
                await DeactivateAsync(entry);
                lock (gate) registry.Remove(entry);
            }
            foreach (var found in discovered)
            {
                PluginEntry? existing;
                lock (gate) existing = registry.ByDirectory(found.Directory);
                if (found.Manifest is { } manifest)
                {
                    if (existing is { State: PluginStatus.Refused, Module: null } && existing.Id != manifest.Id)
                        lock (gate) registry.Remove(existing);
                    await UpsertAsync(manifest, found.Directory);
                }
                else if (existing is null || existing.Id != PluginRegistry.RefusedKey(found.Directory))
                {
                    if (existing is not null)
                    {
                        await DeactivateAsync(existing);
                        lock (gate) registry.Remove(existing);
                    }
                    lock (gate)
                        registry.Add(new PluginEntry(PluginRegistry.Placeholder(PluginRegistry.RefusedKey(found.Directory)), PluginOrigin.External)
                        { Directory = found.Directory, State = PluginStatus.Refused, Reason = found.Refused });
                }
                else lock (gate) existing.Reason = found.Refused;
            }
        }
        finally { passes.Release(); }
        await Schedule();
        Publish();
        return Roster();
    }

    // An unchanged manifest whose entry assembly is unchanged keeps its module, state and activation; anything
    // else drops the module so the next pass loads and activates the new one.
    private async Task UpsertAsync(PluginManifest manifest, string directory)
    {
        PluginEntry? existing;
        lock (gate) existing = registry.Get(manifest.Id);
        if (existing is { Origin: PluginOrigin.Builtin })
        {
            lock (gate)
                registry.Add(new PluginEntry(PluginRegistry.Placeholder(PluginRegistry.RefusedKey(directory)), PluginOrigin.External)
                { Directory = directory, State = PluginStatus.Refused, Reason = $"plugin {manifest.Id} is already a builtin plugin" });
            return;
        }
        if (existing is null)
        {
            lock (gate) registry.Add(new PluginEntry(manifest, PluginOrigin.External) { Directory = directory });
            return;
        }
        var same = existing.Directory == directory && Same(existing.Manifest, manifest) && existing.State != PluginStatus.Refused &&
            (existing.Module is null || HostHash(directory, manifest) == existing.Hash);
        existing.Directory = directory;
        if (same) return;
        await DeactivateAsync(existing);
        lock (gate)
        {
            existing.Manifest = manifest;
            existing.Module = null;
            existing.Hash = null;
            existing.State = PluginStatus.Disabled;
            existing.Reason = null;
            existing.DependencyRefused = false;
            validatedNamespaces.Remove(existing.Id);
        }
    }

    private static bool Same(PluginManifest left, PluginManifest right) =>
        JsonSerializer.Serialize(left, PluginJson.Options) == JsonSerializer.Serialize(right, PluginJson.Options);

    private static string? HostHash(string directory, PluginManifest manifest)
    {
        if (manifest.Host is not { } host) return null;
        try { return PluginLoadContext.Hash(Path.Combine(directory, host)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public async ValueTask<IReadOnlyList<PluginRosterEntry>> RetryAsync(string id, CancellationToken cancellationToken = default)
    {
        await started.WaitAsync(cancellationToken);
        lock (gate)
            if (registry.Get(id) is { State: PluginStatus.Failed } entry) (entry.State, entry.Reason) = (PluginStatus.Disabled, null);
        await Schedule();
        Publish();
        return Roster();
    }

    public async ValueTask<IReadOnlyList<PluginRosterEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await started.WaitAsync(cancellationToken);
        return Roster();
    }

    // Reconciler

    /// <summary>
    /// Runs one reconcile pass, coalescing overlapping callers: a caller mid-pass gets the running task, and the
    /// pending flag guarantees one more full pass before it completes.
    /// </summary>
    public Task Schedule()
    {
        lock (scheduling)
        {
            if (running is not null) { pending = true; return running; }
            return running = Task.Run(LoopAsync);
        }
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            try { await PassAsync(); }
            catch (Exception error) { Log("plugins", "error", "reconcile failed: " + error, null); }
            lock (scheduling)
            {
                if (!pending) { running = null; return; }
                pending = false;
            }
        }
    }

    private async Task PassAsync()
    {
        await passes.WaitAsync();
        try
        {
            if (Volatile.Read(ref disposed) == 0) await ReconcileAsync();
        }
        finally { passes.Release(); }
    }

    private async Task ReconcileAsync()
    {
        IReadOnlyList<PluginEntry> order, cycle;
        Dictionary<string, bool> desired = [];
        var settings = Seams.State.Current.PluginSettings;
        lock (gate)
        {
            (order, cycle) = registry.TopologicalOrder();
            foreach (var entry in cycle.Where(entry => entry.State != PluginStatus.Refused))
                (entry.State, entry.Reason) = (PluginStatus.Refused, "part of a dependency cycle");
            foreach (var entry in order.Where(entry => entry.DependencyRefused))
                (entry.State, entry.Reason, entry.DependencyRefused) = (PluginStatus.Disabled, null, false);
            foreach (var entry in order)
            {
                var wanted = Enabled(entry, settings);
                if (entry is { State: PluginStatus.Failed } && !wanted) (entry.State, entry.Reason) = (PluginStatus.Disabled, null);
                if (entry.State == PluginStatus.Refused) { desired[entry.Id] = false; continue; }
                var blocker = entry.Manifest.DependsOn.Select(dependency => Blocker(dependency, desired)).FirstOrDefault(reason => reason is not null);
                desired[entry.Id] = wanted && blocker is null;
                if (wanted && blocker is not null && entry.State is not (PluginStatus.Active or PluginStatus.Failed))
                    (entry.State, entry.Reason, entry.DependencyRefused) = (PluginStatus.Refused, blocker, true);
            }
        }
        Publish();
        foreach (var entry in order)
        {
            if (!desired[entry.Id] || entry.State is PluginStatus.Active or PluginStatus.Failed or PluginStatus.Refused) continue;
            string? broken;
            lock (gate)
                broken = entry.Manifest.DependsOn.Select(dependency => registry.Get(dependency.Id))
                    .FirstOrDefault(dependency => dependency is not { State: PluginStatus.Active })?.Id;
            if (broken is not null)
            {
                lock (gate) (entry.State, entry.Reason) = (PluginStatus.Failed, $"dependency {broken} failed during this reconcile pass");
                Publish();
                continue;
            }
            await ActivateAsync(entry);
            Publish();
        }
        foreach (var entry in order.Reverse().Concat(cycle))
        {
            if (desired.GetValueOrDefault(entry.Id) || entry.State != PluginStatus.Active && entry.Activation is null) continue;
            await DeactivateAsync(entry);
            lock (gate)
                if (entry.State == PluginStatus.Disabled && Enabled(entry, settings) &&
                    entry.Manifest.DependsOn.Select(dependency => Blocker(dependency, desired)).FirstOrDefault(reason => reason is not null) is { } blocker)
                    (entry.State, entry.Reason, entry.DependencyRefused) = (PluginStatus.Refused, blocker, true);
            Publish();
        }
        Publish();
    }

    private static bool Enabled(PluginEntry entry, IReadOnlyDictionary<string, JsonElement> settings) =>
        settings.TryGetValue(entry.Id, out var space) && space.ValueKind == JsonValueKind.Object && space.TryGetProperty("enabled", out var flag)
            ? flag.ValueKind == JsonValueKind.True
            : entry.Origin == PluginOrigin.Builtin && entry.Manifest.EnabledByDefault;

    // Why a dependency keeps its dependent off, or null when it is usable: known, not failed or refused, at the
    // declared wire version, and itself desired this pass.
    private string? Blocker(PluginDependency dependency, IReadOnlyDictionary<string, bool> desired)
    {
        var found = registry.Get(dependency.Id);
        if (found is null) return $"depends on {dependency.Id}, which is not installed";
        if (found.State is PluginStatus.Failed or PluginStatus.Refused)
            return $"depends on {dependency.Id}, which is {found.State.ToString().ToLowerInvariant()}";
        if (found.WireVersion != dependency.WireVersion)
            return $"depends on {dependency.Id} at wire version {dependency.WireVersion}, but it is at {found.WireVersion}";
        return desired.GetValueOrDefault(dependency.Id) ? null : $"depends on {dependency.Id}, which is off";
    }

    private async Task ActivateAsync(PluginEntry entry)
    {
        var module = entry.Module;
        if (module is null)
        {
            if (entry.Manifest.Host is null) { lock (gate) (entry.State, entry.Reason) = (PluginStatus.Active, null); return; }
            if (entry.Origin == PluginOrigin.Builtin || entry.Directory is null)
            {
                lock (gate) (entry.State, entry.Reason) = (PluginStatus.Failed, $"plugin {entry.Id} has no host module registered");
                return;
            }
            try
            {
                var (loaded, hash) = await Task.Run(() => PluginLoadContext.LoadHost(entry.Directory, entry.Manifest.Host));
                lock (gate) (entry.Module, entry.Hash) = (module = loaded, hash);
            }
            catch (Exception error)
            {
                lock (gate) (entry.State, entry.Reason) = (PluginStatus.Failed, $"failed to load {entry.Id}: {error.Message}");
                return;
            }
        }
        var contract = module.Contract;
        if (PluginRegistry.ContractIntake(contract, entry.Id) is { } refused)
        {
            lock (gate) (entry.State, entry.Reason) = (PluginStatus.Refused, refused);
            return;
        }
        RecheckNamespace(entry.Id);
        var tables = new ActivationTables(registry.NextActivationId());
        lock (gate) entry.Activation = tables;
        try
        {
            var disposer = await module.ActivateAsync(new PluginHostContext(this, entry, contract, tables));
            lock (gate)
            {
                if (!ReferenceEquals(entry.Activation, tables)) return;
                tables.Disposer = disposer;
                (entry.State, entry.Reason) = (PluginStatus.Active, null);
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (ReferenceEquals(entry.Activation, tables)) entry.Activation = null;
                (entry.State, entry.Reason) = (PluginStatus.Failed, error.Message);
            }
            await ReleaseAsync(entry.Id, tables, dispose: false);
        }
    }

    // Stops routing synchronously: the state flips and the activation leaves the registry, so dispatch answers
    // disabled and a late registration or publish finds nothing live, before anything async happens.
    private ActivationTables? Stop(PluginEntry entry)
    {
        lock (gate)
        {
            var tables = entry.Activation;
            entry.Activation = null;
            if (entry.State == PluginStatus.Active) (entry.State, entry.Reason) = (PluginStatus.Disabled, null);
            return tables;
        }
    }

    private async Task DeactivateAsync(PluginEntry entry)
    {
        if (Stop(entry) is { } tables) await ReleaseAsync(entry.Id, tables, dispose: true);
    }

    // Drains the activation's in-flight work, then runs its disposer, each bounded.
    private async Task ReleaseAsync(string id, ActivationTables tables, bool dispose)
    {
        if (!await tables.DrainAsync(Seams.DrainTimeout))
            Log(id, "warn", $"plugin {id} still had calls in flight after {Seams.DrainTimeout.TotalMilliseconds} ms", null);
        foreach (var resource in tables.Resources) Guard(id, resource.Dispose);
        if (!dispose || tables.Disposer is not { } disposer) return;
        try
        {
            // Started inline, so disposers started in order begin in order.
            Task disposal;
            try { disposal = disposer().AsTask(); }
            catch (Exception error) { disposal = Task.FromException(error); }
            if (await Task.WhenAny(disposal, Task.Delay(Seams.DisposeTimeout)) != disposal)
                Log(id, "warn", $"plugin {id} disposer did not settle within {Seams.DisposeTimeout.TotalMilliseconds} ms", null);
            else await disposal;
        }
        catch (Exception error) { Log(id, "error", "the disposer threw: " + error.Message, null); }
    }

    // Settings namespaces

    /// <summary>
    /// Validates a merged namespace against its plugin's loaded settings type, with defaults filled and
    /// <c>enabled</c> kept; a namespace whose contract is not loaded yet passes unvalidated.
    /// </summary>
    public JsonElement ValidateNamespace(string id, JsonElement merged)
    {
        PluginContract? contract;
        lock (gate) contract = registry.Get(id)?.Module?.Contract;
        if (contract is null) return merged;
        var rest = JsonSerializer.SerializeToNode(merged)!.AsObject();
        var enabled = rest["enabled"]?.DeepClone();
        rest.Remove("enabled");
        JsonObject normalized;
        if (contract.SettingsType is not { } type)
        {
            if (rest.Count > 0) throw new ArgumentException($"plugin {id} has no settings, but they name {rest.First().Key}");
            normalized = [];
        }
        else
        {
            try
            {
                var value = rest.Deserialize(type, PluginJson.Options) ?? throw new JsonException("expected an object", "$", null, null);
                normalized = JsonSerializer.SerializeToNode(value, type, PluginJson.Options)!.AsObject();
            }
            catch (JsonException error) { throw new ArgumentException($"plugin {id} settings {PluginErrors.Describe(error)}"); }
        }
        if (enabled is not null) normalized["enabled"] = enabled;
        return JsonSerializer.SerializeToElement(normalized);
    }

    // A namespace written before its contract loaded is checked once it does; an invalid one falls back to
    // defaults, keeping its enabled flag, with one warning.
    private void RecheckNamespace(string id)
    {
        lock (gate)
            if (!validatedNamespaces.Add(id)) return;
        if (!Seams.State.Current.PluginSettings.TryGetValue(id, out var stored)) return;
        try
        {
            var normalized = ValidateNamespace(id, stored);
            if (!JsonElement.DeepEquals(normalized, stored)) Seams.State.ReplacePluginSettings(id, normalized);
        }
        catch (ArgumentException error)
        {
            Log(id, "warn", $"{error.Message}; using the defaults", null);
            var reset = new JsonObject();
            if (stored.TryGetProperty("enabled", out var enabled)) reset["enabled"] = JsonSerializer.SerializeToNode(enabled);
            Seams.State.ReplacePluginSettings(id, ValidateNamespace(id, JsonSerializer.SerializeToElement(reset)));
        }
    }

    internal T Settings<T>(string id) where T : class =>
        SettingsOf<T>(Seams.State.Current.PluginSettings.TryGetValue(id, out var space) ? space : default);

    internal static T SettingsOf<T>(JsonElement space) where T : class
    {
        var rest = space.ValueKind == JsonValueKind.Object ? JsonSerializer.SerializeToNode(space)!.AsObject() : [];
        rest.Remove("enabled");
        try { return rest.Deserialize<T>(PluginJson.Options) ?? new JsonObject().Deserialize<T>(PluginJson.Options)!; }
        catch (JsonException) { return new JsonObject().Deserialize<T>(PluginJson.Options)!; }
    }

    // Shared state: settings observers, workspace lifecycle and the reconcile a settings change asks for.
    private async Task WatchStateAsync()
    {
        HostState? previous = null;
        try
        {
            await foreach (var state in Seams.State.WatchAsync(lifetime.Token))
            {
                if (previous is not null)
                {
                    var changed = state.PluginSettings.Keys.Union(previous.PluginSettings.Keys).Where(id =>
                        !(state.PluginSettings.TryGetValue(id, out var now) && previous.PluginSettings.TryGetValue(id, out var before) && JsonElement.DeepEquals(now, before)))
                        .ToHashSet();
                    if (changed.Count > 0)
                    {
                        foreach (var (entry, tables) in Active().Where(active => changed.Contains(active.Entry.Id)))
                        {
                            var space = state.PluginSettings.GetValueOrDefault(entry.Id);
                            foreach (var observer in tables.SettingsObservers) Guard(entry.Id, () => observer(space));
                        }
                        _ = Schedule();
                    }
                    await WorkspaceEventsAsync(previous, state);
                }
                previous = state;
            }
        }
        catch (OperationCanceledException) { }
    }

    // Dispatch

    public async ValueTask<object?> CallAsync(PluginCallRequest request, CancellationToken cancellationToken = default)
    {
        await started.WaitAsync(cancellationToken);
        return await DispatchAsync(request, cancellationToken);
    }

    // A dependent's in-process call skips the wait for startup: it may run inside the first reconcile.
    internal async ValueTask<object?> DispatchAsync(PluginCallRequest request, CancellationToken cancellationToken)
    {
        var wire = PluginIdentity.MethodName(request.PluginId, request.Method);
        PluginMethodSpec method;
        ActivationTables tables;
        MethodHandler handler;
        lock (gate)
        {
            var entry = registry.Get(request.PluginId) ?? throw new PluginCallException(PluginCallError.Unknown, $"Unknown plugin {request.PluginId}");
            var contract = entry.Module?.Contract;
            if (contract is null)
                throw entry.State == PluginStatus.Active
                    ? new PluginCallException(PluginCallError.Unknown, $"Unknown method {wire}")
                    : new PluginCallException(PluginCallError.Disabled, $"Plugin {request.PluginId} is disabled");
            method = contract.Methods.FirstOrDefault(declared => declared.Name == request.Method)
                ?? throw new PluginCallException(PluginCallError.Unknown, $"Unknown method {wire}");
            if (entry is not { State: PluginStatus.Active, Activation: { } live })
                throw new PluginCallException(PluginCallError.Disabled, $"Plugin {request.PluginId} is disabled");
            tables = live;
            handler = tables.Methods.GetValueOrDefault(request.Method)
                ?? throw new PluginCallException(PluginCallError.Unknown, $"Plugin {request.PluginId} registered no handler for {wire}");
            tables.Enter();
        }
        try
        {
            var parameters = Convert(method.ParamsType, request.Params, error =>
                new PluginCallException(PluginCallError.InvalidParams, $"Invalid params for {wire}: {error}"));
            return await handler(parameters, new PluginCall(request.ClientKey), cancellationToken);
        }
        catch (Exception error) when (error is not PluginCallException && !(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw new PluginCallException(PluginCallError.Failed, error.Message);
        }
        finally { tables.Leave(); }
    }

    // Params are the declared type in process; anything else (a JSON element from the wire, an equivalent type
    // from another load context) deserializes strictly into it, and a failure names the offending path.
    private static object Convert(Type type, object? value, Func<string, Exception> invalid)
    {
        if (value is not null && type.IsInstanceOfType(value)) return value;
        JsonElement element;
        try { element = value as JsonElement? ?? JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), PluginJson.Options); }
        catch (Exception error) when (error is JsonException or NotSupportedException) { throw invalid("$: " + error.Message); }
        try { return element.Deserialize(type, PluginJson.Options) ?? throw invalid("$: expected an object, not null"); }
        catch (JsonException error) { throw invalid(PluginErrors.Describe(error)); }
    }

    public IAsyncEnumerable<object?> SubscribeAsync(PluginSubscription subscription, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var entry = registry.Get(subscription.PluginId) ?? throw new PluginCallException(PluginCallError.Unknown, $"Unknown plugin {subscription.PluginId}");
            if (entry.Module?.Contract is { } contract && contract.Channels.All(channel => channel.Name != subscription.Channel))
                throw new PluginCallException(PluginCallError.Unknown, $"Unknown channel {PluginIdentity.ChannelName(subscription.PluginId, subscription.Channel)}");
            JsonElement? key = subscription.Key switch
            {
                null => null,
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
                JsonElement element => element.Clone(),
                var value => JsonSerializer.SerializeToElement(value, value.GetType(), PluginJson.Options)
            };
            var subscriber = new Subscriber(subscription.PluginId, subscription.Channel, key, subscription.ClientKey, System.Threading.Channels.Channel.CreateUnbounded<object?>());
            subscribers.Add(subscriber);
            return Read(subscriber, cancellationToken);
        }
    }

    private async IAsyncEnumerable<object?> Read(Subscriber subscriber, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            while (true)
            {
                object? payload;
                try
                {
                    if (!await subscriber.Queue.Reader.WaitToReadAsync(stop.Token)) yield break;
                    if (!subscriber.Queue.Reader.TryRead(out payload)) continue;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { yield break; }
                yield return payload;
            }
        }
        finally { lock (gate) subscribers.Remove(subscriber); }
    }

    // Delivers a publish to every subscription it reaches (broadcast or addressed to the subscriber, and for a
    // keyed state channel, matching the subscription's key fields) and to dependents' in-process listeners.
    internal void Publish(string id, string channelName, object? payload, PluginCall? target)
    {
        Subscriber[] reached;
        Action<object?>[] listeners;
        lock (gate)
        {
            var channel = registry.Get(id)?.Module?.Contract.Channels.FirstOrDefault(declared => declared.Name == channelName);
            var keyed = channel is { Kind: PluginChannelKind.State, Key.Count: > 0 } ? channel.Key : null;
            JsonElement? payloadJson = null;
            reached = subscribers.Where(subscriber =>
            {
                if (subscriber.PluginId != id || subscriber.Channel != channelName) return false;
                if (target is not null && subscriber.ClientKey != target.ClientKey) return false;
                if (keyed is null || subscriber.Key is not { ValueKind: JsonValueKind.Object } key) return true;
                payloadJson ??= JsonSerializer.SerializeToElement(payload, payload?.GetType() ?? typeof(object), PluginJson.Options);
                return keyed.All(field => !key.TryGetProperty(field, out var wanted) ||
                    payloadJson.Value.ValueKind == JsonValueKind.Object && payloadJson.Value.TryGetProperty(field, out var actual) && JsonElement.DeepEquals(wanted, actual));
            }).ToArray();
            listeners = local.TryGetValue((id, channelName), out var found) ? found.ToArray() : [];
        }
        foreach (var subscriber in reached) subscriber.Queue.Writer.TryWrite(payload);
        foreach (var listener in listeners) Guard(id, () => listener(payload));
    }

    internal IDisposable SubscribeLocal(string id, string channel, Action<object?> handler)
    {
        lock (gate)
        {
            if (!local.TryGetValue((id, channel), out var list)) local[(id, channel)] = list = [];
            list.Add(handler);
        }
        return new Unsubscribe(() => { lock (gate) local.GetValueOrDefault((id, channel))?.Remove(handler); });
    }

    private sealed class Unsubscribe(Action release) : IDisposable
    {
        private int done;
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) release(); }
    }

    /// <summary>
    /// Serves a request under <c>/plugin/&lt;id&gt;/</c> through the active activation's route, counted toward its
    /// drain; null (a 404) when the plugin is unknown, disabled or mounted no route.
    /// </summary>
    public async ValueTask<PluginHttpResponse?> ServeRouteAsync(string id, PluginHttpRequest request, CancellationToken cancellationToken = default)
    {
        await started.WaitAsync(cancellationToken);
        ActivationTables tables;
        Func<PluginHttpRequest, CancellationToken, ValueTask<PluginHttpResponse>> route;
        lock (gate)
        {
            if (registry.Get(id) is not { State: PluginStatus.Active, Activation: { Route: { } handler } live }) return null;
            (tables, route) = (live, handler);
            tables.Enter();
        }
        try { return await route(request, cancellationToken); }
        finally { tables.Leave(); }
    }

    public async ValueTask<byte[]?> ReadFileAsync(string id, string path, CancellationToken cancellationToken = default)
    {
        await started.WaitAsync(cancellationToken);
        string? root;
        lock (gate)
            root = registry.Get(id) switch
            {
                { Origin: PluginOrigin.External, Directory: { } directory } => directory,
                { Origin: PluginOrigin.Builtin } => Path.Combine(Seams.BuiltinDirectory, id),
                _ => null
            };
        if (root is null || Contained(root, path) is not { } file) return null;
        try { return await File.ReadAllBytesAsync(file, cancellationToken); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    // A path under the plugin's directory, refusing anything that escapes it, a symbolic link included.
    private static string? Contained(string root, string path)
    {
        if (path.Length == 0 || path.Contains('\0') || Path.IsPathRooted(path)) return null;
        root = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;
        var check = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            check = Path.Combine(check, segment);
            if (new FileInfo(check).LinkTarget is not null) return null;
        }
        return full;
    }

    internal string? AssetsDirectory(PluginEntry entry)
    {
        if (entry.Manifest.Assets is not { } assets) return null;
        var root = entry.Origin == PluginOrigin.External ? entry.Directory : Path.Combine(Seams.BuiltinDirectory, entry.Id);
        return root is null ? null : Contained(root, assets);
    }

    /// <summary>Whether an active plugin exposes this exact absolute file for a workspace.</summary>
    public bool AllowsExternalFile(string workspaceRoot, string path)
    {
        foreach (var (entry, tables) in Active())
            foreach (var provider in tables.ExternalFiles)
            {
                IReadOnlyList<string> files = [];
                Guard(entry.Id, () => files = provider(workspaceRoot));
                if (files.Contains(path)) return true;
            }
        return false;
    }

    // Tools

    internal string? ToolClash(string name, ActivationTables self)
    {
        if (self.Tools.Any(tool => tool.Name == name)) return "this plugin";
        return Active().FirstOrDefault(active => !ReferenceEquals(active.Tables, self) && active.Tables.Tools.Any(tool => tool.Name == name)).Entry?.Id;
    }

    /// <summary>The active plugins' tools for one terminal's MCP table, bound to its owner and workspace.</summary>
    public IReadOnlyList<McpServer.McpTool> McpTools(TerminalRef? owner, string cwd) =>
        Active().SelectMany(active => active.Tables.Tools.Select(tool => new McpServer.McpTool(tool.Name, tool.Label, tool.Description, Schema(tool.ParametersType),
            async (arguments, cancellationToken) =>
            {
                object parameters;
                try { parameters = Convert(tool.ParametersType, JsonSerializer.SerializeToElement(arguments), error => new ArgumentException(error)); }
                catch (ArgumentException error) { return ($"Invalid arguments for {tool.Name} — {error.Message}", true); }
                active.Tables.Enter();
                try
                {
                    var result = await tool.RunAsync(parameters, new PluginToolContext(cwd, owner?.WorkspaceId ?? cwd, owner), cancellationToken);
                    return (result.Text, result.IsError);
                }
                finally { active.Tables.Leave(); }
            }))).ToArray();

    private static JsonObject Schema(Type type)
    {
        // A parameter's [Description] is what the agent reads about it.
        var schema = PluginJson.Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (node is JsonObject property && context.PropertyInfo?.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), true)
                    .OfType<DescriptionAttribute>().FirstOrDefault() is { } description) property["description"] = description.Description;
                return node;
            }
        });
        if (schema is not JsonObject json) return new JsonObject { ["type"] = "object" };
        json["additionalProperties"] = false;
        return json;
    }

    // Terminals

    private IReadOnlyDictionary<string, string> Environment(TerminalRef terminal)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entry, tables) in Active())
            foreach (var contributor in tables.Environment)
                Guard(entry.Id, () =>
                {
                    foreach (var (name, value) in contributor(terminal)) variables[name] = value;
                });
        return variables;
    }

    private void OnTerminal(TerminalEvent change)
    {
        foreach (var (entry, tables) in Active())
            foreach (var observer in tables.TerminalObservers) Guard(entry.Id, () => observer(change));
    }

    private TerminalPrefill? Prefill(TerminalRef terminal)
    {
        if (AgentRecord(terminal) is not { } record) return null;
        string? text = null;
        var submit = false;
        foreach (var (entry, tables) in Active())
            foreach (var hook in tables.ReviveHooks)
            {
                RevivePrefill? offered = null;
                Guard(entry.Id, () => offered = hook(terminal, record));
                if (offered is null) continue;
                text ??= offered.Text;
                submit |= offered.Submit;
            }
        return text is null && !submit ? null : new TerminalPrefill(text ?? "", submit);
    }

    // A closed tab forgets its agent record, including one persisted for a tab no client attached this run.
    private void OnSessionClosed(string session, TerminalRef? terminal)
    {
        var dropped = Seams.State.RemoveTerminalAgents(known => known == terminal || PtyTerminalService.SessionFor(known) == session);
        foreach (var closed in dropped.Append(terminal).OfType<TerminalRef>().Distinct()) OnTerminal(new TerminalClosed(closed));
    }

    internal TerminalAgentRecord? AgentRecord(TerminalRef terminal) =>
        Seams.State.Current.TerminalAgents.FirstOrDefault(agent => agent.Terminal == terminal)?.Record;

    internal void SetAgentRecord(TerminalRef terminal, TerminalAgentRecord? record)
    {
        Seams.State.SetTerminalAgent(terminal, record);
        OnTerminal(new TerminalAgentChanged(terminal, record));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // A pass wedged in a plugin's activation must not hold shutdown hostage.
        var held = await passes.WaitAsync(Seams.DrainTimeout + Seams.DisposeTimeout);
        try
        {
            IReadOnlyList<PluginEntry> order, cycle;
            lock (gate) (order, cycle) = registry.TopologicalOrder();
            // Every plugin stops before any disposer starts, dependents first, so a dependent's disposer calling
            // into its dependency gets the disabled answer. The disposers are started, not awaited.
            var stopped = order.Reverse().Concat(cycle).Select(entry => (entry.Id, Tables: Stop(entry))).ToArray();
            foreach (var (id, tables) in stopped)
                if (tables is not null) _ = ReleaseAsync(id, tables, dispose: true);
        }
        finally { if (held) passes.Release(); }
        await lifetime.CancelAsync();
        try { await watching; } catch (Exception) { }
        if (Seams.Terminals is { } terminals)
        {
            terminals.EnvironmentContributor = null;
            terminals.Lifecycle = null;
            terminals.RevivePrefill = null;
            terminals.SessionClosed = null;
        }
        Seams.State.PluginNamespaceValidator = null;
        Seams.State.PluginDependents = null;
        Publish();
    }
}