using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private CancellationTokenSource? workspaceWatch;
    private CancellationTokenSource? diffRefresh;
    private readonly HashSet<string> changedPaths = [];
    // Document keys whose file is gone from disk; the tab keeps its last content and says so.
    private readonly HashSet<string> deletedDocuments = [];
    private DispatcherTimer? watchDebounce;
    private DateTime watchPendingSince;
    private bool watchGitPending;

    // A write storm refreshes at least this often instead of waiting for quiet.
    private static readonly TimeSpan WatchMaxDelay = TimeSpan.FromSeconds(1);

    /// <summary>Coalesced refreshes triggered by the workspace watcher, the native analogue of the reference's fsChanged frames.</summary>
    public int WatchRefreshes { get; private set; }

    private void StopWatching()
    {
        watchDebounce?.Stop();
        changedPaths.Clear();
        watchGitPending = false;
        workspaceWatch?.Cancel();
        workspaceWatch?.Dispose();
        workspaceWatch = null;
        diffRefresh?.Cancel();
        diffRefresh?.Dispose();
        diffRefresh = null;
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
                        if (paths.Count > 0 || changes.GitChanged || changes.Rescan || restored)
                            ScheduleWatchRefresh(paths, changes.GitChanged || changes.Paths.Count > 0 || restored || changes.Rescan);
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

    private void ScheduleWatchRefresh(IReadOnlyList<string> paths, bool gitChanged)
    {
        if (!WorkspaceMounted || lifetime.IsCancellationRequested) return;
        foreach (var path in paths) changedPaths.Add(path);
        watchGitPending |= gitChanged;
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
        var refreshGit = watchGitPending;
        changedPaths.Clear();
        watchGitPending = false;
        workbench.BumpRevisions(workspaceRoot, paths);
        RefreshSpecs(); _ = ProbeSpecsAsync();
        await Task.WhenAll(
            ReloadOpenDocumentsAsync(request, paths),
            refreshGit ? RefreshGitAsync(request) : Task.CompletedTask,
            RefreshFilesAsync(request));
    }

    // Re-lists the root and every loaded folder so expanded folders keep their children.
    private async Task RefreshFilesAsync(long request, IReadOnlyList<string>? loadFolders = null)
    {
        // Remembered expanded folders are listed too, so a restored tree opens them rather than forgetting them.
        var folders = folderCache.Keys.Concat(expandedFolders).Concat(loadFolders ?? []).Prepend("").Distinct().ToArray();
        var listed = new Dictionary<string, IReadOnlyList<ProjectFile>>();
        foreach (var folder in folders)
        {
            if (request != projectRequest || !WorkspaceMounted) return;
            try { listed[folder] = await Task.Run(async () => await host.ListFilesAsync(folder, lifetime.Token), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (folder.Length > 0 && (error is IOException or UnauthorizedAccessException || RemoteErrors.IsFailedPrecondition(error))) { }
            catch (Exception error)
            {
                if (request == projectRequest)
                {
                    startupMilestones?.Complete(request, "files", error.Message);
                    Report(error);
                }
                return;
            }
        }
        if (request != projectRequest || !WorkspaceMounted) return;
        if (toolContent.GetValueOrDefault("files") is { } panel) panel.IsHitTestVisible = true;
        if (listed.Count == folderCache.Count && listed.All(entry => folderCache.TryGetValue(entry.Key, out var current) && current.SequenceEqual(entry.Value)))
        {
            startupMilestones?.Complete(request, "files");
            _ = RefreshFileProjectsAsync(request, false);
            return;
        }
        foreach (var folder in folders) folderCache.Remove(folder);
        foreach (var entry in listed) folderCache[entry.Key] = entry.Value;
        expandedFolders.RemoveWhere(folder => !folderCache.ContainsKey(folder));
        ReconcileFilesPanel();
        startupMilestones?.Complete(request, "files");
        _ = RefreshFileProjectsAsync(request, true);
    }

    private async Task ReloadOpenDocumentsAsync(long request, IReadOnlyCollection<string> paths)
    {
        var changed = paths.ToHashSet(StringComparer.Ordinal);
        var open = Layout.State.Workspaces.GetValueOrDefault(workspaceRoot)?.Documents.Values
            .SelectMany(items => items).Where(tab => tab.Kind is "file" or "markdown" or "viewer").DistinctBy(tab => tab.Id).ToArray() ?? [];
        var tabs = open.Where(tab => tab.Kind is "file" or "markdown" &&
                (changed.Contains(tab.Path) || changed.Any(path => tab.Path.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal))))
            .ToArray();
        // Tabs whose file is not at its path any more; a rename in this batch moves them, the rest are deleted.
        var missing = new List<(string Key, DockTab Tab, FileDocument Known, bool Pending)>();
        var refreshed = false;
        foreach (var tab in tabs)
        {
            var key = workspaceRoot + ":" + tab.Id;
            if (!documents.TryGetValue(key, out var current)) continue;
            var pending = Body<Editor.CodeDocumentView>(key) is { HasPendingChanges: true };
            FileDocument file;
            try { file = await Task.Run(async () => await host.ReadFileAsync(tab.Path, lifetime.Token), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            // Only a file that is really gone marks its tab; any other failed read leaves the tab as it was.
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                if (request == projectRequest) missing.Add((key, tab, current, pending));
                continue;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            catch (Exception error) when (RemoteErrors.IsFailedPrecondition(error)) { continue; }
            if (pending || request != projectRequest || !LiveDocuments().Contains(key)) continue;
            MarkDeletedOnDisk(key, false);
            // Byte-only files read as empty text, so the hash decides whether anything changed.
            if (file.Info?.Sha256 is { } hash ? hash == current.Info?.Sha256 : file.Text == current.Text && file.ImageData is null) continue;
            if (ReloadFileBody(key, tab, file)) continue;
            documents[key] = file; DropDocumentContent(key); refreshed = true;
        }
        // A rename can report the old name in one batch and the new one in the next; a tab already marked deleted looks again.
        foreach (var tab in open)
        {
            var key = workspaceRoot + ":" + tab.Id;
            if (deletedDocuments.Contains(key) && missing.All(item => item.Key != key) && documents.TryGetValue(key, out var known))
                missing.Add((key, tab, known, false));
        }
        foreach (var (key, tab, known, pending) in missing)
        {
            var target = await FindRenamedAsync(tab.Path, known, changed, open);
            if (request != projectRequest) return;
            if (!LiveDocuments().Contains(key)) continue;
            if (target is not null) { FollowRename(workspaceRoot, tab.Path, target); continue; }
            // An editor with unsaved edits keeps its buffer silently, as it did before renames were followed.
            if (!pending) MarkDeletedOnDisk(key, true);
        }
        if (refreshed) surface.RefreshContents();
    }

    /// <summary>
    /// The path an open file was renamed or moved to within this batch of changes: a changed path, or a changed folder
    /// plus the file's place under a changed folder that vanished, whose content hash matches what the tab last read.
    /// Paths another open tab already shows are not candidates. Null when no such file exists: the file was deleted.
    /// </summary>
    private async Task<string?> FindRenamedAsync(string path, FileDocument known, IReadOnlySet<string> changed, IReadOnlyList<DockTab> open)
    {
        if (changed.Count == 0) return null;
        var name = Path.GetFileName(path);
        var shown = open.Select(tab => tab.Path).ToHashSet(StringComparer.Ordinal);
        var candidates = changed.Where(candidate => candidate != path)
            .Concat(changed.Where(folder => HostPaths.IsUnder(path, folder))
                .SelectMany(folder => changed.Where(other => other != folder).Select(other => HostPaths.Join(other, path[(folder.Length + 1)..]))))
            .Where(candidate => !shown.Contains(candidate)).Distinct(StringComparer.Ordinal)
            .OrderBy(candidate => Path.GetFileName(candidate) == name ? 0 : 1).Take(32).ToArray();
        // Empty content matches any empty file, so only the same file name in another folder counts as its move.
        var empty = known.Text.Length == 0 && known.ImageData is null && known.Info?.ByteLength is null or 0;
        foreach (var candidate in candidates)
        {
            if (empty && Path.GetFileName(candidate) != name) continue;
            FileDocument file;
            try { file = await Task.Run(async () => await host.ReadFileAsync(candidate, lifetime.Token), lifetime.Token); }
            catch (OperationCanceledException) { return null; }
            catch (Exception) { continue; }
            var same = known.Info?.Sha256 is { Length: > 0 } hash
                ? file.Info?.Sha256 == hash
                : file.ImageData is null && known.ImageData is null && file.Text == known.Text;
            if (same) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Moves the open tabs of <paramref name="from"/>, or of files under it when it is a folder, to their new paths in
    /// <paramref name="workspace"/>. An editor keeps its buffer, unsaved edits, selection, scroll and undo; another
    /// body is rebuilt once from the content it already has, now described by the new path.
    /// </summary>
    private void FollowRename(string workspace, string from, string to)
    {
        if (from == to || from.Length == 0) return;
        var tabs = Layout.State.Workspaces.GetValueOrDefault(workspace)?.Documents.Values.SelectMany(items => items)
            .Where(tab => tab.Kind is "file" or "markdown" or "viewer" && (tab.Path == from || HostPaths.IsUnder(tab.Path, from)))
            .DistinctBy(tab => tab.Id).ToArray() ?? [];
        if (tabs.Length == 0) return;
        // Retitling the tab redraws its strip, which can take focus from the editor that keeps showing the file.
        var focused = FocusManager?.GetFocusedElement() as Control;
        var rebuilt = false;
        foreach (var tab in tabs)
        {
            var target = tab.Path == from ? to : HostPaths.Join(to, tab.Path[(from.Length + 1)..]);
            var key = workspace + ":" + tab.Id;
            Layout.Retarget(workspace, tab.Id, target);
            deletedDocuments.Remove(key);
            if (documents.TryGetValue(key, out var document)) documents[key] = document with { Path = target };
            if (Body<Editor.CodeDocumentView>(key) is { } editor)
            {
                editor.Retarget(target);
                editor.DeletedOnDisk = false;
            }
            else if (documentContent.ContainsKey(key)) { DropDocumentContent(key); rebuilt = true; }
        }
        if (rebuilt) surface.RefreshContents();
        surface.RefreshModified();
        if (focused is not null && !focused.IsFocused)
            Dispatcher.UIThread.Post(() => { if (focused.IsAttachedToVisualTree() && !focused.IsFocused) focused.Focus(); }, DispatcherPriority.Loaded);
    }

    private void MarkDeletedOnDisk(string key, bool deleted)
    {
        if (EditableDocument(documentContent.GetValueOrDefault(key)) is { } view) view.DeletedOnDisk = deleted;
        if (deleted ? deletedDocuments.Add(key) : deletedDocuments.Remove(key)) surface.RefreshModified();
    }

    private async Task RefreshDiffTabsAsync(long request)
    {
        if (request != projectRequest || !WorkspaceMounted || lifetime.IsCancellationRequested) return;
        diffRefresh?.Cancel();
        diffRefresh?.Dispose();
        diffRefresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = diffRefresh.Token;
        var directory = workspaceRoot;
        foreach (var pane in documentContent.Where(pair => DocumentOwner(pair.Key).StartsWith(workspaceRoot + ":changes:", StringComparison.Ordinal))
            .Select(pair => pair.Value).OfType<ChangesReviewPane>().ToArray()) _ = pane.RefreshAsync();
        var tabs = Layout.State.Workspaces.GetValueOrDefault(workspaceRoot)?.Documents.Values
            .SelectMany(items => items).Where(tab => tab.Kind == "diff").DistinctBy(tab => tab.Id).ToArray() ?? [];
        foreach (var tab in tabs)
        {
            var key = directory + ":" + tab.Id;
            if (!documents.ContainsKey(key)) continue;
            try
            {
                var diff = await Task.Run(async () => await host.GetDiffAsync(tab.Path, tab.Scope, tab.Comparison, token), token);
                if (token.IsCancellationRequested || request != projectRequest || directory != workspaceRoot || !LiveDocuments().Contains(key) ||
                    !documents.TryGetValue(key, out var current)) continue;
                // Git's notice for a byte diff does not change with the bytes; the sides' hashes do.
                if (documentContent.GetValueOrDefault(key) is DiffView { Identity: not null } described) await DescribeDiffAsync(described, tab, key);
                if (current.Text == diff) continue;
                documents[key] = new(tab.Path, diff);
                if (documentContent.GetValueOrDefault(key) is DiffView view) view.Update(diff);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is not OperationCanceledException) { Console.Error.WriteLine(error); }
        }
    }
}