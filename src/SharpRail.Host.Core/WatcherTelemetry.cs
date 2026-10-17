using System.Diagnostics.Tracing;

namespace SharpRail.Host.Core;

internal static partial class WorkspaceWatches
{
    internal sealed record WatchLease(long Id, string Owner);
    internal sealed record WatchInstance(long Id, string Path, bool Enabled);
    internal sealed record WatchState(string Root, int Generation, bool Failed, bool RetryPending,
        IReadOnlyList<WatchInstance> Instances, IReadOnlyList<WatchLease> Leases)
    {
        public IReadOnlyList<string> AllocatedPaths => Instances.Select(instance => instance.Path).ToArray();
        public int EnabledWatchers => Instances.Count(instance => instance.Enabled);
        public IReadOnlyList<string> Owners => Leases.Select(lease => lease.Owner).ToArray();
    }
    internal sealed record WatchAccounting(long Created, long Disposed, IReadOnlyList<WatchState> Roots)
    {
        public int Allocated => Roots.Sum(root => root.AllocatedPaths.Count);
        public int Enabled => Roots.Sum(root => root.EnabledWatchers);
        public int Subscribers => Roots.Sum(root => root.Owners.Count);
        public int Idle => Roots.Count(root => root.Owners.Count == 0);
        public int Retries => Roots.Count(root => root.RetryPending);
    }

    internal readonly record struct WatchCounts(long Created, long Disposed, int Allocated, int Enabled, int Subscribers, int Idle, int Retries);

    // Counter polling does not allocate or retain path/owner snapshots.
    internal static WatchCounts Counts()
    {
        lock (Gate)
        {
            var allocated = 0; var enabled = 0; var subscribers = 0; var idle = 0; var retries = 0;
            foreach (var entry in Entries.Values)
            {
                allocated += entry.Watchers.Count;
                enabled += entry.Enabled.Count;
                subscribers += entry.Subscribers.Count;
                if (entry.Subscribers.Count == 0) idle++;
                if (entry.Retry is not null) retries++;
            }
            return new(created, disposed, allocated, enabled, subscribers, idle, retries);
        }
    }

    // Allocated managed instances and logical leases, never an estimate of native FSEventStream allocations.
    internal static WatchAccounting Accounting()
    {
        lock (Gate)
            return new(created, disposed, Entries.Values.Select(entry => new WatchState(entry.Root,
                entry.Generation, entry.Failed, entry.Retry is not null,
                entry.Watchers.Select(watcher => new WatchInstance(entry.Identities[watcher], watcher.Path, entry.Enabled.Contains(watcher))).ToArray(),
                entry.Subscribers.Select(subscription => new WatchLease(subscription.Key, subscription.Value.Owner)).ToArray())).ToArray());
    }
}

/// <summary>Opt-in EventSource lifecycle tracing and process-wide managed watcher counters for dotnet-counters/dotnet-trace.</summary>
[EventSource(Name = "SharpRail-WorkspaceWatchers")]
internal sealed class WatcherTelemetry : EventSource
{
    internal static readonly WatcherTelemetry Log = new();
    private readonly List<DiagnosticCounter> counters = [];

    private WatcherTelemetry()
    {
        counters.Add(new PollingCounter("allocated-watchers", this, () => WorkspaceWatches.Counts().Allocated));
        counters.Add(new PollingCounter("enabled-watchers", this, () => WorkspaceWatches.Counts().Enabled));
        counters.Add(new PollingCounter("logical-subscriptions", this, () => WorkspaceWatches.Counts().Subscribers));
        counters.Add(new PollingCounter("idle-roots", this, () => WorkspaceWatches.Counts().Idle));
        counters.Add(new PollingCounter("pending-retries", this, () => WorkspaceWatches.Counts().Retries));
        counters.Add(new PollingCounter("created-watchers", this, () => WorkspaceWatches.Counts().Created));
        counters.Add(new PollingCounter("disposed-watchers", this, () => WorkspaceWatches.Counts().Disposed));
    }

    [Event(1, Level = EventLevel.Informational)]
    public void Watcher(string operation, string root, string path, int generation, long id) => WriteEvent(1, operation, root, path, generation, id);

    [Event(2, Level = EventLevel.Informational)]
    public void Lease(string operation, string root, long id, string owner) => WriteEvent(2, operation, root, id, owner);

    [Event(3, Level = EventLevel.Informational)]
    public void Snapshot(string root, int generation, string state, string paths, string owners) => WriteEvent(3, root, generation, state, paths, owners);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command is EventCommand.Enable or EventCommand.Update)
            ThreadPool.QueueUserWorkItem(static _ =>
            {
                foreach (var root in WorkspaceWatches.Accounting().Roots)
                    Log.Snapshot(root.Root, root.Generation, root.Failed ? "failed" : root.RetryPending ? "retry" : "retained",
                        System.Text.Json.JsonSerializer.Serialize(root.Instances), System.Text.Json.JsonSerializer.Serialize(root.Leases));
            });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var counter in counters) counter.Dispose();
        base.Dispose(disposing);
    }
}