using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;

namespace SharpRail.Checks.E2E;

// Stands in for the host's plugin runtime where the app's loader needs what the real host never produces: roster
// rows for builtin UI halves the host does not ship (one speaking another wire version, one whose activation throws)
// and channel pushes for a key the subscriber did not ask for. It layers the roster, plugin settings and plugin roots
// over a real host-state service and enables and cascades like the host's reconciler. The fixture plugin's end-to-end
// scenario runs against the real host instead.
internal sealed class FakePluginHost : IHostStateService, IPluginService
{
    private readonly IHostStateService inner;
    private readonly List<Channel<HostState>> watchers = [];
    private readonly List<(PluginSubscription Subscription, Channel<object?> Pushes)> subscriptions = [];
    private readonly Dictionary<string, PluginRosterEntry> entries;
    private readonly Dictionary<string, JsonElement> settings = [];
    private IReadOnlyList<string> paths = [];
    private HostState latest;
    private long revision;
    private bool forwarding;

    internal FakePluginHost(IHostStateService inner, HostState initial, params PluginRosterEntry[] roster)
    {
        this.inner = inner; latest = initial;
        entries = roster.ToDictionary(entry => entry.Id);
    }

    internal List<PluginCallRequest> Calls { get; } = [];
    internal Dictionary<string, object?> Replies { get; } = [];
    internal int Subscriptions { get { lock (subscriptions) return subscriptions.Count; } }

    internal IReadOnlyList<PluginRosterEntry> Roster() => [.. entries.Values.Select(entry =>
        entry.Status is PluginStatus.Refused ? entry :
        entry with { Status = Enabled(entry.Id) ? PluginStatus.Active : PluginStatus.Disabled })];

    private bool Enabled(string id) =>
        settings.GetValueOrDefault(id) is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True &&
        entries[id].DependsOn.All(Enabled);

    private HostState Compose(HostState state) => state with
    {
        Revision = state.Revision + revision,
        PluginSettings = new Dictionary<string, JsonElement>(settings),
        PluginPaths = paths,
        Plugins = Roster()
    };

    private void Publish()
    {
        var snapshot = Compose(latest);
        lock (watchers) foreach (var watcher in watchers) watcher.Writer.TryWrite(snapshot);
    }

    public ValueTask<HostHandshake> GetHandshakeAsync(CancellationToken cancellationToken = default) => inner.GetHandshakeAsync(cancellationToken);

    public IAsyncEnumerable<LifecycleEvent> WatchLifecycleAsync(CancellationToken cancellationToken = default) => inner.WatchLifecycleAsync(cancellationToken);

    public async ValueTask<HostState> GetStateAsync(CancellationToken cancellationToken = default) =>
        Compose(latest = await inner.GetStateAsync(cancellationToken));

    public async ValueTask<HostState> ChangeAsync(IReadOnlyList<HostStateChange> changes, CancellationToken cancellationToken = default)
    {
        var core = changes.Where(change => change.Kind is not ("plugin-settings" or "plugin-paths")).ToArray();
        if (core.Length > 0) latest = await inner.ChangeAsync(core, cancellationToken);
        foreach (var change in changes)
        {
            if (change.Kind == "plugin-paths") paths = JsonSerializer.Deserialize<string[]>(change.Value) ?? [];
            if (change.Kind != "plugin-settings") continue;
            var merged = change.Value.Length == 0 ? new JsonObject() : (JsonNode.Parse(settings.GetValueOrDefault(change.Key) is { ValueKind: JsonValueKind.Object } current ? current.GetRawText() : "{}") as JsonObject)!;
            if (change.Value.Length > 0)
                foreach (var (name, value) in (JsonNode.Parse(change.Value) as JsonObject)!) merged[name] = value?.DeepClone();
            settings[change.Key] = JsonSerializer.SerializeToElement(merged);
        }
        revision++;
        Publish();
        return Compose(latest);
    }

    public async IAsyncEnumerable<HostState> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<HostState>();
        lock (watchers) watchers.Add(channel);
        if (!forwarding) { forwarding = true; _ = ForwardAsync(); }
        try
        {
            yield return Compose(latest);
            await foreach (var state in channel.Reader.ReadAllAsync(cancellationToken)) yield return state;
        }
        finally { lock (watchers) watchers.Remove(channel); }
    }

    private async Task ForwardAsync()
    {
        try
        {
            await foreach (var state in inner.WatchAsync())
            {
                latest = state;
                Publish();
            }
        }
        catch (OperationCanceledException) { }
    }

    public ValueTask<IReadOnlyList<PluginRosterEntry>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Roster());

    public ValueTask<IReadOnlyList<PluginRosterEntry>> RescanAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Roster());

    public ValueTask<IReadOnlyList<PluginRosterEntry>> RetryAsync(string id, CancellationToken cancellationToken = default) => ValueTask.FromResult(Roster());

    // Records the request and answers only methods explicitly configured by the check.
    public ValueTask<object?> CallAsync(PluginCallRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(request);
        if (Roster().FirstOrDefault(entry => entry.Id == request.PluginId) is not { } entry)
            throw new PluginCallException(PluginCallError.Unknown, $"No plugin {request.PluginId}.");
        if (entry.Status != PluginStatus.Active) throw new PluginCallException(PluginCallError.Disabled, $"Plugin {request.PluginId} is disabled.");
        if (Replies.TryGetValue(request.Method, out var reply)) return ValueTask.FromResult(reply);
        throw new PluginCallException(PluginCallError.Unknown, $"Plugin {request.PluginId} has no method {request.Method}.");
    }

    // A push for any key: the client must drop what its scope does not ask for.
    internal void Push(string plugin, string channel, object payload)
    {
        var element = JsonSerializer.SerializeToElement(payload, PluginJson.Options);
        lock (subscriptions)
            foreach (var (subscription, pushes) in subscriptions)
                if (subscription.PluginId == plugin && subscription.Channel == channel) pushes.Writer.TryWrite(element);
    }

    public async IAsyncEnumerable<object?> SubscribeAsync(PluginSubscription subscription, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pushes = Channel.CreateUnbounded<object?>();
        var entry = (subscription, pushes);
        lock (subscriptions) subscriptions.Add(entry);
        try
        {
            await foreach (var payload in pushes.Reader.ReadAllAsync(cancellationToken)) yield return payload;
        }
        finally { lock (subscriptions) subscriptions.Remove(entry); }
    }

    public ValueTask<byte[]?> ReadFileAsync(string id, string path, CancellationToken cancellationToken = default) => ValueTask.FromResult<byte[]?>(null);
}