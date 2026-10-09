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
    /// Rewrites the workspace registry atomically. A record that leaves it loses its label, creation base and
    /// review target; the result is saved before the snapshot and the lifecycle events of the difference are published.
    /// </summary>
    public IReadOnlyList<WorkspaceRecord> ChangeWorkspaces(Func<IReadOnlyList<WorkspaceRecord>, IEnumerable<WorkspaceRecord>> change)
    {
        lock (gate)
        {
            // Each project's Default workspace leads its rows, as every client lists them.
            var next = change(state.Workspaces).Where(ValidWorkspace).OrderBy(workspace => workspace.Kind != WorkspaceKinds.Default).ToArray();
            if (next.SequenceEqual(state.Workspaces)) return state.Workspaces;
            var paths = next.Select(workspace => workspace.Path).ToHashSet();
            var result = state with { Workspaces = next };
            foreach (var gone in state.Workspaces.Select(workspace => workspace.Path).Where(path => !paths.Contains(path)))
                result = result with
                {
                    WorkspaceLabels = Without(result.WorkspaceLabels, gone),
                    WorkspaceBases = Without(result.WorkspaceBases, gone),
                    WorkspaceDiffBases = Without(result.WorkspaceDiffBases, gone)
                };
            return Publish(result, persist: true).Workspaces;
        }
    }

    /// <summary>Records the ref a workspace was just created from as its review target, clearing any override left at that path.</summary>
    public void RecordWorkspaceBase(string path, string reference)
    {
        lock (gate)
            Publish(state with
            {
                WorkspaceBases = new Dictionary<string, string>(state.WorkspaceBases) { [path] = GitRefs.Require(reference) },
                WorkspaceDiffBases = Without(state.WorkspaceDiffBases, path)
            }, persist: true);
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