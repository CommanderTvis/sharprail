using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private CancellationTokenSource? workspaceWatch;
    // Document keys whose file is gone from disk; the tab keeps its last content and says so.
    private readonly HashSet<string> deletedDocuments = [];

    public int WatchRefreshes { get; private set; }

    private void StopWatching()
    {
        workspaceWatch?.Cancel();
        workspaceWatch?.Dispose();
        workspaceWatch = null;
    }

    private void StartWatching(long request)
    {
        if (request != projectRequest || !WorkspaceMounted || lifetime.IsCancellationRequested) return;
        StopWatching();
        workspaceWatch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = WatchWorkspaceAsync(request, workspaceWatch.Token);
    }

    private async Task WatchWorkspaceAsync(long request, CancellationToken cancellationToken)
    {
        var firstFrame = true;
        var generation = state.Generation;
        while (!cancellationToken.IsCancellationRequested && request == projectRequest)
        {
            try
            {
                await foreach (var change in host.WatchFilesAsync(cancellationToken))
                {
                    if (request != projectRequest || cancellationToken.IsCancellationRequested) return;
                    var refreshGit = !firstFrame;
                    firstFrame = false;
                    await RefreshWatchedAsync(request, change, refreshGit);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                Console.Error.WriteLine("Workspace watching is unavailable: " + error.Message);
                // A lost host re-hydrates the window under its next generation, which subscribes again.
                if (remote && (state.Connection.Status == SharpRail.Host.Client.HostConnectionStatus.Disconnected || state.Generation != generation)) return;
                try { await Task.Delay(1000, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RefreshWatchedAsync(long request, FileChange change, bool refreshGit)
    {
        WatchRefreshes++;
        if (change.Rescan || change.Paths.Any(path => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        { RefreshSpecs(); _ = ProbeSpecsAsync(); }
        await Task.WhenAll(
            ReloadOpenDocumentsAsync(request, change.Paths, change.Rescan),
            refreshGit ? RefreshGitAsync(request) : Task.CompletedTask,
            RefreshFilesAsync(request));
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
            catch (Grpc.Core.RpcException error) when (folder.Length > 0 && error.StatusCode == Grpc.Core.StatusCode.FailedPrecondition) { }
            catch (Exception error) { if (request == projectRequest) Report(error); return; }
            if (request != projectRequest) return;
        }
        if (request != projectRequest || !WorkspaceMounted) return;
        if (listed.Count == folderCache.Count && listed.All(entry => folderCache.TryGetValue(entry.Key, out var current) && current.SequenceEqual(entry.Value)))
            return;
        folderCache.Clear();
        foreach (var entry in listed) folderCache[entry.Key] = entry.Value;
        expandedFolders.RemoveWhere(folder => !folderCache.ContainsKey(folder));
        toolContent.Remove("files"); surface.RefreshContents("files");
    }

    private async Task ReloadOpenDocumentsAsync(long request, IReadOnlyCollection<string> paths, bool rescan)
    {
        var changed = paths.ToHashSet(StringComparer.Ordinal);
        var tabs = Layout.State.Workspaces.GetValueOrDefault(workspaceRoot)?.Documents.Values
            .SelectMany(items => items).Where(tab => tab.Kind is "file" or "markdown" &&
                (rescan || changed.Contains(tab.Path) || changed.Any(path => tab.Path.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal))))
            .DistinctBy(tab => tab.Id).ToArray() ?? [];
        var refreshed = false;
        foreach (var tab in tabs)
        {
            if (request != projectRequest || lifetime.IsCancellationRequested) return;
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
