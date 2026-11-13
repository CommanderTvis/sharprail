using Tereditor.Host.Abstractions;

namespace Tereditor.Host.Core;

// An open project, or a workspace of one, whose folder was deleted keeps no processes: its shells end and its language
// servers stop, once. Its stored entry stays (hidden by Visible): removing it would tell plugins the workspace is gone
// and they would forget its data, which a briefly missing folder, such as one on an unmounted volume, must not cost.
public sealed partial class HostStateStore
{
    private readonly SemaphoreSlim sweepGate = new(1, 1);
    // When each watched folder was first seen missing, the mount each was last seen on, and the folders already acted on
    // with the paths whose processes ended.
    private readonly Dictionary<string, TimeSpan> missingSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> mountOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> deletedFolders = new(StringComparer.Ordinal);
    private CancellationTokenSource? folderLoop;

    /// <summary>How often the host looks for deleted project and workspace folders while it runs terminals.</summary>
    internal static readonly TimeSpan FolderInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long a folder must stay missing, across sweeps, before its processes end.</summary>
    internal TimeSpan FolderGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The sweep's clock; checks replace it to pass the grace without waiting.</summary>
    internal Func<TimeSpan> FolderClock { get; set; } = () => TimeSpan.FromMilliseconds(Environment.TickCount64);

    // The sweep runs only in a host with terminals: the app's own and a remote one.
    private void WatchFolders(bool on)
    {
        folderLoop?.Cancel();
        folderLoop = null;
        if (!on) return;
        var loop = folderLoop = new();
        var token = loop.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(FolderInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                    try { await SweepFoldersAsync(); }
                    catch (Exception error) when (error is not OperationCanceledException) { System.Diagnostics.Trace.WriteLine("Deleted folders: " + error.Message); }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    /// <summary>
    /// One look at every open project's folder and its workspaces' folders, as the loop takes it; checks call it directly.
    /// A folder missing for <see cref="FolderGrace"/> ends its processes; one that comes back may start them again.
    /// </summary>
    internal async Task SweepFoldersAsync()
    {
        await sweepGate.WaitAsync();
        try
        {
            var now = FolderClock();
            HostState current;
            lock (gate) current = state;
            string[]? mounts = null;
            string[] Mounts() => mounts ??= MountRoots();

            // A folder acted on that is back, or no longer open, is watched like any other again.
            foreach (var (root, paths) in deletedFolders.ToArray())
            {
                var project = current.Projects.Contains(root);
                if (!project && !current.Workspaces.Any(workspace => workspace.Path == root && current.Projects.Contains(workspace.ProjectRoot)))
                    deletedFolders.Remove(root);
                else if (FolderExists(root))
                {
                    deletedFolders.Remove(root);
                    if (languages is { } reopened)
                        foreach (var path in paths) reopened.ReopenWorkspace(path);
                    if (project) StartWorkspaceLanguages(root);
                    System.Diagnostics.Trace.WriteLine($"The folder of {FolderName(current, root)} is back: {root}");
                    // The rail shows it again with the next snapshot.
                    lock (gate) Publish(state with { }, persist: false);
                }
            }

            // Workspaces are watched only while their project's folder is there; a missing project covers them.
            var watched = current.Projects.Where(project => !deletedFolders.ContainsKey(project)).ToList();
            foreach (var project in watched.ToArray())
                if (FolderExists(project))
                    watched.AddRange(current.WorkspacesOf(project).Select(workspace => workspace.Path)
                        .Where(path => path != project && !deletedFolders.ContainsKey(path)));
            foreach (var path in missingSince.Keys.Except(watched).ToArray()) missingSince.Remove(path);
            foreach (var path in mountOf.Keys.Except(current.Projects).Except(current.Workspaces.Select(workspace => workspace.Path)).ToArray()) mountOf.Remove(path);

            var gone = new List<string>();
            foreach (var path in watched)
            {
                if (FolderExists(path))
                {
                    missingSince.Remove(path);
                    if (!mountOf.ContainsKey(path) && MountOf(path, Mounts()) is { } mount) mountOf[path] = mount;
                }
                else if (Unmounted(path, Mounts())) missingSince.Remove(path);
                else if (!missingSince.TryGetValue(path, out var since)) missingSince[path] = now;
                else if (now - since >= FolderGrace) gone.Add(path);
            }

            foreach (var root in gone)
            {
                missingSince.Remove(root);
                var project = current.Projects.Contains(root);
                var workspaces = project ? current.WorkspacesOf(root).Select(workspace => workspace.Path).Where(path => path != root).ToArray() : [];
                deletedFolders[root] = [.. workspaces.Prepend(root)];
                await EndDeletedFolderAsync(root, workspaces, FolderName(current, root));
            }
            if (gone.Count > 0) Republish();
        }
        finally { sweepGate.Release(); }
    }

    // The same closing a project gets, under the same gate, except that its tabs and stored entry stay for a restored folder.
    private async Task EndDeletedFolderAsync(string root, string[] workspaces, string name)
    {
        System.Diagnostics.Trace.WriteLine($"Closed {name}: its folder was deleted ({root})");
        await changesGate.WaitAsync();
        try
        {
            if (Terminals is { } ending)
                await ending.EndDeletedFolderAsync(root, workspaces, "Closed " + name, "Its folder was deleted, so its terminals and language servers were stopped.");
            if (languages is { } stopping)
                foreach (var path in workspaces.Prepend(root)) await stopping.StopWorkspaceAsync(path, "its folder was deleted");
        }
        finally { changesGate.Release(); }
    }

    private static string FolderName(HostState current, string path) =>
        current.WorkspaceLabels.GetValueOrDefault(path) ?? current.ProjectLabels.GetValueOrDefault(path) ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    // A folder whose volume is gone is not deleted: its last mount is no longer mounted, or, never seen, its parent is missing too.
    private bool Unmounted(string path, string[] mounts)
    {
        if (mountOf.TryGetValue(path, out var mount)) return !mounts.Contains(mount, StringComparer.Ordinal);
        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is not { Length: > 0 } parent || !FolderExists(parent);
    }

    private static string? MountOf(string path, string[] mounts) =>
        mounts.Where(mount => path == mount || path.StartsWith(Path.EndsInDirectorySeparator(mount) ? mount : mount + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .MaxBy(mount => mount.Length);

    private static string[] MountRoots()
    {
        try { return [.. DriveInfo.GetDrives().Select(drive => Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName))]; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }
}