using System.Runtime.CompilerServices;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    public async IAsyncEnumerable<FileChange> WatchFilesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var directory = root;
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Directory does not exist: {directory}");
        var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var gate = new object();
        var rescan = false;
        var lastChange = DateTime.UtcNow;
        var watchers = new List<FileSystemWatcher>();
        void Changed(string? path)
        {
            lock (gate)
            {
                lastChange = DateTime.UtcNow;
                if (path is null || paths.Count >= 100) rescan = true;
                else paths.Add(path);
            }
            signals.Writer.TryWrite(true);
        }
        void Watch(string path, bool recursive, Func<string, bool> relevant, bool workspace)
        {
            if (!Directory.Exists(path)) return;
            var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = recursive, InternalBufferSize = 64 * 1024 };
            watchers.Add(watcher);
            void Notify(string fullPath)
            {
                if (relevant(fullPath)) Changed(workspace ? Path.GetRelativePath(directory, fullPath).Replace('\\', '/') : null);
            }
            watcher.Created += (_, e) => Notify(e.FullPath);
            watcher.Changed += (_, e) => Notify(e.FullPath);
            watcher.Deleted += (_, e) => Notify(e.FullPath);
            watcher.Renamed += (_, e) => { Notify(e.OldFullPath); Notify(e.FullPath); };
            watcher.Error += (_, e) => { Console.Error.WriteLine("Workspace watcher error: " + e.GetException().Message); Changed(null); };
            watcher.EnableRaisingEvents = true;
        }
        string? commonDirectory = null;
        // A fetch that moved this workspace's review target changed its diff without touching a file in it.
        void Moved(string common, string reference)
        {
            if (common == commonDirectory && state?.Current.DiffBase(directory) == reference) signals.Writer.TryWrite(true);
        }
        try
        {
            Watch(directory, true, path => !Path.GetRelativePath(directory, path).Replace('\\', '/').Split('/').Any(part => part is ".git" or ".sharprail" or ".tools" or "node_modules" or ".DS_Store"), true);
            (var gitDirectory, commonDirectory) = ResolveGitDirectories(directory);
            BaseMoved += Moved;
            if (gitDirectory is not null)
            {
                Watch(gitDirectory, false, path => Path.GetFileName(path) is "HEAD" or "index", false);
                Watch(commonDirectory!, false, path => Path.GetFileName(path) == "packed-refs", false);
                Watch(Path.Combine(commonDirectory!, "refs"), true, _ => true, false);
            }
            // Re-read after registration to cover changes between the initial listing and subscription.
            yield return new([], true);
            while (await signals.Reader.WaitToReadAsync(cancellationToken))
            {
                var started = DateTime.UtcNow;
                while (true)
                {
                    await Task.Delay(50, cancellationToken);
                    DateTime last;
                    lock (gate) last = lastChange;
                    if (DateTime.UtcNow - last >= TimeSpan.FromMilliseconds(250) || DateTime.UtcNow - started >= TimeSpan.FromSeconds(1)) break;
                }
                FileChange change;
                lock (gate)
                {
                    while (signals.Reader.TryRead(out _)) { }
                    change = new(paths.ToArray(), rescan);
                    paths.Clear(); rescan = false;
                }
                yield return change;
            }
        }
        finally
        {
            BaseMoved -= Moved;
            foreach (var watcher in watchers) watcher.Dispose();
        }
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

}