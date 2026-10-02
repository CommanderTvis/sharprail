using Avalonia.Threading;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Plugins;

/// <summary>
/// The app's plugin-requested workspace watches: one per workspace on a project session of its own, shared by
/// every activation that asked for it. Revisions go to the workbench unless a window has the workspace mounted.
/// Used on the UI thread only.
/// </summary>
internal sealed class PluginWorkspaceWatches(Workbench workbench, Func<IProjectServices>? sessions)
{
    private readonly Dictionary<string, Watch> watches = [];

    /// <summary>Holds <paramref name="workspace"/> watched until the returned lease is disposed; the task completes at readiness.</summary>
    public (IDisposable Lease, Task Ready) Acquire(string workspace)
    {
        if (sessions is null) return (EmptyLease, Task.CompletedTask);
        if (!watches.TryGetValue(workspace, out var watch))
        {
            watch = watches[workspace] = new(this, workspace);
            watch.Start(sessions);
        }
        watch.Holders++;
        var released = false;
        return (new EditorEvents.Subscription(() =>
        {
            if (released) return;
            released = true;
            if (--watch.Holders == 0) Stop(watch);
        }), watch.Ready.Task);
    }

    private static readonly IDisposable EmptyLease = new EditorEvents.Subscription(() => { });

    private void Stop(Watch watch)
    {
        if (watches.GetValueOrDefault(watch.Workspace) == watch) watches.Remove(watch.Workspace);
        watch.Lifetime.Cancel();
    }

    private void Deliver(Watch watch, WorkspaceFileChanges changes, bool broad)
    {
        if (watch.Lifetime.IsCancellationRequested) return;
        var mounted = workbench.Windows.Any(window => window.WorkspaceMounted && window.WorkspaceRoot == watch.Workspace);
        if (!mounted && (broad || changes.Rescan)) workbench.InvalidateRevisions(watch.Workspace);
        else if (!mounted && (changes.Paths.Count > 0 || changes.GitChanged)) workbench.BumpRevisions(watch.Workspace, changes.Paths);
        if (broad) watch.Ready.TrySetResult();
    }

    private sealed class Watch(PluginWorkspaceWatches owner, string workspace)
    {
        public string Workspace { get; } = workspace;
        public int Holders;
        public CancellationTokenSource Lifetime { get; } = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start(Func<IProjectServices> sessions)
        {
            var token = Lifetime.Token;
            _ = Task.Run(async () =>
            {
                var session = sessions();
                try { await RunAsync(session, token); }
                finally { (session as IDisposable)?.Dispose(); }
            });
        }

        private async Task RunAsync(IProjectServices session, CancellationToken token)
        {
            var restored = false;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await session.OpenProjectAsync(Workspace, token);
                    var first = true;
                    await foreach (var changes in session.WatchFilesAsync(token))
                    {
                        var broad = first || restored;
                        first = restored = false;
                        Dispatcher.UIThread.Post(() => owner.Deliver(this, changes, broad));
                    }
                }
                catch (Exception) when (token.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    if (!Ready.Task.IsCompleted)
                    {
                        Dispatcher.UIThread.Post(() => { owner.Stop(this); Ready.TrySetException(error); });
                        return;
                    }
                    Console.Error.WriteLine($"Plugin watch of {Workspace} interrupted: {error.Message}");
                }
                restored = true;
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { break; }
            }
            Ready.TrySetCanceled(token);
        }
    }
}