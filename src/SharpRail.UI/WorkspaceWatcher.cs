using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private CancellationTokenSource? workspaceWatch;
    private readonly HashSet<string> changedPaths = [];
    // Document keys whose file is gone from disk; the tab keeps its last content and says so.
    private readonly HashSet<string> deletedDocuments = [];
    private DispatcherTimer? watchDebounce;
    private DateTime watchPendingSince;

    // A write storm refreshes at least this often instead of waiting for quiet.
    private static readonly TimeSpan WatchMaxDelay = TimeSpan.FromSeconds(1);

    /// <summary>Coalesced refreshes triggered by the workspace watcher, the native analogue of the reference's fsChanged frames.</summary>
    public int WatchRefreshes { get; private set; }

    private void StopWatching()
    {
        watchDebounce?.Stop();
        changedPaths.Clear();
        workspaceWatch?.Cancel();
        workspaceWatch?.Dispose();
        workspaceWatch = null;
    }

    private void StartWatching(long request)
    {
        if (request != projectRequest || !WorkspaceMounted || lifetime.IsCancellationRequested) return;
        StopWatching();
        var directory = workspaceRoot;
        var cancellation = workspaceWatch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = cancellation.Token;
        _ = Task.Run(() => WatchWorkspaceFilesAsync(directory, request, token), token);
    }

    private async Task WatchWorkspaceFilesAsync(string directory, long request, CancellationToken cancellationToken)
    {
        var reconnect = false;
        var generation = state.Generation;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var firstBatch = true;
                await foreach (var changes in host.WatchFilesAsync(cancellationToken))
                {
                    var ready = firstBatch;
                    firstBatch = false;
                    var restored = reconnect;
                    reconnect = false;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (request != projectRequest || directory != workspaceRoot || !WorkspaceMounted || cancellationToken.IsCancellationRequested) return;
                        var paths = changes.Paths;
                        if (ready || restored || changes.Rescan)
                            paths = paths.Concat(Layout.State.Workspaces.GetValueOrDefault(directory)?.Documents.Values
                                .SelectMany(items => items).Where(tab => tab.Kind is "file" or "markdown" or "viewer").Select(tab => tab.Path) ?? []).Distinct().ToArray();
                        if (paths.Count > 0 || changes.GitChanged || changes.Rescan || restored) ScheduleWatchRefresh(paths);
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error) { Console.Error.WriteLine("Workspace watching interrupted: " + error.Message); }
            if (remote && (state.Connection.Status == SharpRail.Host.Client.HostConnectionStatus.Disconnected || state.Generation != generation)) return;
            reconnect = true;
            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ScheduleWatchRefresh(IReadOnlyList<string> paths)
    {
        if (!WorkspaceMounted || lifetime.IsCancellationRequested) return;
        foreach (var path in paths) changedPaths.Add(path);
        if (watchDebounce is null)
        {
            watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            watchDebounce.Tick += (_, _) =>
            {
                watchDebounce.Stop();
                if (WorkspaceMounted && !lifetime.IsCancellationRequested) _ = RefreshWatchedAsync(projectRequest);
            };
        }
        if (!watchDebounce.IsEnabled) watchPendingSince = DateTime.UtcNow;
        else if (DateTime.UtcNow - watchPendingSince >= WatchMaxDelay) return;
        watchDebounce.Stop(); watchDebounce.Start();
    }

    private async Task RefreshWatchedAsync(long request)
    {
        WatchRefreshes++;
        var paths = changedPaths.ToArray();
        changedPaths.Clear();
        workbench.BumpRevisions(workspaceRoot, paths);
        RefreshSpecs(); _ = ProbeSpecsAsync();
        await Task.WhenAll(
            ReloadOpenDocumentsAsync(request, paths),
            RefreshGitAsync(request),
            paths.Length > 0 ? RefreshFilesAsync(request) : Task.CompletedTask);
    }

    // Re-lists the root and every loaded folder so expanded folders keep their children.
    private async Task RefreshFilesAsync(long request)
    {
        var folders = folderCache.Keys.ToArray();
        var listed = new Dictionary<string, IReadOnlyList<ProjectFile>>();
        foreach (var folder in folders)
        {
            try { listed[folder] = await Task.Run(async () => await host.ListFilesAsync(folder, lifetime.Token), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (folder.Length > 0 && error is IOException or UnauthorizedAccessException) { }
            catch (Exception error) { if (request == projectRequest) Report(error); return; }
        }
        if (request != projectRequest || !WorkspaceMounted) return;
        if (listed.Count == folderCache.Count && listed.All(entry => folderCache.TryGetValue(entry.Key, out var current) && current.SequenceEqual(entry.Value)))
            return;
        folderCache.Clear();
        foreach (var entry in listed) folderCache[entry.Key] = entry.Value;
        expandedFolders.RemoveWhere(folder => !folderCache.ContainsKey(folder));
        toolContent.Remove("files"); surface.RefreshContents("files");
    }

    private async Task ReloadOpenDocumentsAsync(long request, IReadOnlyCollection<string> paths)
    {
        var changed = paths.ToHashSet(StringComparer.Ordinal);
        var tabs = Layout.State.Workspaces.GetValueOrDefault(workspaceRoot)?.Documents.Values
            .SelectMany(items => items).Where(tab => tab.Kind is "file" or "markdown" &&
                (changed.Contains(tab.Path) || changed.Any(path => tab.Path.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal))))
            .DistinctBy(tab => tab.Id).ToArray() ?? [];
        var refreshed = false;
        foreach (var tab in tabs)
        {
            var key = workspaceRoot + ":" + tab.Id;
            if (!documents.TryGetValue(key, out var current)) continue;
            if (Body<Editor.CodeDocumentView>(key) is { HasPendingChanges: true }) continue;
            FileDocument file;
            try { file = await Task.Run(async () => await host.ReadFileAsync(tab.Path, lifetime.Token), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            // Only a file that is really gone marks its tab; any other failed read leaves the tab as it was.
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                if (request == projectRequest) MarkDeletedOnDisk(key, true);
                continue;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.FailedPrecondition) { continue; }
            if (request != projectRequest || !LiveDocuments().Contains(key)) continue;
            MarkDeletedOnDisk(key, false);
            // Byte-only files read as empty text, so the hash decides whether anything changed.
            if (file.Info?.Sha256 is { } hash ? hash == current.Info?.Sha256 : file.Text == current.Text && file.ImageData is null) continue;
            if (ReloadFileBody(key, tab, file)) continue;
            documents[key] = file; DropDocumentContent(key); refreshed = true;
        }
        if (refreshed) surface.RefreshContents();
    }

    private void MarkDeletedOnDisk(string key, bool deleted)
    {
        if (documentContent.GetValueOrDefault(key) is Editor.CodeDocumentView view) view.DeletedOnDisk = deleted;
        if (deleted ? deletedDocuments.Add(key) : deletedDocuments.Remove(key)) surface.RefreshModified();
    }

    private async Task RefreshDiffTabsAsync(long request)
    {
        var tabs = Layout.State.Workspaces.GetValueOrDefault(workspaceRoot)?.Documents.Values
            .SelectMany(items => items).Where(tab => tab.Kind == "diff").ToArray() ?? [];
        foreach (var tab in tabs)
        {
            var key = workspaceRoot + ":" + tab.Id;
            if (!documents.ContainsKey(key)) continue;
            try
            {
                var diff = await Task.Run(async () => await host.GetDiffAsync(tab.Path, tab.Scope, tab.Comparison, lifetime.Token), lifetime.Token);
                if (request != projectRequest || !LiveDocuments().Contains(key) || !documents.TryGetValue(key, out var current)) continue;
                // Git's notice for a byte diff does not change with the bytes; the sides' hashes do.
                if (documentContent.GetValueOrDefault(key) is DiffView { Identity: not null } described) await DescribeDiffAsync(described, tab, key);
                if (current.Text == diff) continue;
                documents[key] = new(tab.Path, diff);
                if (documentContent.GetValueOrDefault(key) is DiffView view) view.Update(diff);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OperationCanceledException) { Console.Error.WriteLine(error); }
        }
    }
}