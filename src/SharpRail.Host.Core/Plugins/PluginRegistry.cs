using System.Collections.Immutable;
using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

internal delegate ValueTask<object?> MethodHandler(object parameters, PluginCall call, CancellationToken cancellationToken);

/// <summary>
/// Everything one activation registered, torn down as a unit. Its in-flight counter is what deactivation
/// drains: entering a call increments it, leaving decrements it, and reaching zero releases every waiter.
/// </summary>
internal sealed class ActivationTables(long id)
{
    private readonly Lock gate = new();
    private int inFlight;
    private TaskCompletionSource? drained;

    public long Id { get; } = id;
    public PluginDisposer? Disposer { get; set; }
    public ImmutableDictionary<string, MethodHandler> Methods { get; set; } = ImmutableDictionary<string, MethodHandler>.Empty;
    public Func<PluginHttpRequest, CancellationToken, ValueTask<PluginHttpResponse>>? Route { get; set; }
    public ImmutableList<PluginToolDefinition> Tools { get; set; } = [];
    public ImmutableList<Func<TerminalRef, IReadOnlyDictionary<string, string>>> Environment { get; set; } = [];
    public ImmutableList<Func<string, IReadOnlyList<string>>> ExternalFiles { get; set; } = [];
    public ImmutableList<Action<TerminalEvent>> TerminalObservers { get; set; } = [];
    public ImmutableList<Func<TerminalRef, TerminalAgentRecord, RevivePrefill?>> ReviveHooks { get; set; } = [];
    public ImmutableList<Action<WorkspaceEvent>> WorkspaceObservers { get; set; } = [];
    public ImmutableList<Action<WorkspaceFilesChanged>> FileObservers { get; set; } = [];
    public ImmutableList<Action<JsonElement>> SettingsObservers { get; set; } = [];
    // Dependency subscriptions and workspace watches, released when the activation ends.
    public ImmutableList<IDisposable> Resources { get; set; } = [];
    public Lock Registration { get; } = new();

    public void Enter()
    {
        lock (gate) inFlight++;
    }

    public void Leave()
    {
        TaskCompletionSource? release = null;
        lock (gate)
            if (--inFlight == 0) (release, drained) = (drained, null);
        release?.TrySetResult();
    }

    /// <summary>Completes when nothing is in flight, or false when the timeout elapsed first.</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        Task waiter;
        lock (gate)
        {
            if (inFlight == 0) return true;
            waiter = (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        return await Task.WhenAny(waiter, Task.Delay(timeout)) == waiter;
    }
}

internal sealed class PluginEntry(PluginManifest manifest, PluginOrigin origin)
{
    public PluginManifest Manifest { get; set; } = manifest;
    public PluginOrigin Origin { get; } = origin;
    public string? Directory { get; set; }
    public PluginHostModule? Module { get; set; }
    public string? Hash { get; set; }
    public PluginStatus State { get; set; } = PluginStatus.Disabled;
    public string? Reason { get; set; }
    // Refused for a dependency in the last pass, rather than for its manifest, contract or a cycle: each pass decides again.
    public bool DependencyRefused { get; set; }
    public ActivationTables? Activation { get; set; }
    public string Id => Manifest.Id;
    public int WireVersion => Module?.Contract.WireVersion ?? Manifest.WireVersion;

    public PluginRosterEntry Roster() => new(Id, Manifest.Label, Manifest.Icon, Manifest.Version, WireVersion, Origin, State)
    {
        Description = Manifest.Description,
        Reason = Reason,
        DependsOn = Manifest.DependsOn.Select(dependency => dependency.Id).ToArray(),
        Contributes = Manifest.Contributes,
        Channels = Module?.Contract.Channels.ToDictionary(channel => channel.Name, channel => new PluginRosterChannel(channel.Kind, channel.Snapshot, channel.Key.ToArray()))
            ?? new Dictionary<string, PluginRosterChannel>(),
        Ui = Origin == PluginOrigin.External ? Manifest.Ui : null,
        Assets = Manifest.Assets
    };
}

/// <summary>The known plugins in registration order. Callers hold the runtime's lock.</summary>
internal sealed class PluginRegistry
{
    private readonly List<PluginEntry> entries = [];
    private long activations;

    public long NextActivationId() => Interlocked.Increment(ref activations);

    public IReadOnlyList<PluginEntry> All => entries;

    public PluginEntry? Get(string id) => entries.FirstOrDefault(entry => entry.Id == id);

    public PluginEntry? ByDirectory(string directory) => entries.FirstOrDefault(entry => entry.Directory == directory);

    public void Add(PluginEntry entry)
    {
        entries.RemoveAll(existing => existing.Id == entry.Id);
        entries.Add(entry);
    }

    public void Remove(PluginEntry entry) => entries.Remove(entry);

    public static string RefusedKey(string directory) => "__refused:" + Path.GetFileName(directory);

    public static PluginManifest Placeholder(string key) => new(key, key, "puzzle-2-line", "0.0.0", PluginApi.Generation, 0);

    /// <summary>Dependencies before dependents; whatever a cycle keeps out of the order is returned separately.</summary>
    public (IReadOnlyList<PluginEntry> Order, IReadOnlyList<PluginEntry> Cycle) TopologicalOrder()
    {
        var known = entries.ToDictionary(entry => entry.Id);
        var remaining = entries.ToDictionary(entry => entry.Id, entry => entry.Manifest.DependsOn.Count(dependency => known.ContainsKey(dependency.Id)));
        var order = new List<PluginEntry>();
        var queue = new Queue<PluginEntry>(entries.Where(entry => remaining[entry.Id] == 0));
        while (queue.TryDequeue(out var next))
        {
            order.Add(next);
            foreach (var dependent in entries.Where(entry => entry.Manifest.DependsOn.Any(dependency => dependency.Id == next.Id)))
                if (--remaining[dependent.Id] == 0) queue.Enqueue(dependent);
        }
        return (order, entries.Where(entry => !order.Contains(entry)).ToArray());
    }

    /// <summary>Every plugin that depends on <paramref name="id"/>, directly or through another.</summary>
    public IReadOnlyList<string> Dependents(string id)
    {
        var found = new List<string>();
        var queue = new Queue<string>([id]);
        while (queue.TryDequeue(out var current))
            foreach (var entry in entries.Where(entry => entry.Manifest.DependsOn.Any(dependency => dependency.Id == current)))
                if (entry.Id != id && !found.Contains(entry.Id)) { found.Add(entry.Id); queue.Enqueue(entry.Id); }
        return found;
    }

    /// <summary>
    /// What <see cref="PluginContract"/>'s shape cannot rule out: duplicate names, a snapshot method the
    /// contract does not declare, a settings type claiming <c>enabled</c>, and a contract for another id.
    /// </summary>
    public static string? ContractIntake(PluginContract contract, string manifestId)
    {
        if (contract.Id != manifestId) return $"plugin {manifestId} loaded a contract for \"{contract.Id}\"";
        if (contract.Methods.GroupBy(method => method.Name).FirstOrDefault(group => group.Count() > 1) is { } method)
            return $"plugin {contract.Id} declares method \"{method.Key}\" twice";
        if (contract.Channels.GroupBy(channel => channel.Name).FirstOrDefault(group => group.Count() > 1) is { } channel)
            return $"plugin {contract.Id} declares channel \"{channel.Key}\" twice";
        foreach (var state in contract.Channels.Where(channel => channel.Kind == PluginChannelKind.State))
            if (contract.Methods.All(method => method.Name != state.Snapshot))
                return $"plugin {contract.Id} channel \"{state.Name}\" names unknown snapshot method \"{state.Snapshot}\"";
        if (contract.SettingsType is { } settings &&
            PluginJson.Options.GetTypeInfo(settings).Properties.Any(property => property.Name == "enabled"))
            return $"plugin {contract.Id} settings type may not declare \"enabled\"";
        return null;
    }
}