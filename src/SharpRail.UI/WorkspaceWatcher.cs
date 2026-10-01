using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly HashSet<string> changedPaths = [];
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
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
    }

    private void StartWatching(long request)
    {
        if (remote || request != projectRequest || !WorkspaceMounted || lifetime.IsCancellationRequested) return;
        StopWatching();
        var directory = workspaceRoot;
        var (gitDirectory, commonDirectory) = ResolveGitDirectories(directory);
        try
        {
            Watch(directory, true, path => !IsGitPath(directory, path));
            if (gitDirectory is not null)
            {
                Watch(gitDirectory, false, path => Path.GetFileName(path) == "HEAD");
                Watch(Path.Combine(commonDirectory!, "refs"), true, _ => true);
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        { Console.Error.WriteLine("Workspace watching is unavailable: " + error.Message); }
    }

    private static bool IsGitPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal);
    }

    private void Watch(string directory, bool recursive, Func<string, bool> relevant)
    {
        if (!Directory.Exists(directory)) return;
        var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = recursive, InternalBufferSize = 64 * 1024 };
        void Changed(string? path) { if (path is not null && relevant(path)) Dispatcher.UIThread.Post(() => ScheduleWatchRefresh(directory, path)); }
        watcher.Created += (_, e) => Changed(e.FullPath);
        watcher.Changed += (_, e) => Changed(e.FullPath);
        watcher.Deleted += (_, e) => Changed(e.FullPath);
        watcher.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
        watcher.EnableRaisingEvents = true;
        watchers.Add(watcher);
    }

    private void ScheduleWatchRefresh(string watched, string path)
    {
        if (!WorkspaceMounted || lifetime.IsCancellationRequested) return;
        if (watched == workspaceRoot) changedPaths.Add(Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/'));
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
        // Listing is cheap and leaves the tree untouched when nothing moved; FSEvents may report a creation as a change.
        if (paths.Length > 0) await RefreshFilesAsync(request);
        if (paths.Any(path => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        { toolContent.Remove("specs"); surface.RefreshContents("specs"); }
        await ReloadOpenDocumentsAsync(request, paths);
        await RefreshGitAsync(request);
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
            .SelectMany(items => items).Where(tab => tab.Kind is "file" or "markdown" && changed.Contains(tab.Path))
            .DistinctBy(tab => tab.Id).ToArray() ?? [];
        var refreshed = false;
        foreach (var tab in tabs)
        {
            var key = workspaceRoot + ":" + tab.Id;
            if (!documents.TryGetValue(key, out var current)) continue;
            if (documentContent.GetValueOrDefault(key) is Editor.CodeDocumentView { HasPendingChanges: true }) continue;
            FileDocument file;
            try { file = await Task.Run(async () => await host.ReadFileAsync(tab.Path, lifetime.Token), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (request != projectRequest || !LiveDocuments().Contains(key) || file.Text == current.Text && file.ImageData is null) continue;
            if (documentContent.GetValueOrDefault(key) is Editor.CodeDocumentView view)
            {
                if (view.Reload(file.Text)) documents[key] = file;
                continue;
            }
            documents[key] = file; DropDocumentContent(key); refreshed = true;
        }
        if (refreshed) surface.RefreshContents();
    }

    private static (string? GitDirectory, string? CommonDirectory) ResolveGitDirectories(string directory)
    {
        try
        {
            var marker = Path.Combine(directory, ".git");
            if (Directory.Exists(marker)) return (marker, marker);
            if (!File.Exists(marker)) return (null, null);
            var text = File.ReadAllText(marker).Trim();
            if (!text.StartsWith("gitdir:", StringComparison.Ordinal)) return (null, null);
            var gitDirectory = Path.GetFullPath(text["gitdir:".Length..].Trim(), directory);
            var common = Path.Combine(gitDirectory, "commondir");
            var commonDirectory = File.Exists(common) ? Path.GetFullPath(File.ReadAllText(common).Trim(), gitDirectory) : gitDirectory;
            return (gitDirectory, commonDirectory);
        }
        catch (IOException) { return (null, null); }
        catch (UnauthorizedAccessException) { return (null, null); }
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
                if (request != projectRequest || !LiveDocuments().Contains(key) ||
                    !documents.TryGetValue(key, out var current) || current.Text == diff) continue;
                documents[key] = new(tab.Path, diff);
                if (documentContent.GetValueOrDefault(key) is DiffView view) view.Update(diff);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OperationCanceledException) { Console.Error.WriteLine(error); }
        }
    }
}