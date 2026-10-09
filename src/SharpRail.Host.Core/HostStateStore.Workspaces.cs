using System.Runtime.CompilerServices;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class HostStateStore
{
    private readonly List<Channel<LifecycleEvent>> lifecycleWatchers = [];
    private readonly SemaphoreSlim workspaceGate = new(1, 1);

    private sealed class Gate(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    /// <summary>
    /// Serialises work that reads worktrees from Git and then rewrites the registry, across every session of this
    /// host: a slow list must not write back what a create or removal has changed since it read.
    /// </summary>
    public async Task<IDisposable> LockWorkspacesAsync(CancellationToken cancellationToken)
    {
        await workspaceGate.WaitAsync(cancellationToken);
        return new Gate(workspaceGate);
    }

    private static bool ValidWorkspace(WorkspaceRecord? workspace) => workspace is { Id.Length: > 0, Branch: not null, BaseBranch: not null } &&
        workspace.Kind is WorkspaceKinds.Default or WorkspaceKinds.Managed or WorkspaceKinds.External &&
        ValidPath(workspace.ProjectRoot ?? "") && ValidPath(workspace.Path ?? "") &&
        (workspace.Kind == WorkspaceKinds.Default) == (workspace.Path == workspace.ProjectRoot);

    /// <summary>
    /// Rewrites the workspace registry atomically. A record that leaves it loses its label; the result is saved
    /// before the snapshot and the lifecycle events of the difference are published.
    /// </summary>
    public IReadOnlyList<WorkspaceRecord> ChangeWorkspaces(Func<IReadOnlyList<WorkspaceRecord>, IEnumerable<WorkspaceRecord>> change)
    {
        lock (gate)
        {
            // Each project's Default workspace leads its rows, as every client lists them.
            var next = change(state.Workspaces).Where(ValidWorkspace).OrderBy(workspace => workspace.Kind != WorkspaceKinds.Default).ToArray();
            if (next.SequenceEqual(state.Workspaces)) return state.Workspaces;
            var paths = next.Select(workspace => workspace.Path).ToHashSet();
            var gone = state.Workspaces.Select(workspace => workspace.Path).Where(path => !paths.Contains(path) && state.WorkspaceLabels.ContainsKey(path)).ToHashSet();
            var labels = gone.Count == 0 ? state.WorkspaceLabels : state.WorkspaceLabels.Where(entry => !gone.Contains(entry.Key)).ToDictionary();
            return Publish(state with { Workspaces = next, WorkspaceLabels = labels }, persist: true).Workspaces;
        }
    }

    public async IAsyncEnumerable<LifecycleEvent> WatchLifecycleAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<LifecycleEvent>(new() { SingleReader = true });
        lock (gate) lifecycleWatchers.Add(channel);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken)) yield return item;
        }
        finally { lock (gate) lifecycleWatchers.Remove(channel); }
    }

    private void PublishLifecycle(HostState previous, HostState next)
    {
        if (lifecycleWatchers.Count == 0 || ReferenceEquals(previous.Projects, next.Projects) && ReferenceEquals(previous.Workspaces, next.Workspaces)) return;
        var events = new List<LifecycleEvent>();
        events.AddRange(previous.Projects.Except(next.Projects).Select(project => new LifecycleEvent(LifecycleEvent.Projects, "closed", project)));
        events.AddRange(next.Projects.Except(previous.Projects).Select(project => new LifecycleEvent(LifecycleEvent.Projects, "opened", project)));
        var before = previous.Workspaces.ToDictionary(workspace => workspace.Id);
        foreach (var workspace in next.Workspaces)
        {
            if (!before.Remove(workspace.Id, out var known)) events.Add(new(LifecycleEvent.Workspaces, "created", workspace.ProjectRoot, workspace.Id, workspace));
            else if (known != workspace) events.Add(new(LifecycleEvent.Workspaces, "updated", workspace.ProjectRoot, workspace.Id, workspace));
        }
        events.AddRange(before.Values.Select(workspace => new LifecycleEvent(LifecycleEvent.Workspaces, "removed", workspace.ProjectRoot, workspace.Id)));
        foreach (var item in events)
            foreach (var watcher in lifecycleWatchers) watcher.Writer.TryWrite(item);
    }
}